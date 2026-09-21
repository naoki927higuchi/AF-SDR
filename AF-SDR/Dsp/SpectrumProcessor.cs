using System.Numerics;

namespace AfSdr.Dsp;

internal sealed class SpectrumProcessor
{
    public const int DefaultSize = 4096;
    internal static readonly int[] SupportedSizes = [1024, 2048, 4096, 8192, 16384];
    public int Size { get; }
    private readonly Complex[] fft;
    private readonly double[] window;
    private readonly double[] average;
    private readonly double normalization;
    private bool initialized;

    public SpectrumProcessor(int size = DefaultSize, FftWindow windowType = FftWindow.Hann)
    {
        if (!SupportedSizes.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        fft = new Complex[size];
        window = new double[size];
        average = new double[size];
        for (int i = 0; i < Size; i++) window[i] = WindowFunctions.Value(windowType, i, Size);
        normalization = Math.Pow(window.Sum(), 2);
    }

    public float[] Process(ReadOnlySpan<byte> iq) => Process(IqSamples.FromRtl(iq));

    public float[] Process(ReadOnlySpan<float> iq)
    {
        if (iq.Length < Size * 2) throw new ArgumentException("I/Q block is too short.");
        // Remove the block's DC offset before applying the window.
        double meanI = 0, meanQ = 0;
        for (int i = 0; i < Size; i++) { meanI += iq[2 * i]; meanQ += iq[2 * i + 1]; }
        meanI /= Size; meanQ /= Size;
        for (int i = 0; i < Size; i++)
            fft[i] = new Complex(iq[2 * i] - meanI, iq[2 * i + 1] - meanQ) * window[i];
        for (int i = 1, j = 0; i < Size; i++)
        {
            int bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (fft[i], fft[j]) = (fft[j], fft[i]);
        }
        for (int length = 2; length <= Size; length <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < Size; start += length)
            {
                Complex phase = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    Complex a = fft[start + j], b = fft[start + j + length / 2] * phase;
                    fft[start + j] = a + b; fft[start + j + length / 2] = a - b;
                    phase *= step;
                }
            }
        }
        var result = new float[Size];
        for (int i = 0; i < Size; i++)
        {
            Complex bin = fft[(i + Size / 2) % Size];
            double power = (bin.Real * bin.Real + bin.Imaginary * bin.Imaginary) / normalization;
            average[i] = initialized ? average[i] * 0.75 + power * 0.25 : power;
            result[i] = (float)(10 * Math.Log10(Math.Max(average[i], 1e-14)));
        }
        initialized = true;
        return result;
    }
}
