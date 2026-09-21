using System.Numerics;

namespace AfSdr.Dsp;

internal enum DigitalMode { Iq, Bpsk, Qpsk }
internal sealed record DigitalSettings(bool Enabled = false, DigitalMode Mode = DigitalMode.Qpsk, int SymbolRate = 9600, double Rolloff = 0.35)
{
    internal void Validate(uint rate)
    {
        if (!Enum.IsDefined(Mode) || SymbolRate < 1000 || SymbolRate > Math.Min(100000, rate / 8)
            || !new[] { 0.2, 0.35, 0.5, 1.0 }.Contains(Rolloff))
            throw new ArgumentException("デジタル設定は1～100 kbaud、かつ受信サンプルレートの1/8以下にしてください。");
    }
}
internal sealed record ConstellationFrame(PointF[] Points, double FrequencyErrorHz, long Symbols, int Discontinuities);

// Streaming RRC -> Gardner timing loop -> symbol-rate Costas carrier loop.
internal sealed class ConstellationProcessor
{
    private readonly List<ComplexFir> decimators = [];
    private readonly ComplexFir matched;
    private readonly DigitalSettings settings;
    private readonly double nominalPeriod;
    private double period, nextTime, sampleTime = -1, phase, frequency, power = 0.1;
    private Complex previousInput, previousHalf, previousSymbol;
    private bool half;
    private int halfCount;
    private long symbols;
    private readonly Queue<PointF> points = new();
    internal double FrequencyErrorHz => frequency * settings.SymbolRate / (2 * Math.PI);

    internal ConstellationProcessor(uint rate, DigitalSettings settings)
    {
        settings.Validate(rate);
        this.settings = settings;
        double workingRate = rate;
        while (workingRate >= 16 * settings.SymbolRate)
        {
            decimators.Add(new ComplexFir(Fir.LowPass(63, 0.25), 2));
            workingRate /= 2;
        }
        nominalPeriod = period = workingRate / settings.SymbolRate;
        matched = new ComplexFir(RootRaisedCosine(nominalPeriod, settings.Rolloff), 1);
        nextTime = 12 * nominalPeriod;
    }

    internal static float[] RootRaisedCosine(double samplesPerSymbol, double beta)
    {
        int length = ((int)Math.Ceiling(10 * samplesPerSymbol)) | 1;
        var taps = new float[length];
        double sum = 0;
        for (int n = 0; n < length; n++)
        {
            double t = (n - (length - 1) / 2.0) / samplesPerSymbol;
            double value;
            if (Math.Abs(t) < 1e-9) value = 1 + beta * (4 / Math.PI - 1);
            else if (Math.Abs(Math.Abs(4 * beta * t) - 1) < 1e-8)
                value = beta / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * beta)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * beta)));
            else value = (Math.Sin(Math.PI * t * (1 - beta)) + 4 * beta * t * Math.Cos(Math.PI * t * (1 + beta)))
                / (Math.PI * t * (1 - 16 * beta * beta * t * t));
            taps[n] = (float)value; sum += value;
        }
        for (int n = 0; n < length; n++) taps[n] /= (float)sum;
        return taps;
    }

    internal void Process(ReadOnlySpan<byte> iq)
    {
        if ((iq.Length & 1) != 0) throw new ArgumentException("I/Q samples must be paired.");
        for (int n = 0; n < iq.Length; n += 2)
        {
            float i = (iq[n] - 127.5f) / 128, q = (iq[n + 1] - 127.5f) / 128;
            bool ready = true;
            foreach (var stage in decimators)
                if (!stage.Push(i, q, out i, out q)) { ready = false; break; }
            if (!ready) continue;
            matched.Push(i, q, out i, out q);
            double magnitude = i * i + q * q;
            power += 0.002 * (magnitude - power);
            var input = new Complex(i, q) / Math.Sqrt(Math.Max(power, 1e-6));
            sampleTime++;
            if (settings.Mode == DigitalMode.Iq)
            {
                if (sampleTime >= nextTime) { Add(input); nextTime += nominalPeriod / 2; }
            }
            else if (sampleTime >= nextTime)
            {
                double fraction = Math.Clamp(nextTime - (sampleTime - 1), 0, 1);
                var value = previousInput + fraction * (input - previousInput);
                double correction = 0;
                if (!half)
                {
                    if (halfCount >= 2)
                    {
                        double error = Math.Clamp(((previousSymbol - value) * Complex.Conjugate(previousHalf)).Real, -1, 1);
                        period = Math.Clamp(period + 0.0002 * error, nominalPeriod * 0.99, nominalPeriod * 1.01);
                        correction = 0.03 * error;
                    }
                    previousSymbol = value;
                    var rotated = value * Complex.FromPolarCoordinates(1, -phase);
                    double carrierError = settings.Mode == DigitalMode.Bpsk
                        ? Math.Sign(rotated.Real) * rotated.Imaginary
                        : Math.Sign(rotated.Real) * rotated.Imaginary - Math.Sign(rotated.Imaginary) * rotated.Real;
                    carrierError = Math.Clamp(carrierError, -1, 1);
                    frequency = Math.Clamp(frequency + 0.0004 * carrierError, -0.15, 0.15);
                    phase = Math.IEEERemainder(phase + frequency + 0.04 * carrierError, 2 * Math.PI);
                    Add(rotated); symbols++;
                }
                else previousHalf = value;
                half = !half; halfCount = Math.Min(halfCount + 1, 2);
                nextTime += period / 2 + correction;
            }
            previousInput = input;
        }
    }
    private void Add(Complex value)
    {
        points.Enqueue(new PointF((float)value.Real, (float)value.Imaginary));
        if (points.Count > 1024) points.Dequeue();
    }
    internal ConstellationFrame Snapshot(int discontinuities) => new(points.ToArray(), FrequencyErrorHz, symbols, discontinuities);
}
