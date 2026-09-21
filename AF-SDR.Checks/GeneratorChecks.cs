using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AfSignalGenerator;
using AfSdr.Dsp;
using GeneratorSettingsStore = AfSignalGenerator.SettingsStore;

namespace AfSdr.Checks;

internal static class GeneratorChecks
{
    private static void Require(bool pass, string message) { if (!pass) throw new Exception("SignalGenerator: " + message); }
    internal static void Run(string? goldenFolder = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AFSG-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var golden = new SignalSettings { Duration = .8 };
            var first = SignalWriter.Generate(golden, directory);
            var second = SignalWriter.Generate(golden, directory);
            Require(File.ReadAllBytes(first.WavePath).SequenceEqual(File.ReadAllBytes(second.WavePath)), "same seed/settings -> bit identical WAV");
            Require(File.ReadAllText(first.JsonPath) == File.ReadAllText(second.JsonPath), "deterministic sidecar");
            using (var reader = new IqWaveReader(first.WavePath))
            {
                Require(reader.Info.Rate == 250000 && reader.Info.Bits == 32 && reader.Info.Format == 3 && reader.Info.Frames == 200000 && reader.Info.FilenameFrequency == golden.Fc, "AF-SDR native RF64 float reader compatibility");
                var dsp = new ConstellationProcessor(250000, new DigitalSettings(true, DigitalMode.Qpsk));
                float[] iq; double power = 0; long samples = 0;
                while ((iq = reader.Read(8192)).Length > 0) { dsp.Process(iq); foreach (float v in iq) power += v * v; samples += iq.Length / 2; }
                double db = 10 * Math.Log10(power / samples); Require(Math.Abs(db + 20) < .00001, "measured complex RMS exactly -20 dBFS");
                var frame = dsp.Snapshot(0); double rms = Math.Sqrt(frame.Points.Average(p => p.X * p.X + p.Y * p.Y));
                double evm = Math.Sqrt(frame.Points.Average(p => Math.Pow(Math.Abs(p.X / rms) - Math.Sqrt(.5), 2) + Math.Pow(Math.Abs(p.Y / rms) - Math.Sqrt(.5), 2)));
                Console.WriteLine($"Generator Golden QPSK through AF-SDR: EVM {evm:P3}, {db:F6} dBFS, CFO {frame.FrequencyErrorHz:F4} Hz");
                Require(evm < .06 && Math.Abs(frame.FrequencyErrorHz) < 2 && frame.Points.Length == 1024, "Golden Signal forms four correct clusters");
            }
            foreach (var mode in Enum.GetValues<Modulation>())
            {
                var choices = mode == Modulation.QAM ? new[] { 16, 64, 256 } : mode == Modulation.FSK ? new[] { 2, 4, 8 } : mode == Modulation.ASK ? new[] { 0, 2, 4, 8 } : new[] { 0 };
                foreach (int option in choices)
                {
                    var s = golden with { Duration = .06, Modulation = mode, QamOrder = mode == Modulation.QAM ? option : 16,
                        FskTones = mode == Modulation.FSK ? option : 2, AmplitudeMode = option == 0 ? AmplitudeMode.OOK : AmplitudeMode.ASK, AskOrder = option is 2 or 4 or 8 ? option : 4 };
                    var result = SignalWriter.Generate(s, directory);
                    using var reader = new IqWaveReader(result.WavePath);
                    var iq = reader.Read(65536); Require(iq.Length == 30000 && iq.All(float.IsFinite), mode + " finite readable IQ");
                    if (mode == Modulation.QAM)
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(result.JsonPath));
                        double gain = json.RootElement.GetProperty("Resolved").GetProperty("CleanGain").GetDouble();
                        double sps = 250000.0 / 9600, mse = 0; int count = 0, axis = (int)Math.Sqrt(option);
                        double scale = Math.Sqrt(2.0 * (option - 1) / 3);
                        for (int symbol = 13; (symbol + 13) * sps < iq.Length / 2; symbol++)
                        {
                            double center = symbol * sps; Complex point = Complex.Zero;
                            for (int n = (int)Math.Ceiling(center - 12 * sps); n <= center + 12 * sps; n++)
                                point += new Complex(iq[2 * n], iq[2 * n + 1]) * SignalEngine.Rrc((n - center) / sps, .35) / (sps * gain);
                            double nearestI = Enumerable.Range(0, axis).Select(v => (2 * v - axis + 1) / scale).MinBy(v => Math.Abs(v - point.Real));
                            double nearestQ = Enumerable.Range(0, axis).Select(v => (2 * v - axis + 1) / scale).MinBy(v => Math.Abs(v - point.Imaginary));
                            mse += Math.Pow(point.Real - nearestI, 2) + Math.Pow(point.Imaginary - nearestQ, 2); count++;
                        }
                        Require(Math.Sqrt(mse / count) < .02, option + " QAM waveform matched-filter constellation");
                    }
                    if (mode is Modulation.FSK or Modulation.MSK or Modulation.GMSK)
                    {
                        double maxError = 0;
                        for (int n = 0; n < iq.Length; n += 2) maxError = Math.Max(maxError, Math.Abs(iq[n] * iq[n] + iq[n + 1] * iq[n + 1] - .01));
                        Require(maxError < 1e-7, mode + " constant envelope / no phase resets");
                    }
                }
            }
            var impaired = golden with { Modulation = Modulation.BPSK, FrequencyOffset = 450, FrequencyDrift = 120, FrequencyJitter = 8, BaudOffsetPpm = 500, BaudDriftPpmPerSecond = 20, BaudJitterPpm = 30, Awgn = true, SnrDb = 18, RandomPhase = true, RandomTiming = true };
            var impaired1 = SignalWriter.Generate(impaired, directory); var impaired2 = SignalWriter.Generate(impaired, directory);
            Require(impaired1.Sha256 == impaired2.Sha256, "impairments, random phase/timing and AWGN reproducible");
            Require(Math.Abs(impaired1.MeasuredSnrDb!.Value - 18) < .1, "AWGN measured power ratio");
            var cleanResult = SignalWriter.Generate(impaired with { Awgn = false }, directory);
            using (var clean = new IqWaveReader(cleanResult.WavePath))
            using (var noisy = new IqWaveReader(impaired1.WavePath))
            {
                double cleanPower = 0, differencePower = 0; float[] a;
                while ((a = clean.Read(65536)).Length > 0)
                {
                    var b = noisy.Read(65536);
                    for (int n = 0; n < a.Length; n++) { cleanPower += a[n] * a[n]; differencePower += Math.Pow(b[n] - a[n], 2); }
                }
                Require(Math.Abs(10 * Math.Log10(cleanPower / differencePower) - 18) < .1, "AWGN independently measured from actual noisy-minus-clean WAV");
            }
            Require(SignalWriter.Generate(impaired with { Seed = 2 }, directory).Sha256 != impaired1.Sha256, "different seed changes signal");
            VerifyModels();
            var original = new AppSettings { Parameters = impaired, InputFolder = directory, OutputFolder = directory };
            string settingsPath = Path.Combine(directory, "settings.json"); GeneratorSettingsStore.Save(settingsPath, original);
            Require(GeneratorSettingsStore.Load(settingsPath, out _) == original, "all settings round trip");
            var restored = original.Golden(); Require(restored.Parameters == new SignalSettings() && restored.InputFolder == directory && restored.OutputFolder == directory, "reset all parameters but preserve paths");
            using (var form = new GeneratorForm(settingsPath))
            {
                Require(form.ReadParameters() == impaired && form.CaptureSettings() == original, "all UI values restored");
                foreach (var mode in Enum.GetValues<Modulation>()) { form.ApplyParameters(impaired with { Modulation = mode }); Require(form.ReadParameters().Modulation == mode, "dynamic options " + mode); }
                form.ApplyParameters(new()); Require(form.ReadParameters() == new SignalSettings() && form.CaptureSettings().OutputFolder == directory, "UI Golden reset keeps output path");
                void Handles(Control c) { _ = c.Handle; foreach (Control child in c.Controls) Handles(child); c.PerformLayout(); }
                Handles(form); using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(Environment.CurrentDirectory, "generator-check.png"));
            }
            foreach (var bad in new[] { golden with { Baud = 100000 }, golden with { FrequencyDrift = 100000, Duration = 10 }, golden with { BaudDriftPpmPerSecond = -100000, Duration = 10 }, golden with { Fc = 1 }, golden with { SnrDb = double.NaN } }) Require(bad.Validate().Length > 0, "reject impossible combination");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); int count = Directory.GetFiles(directory).Length;
                try { SignalWriter.Generate(golden, directory, token: cancel.Token); throw new Exception("not cancelled"); } catch (OperationCanceledException) { }
                Require(Directory.GetFiles(directory).Length == count && Directory.GetFiles(directory, "*.tmp*").Length == 0, "cancellation leaves no partial files");
            }
            using (var cancel = new CancellationTokenSource())
            {
                int count = Directory.GetFiles(directory).Length;
                try { SignalWriter.Generate(golden, directory, new CancelProgress(cancel), cancel.Token); throw new Exception("write not cancelled"); }
                catch (OperationCanceledException) { }
                Require(Directory.GetFiles(directory).Length == count && Directory.GetFiles(directory, "*.tmp*").Length == 0, "cancel during write removes incomplete pair");
            }
            if (goldenFolder is not null)
            {
                var result = SignalWriter.Generate(new SignalSettings(), goldenFolder);
                Console.WriteLine("Golden 10 second vector: " + result.WavePath);
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: SignalGenerator all modes/options, Golden AF-SDR EVM, RMS/SNR, clock/carrier models, Gaussian continuity/statistics, deterministic WAV/JSON, persistence/reset and cancellation.");
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<int>
    {
        public void Report(int value) { if (value >= 50) cancellation.Cancel(); }
    }
    private static void VerifyModels()
    {
        var s = new SignalSettings { Modulation = Modulation.BPSK, FrequencyOffset = 300, FrequencyDrift = 200, BaudOffsetPpm = 1000, BaudDriftPpmPerSecond = 100, TimingOffsetSymbols = .2, InitialPhaseDegrees = 40 };
        var engine = new SignalEngine(s); SignalSample previous = default;
        for (int n = 0; n < 25000; n++)
        {
            var value = engine.Next(); double t = n / (double)s.SampleRate;
            Require(Math.Abs(value.CarrierHz - (300 + 200 * t)) < 1e-9, "continuous frequency ramp");
            double u = 24.2 + s.Baud * ((1 + .001) * t + .5 * .0001 * t * t);
            Require(Math.Abs(value.Coordinate - u) < 1e-8, "integrated baud offset/drift/timing");
            if (n > 0 && value.Value.Magnitude > .01 && previous.Value.Magnitude > .01)
            {
                Complex d = value.Value * value.Value * Complex.Conjugate(previous.Value * previous.Value);
                double hz = d.Phase * s.SampleRate / (4 * Math.PI);
                Require(Math.Abs(hz - (previous.CarrierHz + value.CarrierHz) / 2) < 1e-6, "actual waveform carries specified offset/drift");
            }
            previous = value;
        }
        var process = new SmoothGaussian(8273, .01); var values = new double[200000];
        for (int n = 0; n < values.Length; n++) values[n] = process.At(n * .0001);
        double mean = values.Average(), rms = Math.Sqrt(values.Average(x => x * x)), kurtosis = values.Average(x => Math.Pow(x, 4)) / Math.Pow(rms, 4);
        Require(Math.Abs(mean) < .1 && Math.Abs(rms - 1) < .08 && kurtosis is > 2.6 and < 3.4, "Gaussian ensemble statistics");
        Require(values.Zip(values.Skip(1), (a, b) => Math.Abs(a - b)).Max() < .3, "correlated, non-hopping jitter");
        var bits = new DataSource(DataPattern.PRBS15, 1); var set = new HashSet<int>(); int word = 0;
        for (int i = 0; i < 32767 + 14; i++) { word = ((word << 1) | bits.Bits(1)) & 32767; if (i >= 14) set.Add(word); }
        Require(set.Count == 32767 && !set.Contains(0), "PRBS15 maximal period");
        var ms = new SignalEngine(new SignalSettings { Modulation = Modulation.MSK });
        Complex last = ms.Next().Value;
        for (int n = 1; n < 4000; n++) { Complex current = ms.Next().Value; double hz = (current * Complex.Conjugate(last)).Phase * 250000 / (2 * Math.PI); Require(Math.Abs(hz) <= 2400.00001, "MSK h=0.5 deviation and continuous phase"); last = current; }
        var boundary = new SignalEngine(new SignalSettings { Modulation = Modulation.MSK, SampleRate = 307200 });
        Complex previousBoundary = boundary.Next().Value;
        for (int symbol = 0; symbol < 100; symbol++)
        {
            Complex current = Complex.Zero;
            for (int n = 0; n < 32; n++) current = boundary.Next().Value;
            Require(Math.Abs(Math.Abs((current * Complex.Conjugate(previousBoundary)).Phase) - Math.PI / 2) < 1e-9, "MSK exact +/-pi/2 per symbol, including transitions");
            previousBoundary = current;
        }
    }
}
