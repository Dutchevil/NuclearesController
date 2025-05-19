using System.Globalization;
using System.Text;

namespace NuclearesController;

internal class Program
{
    private const int PORT = 8785;
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);
    private static HttpClient hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}/") };
    private static readonly object logObj = new();

    private const float desiredTempMin = 310;
    private const float desiredTempMax = 360;
    private const double maxTargetReactivity = 2;
    private const double reactivitySlopeLengthDegrees = 25;
    private const float targetReactivity = 0.10f;
    private static int currentRodBankIndex = 0;
    private static readonly Queue<float> reactivityHistory = new(3);
    private const float desiredCondenserTemp = 65f;


    public static void Log(string msg, LogLevel level)
    {
        lock (logObj)
        {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => throw new NotImplementedException(),
            };
            Console.WriteLine(msg);
            Console.ForegroundColor = fg;
        }
    }

    public static void Info(string msg) => Log(msg, LogLevel.Info);
    public static void Warn(string msg) => Log(msg, LogLevel.Warning);
    public static void Error(string msg) => Log(msg, LogLevel.Error);

    public static async Task<string> GetVariableRawAsync(string varname) => await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token);
    public static Dictionary<string, object> varCache = [];
    public static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T>
    {
        if (!varname.Equals("TIME_STAMP", StringComparison.InvariantCultureIgnoreCase) && varCache.TryGetValue(varname, out var rv))
            return (T)rv;

        var rv2 = T.Parse((await GetVariableRawAsync(varname)).Replace(",", "."), null);
        varCache[varname] = rv2;
        return rv2;
    }

    public static async Task SetVariableAsync(string varname, object value)
    {
        string strVal = (value?.ToString() ?? "null").Replace('.', ',');
        var resp = await hc.PostAsync($"?variable={varname}&value={strVal}", null);
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"Non success status code for setting variable {varname} to {strVal}");
        }
    }

    private static int currentTimestamp = 0;
    private static async Task WaitForNextTimeStepAsync()
    {
        while (true)
        {
            var nextTs = await GetVariableAsync<int>("TIME_STAMP");
            if (nextTs != currentTimestamp) { currentTimestamp = nextTs; break; }
            await Task.Delay(500);
        }
    }

    private static async Task WaitForWebserverAvailableAsync()
    {
    retry:
        try { await hc.GetStringAsync("?variable=CORE_TEMP"); } catch { Console.WriteLine("Waiting for webserver to be online..."); goto retry; }
    }

    private static async Task Main(string[] args)
    {
        var c = new CultureInfo("en-US");
        c.NumberFormat.NumberGroupSeparator = " ";
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentCulture = c;
        var origConsoleColor = Console.ForegroundColor;
        Console.OutputEncoding = Encoding.Default;

    restart:
        try
        {
            Console.WriteLine("Starting controller...");
            Console.Title = "Nucleares Controller";

            await WaitForWebserverAvailableAsync();
            Console.Clear();

            int rodCount = await GetVariableAsync<int>("RODS_QUANTITY");
            double rodGainScale = 1.0 / Math.Min(rodCount, 9);

            var reactivityToRodsPid = new PID(
                1.5 * rodGainScale,
                0.1 * rodGainScale,
                0.0,
                await GetVariableAsync<float>("RODS_POS_ACTUAL"),
                true,
                (0, 100)
            );
            var condenserPumpSpeedPid = new PID(0.00005, 0.05, 0.01, await GetVariableAsync<float>("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"), true, (0, 100));

            const int setInterval = 4;
            int setIntervalRemaining = 0;
            double lastRodSet = -1000;

            while (true)
            {
                await WaitForNextTimeStepAsync();
                varCache.Clear();

                float temp = await GetVariableAsync<float>("CORE_TEMP");
                float xenonGen = await GetVariableAsync<float>("CORE_XENON_GENERATION");
                float xenonTotal = await GetVariableAsync<float>("CORE_XENON_CUMULATIVE");
                float iodineGen = await GetVariableAsync<float>("CORE_IODINE_GENERATION");
                float iodineTotal = await GetVariableAsync<float>("CORE_IODINE_CUMULATIVE");
                float reactivity = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");
                // Add current reactivity to history
                if (reactivityHistory.Count == 3)
                    reactivityHistory.Dequeue();
                reactivityHistory.Enqueue(reactivity);

                // Calculate average reactivity
                float avgReactivity = reactivityHistory.Average();

                float condenserTemp = await GetVariableAsync<float>("CONDENSER_TEMPERATURE");
                string opMode = await GetVariableAsync<string>("CORE_OPERATION_MODE");

                bool inTempRange = temp >= desiredTempMin && temp <= desiredTempMax;

                double desiredReactivity;
                // Adjust reactivity slope + baseline heat boost logic
                double tempError = 0;
                if (temp < desiredTempMin)
                {
                    tempError = desiredTempMin - temp;
                    double slopeReactivity = Math.Clamp(tempError / reactivitySlopeLengthDegrees * maxTargetReactivity, 0.05, 0.4);

                    double heatBoost = Math.Clamp((310 - temp) / 30.0, 0.05, 0.2); // floor scaling
                    desiredReactivity = Math.Max(slopeReactivity, heatBoost);

                    if (desiredReactivity == heatBoost)
                        Warn($"⚠ Enforcing reactivity floor {heatBoost:N2} to recover from cold");
                }


                else if (temp > desiredTempMax)
                {
                    tempError = desiredTempMax - temp;
                    desiredReactivity = Math.Clamp(tempError / reactivitySlopeLengthDegrees * maxTargetReactivity, -0.4, -0.05);
                }
                else
                {
                    // In range but drifting — add gentle bias if below center of range
                    float midpoint = (desiredTempMin + desiredTempMax) / 2;
                    if (temp < midpoint)
                        desiredReactivity = targetReactivity + 0.05;
                    else
                        desiredReactivity = targetReactivity;
                }

                // poison override: force extra heat when xenon is high
                if (xenonTotal > 50 || reactivity < 0)
                {
                    double poisonBoost = Math.Clamp((xenonTotal - 50) / 10.0, 0, 1); // scaled 0.0 to 0.8
                    desiredReactivity += poisonBoost * 0.5; // adds up to +0.5 reactivity
                }

                // 🚨 emergency override: force full power to save core from xenon stall
                if (xenonTotal > 60 && temp < 300 && reactivity < 0)
                {
                    desiredReactivity = 1.0; // go maximum aggressive
                    Warn("⚠ Emergency xenon override triggered!");
                }

                    bool tempHighEnough = temp >= 100;
                if (!tempHighEnough && xenonTotal > 200f)
                    Warn("Xenon poisoning likely: temp too low for suppression.");

                float newRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);



                bool emergency = xenonTotal > 60 && temp < 300 && reactivity < 0;
                if (emergency) Warn("⚠ EMERGENCY MODE: Controlled all-bank retraction in progress");

                if (rodCount > 0)
                {
                    if (emergency)
                    {
                        for (int i = 0; i < Math.Min(rodCount, 9); i++)
                        {
                            string rodVar = $"ROD_BANK_POS_{i}_ORDERED";
                            float currentPos = await GetVariableAsync<float>($"ROD_BANK_POS_{i}_ACTUAL");

                            float step = 1.0f; // Withdraw slowly per tick
                            float target = MathF.Max(currentPos - step, 10f); // Stop at 30%, never go below
                            await SetVariableAsync(rodVar, target);
                        }
                    }
                    else if (!inTempRange || Math.Abs(reactivity - targetReactivity) > 0.05f)
                    {
                        int banksToMove;
                        if (avgReactivity < 0.05 && temp < 310)
                            banksToMove = 3;
                        if (temp < 290)
                            banksToMove = 3;
                        else if (avgReactivity < 0.0)
                            banksToMove = 2;
                        else
                            banksToMove = 1;
                        Info($"Avg Reactivity: {avgReactivity:N2}, Temp: {temp:N1} °C → Moving {banksToMove} bank(s)");
                        for (int i = 0; i < banksToMove; i++)
                        {
                            if (currentRodBankIndex >= rodCount || currentRodBankIndex > 8)
                                currentRodBankIndex = 0;

                            string rodVar = $"ROD_BANK_POS_{currentRodBankIndex}_ORDERED";
                            await SetVariableAsync(rodVar, newRodsPos);
                            currentRodBankIndex = (currentRodBankIndex + 1) % Math.Min(rodCount, 9);
                        }
                    }
                }

                // Condenser PID control
                float newCondenserSpeed = (float)condenserPumpSpeedPid.Step(currentTimestamp, desiredCondenserTemp, condenserTemp);
                if (newCondenserSpeed < 1f) newCondenserSpeed = 1f;
                await SetVariableAsync("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED", newCondenserSpeed.ToString("N2"));

                if (true || setIntervalRemaining-- <= 0 || Math.Abs(lastRodSet - newRodsPos) > 0.4)
                {
                    setIntervalRemaining = setInterval;
                    lastRodSet = newRodsPos;
                }
                Console.WriteLine($"------------------------------------------------------------------");
                Console.WriteLine($"Temp: {temp:N1} °C | Reactivity: {reactivity:N2} | Rods: {newRodsPos:N1}% | Bank: {currentRodBankIndex}");
                Console.WriteLine($"Xenon Generation: {xenonGen:N2} | Iodine Generation: {iodineGen:N2}");
                Console.WriteLine($"Xenon Total: {xenonTotal:N2} | Iodine Total: {iodineTotal:N2}");
                Console.WriteLine($"Condenser Temp: {condenserTemp:N1} °C | Condenser Speed: {newCondenserSpeed:N2}%");
                Console.WriteLine($"In temp range: {inTempRange}");
                Console.WriteLine($"Reactivity history: [{string.Join(", ", reactivityHistory.Select(r => r.ToString("N2")))}]");

            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            await Task.Delay(10_000);
            goto restart;
        }
    }
}