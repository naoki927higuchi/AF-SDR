namespace AfSdr.Dsp;

internal enum FftWindow { Hann, Hamming, Blackman, BlackmanHarris, Rectangular }

internal static class WindowFunctions
{
    internal static double Value(FftWindow window, int index, int size)
    {
        double x = 2 * Math.PI * index / (size - 1);
        return window switch
        {
            FftWindow.Hann => 0.5 - 0.5 * Math.Cos(x),
            FftWindow.Hamming => 0.54 - 0.46 * Math.Cos(x),
            FftWindow.Blackman => 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x),
            FftWindow.BlackmanHarris => 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x),
            FftWindow.Rectangular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(window))
        };
    }
}
