namespace AfSdr.Dsp;

// Streaming broadcast WFM mono. State is preserved across USB blocks, not FFT frames.
internal sealed class FmDemodulator
{
    internal const int AudioRate = 48_000;
    private readonly List<ComplexFir> decimators = [];
    private readonly ComplexFir channel;
    private readonly AudioResampler audio;
    private readonly double discriminatorScale;
    private float previousI, previousQ;
    private bool hasPrevious;
    private double deEmphasis, dcInput, dcOutput;
    private int fade;
    private readonly double deAlpha = 1 - Math.Exp(-1.0 / (AudioRate * 50e-6));
    private readonly double dcPole = Math.Exp(-2 * Math.PI * 20 / AudioRate);

    internal FmDemodulator(uint sampleRate, uint rxBandwidth = 200_000)
    {
        if (!ReceiveSettings.ValidRate(sampleRate)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!ReceiveSettings.RxBandwidths.Contains(rxBandwidth) || rxBandwidth > sampleRate) throw new ArgumentOutOfRangeException(nameof(rxBandwidth));
        double rate = sampleRate;
        while (rate >= 500_000)
        {
            decimators.Add(new ComplexFir(Fir.LowPass(63, 0.25), 2));
            rate /= 2;
        }
        channel = new ComplexFir(Fir.LowPass(129, rxBandwidth / 2.0 / rate), 1);
        audio = new AudioResampler(rate);
        discriminatorScale = rate / (2 * Math.PI * 75_000);
    }

    internal float[] Process(ReadOnlySpan<byte> iq)
    {
        if ((iq.Length & 1) != 0) throw new ArgumentException("I/Q samples must be paired.");
        var output = new List<float>(iq.Length / 8);
        for (int n = 0; n < iq.Length; n += 2)
        {
            float i = (iq[n] - 127.5f) / 128, q = (iq[n + 1] - 127.5f) / 128;
            bool available = true;
            foreach (var stage in decimators)
                if (!stage.Push(i, q, out i, out q)) { available = false; break; }
            if (!available) continue;
            channel.Push(i, q, out i, out q);
            double angle = hasPrevious ? Math.Atan2(q * previousI - i * previousQ, i * previousI + q * previousQ) : 0;
            previousI = i; previousQ = q; hasPrevious = true;
            if (!audio.Push((float)(angle * discriminatorScale), out float sample)) continue;
            // 50 us de-emphasis (Japan); 20 Hz DC blocker removes residual tuning offset.
            deEmphasis += deAlpha * (sample - deEmphasis);
            dcOutput = deEmphasis - dcInput + dcPole * dcOutput;
            dcInput = deEmphasis;
            float ramp = Math.Min(++fade / 960f, 1); // 20 ms fade after start/discontinuity
            if (fade > 960) fade = 960;
            output.Add((float)Math.Clamp(dcOutput * 0.7 * ramp, -1, 1));
        }
        return output.ToArray();
    }
}

internal static class Fir
{
    internal static float[] LowPass(int length, double cutoff, double fraction = 0)
    {
        var taps = new float[length];
        double sum = 0;
        for (int n = 0; n < length; n++)
        {
            double x = n - (length - 1) / 2.0 - fraction;
            double sinc = Math.Abs(x) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (length - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (length - 1));
            taps[n] = (float)(sinc * window);
            sum += taps[n];
        }
        for (int n = 0; n < length; n++) taps[n] /= (float)sum;
        return taps;
    }
}

internal sealed class ComplexFir
{
    private readonly (int Offset, float Tap)[] taps;
    private readonly float[] real, imag;
    private readonly int mask, decimation;
    private int position, phase;

    internal ComplexFir(float[] coefficients, int decimation)
    {
        this.decimation = decimation;
        int length = 1;
        while (length < coefficients.Length) length <<= 1;
        real = new float[length]; imag = new float[length]; mask = length - 1;
        taps = coefficients.Select((t, n) => (Offset: n, Tap: t)).Where(t => Math.Abs(t.Tap) > 1e-9).ToArray();
    }

    internal bool Push(float i, float q, out float outputI, out float outputQ)
    {
        position = (position + 1) & mask;
        real[position] = i; imag[position] = q;
        outputI = outputQ = 0;
        if (++phase < decimation) return false;
        phase = 0;
        foreach (var tap in taps)
        {
            int index = (position - tap.Offset) & mask;
            outputI += real[index] * tap.Tap;
            outputQ += imag[index] * tap.Tap;
        }
        return true;
    }
}

// 64 fractional-delay phases. Lowpass filtering happens BEFORE conversion to 48 kHz.
internal sealed class AudioResampler
{
    private readonly double inputRate;
    private readonly float[][] coefficients;
    private readonly float[] delay;
    private readonly int mask;
    private int position;
    private double phase;

    internal AudioResampler(double inputRate)
    {
        this.inputRate = inputRate;
        int taps = ((int)Math.Ceiling(6 * inputRate / 4_000)) | 1;
        coefficients = Enumerable.Range(0, 64).Select(p => Fir.LowPass(taps, 15_000 / inputRate, p / 64.0)).ToArray();
        int length = 1;
        while (length < taps) length <<= 1;
        delay = new float[length]; mask = length - 1;
    }

    internal bool Push(float sample, out float output)
    {
        position = (position + 1) & mask;
        delay[position] = sample;
        output = 0;
        phase += FmDemodulator.AudioRate;
        if (phase < inputRate) return false;
        phase -= inputRate;
        int bank = Math.Clamp((int)(phase / FmDemodulator.AudioRate * 64), 0, 63);
        var taps = coefficients[bank];
        for (int n = 0; n < taps.Length; n++) output += delay[(position - n) & mask] * taps[n];
        return true;
    }
}
