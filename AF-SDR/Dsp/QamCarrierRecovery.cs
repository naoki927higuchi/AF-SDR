using System.Numerics;
namespace AfSdr.Dsp;

// Blind fourth-power coarse acquisition, phase search, then decision-directed PLL.
// Decisions steer the loop only: displayed samples are never snapped to ideal points.
internal sealed class QamCarrierRecovery(int order, double symbolRate)
{
    private readonly Complex[] acquisition = new Complex[2048];
    private int count;
    private double phase, frequency, power = 1;
    internal bool Acquired { get; private set; }
    internal double FrequencyHz => frequency * symbolRate / (2 * Math.PI);
    internal static Complex Nearest(Complex value, int order)
    {
        int side = (int)Math.Sqrt(order);
        double scale = Math.Sqrt(2.0 * (order - 1) / 3);
        double Level(double v) => (2 * Math.Clamp(Math.Round((v * scale + side - 1) / 2), 0, side - 1) - side + 1) / scale;
        return new Complex(Level(value.Real), Level(value.Imaginary));
    }
    internal bool Push(Complex value, out Complex output)
    {
        output = Complex.Zero;
        if (!Acquired)
        {
            acquisition[count++] = value;
            if (count == acquisition.Length) Acquire();
            return false;
        }
        power += 0.0005 * (value.Magnitude * value.Magnitude - power);
        output = value / Math.Sqrt(Math.Max(power, 1e-6)) * Complex.FromPolarCoordinates(1, -phase);
        var decision = Nearest(output, order);
        double error = Math.Clamp((output * Complex.Conjugate(decision)).Imaginary / Math.Max(0.1, decision.Magnitude * decision.Magnitude), -0.3, 0.3);
        frequency = Math.Clamp(frequency + 0.0002 * error, -0.15, 0.15);
        phase = Math.IEEERemainder(phase + frequency + 0.03 * error, 2 * Math.PI);
        return true;
    }
    private void Acquire()
    {
        int size = acquisition.Length;
        power = acquisition.Average(z => z.Magnitude * z.Magnitude);
        var spectrum = new Complex[size];
        for (int n = 0; n < size; n++)
        {
            var square = acquisition[n] * acquisition[n];
            spectrum[n] = -square * square * (0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (size - 1)));
        }
        Transform(spectrum);
        int peak = 0;
        // Limit acquisition to the same ±0.15 rad/symbol range as the tracking loop.
        for (int k = 0; k < size; k++)
        {
            int signed = k <= size / 2 ? k : k - size;
            if (Math.Abs(signed * 2 * Math.PI / size / 4) <= 0.15 && spectrum[k].Magnitude > spectrum[peak].Magnitude) peak = k;
        }
        double a = Math.Log(Math.Max(1e-20, spectrum[(peak + size - 1) % size].Magnitude));
        double b = Math.Log(Math.Max(1e-20, spectrum[peak].Magnitude));
        double c = Math.Log(Math.Max(1e-20, spectrum[(peak + 1) % size].Magnitude));
        double denominator = a - 2 * b + c;
        double offset = Math.Abs(denominator) < 1e-12 ? 0 : Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
        frequency = ((peak <= size / 2 ? peak : peak - size) + offset) * 2 * Math.PI / size / 4;
        double bestError = double.MaxValue, bestPhase = 0;
        for (int trial = 0; trial < 128; trial++)
        {
            double candidate = trial * Math.PI / 256;
            double error = 0;
            for (int n = size - 512; n < size; n++)
            {
                var point = acquisition[n] / Math.Sqrt(Math.Max(power, 1e-6)) * Complex.FromPolarCoordinates(1, -candidate - frequency * n);
                var difference = point - Nearest(point, order);
                error += difference.Real * difference.Real + difference.Imaginary * difference.Imaginary;
            }
            if (error < bestError) { bestError = error; bestPhase = candidate; }
        }
        phase = Math.IEEERemainder(bestPhase + frequency * size, 2 * Math.PI);
        Acquired = true;
    }
    private static void Transform(Complex[] values)
    {
        int size = values.Length;
        for (int i = 1, j = 0; i < size; i++)
        {
            int bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= size; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < size; start += length)
            {
                var rotation = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    var a = values[start + j]; var b = values[start + j + length / 2] * rotation;
                    values[start + j] = a + b; values[start + j + length / 2] = a - b; rotation *= step;
                }
            }
        }
    }
}
