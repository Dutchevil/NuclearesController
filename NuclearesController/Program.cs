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
    private const double maxTargetReactivity = 4;
    private const double reactivitySlopeLengthDegrees = 15;
    private const float targetReactivity = 0.10f;
    private static int currentRodBankIndex = 0;
    private static readonly Queue<float> reactivityHistory = new(3);
    private const float desiredCondenserTemp = 65f;

    // Houdt de laatst toegepaste rod-positie bij en de nog ongebruikte correctiefactor
    private static float lastAppliedRodsPos = 0;
    private static float rodAdjustmentAccumulator = 0;

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

    public static async Task<string> GetVariableRawAsync(string varname) =>
        await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token);
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
            await Task.Delay(250);
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

                // Voeg gemeten reactiviteit toe aan de geschiedenis (max 3)
                if (reactivityHistory.Count == 3)
                    reactivityHistory.Dequeue();
                reactivityHistory.Enqueue(reactivity);
                float avgReactivity = reactivityHistory.Average();

                float condenserTemp = await GetVariableAsync<float>("CONDENSER_TEMPERATURE");
                string opMode = await GetVariableAsync<string>("CORE_OPERATION_MODE");

                bool inTempRange = temp >= desiredTempMin && temp <= desiredTempMax;

                // Bepaal desiredReactivity op basis van de temperatuur:
                double desiredReactivity;
                if (temp < desiredTempMin)
                {
                    double missing = desiredTempMin - temp; // hoeveel onder desiredTempMin
                    double extraReact = (missing / reactivitySlopeLengthDegrees) * maxTargetReactivity;
                    extraReact = Math.Min(Math.Max(extraReact, 0.0), 1.0);
                    desiredReactivity = targetReactivity + extraReact;
                }
                else if (temp > desiredTempMax)
                {
                    desiredReactivity = 0.0;
                }
                else
                {
                    desiredReactivity = targetReactivity;
                }

                // poison override: force extra heat when xenon is high
                // Omdat Math.Clamp niet beschikbaar is, gebruiken we Math.Min/Math.Max
                {
                    double poisonBoost = Math.Min(Math.Max((xenonTotal - 50) / 10.0, 0), 1);
                    desiredReactivity += poisonBoost * 0.5;
                }

                // Emergency override:
                if (xenonTotal > 60 && temp < 300 && reactivity < 0)
                {
                    desiredReactivity = 1.0;
                    Warn("⚠ Emergency xenon override triggered!");
                }

                bool tempHighEnough = temp >= 100;
                if (!tempHighEnough && xenonTotal > 200f)
                    Warn("Xenon poisoning likely: temp too low for suppression.");

                float newRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);

                // Quantisatie met accumulator
                float rawNewRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);
                if (lastAppliedRodsPos == 0)
                    lastAppliedRodsPos = rawNewRodsPos;

                float diff = rawNewRodsPos - lastAppliedRodsPos;
                rodAdjustmentAccumulator += diff;
                float stepSize = 0.1f;
                int steps = (int)(rodAdjustmentAccumulator / stepSize);
                float effectiveChange = steps * stepSize;
                if (steps == 0 && diff != 0)
                {
                    effectiveChange = diff > 0 ? stepSize : -stepSize;
                    rodAdjustmentAccumulator = 0;
                }
                float effectiveNewRodsPos = lastAppliedRodsPos + effectiveChange;
                effectiveNewRodsPos = Math.Min(Math.Max(effectiveNewRodsPos, 10f), 100f);
                if (steps != 0)
                    rodAdjustmentAccumulator -= effectiveChange;
                lastAppliedRodsPos = effectiveNewRodsPos;

                // Nu de actuator voor de rodbanken
                bool emergency = xenonTotal > 60 && temp < 300 && reactivity < 0;
                if (rodCount > 0)
                {
                    if (emergency)
                    {
                        for (int i = 0; i < Math.Min(rodCount, 9); i++)
                        {
                            string rodVar = $"ROD_BANK_POS_{i}_ORDERED";
                            float currentPos = await GetVariableAsync<float>($"ROD_BANK_POS_{i}_ACTUAL");
                            float step = 1.0f; // vaste stap in emergency mode
                            float target = MathF.Max(currentPos - step, 10f);
                            await SetVariableAsync(rodVar, target.ToString("N1"));
                        }
                    }
                    else if (!inTempRange || Math.Abs(reactivity - targetReactivity) > 0.05f)
                    {
                        // Adaptive berekening van het aantal banks
                        int banksToMove;
                        {
                            // Bepaal de error (hoeveel moet de gemiddelde reactivity stijgen) 
                            float error = (float)(desiredReactivity - avgReactivity);
                            if (error < 0)
                                error = 0;
                            banksToMove = Math.Max(1, (int)Math.Ceiling(error / 0.1f));
                            banksToMove = Math.Min(banksToMove, Math.Min(rodCount, 9));
                        }
                        Info($"Avg Reactivity: {avgReactivity:N2}, Temp: {temp:N1} °C → Moving {banksToMove} bank(s). NewRodPos: {effectiveNewRodsPos:N1}");
                        for (int i = 0; i < banksToMove; i++)
                        {
                            if (currentRodBankIndex >= rodCount || currentRodBankIndex > 8)
                                currentRodBankIndex = 0;
                            string rodVar = $"ROD_BANK_POS_{currentRodBankIndex}_ORDERED";
                            await SetVariableAsync(rodVar, effectiveNewRodsPos.ToString("N1"));
                            currentRodBankIndex = (currentRodBankIndex + 1) % Math.Min(rodCount, 9);
                        }
                    }
                }

                // Condenser PID control:
                float newCondenserSpeed = (float)condenserPumpSpeedPid.Step(currentTimestamp, desiredCondenserTemp, condenserTemp);
                if (newCondenserSpeed < 1f)
                    newCondenserSpeed = 1f;
                await SetVariableAsync("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED", newCondenserSpeed.ToString("N2"));

                if (true || setIntervalRemaining-- <= 0 || Math.Abs(lastRodSet - newRodsPos) > 0.4)
                {
                    setIntervalRemaining = setInterval;
                    lastRodSet = newRodsPos;
                }
                Console.WriteLine("------------------------------------------------------------------");
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
            await Task.Delay(5_000);
            goto restart;
        }
    }
}
