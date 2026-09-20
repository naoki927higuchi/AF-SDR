using System.Diagnostics;
using AfSdr.Audio;
using AfSdr.Dsp;

namespace AfSdr.Checks;

internal static class FmChecks
{
    internal static void Run()
    {
        foreach (uint rate in ReceiveSettings.Rates)
        {
            byte[] iq = Modulated(rate, 0.25);
            var demod = new FmDemodulator(rate);
            var output = new List<float>();
            var watch = Stopwatch.StartNew();
            for (int offset = 0; offset < iq.Length; offset += 32768)
                output.AddRange(demod.Process(iq.AsSpan(offset, Math.Min(32768, iq.Length - offset))));
            Require(Math.Abs(output.Count - 12000) <= 2, "48 kHz sample count at " + rate);
            float[] stable = output.Skip(2400).ToArray();
            double amplitude = ToneAmplitude(stable, 1000);
            double rms = Math.Sqrt(stable.Average(x => (double)x * x));
            Require(amplitude is > 0.23 and < 0.30, $"FM 1 kHz amplitude at {rate}: {amplitude}");
            Require(Math.Sqrt(Math.Max(0, rms * rms - amplitude * amplitude / 2)) < 0.015,
                "FM distortion/DC at " + rate);
            Console.WriteLine($"FM {rate / 1e6:0.###} MS/s: 250 ms IQ -> {output.Count} audio samples in {watch.ElapsedMilliseconds} ms");
        }
        byte[] continuity = Modulated(1_024_000, 0.1);
        float[] whole = new FmDemodulator(1_024_000).Process(continuity);
        var split = new FmDemodulator(1_024_000);
        var parts = new List<float>();
        for (int offset = 0; offset < continuity.Length; offset += 8182)
            parts.AddRange(split.Process(continuity.AsSpan(offset, Math.Min(8182, continuity.Length - offset))));
        Require(whole.SequenceEqual(parts), "FM state preserved across arbitrary I/Q block boundaries");

        var resampler = new AudioResampler(256_000);
        var rejected = new List<float>();
        for (int n = 0; n < 256_000 / 5; n++)
            if (resampler.Push((float)Math.Sin(2 * Math.PI * 19000 * n / 256000), out float value)) rejected.Add(value);
        Require(Math.Sqrt(rejected.Skip(2000).Average(x => (double)x * x)) < 0.001, "19 kHz stereo pilot rejection");

        var buffer = new AudioBuffer();
        var pcm = new short[960];
        buffer.Write(Enumerable.Repeat(0.5f, 10000).ToArray());
        buffer.Read(pcm, 0);
        Require(pcm.All(v => v == 0), "Volume zero is mute");
        buffer.Read(pcm, 1);
        Require(pcm[^1] is > 15000 and < 17000, "PCM volume scaling");
        buffer.Write(new float[30000]);
        Require(buffer.DroppedSamples > 0, "Audio latency buffer is bounded");
        buffer.Clear(); buffer.Read(pcm, 1);
        Require(pcm.All(v => v == 0), "Cleared/underrun audio is silence");
    }

    // Optional host integration check: opens only the default audio device and submits SILENCE.
    internal static void SilentOutputSmoke()
    {
        for (int cycle = 0; cycle < 3; cycle++)
        {
            using var output = new WaveAudioOutput(0);
            output.Ready.GetAwaiter().GetResult();
            for (int n = 0; n < 12; n++) { output.Write(new float[960]); Thread.Sleep(20); }
            Require(output.Failure is null, "Native audio open/write/stop lifecycle");
        }
        Console.WriteLine("PASS: default audio device opened, silent PCM written, stopped and reopened three times.");
    }

    private static byte[] Modulated(uint rate, double seconds)
    {
        var iq = new byte[(int)(rate * seconds) * 2];
        double phase = 0;
        for (int n = 0; n < iq.Length / 2; n++)
        {
            phase += 2 * Math.PI * 30_000 / rate * Math.Sin(2 * Math.PI * 1000 * n / rate);
            iq[2 * n] = (byte)Math.Round(127.5 + 90 * Math.Cos(phase));
            iq[2 * n + 1] = (byte)Math.Round(127.5 + 90 * Math.Sin(phase));
        }
        return iq;
    }

    private static double ToneAmplitude(float[] samples, double frequency)
    {
        double real = 0, imag = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            real += samples[i] * Math.Cos(2 * Math.PI * frequency * i / 48000);
            imag += samples[i] * Math.Sin(2 * Math.PI * frequency * i / 48000);
        }
        return 2 * Math.Sqrt(real * real + imag * imag) / samples.Length;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
