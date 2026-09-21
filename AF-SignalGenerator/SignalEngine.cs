using System.Numerics;

namespace AfSignalGenerator;

// Versioned, runtime-independent integer PRNG; domains isolate data, phase, clocks and AWGN.
internal sealed class RandomSource(ulong seed)
{
    private ulong state = seed;
    internal ulong Next()
    {
        ulong z = unchecked(state += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }
    internal double Uniform() => ((Next() >> 11) + .5) / 9007199254740992.0;
    internal double Gaussian() => Math.Sqrt(-2 * Math.Log(Uniform())) * Math.Cos(2 * Math.PI * Uniform());
}

// C1-continuous Gaussian process. Variance normalization gives sigma^2 at EVERY time,
// unlike unnormalized interpolation. Knot interval is not a -3 dB bandwidth.
internal sealed class SmoothGaussian
{
    private readonly RandomSource random;
    private readonly double interval, offset;
    private long index;
    private double left, right;
    internal SmoothGaussian(ulong seed, double seconds)
    {
        random = new(seed); interval = seconds; offset = random.Uniform();
        left = random.Gaussian(); right = random.Gaussian();
    }
    internal double At(double t)
    {
        double u = t / interval + offset; long next = (long)Math.Floor(u);
        while (index < next) { left = right; right = random.Gaussian(); index++; }
        double h = (1 - Math.Cos(Math.PI * (u - next))) / 2;
        return ((1 - h) * left + h * right) / Math.Sqrt((1 - h) * (1 - h) + h * h);
    }
}

internal sealed class DataSource
{
    private readonly DataPattern pattern;
    private readonly RandomSource random;
    private uint register;
    private ulong word;
    private int remaining;
    internal DataSource(DataPattern pattern, uint seed)
    {
        this.pattern = pattern; random = new(seed ^ 0x6a09e667f3bcc909UL);
        uint mask = pattern == DataPattern.PRBS15 ? 0x7fffu : 0x7fffffu;
        register = seed & mask; if (register == 0) register = 1;
    }
    internal int Bits(int count)
    {
        int value = 0;
        for (int n = 0; n < count; n++)
        {
            int bit;
            if (pattern == DataPattern.RandomBits)
            {
                if (remaining == 0) { word = random.Next(); remaining = 64; }
                bit = (int)(word & 1); word >>= 1; remaining--;
            }
            else
            {
                int degree = pattern == DataPattern.PRBS15 ? 15 : 23, tap = degree == 15 ? 14 : 18;
                bit = (int)((register >> (degree - 1)) & 1);
                uint feedback = ((register >> (degree - 1)) ^ (register >> (tap - 1))) & 1;
                register = ((register << 1) | feedback) & ((1u << degree) - 1);
            }
            value = (value << 1) | bit;
        }
        return value;
    }
}

internal readonly record struct SignalSample(Complex Value, double CarrierHz, double Baud, double Coordinate,
    double CarrierJitterHz, double BaudJitterPpm, double CarrierPhase);

internal sealed class SignalEngine
{
    internal const int Radius = 12;
    private const int Resolution = 2048;
    private readonly SignalSettings s;
    private readonly DataSource data;
    private readonly SmoothGaussian carrierJitter, clockJitter;
    private readonly Complex[] symbols = new Complex[64];
    private readonly double[] pulse;
    private long generated = -1, frame;
    private double differentialPhase, modulationPhase, carrierPhase, coordinate;
    private double previousCarrier, previousBaud, previousDeviation, coordinateCompensation;
    internal double ResolvedPhaseDegrees { get; }
    internal double ResolvedTimingSymbols { get; }
    internal SignalEngine(SignalSettings settings)
    {
        s = settings;
        data = new(s.DataPattern, s.Seed);
        carrierJitter = new(s.Seed ^ 0xbb67ae8584caa73bUL, s.CarrierKnotMs / 1000);
        clockJitter = new(s.Seed ^ 0x3c6ef372fe94f82bUL, s.BaudKnotMs / 1000);
        ResolvedPhaseDegrees = s.RandomPhase ? 360 * new RandomSource(s.Seed ^ 0xa54ff53a5f1d36f1UL).Uniform() : s.InitialPhaseDegrees;
        ResolvedTimingSymbols = s.RandomTiming ? new RandomSource(s.Seed ^ 0x510e527fade682d1UL).Uniform() : s.TimingOffsetSymbols;
        carrierPhase = ResolvedPhaseDegrees * Math.PI / 180;
        coordinate = 2 * Radius + ResolvedTimingSymbols;
        pulse = new double[Radius * Resolution + 1];
        for (int n = 0; n < pulse.Length; n++) pulse[n] = s.UsesRrc ? Rrc(n / (double)Resolution, s.RrcAlpha)
            : GaussianPulse(n / (double)Resolution, s.Modulation == Modulation.GMSK ? s.Bt : .5);
    }
    internal static double Rrc(double t, double beta)
    {
        if (Math.Abs(t) < 1e-10) return 1 + beta * (4 / Math.PI - 1);
        if (beta == 0) return Math.Sin(Math.PI * t) / (Math.PI * t);
        if (Math.Abs(Math.Abs(4 * beta * t) - 1) < 1e-8)
            return beta / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * beta)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * beta)));
        return (Math.Sin(Math.PI * t * (1 - beta)) + 4 * beta * t * Math.Cos(Math.PI * t * (1 + beta))) / (Math.PI * t * (1 - 16 * beta * beta * t * t));
    }
    private static double Erf(double x)
    {
        double sign = Math.Sign(x); x = Math.Abs(x); double t = 1 / (1 + .3275911 * x);
        return sign * (1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - .284496736) * t + .254829592) * t * Math.Exp(-x * x));
    }
    internal static double GaussianPulse(double t, double bt)
    {
        double a = 2 * Math.PI * bt / Math.Sqrt(2 * Math.Log(2));
        return .5 * (Erf(a * (t + .5)) - Erf(a * (t - .5)));
    }
    private double Pulse(double t)
    {
        double x = Math.Abs(t) * Resolution; int n = (int)x;
        return n >= pulse.Length - 1 ? 0 : pulse[n] + (pulse[n + 1] - pulse[n]) * (x - n);
    }
    private static int GrayDecode(int v) { int result = v; while ((v >>= 1) > 0) result ^= v; return result; }
    private Complex NextSymbol()
    {
        switch (s.Modulation)
        {
            case Modulation.BPSK: return new(data.Bits(1) == 0 ? 1 : -1, 0);
            case Modulation.QPSK:
                int q = data.Bits(2); return new Complex((q & 2) == 0 ? 1 : -1, (q & 1) == 0 ? 1 : -1) / Math.Sqrt(2);
            case Modulation.PSK8: return Complex.FromPolarCoordinates(1, GrayDecode(data.Bits(3)) * Math.PI / 4);
            case Modulation.Pi4QPSK:
                differentialPhase += new[] { 1, 3, -1, -3 }[data.Bits(2)] * Math.PI / 4;
                differentialPhase = Math.IEEERemainder(differentialPhase, 2 * Math.PI);
                return Complex.FromPolarCoordinates(1, differentialPhase);
            case Modulation.QAM:
                int axis = (int)Math.Sqrt(s.QamOrder), bits = (int)Math.Log2(axis);
                return new Complex(2 * GrayDecode(data.Bits(bits)) - axis + 1, 2 * GrayDecode(data.Bits(bits)) - axis + 1) / Math.Sqrt(2.0 * (s.QamOrder - 1) / 3);
            case Modulation.ASK:
                int levels = s.AmplitudeMode == AmplitudeMode.OOK ? 2 : s.AskOrder;
                int code = data.Bits((int)Math.Log2(levels));
                return new(s.AmplitudeMode == AmplitudeMode.OOK ? code : (code + 1.0) / levels, 0);
            case Modulation.FSK: return new((data.Bits((int)Math.Log2(s.FskTones)) - (s.FskTones - 1) / 2.0) * s.FskSpacing, 0);
            default: return new(2 * data.Bits(1) - 1, 0);
        }
    }
    internal SignalSample Next()
    {
        double t = frame / (double)s.SampleRate;
        double fj = s.FrequencyJitter == 0 ? 0 : s.FrequencyJitter * carrierJitter.At(t);
        double bj = s.BaudJitterPpm == 0 ? 0 : s.BaudJitterPpm * clockJitter.At(t);
        double frequency = s.FrequencyOffset + s.FrequencyDrift * t + fj;
        double baud = s.Baud * (1 + (s.BaudOffsetPpm + s.BaudDriftPpmPerSecond * t + bj) * 1e-6);
        if (baud <= 0 || baud > s.SampleRate / 8.0 || Math.Abs(frequency) + s.HalfBandwidth(baud) >= s.SampleRate * .45)
            throw new InvalidDataException($"t={t:F6} s: 実際のジッターを含む信号が帯域/サンプル条件を超えました。誤差またはBaudを下げてください。");
        double previousCoordinate = coordinate;
        if (frame > 0)
        {
            // Compensated integration prevents artificial clock drift on long recordings.
            double step = (previousBaud + baud) / (2 * s.SampleRate) - coordinateCompensation;
            double nextCoordinate = coordinate + step;
            coordinateCompensation = (nextCoordinate - coordinate) - step;
            coordinate = nextCoordinate;
            carrierPhase = Math.IEEERemainder(carrierPhase + Math.PI * (previousCarrier + frequency) / s.SampleRate, 2 * Math.PI);
        }
        long center = (long)Math.Floor(coordinate);
        while (generated < center + Radius) { generated++; symbols[generated & 63] = NextSymbol(); }
        Complex sample;
        if (s.UsesRrc || s.Modulation is Modulation.ASK or Modulation.GMSK)
        {
            sample = Complex.Zero;
            for (long k = center - Radius + 1; k <= center + Radius; k++) sample += symbols[k & 63] * Pulse(coordinate - k);
        }
        else sample = symbols[center & 63];
        if (s.Modulation is Modulation.FSK or Modulation.MSK or Modulation.GMSK)
        {
            double deviation = s.Modulation == Modulation.FSK ? sample.Real : sample.Real * baud / 4;
            if (frame > 0)
            {
                double increment;
                if (s.Modulation == Modulation.GMSK) increment = Math.PI * (previousDeviation + deviation) / s.SampleRate;
                else
                {
                    long before = (long)Math.Floor(previousCoordinate);
                    double a = symbols[before & 63].Real, b = symbols[center & 63].Real;
                    // Integrate the RECTANGULAR pulse across its actual symbol boundary.
                    // Endpoint trapezoids here would create unintended data-dependent phase jitter.
                    if (s.Modulation == Modulation.MSK)
                    {
                        double area = before == center ? a * (coordinate - previousCoordinate)
                            : a * (center - previousCoordinate) + b * (coordinate - center);
                        increment = Math.PI / 2 * area;
                    }
                    else
                    {
                        double dt = 1.0 / s.SampleRate, boundaryTime = dt;
                        if (before != center)
                        {
                            double distance = center - previousCoordinate, slope = (baud - previousBaud) / dt;
                            boundaryTime = Math.Clamp(2 * distance / (previousBaud + Math.Sqrt(previousBaud * previousBaud + 2 * slope * distance)), 0, dt);
                        }
                        increment = 2 * Math.PI * (a * boundaryTime + b * (dt - boundaryTime));
                    }
                }
                modulationPhase = Math.IEEERemainder(modulationPhase + increment, 2 * Math.PI);
            }
            previousDeviation = deviation;
            sample = Complex.FromPolarCoordinates(1, modulationPhase);
        }
        sample *= Complex.FromPolarCoordinates(1, carrierPhase);
        previousBaud = baud; previousCarrier = frequency; frame++;
        return new(sample, frequency, baud, coordinate, fj, bj, carrierPhase);
    }
}
