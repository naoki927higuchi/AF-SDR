using System.Numerics;
using AfSdr.Dsp;
namespace AfSdr.Checks;
internal static class ModulationChecks
{
    internal static readonly List<(DigitalSettings Settings, ConstellationFrame Frame)> Previews = [];
    internal static void Run()
    {
        var configurations = new[]
        {
            new DigitalSettings(true, DigitalMode.Qam, QamOrder: 16),
            new DigitalSettings(true, DigitalMode.Qam, QamOrder: 64),
            new DigitalSettings(true, DigitalMode.Pi4Qpsk),
            new DigitalSettings(true, DigitalMode.Ask, AskOrder: 2),
            new DigitalSettings(true, DigitalMode.Ask, AskOrder: 4),
            new DigitalSettings(true, DigitalMode.Fsk, FskOrder: 2),
            new DigitalSettings(true, DigitalMode.Fsk, FskOrder: 4),
            new DigitalSettings(true, DigitalMode.Msk),
            new DigitalSettings(true, DigitalMode.Fsk, FskOrder: 4, FskSpacing: 20000)
        };
        foreach (uint rate in new uint[] { 250000, 2048000 })
        foreach (var settings in configurations)
        {
            byte[] iq = Signal(settings, rate, 1.2, 60);
            var dsp = new ConstellationProcessor(rate, settings);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int n = 0; n < iq.Length; n += 32768) dsp.Process(iq.AsSpan(n, Math.Min(32768, iq.Length - n)));
            var frame = dsp.Snapshot(0);
            if (rate == 250000) Previews.Add((settings, frame));
            Require(frame.Points.Length == 1024 && frame.Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "Bounded finite measured points");
            Console.Write($"{rate} S/s ");
            double error;
            var occupied = new HashSet<int>();
            if (settings.Mode == DigitalMode.Qam)
            {
                error = Math.Sqrt(frame.Points.Average(p =>
                {
                    var measured = new Complex(p.X, p.Y); var nearest = QamCarrierRecovery.Nearest(measured, settings.QamOrder);
                    occupied.Add(HashCode.Combine(Math.Round(nearest.Real, 3), Math.Round(nearest.Imaginary, 3)));
                    return (measured - nearest).Magnitude * (measured - nearest).Magnitude;
                }));
                Console.WriteLine($"{settings.QamOrder}QAM: EVM {error:P2}, occupied {occupied.Count}, CFO {frame.FrequencyErrorHz:F2} Hz, {clock.ElapsedMilliseconds} ms");
                Require(error < 0.10 && occupied.Count == settings.QamOrder && Math.Abs(frame.FrequencyErrorHz - 60) < 8, "QAM all clusters / frequency recovery");
            }
            else if (settings.Mode == DigitalMode.Pi4Qpsk)
            {
                error = Math.Sqrt(frame.Points.Average(p => Math.Pow(Math.Abs(p.X) - Math.Sqrt(0.5), 2) + Math.Pow(Math.Abs(p.Y) - Math.Sqrt(0.5), 2)));
                Console.WriteLine($"pi/4: differential EVM {error:P2}, CFO {frame.FrequencyErrorHz:F2} Hz");
                Require(error < 0.12 && Math.Abs(frame.FrequencyErrorHz - 60) < 8, "pi/4 differential phase clusters");
            }
            else
            {
                int levels = settings.Mode == DigitalMode.Ask ? settings.AskOrder : settings.Tones;
                double[] expected = Enumerable.Range(0, levels).Select(k => settings.Mode == DigitalMode.Ask
                    ? k / Math.Sqrt((levels - 1) * (2 * levels - 1) / 6.0) : (k - (levels - 1) / 2.0) * settings.Spacing + 60).ToArray();
                error = Math.Sqrt(frame.Points.Average(p =>
                {
                    int nearest = Enumerable.Range(0, levels).MinBy(k => Math.Abs(p.X - expected[k])); occupied.Add(nearest);
                    return Math.Pow(p.X - expected[nearest], 2);
                }));
                double normalized = error / (settings.FrequencyMode ? settings.Spacing : 1);
                Console.WriteLine($"{settings.Mode} {levels}: normalized RMS {normalized:P2}, levels {occupied.Count}, {clock.ElapsedMilliseconds} ms");
                Require(normalized < 0.14 && occupied.Count == levels, "Envelope / frequency levels reflect transmitted signal");
                Require(frame.Trace?.Length == 128 && frame.Trace.All(float.IsFinite), "Amplitude / frequency time trace");
            }
            var whole = new ConstellationProcessor(rate, settings); whole.Process(iq);
            Require(whole.Snapshot(0).Points.SequenceEqual(frame.Points), "Extended modes preserve block-boundary state");
        }
        foreach (var settings in configurations)
        {
            var zero = new ConstellationProcessor(250000, settings);
            zero.Process(Enumerable.Repeat((byte)128, 32768).ToArray());
            Require(zero.Snapshot(0).Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "No NaN for low-level input");
        }
        Console.WriteLine("PASS: QAM, pi/4 shift, ASK/OOK, FSK and MSK measured signal displays.");
    }
    internal static byte[] Signal(DigitalSettings settings, uint rate, double seconds, double offset)
    {
        var random = new Random(614);
        double baud = settings.SymbolRate * 1.0001, phase = 0.61;
        int count = (int)(baud * seconds) + 40;
        var symbols = new Complex[count];
        for (int k = 0; k < count; k++)
        {
            if (settings.Mode == DigitalMode.Qam)
            {
                int side = (int)Math.Sqrt(settings.QamOrder);
                symbols[k] = new Complex(2 * random.Next(side) - side + 1, 2 * random.Next(side) - side + 1) / Math.Sqrt(2.0 * (settings.QamOrder - 1) / 3);
            }
            else if (settings.Mode == DigitalMode.Pi4Qpsk)
            {
                phase += (2 * random.Next(4) + 1) * Math.PI / 4;
                symbols[k] = Complex.FromPolarCoordinates(1, phase);
            }
            else if (settings.Mode == DigitalMode.Ask)
                symbols[k] = new Complex(random.Next(settings.AskOrder) / Math.Sqrt((settings.AskOrder - 1) * (2 * settings.AskOrder - 1) / 6.0), 0);
            else symbols[k] = new Complex((random.Next(settings.Tones) - (settings.Tones - 1) / 2.0) * settings.Spacing, 0);
        }
        phase = 0.61;
        var iq = new byte[(int)(rate * seconds) * 2];
        for (int n = 0; n < iq.Length / 2; n++)
        {
            double time = n * baud / rate + 10.37;
            Complex value;
            if (settings.FrequencyMode)
            {
                phase += 2 * Math.PI * (symbols[(int)time].Real + offset) / rate;
                value = Complex.FromPolarCoordinates(0.35, phase);
            }
            else
            {
                value = Complex.Zero;
                if (settings.Mode == DigitalMode.Ask) value = symbols[(int)time];
                else for (int k = (int)time - 5; k <= (int)time + 5; k++)
                {
                    double t = time - k, b = settings.Rolloff;
                    double pulse = Math.Abs(t) < 1e-10 ? 1 + b * (4 / Math.PI - 1)
                        : Math.Abs(Math.Abs(4 * b * t) - 1) < 1e-8 ? b / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * b)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * b)))
                        : (Math.Sin(Math.PI * t * (1 - b)) + 4 * b * t * Math.Cos(Math.PI * t * (1 + b))) / (Math.PI * t * (1 - 16 * b * b * t * t));
                    value += symbols[k] * pulse;
                }
                value *= Complex.FromPolarCoordinates(0.3, 0.61 + 2 * Math.PI * offset * n / rate);
            }
            iq[n * 2] = (byte)Math.Clamp(Math.Round(127.5 + 128 * (value.Real + (random.NextDouble() - 0.5) * 0.004)), 0, 255);
            iq[n * 2 + 1] = (byte)Math.Clamp(Math.Round(127.5 + 128 * (value.Imaginary + (random.NextDouble() - 0.5) * 0.004)), 0, 255);
        }
        return iq;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}
