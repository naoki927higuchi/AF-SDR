using System.Numerics;
using AfSdr.Dsp;
namespace AfSdr.Checks;
internal static class ConstellationChecks
{
    internal static ConstellationFrame? Preview;
    internal static void Run()
    {
        foreach (uint rate in new uint[] { 250000, 1024000, 3200000 })
        foreach (var mode in new[] { DigitalMode.Bpsk, DigitalMode.Qpsk })
        {
            var config = new DigitalSettings(true, mode, 9600, 0.35);
            byte[] signal = Signal(rate, 9600 * 1.0001, mode, 60, 0.8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var dsp = new ConstellationProcessor(rate, config);
            for (int offset = 0; offset < signal.Length; offset += 1234) dsp.Process(signal.AsSpan(offset, Math.Min(1234, signal.Length - offset)));
            var frame = dsp.Snapshot(0);
            Preview = frame;
            double rms = Math.Sqrt(frame.Points.Average(p => p.X * p.X + p.Y * p.Y));
            double mse = frame.Points.Average(p =>
            {
                double i = p.X / rms, q = p.Y / rms;
                return mode == DigitalMode.Bpsk ? Math.Pow(Math.Abs(i) - 1, 2) + q * q
                    : Math.Pow(Math.Abs(i) - Math.Sqrt(0.5), 2) + Math.Pow(Math.Abs(q) - Math.Sqrt(0.5), 2);
            });
            Console.WriteLine($"{rate} S/s {mode} ({clock.ElapsedMilliseconds} ms / 800 ms IQ): EVM {Math.Sqrt(mse):P2}, carrier {frame.FrequencyErrorHz:F2} Hz, symbols {frame.Symbols}");
            Require(Math.Sqrt(mse) < 0.18 && Math.Abs(frame.FrequencyErrorHz - 60) < 8, "PSK constellation clusters with carrier/timing offset");
            var whole = new ConstellationProcessor(rate, config); whole.Process(signal);
            Require(whole.Snapshot(0).Points.SequenceEqual(frame.Points), "DSP state preserved across arbitrary USB boundaries");
            Require(frame.Symbols > 7400 && frame.Symbols < 7800, "One sample per recovered symbol");
        }
        var raw = new ConstellationProcessor(250000, new DigitalSettings(true, DigitalMode.Iq));
        raw.Process(Enumerable.Repeat((byte)128, 32768).ToArray());
        Require(raw.Snapshot(0).Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "Finite low-level IQ");
        Console.WriteLine("PASS: BPSK/QPSK timing/carrier recovery, streaming continuity and bounded IQ display.");
    }
    internal static byte[] Signal(uint rate, double baud, DigitalMode mode, double offsetHz, double seconds)
    {
        var random = new Random(781);
        var symbols = Enumerable.Range(0, (int)(baud * seconds) + 30).Select(_ => mode == DigitalMode.Bpsk
            ? new Complex(random.Next(2) * 2 - 1, 0)
            : new Complex(random.Next(2) * 2 - 1, random.Next(2) * 2 - 1) / Math.Sqrt(2)).ToArray();
        var iq = new byte[(int)(rate * seconds) * 2];
        for (int n = 0; n < iq.Length / 2; n++)
        {
            double time = n * baud / rate + 10.37;
            var sample = Complex.Zero;
            for (int k = (int)time - 5; k <= (int)time + 5; k++)
            {
                double t = time - k, beta = 0.35;
                double pulse = Math.Abs(t) < 1e-10 ? 1 + beta * (4 / Math.PI - 1)
                    : (Math.Sin(Math.PI * t * (1 - beta)) + 4 * beta * t * Math.Cos(Math.PI * t * (1 + beta))) / (Math.PI * t * (1 - 16 * beta * beta * t * t));
                sample += symbols[k] * pulse;
            }
            sample *= Complex.FromPolarCoordinates(0.35, 0.6 + 2 * Math.PI * offsetHz * n / rate);
            iq[n * 2] = (byte)Math.Clamp(Math.Round(127.5 + 128 * (sample.Real + (random.NextDouble() - 0.5) * 0.005)), 0, 255);
            iq[n * 2 + 1] = (byte)Math.Clamp(Math.Round(127.5 + 128 * (sample.Imaginary + (random.NextDouble() - 0.5) * 0.005)), 0, 255);
        }
        return iq;
    }
    private static void Require(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); }
}
