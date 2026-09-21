namespace AfSdr.Dsp;

// Positive offset removes a positive-frequency input error: z * exp(-j phase).
// The oscillator belongs to the sample stream, never to a display frame.
internal sealed class FineFrequencyShifter(uint sampleRate)
{
    private double phase, step;
    internal void SetOffset(double hz) => step = 2 * Math.PI * hz / sampleRate;
    internal void Shift(ref float i, ref float q)
    {
        if (phase != 0)
        {
            var (sin, cos) = Math.SinCos(phase);
            float real = (float)(i * cos + q * sin);
            q = (float)(q * cos - i * sin);
            i = real;
        }
        phase = Math.IEEERemainder(phase + step, 2 * Math.PI);
    }
}
