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
    public static Dictionary<string, object> varCache = new();
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

            // Stel de maximale foutdrempel in waarvoor de volledige stap wordt bereikt:
            const float maxErrorForFullStep = 1.0f; // Pas dit aan op basis van je systeem

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
                    // We verhogen hier het maximum extrareactiviteit tot 1.0 (kun je aanpassen)
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

                // Bereken de PID-uitgang
                float newRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);

                // Dynamische quantisatie met accumulator:
                // Bereken eerst de ruwe waarde vanuit de PID-controller
                float rawNewRodsPos = (float)reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivity);

                // Initialiseer lastAppliedRodsPos als dit de eerste iteratie is
                if (lastAppliedRodsPos == 0)
                {
                    lastAppliedRodsPos = rawNewRodsPos;
                }

                // Bereken het verschil tussen de ruwe PID-uitgang en de laatst toegepaste positie
                float diff = rawNewRodsPos - lastAppliedRodsPos;
                rodAdjustmentAccumulator += diff;

                // Bereken de fout in de reactiviteit (hoeveel moet de gemiddelde reactivity stijgen naar desiredReactivity)
                float errorVal = (float)(desiredReactivity - avgReactivity);
                if (errorVal < 0)
                    errorVal = 0;

                // Bepaal een dynamische stapgrootte, lineair geschaald van 0.1 tot maximaal 1.0
                float dynamicStepSize = 0.1f + ((errorVal / maxErrorForFullStep) * (1.0f - 0.1f));
                dynamicStepSize = Math.Min(Math.Max(dynamicStepSize, 0.1f), 1.0f);

                // Bereken het aantal volledige stappen dat in de accumulator zit op basis van de dynamische stapgrootte:
                int steps = (int)(rodAdjustmentAccumulator / dynamicStepSize);
                float effectiveChange = steps * dynamicStepSize;

                // Als er geen volledige stap is maar er is wel verschil, forceren we een stap
                if (steps == 0 && diff != 0)
                {
                    effectiveChange = diff > 0 ? dynamicStepSize : -dynamicStepSize;
                    rodAdjustmentAccumulator = 0;  // Reset de accumulator wanneer we forcerend bijstellen
                }

                // Bereken de effectieve nieuwe rodpositie en beperk deze tot de grenzen (bijv. 10% tot 100%)
                float effectiveNewRodsPos = lastAppliedRodsPos + effectiveChange;
                effectiveNewRodsPos = Math.Min(Math.Max(effectiveNewRodsPos, 10f), 100f);

                // Pas de accumulator aan wanneer een volledige stap is toegepast
                if (steps != 0)
                    rodAdjustmentAccumulator -= effectiveChange;

                // Update de laatst toegepaste positie voor de volgende iteratie
                lastAppliedRodsPos = effectiveNewRodsPos;

                // Bepaal adaptief het aantal banks dat moet bewegen op basis van de fout, de dynamicStepSize én de mate waarin de reactiviteit negatief is:
                float extraBanks = 0.0f;
                if (avgReactivity < 0)
                {
                    // Hier bepaalt de multiplier hoeveel extra banks er worden opgeteld. 
                    // Een multiplier van 10 betekent bijvoorbeeld dat bij een avgReactivity van -0.05
                    // er extraBanks = 0.5 wordt, wat bij afronding een extra bank kan opleveren.
                    float multiplier = 10.0f; // Pas deze waarde aan op basis van de gewenste gevoeligheid.
                    extraBanks = -avgReactivity * multiplier;
                }

                int banksToMove = Math.Max(1, (int)Math.Ceiling((errorVal / dynamicStepSize) + extraBanks));
                banksToMove = Math.Min(banksToMove, Math.Min(rodCount, 9));

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
                    else if (!inTempRange || Math.Abs(reactivity - targetReactivity) > 0.10f)
                    {
                        Info($"Avg Reactivity: {avgReactivity:N2}, Temp: {temp:N1} °C, dynamicStepSize: {dynamicStepSize:N2} → Moving {banksToMove} bank(s). NewRodPos: {effectiveNewRodsPos:N1}");
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
            await Task.Delay(5000);
            goto restart;
        }
    }
}
