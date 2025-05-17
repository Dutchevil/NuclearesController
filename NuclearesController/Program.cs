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
    private const float desiredCondenserTemp = 65f;

    // for smoothing iodineGen
    private static readonly Queue<float> iodineGenHistory = new();
    private const int IodineAvgLength = 10;

    // for boron‑PID control
    private static PID? boronPid;
    private const float targetBoronPpm = 3300f;

    // cooldown so we only recalc boron every N ticks
    private static int boronEvalCooldown = 0;
    private const int BoronEvalInterval = 5;

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
            float initialBoron = await GetVariableAsync<float>("CHEM_BORON_PPM");
            boronPid = new PID(0.05, 0.001, 0.01, initialBoron, true, (-50, 50));

            const int setInterval = 4;
            int setIntervalRemaining = 0;
            double lastRodSet = -1000;

            while (true)
            {
                await WaitForNextTimeStepAsync();
                varCache.Clear();

                float temp = await GetVariableAsync<float>("CORE_TEMP");
                float xenon = await GetVariableAsync<float>("CORE_XENON_CUMULATIVE");
                float iodine = await GetVariableAsync<float>("CORE_IODINE_CUMULATIVE");
                iodineGenHistory.Enqueue(iodine);
             if (iodineGenHistory.Count > IodineAvgLength)
                    iodineGenHistory.Dequeue();
                float avgIodineGen = iodineGenHistory.Average();
                float reactivity = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");
                float condenserTemp = await GetVariableAsync<float>("CONDENSER_TEMPERATURE");
                string opMode = await GetVariableAsync<string>("CORE_OPERATION_MODE");

                bool inTempRange = temp >= desiredTempMin && temp <= desiredTempMax;

                double desiredReactivity;
                if (!inTempRange)
                {
                    double tempError = temp > desiredTempMax ? temp - desiredTempMax : temp - desiredTempMin;
                    desiredReactivity = Math.Clamp(-tempError, -reactivitySlopeLengthDegrees, reactivitySlopeLengthDegrees)
                                         / reactivitySlopeLengthDegrees * maxTargetReactivity;
                }
                else
                {
                    // within temp range, but drifting low? apply gentle reheat
                    if (temp < desiredTempMin + 3)
                        desiredReactivity = 0.1;
                    else
                        desiredReactivity = targetReactivity;
                }

                // poison override: force extra heat when xenon is high
                if (xenon > 50 || reactivity < 0)
                {
                    double poisonBoost = Math.Clamp((xenon - 50) / 10.0, 0, 1); // scaled 0.0 to 0.8
                    desiredReactivity += poisonBoost * 0.5; // adds up to +0.5 reactivity
                }

                // 🚨 emergency override: force full power to save core from xenon stall
                if (xenon > 60 && temp < 300 && reactivity < 0)
                {
                    desiredReactivity = 1.0; // go maximum aggressive
                    Warn("⚠ Emergency xenon override triggered!");
                }

                    bool tempHighEnough = temp >= 100;
                if (!tempHighEnough && xenon > 200f)
                    Warn("Xenon poisoning likely: temp too low for suppression.");

                float newRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);



                bool emergency = xenon > 60 && temp < 300 && reactivity < 0;
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
                        if (currentRodBankIndex >= rodCount || currentRodBankIndex > 8) currentRodBankIndex = 0;
                        string rodVar = $"ROD_BANK_POS_{currentRodBankIndex}_ORDERED";
                        await SetVariableAsync(rodVar, newRodsPos);
                        currentRodBankIndex = (currentRodBankIndex + 1) % Math.Min(rodCount, 9);
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

                if (--boronEvalCooldown <= 0)
                {
                    boronEvalCooldown = BoronEvalInterval;

                    float boronPpm = await GetVariableAsync<float>("CHEM_BORON_PPM");
                    float doseRate = 0f, filterRate = 0f;
                    bool ruleUsed = false;

                    if (temp < 300 && reactivity < 0.01f)
                    {
                        filterRate = 50f;
                        ruleUsed = true;
                        Warn("Cold reactor: purging boron.");
                    }
                    else if (avgIodineGen > 1.2f && temp > 320 && boronPpm < 4500f)
                    {
                        doseRate = MathF.Min((avgIodineGen - 1.2f) * 50f, 50f);
                        ruleUsed = true;
                        Info($"Dosing boron due to high iodine generation: {avgIodineGen:N2}");
                    }

                    if (boronPpm > 5000)
                    {
                        doseRate = 0f;
                        filterRate = 50f;
                        ruleUsed = true;
                        Warn("Boron ppm too high — filtering excess.");
                    }

                    if (!ruleUsed)
                    {
                        float boronOut = (float)boronPid.Step(currentTimestamp, targetBoronPpm, boronPpm);
                        doseRate = Math.Clamp(boronOut, 0f, 50f);
                        filterRate = Math.Clamp(-boronOut, 0f, 50f);

                        Console.WriteLine($"[BORON] PPM: {boronPpm:N1} | PID Out: {boronOut:N2} | Dose: {doseRate:N1} | Filter: {filterRate:N1}");
                        Console.WriteLine($"[IODINE AVG] {avgIodineGen:N2} | Reactivity: {reactivity:N2} | Temp: {temp:N1}");
                    }

                    await SetVariableAsync("CHEM_BORON_DOSAGE_ORDERED_RATE", doseRate);
                    await SetVariableAsync("CHEM_BORON_FILTER_ORDERED_SPEED", filterRate);
                }

                Console.WriteLine($"Temp: {temp:N1} °C | Reactivity: {reactivity:N2} | Rods: {newRodsPos:N1}% | Bank: {currentRodBankIndex}");
                Console.WriteLine($"Xenon: {xenon:N2} | Iodine: {iodine:N2} | In temp range: {inTempRange}");
                Console.WriteLine($"Condenser Temp: {condenserTemp:N1} °C | Condenser Speed: {newCondenserSpeed:N2}%");
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