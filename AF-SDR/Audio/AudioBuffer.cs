namespace AfSdr.Audio;

// Bounded jitter buffer. A small adaptive playback correction absorbs independent clock drift.
internal sealed class AudioBuffer
{
    private const int Target = 5760; // 120 ms covers 65.5 ms USB blocks at 250 kS/s
    private readonly float[] samples = new float[14400]; // 300 ms maximum
    private readonly object gate = new();
    private int read, count;
    private double fraction, gain;
    private bool primed;
    internal bool Reliable { get; set; }
    internal bool Draining { get; set; }
    internal int Count { get { lock (gate) return count; } }
    internal bool HasRoom(int length) { lock (gate) return count + length <= samples.Length; }
    internal long DroppedSamples { get; private set; }

    internal void Write(ReadOnlySpan<float> source)
    {
        lock (gate)
        {
            foreach (float value in source)
            {
                if (count == samples.Length)
                {
                    int discard = count - Target;
                    read = (read + discard) % samples.Length;
                    count -= discard;
                    DroppedSamples += discard;
                }
                samples[(read + count) % samples.Length] = value;
                count++;
            }
        }
    }

    internal void Read(short[] output, float volume)
    {
        lock (gate)
        {
            if (!primed && (count >= Target || Draining && count > 0)) primed = true;
            double step = Reliable ? 1 : 1 + Math.Clamp((count - Target) / (double)Target * 0.002, -0.001, 0.001);
            for (int n = 0; n < output.Length; n++)
            {
                gain += (Math.Clamp(volume, 0, 1) - gain) * 0.004;
                if (!primed || count < (Reliable ? 1 : 3))
                {
                    output[n] = 0;
                    primed = false; gain = 0; fraction = 0;
                    continue;
                }
                float a = samples[read], b = count > 1 ? samples[(read + 1) % samples.Length] : a;
                output[n] = (short)(Math.Clamp((a + (b - a) * fraction) * gain, -1, 1) * short.MaxValue);
                fraction += step;
                int consume = (int)fraction;
                fraction -= consume;
                read = (read + consume) % samples.Length;
                count -= consume;
            }
        }
    }

    internal void Clear()
    {
        lock (gate) { read = count = 0; fraction = gain = 0; primed = false; Draining = false; }
    }
}
