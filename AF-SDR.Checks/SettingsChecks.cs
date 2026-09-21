using System.Reflection;
using System.Runtime.InteropServices;
using AfSdr.Audio;
using AfSdr.Dsp;
using AfSdr.Native;

namespace AfSdr.Checks;

internal static class SettingsChecks
{
    internal static async Task RunAsync()
    {
        Require(FrequencyInput.Format(78_400_000) == "78.4M", "SI display");
        foreach (uint value in new uint[] { 1, 999, 1001, 999999, 78400001, 1_000_000_001, uint.MaxValue })
            Require(FrequencyInput.TryParse(FrequencyInput.Format(value), out uint parsed, out _) && parsed == value, "SI format retains every Hz");
        foreach (var window in Enum.GetValues<FftWindow>())
        {
            const int size = 4096, bin = 251;
            var iq = new byte[size * 2];
            for (int n = 0; n < size; n++)
            {
                iq[2 * n] = (byte)Math.Round(127.5 + 64 * Math.Cos(2 * Math.PI * bin * n / size));
                iq[2 * n + 1] = (byte)Math.Round(127.5 + 64 * Math.Sin(2 * Math.PI * bin * n / size));
            }
            var values = new SpectrumProcessor(size, window).Process(iq);
            Require(Array.IndexOf(values, values.Max()) == size / 2 + bin && Math.Abs(values.Max() + 6.0206) < 0.15,
                "Window frequency / coherent gain " + window);
            Require(Math.Abs(WindowFunctions.Value(window, 17, size) - WindowFunctions.Value(window, size - 18, size)) < 1e-10, "Symmetric window");
        }

        double narrow = ChannelResponse(100_000), wide = ChannelResponse(200_000);
        Require(narrow < 0.01 && wide > 0.9, "RxBW changes actual RF passband at 80 kHz offset");
        using (var history = new WaterfallHistory())
        using (var bitmap = new Bitmap(4096, 300))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            history.Add(Enumerable.Repeat(-60f, 4096).ToArray());
            history.SetLevels(-80, -40);
            history.Draw(graphics, new RectangleF(0, 0, 4096, 300));
            Require(history.Count == 1 && bitmap.GetPixel(50, 0).ToArgb() == WaterfallHistory.LevelColor(-60, -80, -40).ToArgb(), "Recolor existing history without reset");
        }

        var device = new FakeDevice();
        var audio = new FakeAudio();
        int audioOpens = 0;
        var receiver = new Receiver(device, _ => { Interlocked.Increment(ref audioOpens); return audio; });
        var settings = new ReceiveSettings(2_048_000);
        await receiver.StartAsync(0, 80_000_000, settings);
        try
        {
            await Until(() => receiver.Spectrum?.Length == 4096);
            settings = settings with { FmEnabled = true };
            int specRevision = receiver.SpectrumRevision;
            await receiver.UpdateAsync(80_000_000, settings);
            await Until(() => audio.Writes > 2);
            Require(receiver.SpectrumRevision == specRevision && device.Configures == 1, "FM ON preserves FFT / device");
            int audioRevision = receiver.AudioRevision, clears = audio.Clears;
            settings = settings with { Digital = new DigitalSettings(true) };
            await receiver.UpdateAsync(80_000_000, settings);
            await Until(() => receiver.Constellation is not null);
            Require(receiver.AudioRevision == audioRevision && receiver.SpectrumRevision == specRevision && device.Configures == 1,
                "Digital ON preserves device, FFT and audio");
            int digitalRevision = receiver.DigitalRevision;
            settings = settings with { Digital = settings.Digital! with { FineFrequencyOffset = 100.1 } };
            var fineChange = await receiver.UpdateAsync(80_000_000, settings);
            Require(fineChange == new SettingsChange(false, false, false, false) && receiver.DigitalRevision == digitalRevision
                && receiver.AudioRevision == audioRevision && receiver.SpectrumRevision == specRevision && device.Configures == 1 && audio.Clears == clears,
                "Fine tuning preserves every revision, native device and audio queue");
            settings = settings with { Digital = settings.Digital! with { SymbolRate = 9603 } };
            var baudChange = await receiver.UpdateAsync(80_000_000, settings);
            Require(baudChange == new SettingsChange(false, false, false, true) && receiver.AudioRevision == audioRevision
                && receiver.SpectrumRevision == specRevision && device.Configures == 1 && audio.Clears == clears,
                "Symbol Rate rebuilds digital DSP only, preserving audio/FFT/device");
            digitalRevision = receiver.DigitalRevision;
            settings = settings with { FftSize = 16384, Window = FftWindow.BlackmanHarris };
            await receiver.UpdateAsync(80_000_000, settings);
            await Until(() => receiver.Spectrum?.Length == 16384);
            Require(receiver.AudioRevision == audioRevision && audio.Clears == clears && audioOpens == 1 && device.Configures == 1,
                "FFT update preserves FM state / audio output / device");
            Require(receiver.DigitalRevision == digitalRevision, "FFT changes preserve digital synchronization");
            settings = settings with { Digital = new DigitalSettings(true, DigitalMode.Bpsk, 4800) };
            await receiver.UpdateAsync(80_000_000, settings);
            Require(receiver.AudioRevision == audioRevision && device.Configures == 1, "Digital mode/baud changes preserve audio and USB");
            foreach (var mode in new[] { DigitalMode.Qam, DigitalMode.Pi4Qpsk, DigitalMode.Ask, DigitalMode.Fsk, DigitalMode.Msk })
            {
                int fftRevision = receiver.SpectrumRevision;
                settings = settings with { Digital = new DigitalSettings(true, mode, QamOrder: 64, AskOrder: 4, FskOrder: 4) };
                await receiver.UpdateAsync(80_000_000, settings);
                await Until(() => receiver.Constellation is not null);
                Require(receiver.AudioRevision == audioRevision && receiver.SpectrumRevision == fftRevision && device.Configures == 1 && receiver.DigitalFailure is null,
                    "Extended modulation changes only digital DSP: " + mode);
            }
            settings = settings with { Digital = settings.Digital! with { Enabled = false } };
            await receiver.UpdateAsync(80_000_000, settings);
            Require(receiver.Constellation is null, "Digital OFF hides stale results");
            specRevision = receiver.SpectrumRevision;
            settings = settings with { RxBandwidth = 150_000 };
            await receiver.UpdateAsync(80_000_000, settings);
            await Until(() => audio.Clears > clears);
            Require(receiver.SpectrumRevision == specRevision && audioOpens == 1 && device.Configures == 1, "RxBW resets only FM state");
            settings = settings with { ManualGain = 197 };
            await receiver.UpdateAsync(80_000_000, settings);
            Require(device.Opens == 1 && device.Closes == 0 && device.Configures == 2 && receiver.AppliedGain == 197,
                "RF gain reconfigures without reopening device");
            settings = settings with { SampleRate = 1_024_000 };
            await receiver.UpdateAsync(90_000_000, settings);
            Require(receiver.Frequency == 90_000_000 && receiver.SampleRate == 1_024_000 && device.Opens == 1, "Retune/rate keeps device handle");
            specRevision = receiver.SpectrumRevision;
            settings = settings with { FmEnabled = false };
            await receiver.UpdateAsync(90_000_000, settings);
            await Until(() => audio.Disposes == 1);
            Require(receiver.SpectrumRevision == specRevision && device.Closes == 0, "FM OFF closes only audio");
            Require(receiver.Failure is null && receiver.AudioFailure is null, "Live setting transitions have no errors");
        }
        finally { await receiver.StopAsync(); }
        Require(device.Closes == 1, "Disconnect closes device once");
        Console.WriteLine("PASS: SI, FFT windows, RxBW filter, recoloring and isolated live setting updates (fake device/audio).");
    }

    private static double ChannelResponse(uint width)
    {
        var demod = new FmDemodulator(2_048_000, width);
        var filter = (ComplexFir)typeof(FmDemodulator).GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(demod)!;
        double sum = 0;
        for (int n = 0; n < 4096; n++)
        {
            filter.Push((float)Math.Cos(2 * Math.PI * 80000 * n / 256000), (float)Math.Sin(2 * Math.PI * 80000 * n / 256000), out float i, out float q);
            if (n >= 2048) sum += Math.Sqrt(i * i + q * q);
        }
        return sum / 2048;
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (int n = 0; n < 500; n++) { if (predicate()) return; await Task.Delay(10); }
        throw new Exception("Timed out waiting for setting change.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }

    private sealed class FakeAudio : IAudioOutput
    {
        internal int Writes, Clears, Disposes;
        public Exception? Failure => null;
        public float Volume { set { } }
        public Task Ready => Task.CompletedTask;
        public void Write(float[] samples) => Interlocked.Increment(ref Writes);
        public void Clear() => Interlocked.Increment(ref Clears);
        public void Dispose() => Interlocked.Increment(ref Disposes);
    }
    private sealed class FakeDevice : IRtlDevice
    {
        internal int Opens, Closes, Configures;
        private int reading;
        private readonly ManualResetEventSlim cancel = new();
        public void Open(uint index) => Opens++;
        public void Close() { Require(reading == 0, "No close while streaming"); Closes++; }
        public (uint Frequency, uint Rate, int[] Gains, int? Gain) Configure(uint frequency, ReceiveSettings settings)
        {
            Require(reading == 0, "No device mutation while streaming");
            Configures++;
            return (frequency, settings.SampleRate, [0, 197, 496], settings.ManualGain);
        }
        public int Read(RtlSdrNative.ReadCallback callback)
        {
            cancel.Reset(); Interlocked.Exchange(ref reading, 1);
            IntPtr pointer = Marshal.AllocHGlobal(32768);
            Marshal.Copy(Enumerable.Repeat((byte)128, 32768).ToArray(), 0, pointer, 32768);
            try { while (!cancel.Wait(10)) callback(pointer, 32768, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(pointer); Interlocked.Exchange(ref reading, 0); }
            return 0;
        }
        public void Cancel() => cancel.Set();
    }
}
