using System.Diagnostics;
using System.Reflection;
using System.Text;
using AfSdr.Audio;
using AfSdr.Dsp;

namespace AfSdr.Checks;

internal static class IqWaveChecks
{
    private static void Require(bool value, string name) { if (!value) throw new Exception("IQ WAV: " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException) { return; }
        throw new Exception("IQ WAV accepted invalid input: " + name);
    }
    private static byte[] Wave(int bits = 16, bool rf64 = false, bool extensible = false, int frames = 8192, uint rate = 250000)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.ASCII, true);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        Four(rf64 ? "RF64" : "RIFF"); w.Write(uint.MaxValue); Four("WAVE");
        if (rf64) { Four("ds64"); w.Write(28u); w.Write(0UL); w.Write((ulong)(frames * 2 * bits / 8)); w.Write((ulong)frames); w.Write(0u); }
        Four("JUNK"); w.Write(3u); w.Write(new byte[] { 4, 5, 6, 0 }); // odd padding
        Four("fmt "); w.Write(extensible ? 40u : 16u); w.Write((ushort)(extensible ? 0xfffe : bits == 32 ? 3 : 1));
        w.Write((ushort)2); w.Write(rate); w.Write(rate * (uint)(bits / 4)); w.Write((ushort)(bits / 4)); w.Write((ushort)bits);
        if (extensible) { w.Write((ushort)22); w.Write((ushort)bits); w.Write(0u); w.Write(new Guid(bits == 32 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray()); }
        Four("data"); w.Write(rf64 ? uint.MaxValue : (uint)(frames * bits / 4));
        for (int n = 0; n < frames; n++)
            foreach (double v in new[] { .25 * Math.Cos(2 * Math.PI * 317 * n / 4096), .25 * Math.Sin(2 * Math.PI * 317 * n / 4096) })
                if (bits == 8) w.Write((byte)Math.Round(128 + v * 128)); else if (bits == 16) w.Write((short)Math.Round(v * 32768)); else w.Write((float)v);
        long length = stream.Length;
        stream.Position = rf64 ? 20 : 4;
        if (rf64) w.Write((ulong)(length - 8)); else w.Write((uint)(length - 8));
        return stream.ToArray();
    }

    internal static void Run(string? original)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AF-SDR-IQ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "13-46-26_283487500Hz.wav");
        try
        {
            foreach (int bits in new[] { 8, 16, 32 }) foreach (bool rf in new[] { false, true }) foreach (bool ext in new[] { false, true })
            {
                File.WriteAllBytes(path, Wave(bits, rf, ext));
                using var r = new IqWaveReader(path);
                Require(r.Info.Frames == 8192 && r.Info.Rate == 250000 && r.Info.FilenameFrequency == 283487500, "format/length/frequency");
                float[] iq = r.Read(4096); Require(iq[0] == .25f && iq[1] == 0, "normalization");
                float[] spectrum = new SpectrumProcessor().Process(iq);
                Require(Array.IndexOf(spectrum, spectrum.Max()) == 2048 + 317, "positive tone direction");
                Require(Math.Abs(spectrum.Max() - 20 * Math.Log10(.25)) < .1, "tone precision");
                r.Seek(0); spectrum = new SpectrumProcessor().Process(r.Read(4096, true));
                Require(Array.IndexOf(spectrum, spectrum.Max()) == 2048 - 317, "swap reverses frequency");
                double phase = 0; var translated = FileReceiver.Translate(iq, 317 * 250000.0 / 4096, 250000, ref phase);
                Require(translated.Where((_, n) => n % 2 == 1).Max(Math.Abs) < .006, "NCO sign and precision");
                r.Seek(r.Info.Frames - 1); Require(r.Read(4096).Length == 2 && r.Read(4096).Length == 0, "last frame/EOF");
            }
            Require(IqWaveReader.FrequencyFromName("SDRSharp_20231017_134626Z_283487500Hz_IQ_000.wav") == 283487500, "SDRSharp name");
            Require(IqWaveReader.FrequencyFromName("x_283.4875MHz.wav") == 283487500, "SI name");
            Require(IqWaveReader.FrequencyFromName("100Hz_200Hz.wav") is null && IqWaveReader.FrequencyFromName("unknown.wav") is null, "ambiguous/missing metadata");
            var good = Wave(rf64: true);
            using (var small = new IqWaveReader(new MemoryStream(good), "test.wav"))
            {
                long start = small.Info.DataOffset, size = 0x100000004L;
                byte[] header = good[..(int)start];
                BitConverter.GetBytes((ulong)(start + size - 8)).CopyTo(header, 20);
                BitConverter.GetBytes((ulong)size).CopyTo(header, 28);
                BitConverter.GetBytes((ulong)(size / 4)).CopyTo(header, 36);
                using var large = new IqWaveReader(new SparseStream(header, start + size), "large.wav");
                large.Seek(large.Info.Frames - 1);
                Require(large.Read(10).Length == 2 && large.Position == size / 4, "RF64 >4 GiB seek/read without allocation");
            }
            foreach (int cut in new[] { 0, 11, 19, 47, good.Length - 1 })
            { File.WriteAllBytes(path, good[..cut]); Reject(() => { using var r = new IqWaveReader(path); }, "truncated " + cut); }
            var badCount = (byte[])good.Clone(); BitConverter.GetBytes(7UL).CopyTo(badCount, 36);
            File.WriteAllBytes(path, badCount); Reject(() => { using var r = new IqWaveReader(path); }, "ds64 sample count");
            var floatWave = Wave(32); File.WriteAllBytes(path, floatWave);
            long offset; using (var r = new IqWaveReader(path)) offset = r.Info.DataOffset;
            BitConverter.GetBytes(float.NaN).CopyTo(floatWave, offset);
            File.WriteAllBytes(path, floatWave); Reject(() => { using var r = new IqWaveReader(path); r.Read(1); }, "NaN");
            BitConverter.GetBytes(1.25f).CopyTo(floatWave, offset);
            File.WriteAllBytes(path, floatWave); using (var r = new IqWaveReader(path)) Require(r.Read(1)[0] == 1.25f, "finite float >1 not clipped");
            File.WriteAllBytes(path, Wave(rate: 192000)); using (var r = new IqWaveReader(path)) Require(!r.Info.Playable, "unsupported rate can be inspected");
            // Continuous file rates do not inherit the RTL-SDR rate gap.
            new ReceiveSettings(500000).Validate(true); _ = new FmDemodulator(500000);
            File.WriteAllBytes(path, Wave(frames: 150000));
            Task.Run(() => PlaybackAsync(path)).GetAwaiter().GetResult();
            if (original is not null) VerifyOriginal(original);
            Ui(path);
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: RIFF/RF64 PCM8/16/float32/extensible, precision, IQ direction/NCO, bounds, playback/pause/seek/loop/EOF, file UI. No RTL hardware opened.");
    }

    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { if (clock.Elapsed.TotalSeconds > 8) throw new Exception("File playback timeout"); await Task.Delay(5); }
    }
    private static async Task PlaybackAsync(string path)
    {
        var audio = new FakeAudio();
        var settings = new ReceiveSettings(250000, FmEnabled: true, Digital: new(true));
        var file = new FileReceiver(path, 283487500, settings, createAudio: _ => audio);
        try
        {
            await Task.Delay(40); Require(file.Position == 0 && audio.Samples == 0 && file.Paused, "manual start only");
            await file.SetPausedAsync(false); await Until(() => file.Position >= 25000);
            await file.SetPausedAsync(true); long position = file.Position; var spec = file.Spectrum; int revision = file.DisplayRevision;
            await Task.Delay(70); Require(file.Position == position && audio.Paused, "pause keeps position/output");
            await file.UpdateAsync(283497500, settings);
            Require(file.Position == position && ReferenceEquals(spec, file.Spectrum) && file.DisplayRevision == revision && audio.Flushes > 0, "tune preserves original spectrum and flushes audio");
            bool rejected = false; try { await file.UpdateAsync(283587500, settings); } catch (ArgumentException) { rejected = true; }
            Require(rejected && file.Frequency == 283497500 && file.Failure is null, "out-of-band tune harmless rejection");
            await file.SeekAsync(100000); Require(file.Position == 100000 && file.Spectrum is null && file.Constellation is null, "seek clears all old DSP");
            await file.SetPausedAsync(false); await Until(() => file.Ended);
            Require(file.Position == 150000 && file.Failure is null && file.AudioFailure is null && file.DigitalFailure is null && audio.Drains > 0, "EOF drains normally");
            file.Loop = true; await file.SeekAsync(147500); revision = file.DisplayRevision;
            await file.SetPausedAsync(false); await Until(() => file.DisplayRevision > revision);
            await file.SetPausedAsync(true); Require(!file.Ended, "loop resets and continues");
            await file.SeekAsync(0, true); Require(file.Position == 0 && file.Paused, "stop rewinds");
            await file.ReconfigureAsync(100000000, true); Require(file.RecordCenter == 100000000 && file.Frequency == 100000000, "metadata correction recenters selection");
            file.Loop = false; audio.Samples = 0;
            var clock = Stopwatch.StartNew(); await file.SetPausedAsync(false); await Until(() => file.Ended);
            Require(audio.Samples == 28800, "no IQ drops: 0.6 sec gives exactly 28800 PCM samples");
            Require(clock.Elapsed.TotalSeconds is > .5 and < 3, "sample-clock paced playback");
        }
        finally { await file.StopAsync(); }
    }

    private static void VerifyOriginal(string path)
    {
        using var reader = new IqWaveReader(path);
        Require(reader.Info.DataOffset == 80 && reader.Info.Frames == 3579904 && reader.Info.Rate == 250000 && reader.Info.Seconds == 14.319616, "original RF64 geometry");
        var iq = reader.Read(4096); Require(iq[0] == 495 / 32768f && iq[1] == -1323 / 32768f, "original first I/Q exact");
        long samples = iq.Length / 2;
        while ((iq = reader.Read(32768)).Length > 0) samples += iq.Length / 2;
        Require(samples == 3579904, "original fully readable");
        Task.Run(async () =>
        {
            var audio = new FakeAudio();
            var file = new FileReceiver(path, 283487500, new ReceiveSettings(250000, FmEnabled: true), createAudio: _ => audio);
            try
            {
                var clock = Stopwatch.StartNew(); await file.SetPausedAsync(false);
                while (!file.Ended && file.Failure is null && clock.Elapsed.TotalSeconds < 25) await Task.Delay(20);
                Require(file.Ended && file.Failure is null && file.AudioFailure is null && file.Spectrum is not null, "original through realtime FFT/FM path");
                Require(audio.Samples == (long)Math.Floor(3579904 * 48000.0 / 250000), "original FM processes every IQ sample");
                Console.WriteLine($"Original realtime playback: {clock.Elapsed.TotalSeconds:F3} s, {audio.Samples} PCM samples (fake audio sink)");
            }
            finally { await file.StopAsync(); }
        }).GetAwaiter().GetResult();
        Console.WriteLine("PASS original SDR# recording: " + reader.Info);
    }

    private static void Ui(string path)
    {
        using var form = new MainForm(Path.Combine(Path.GetDirectoryName(path)!, "settings.json"));
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        void Invoke(string name)
        {
            var task = (Task)typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); if (clock.Elapsed.TotalSeconds > 10) throw new Exception("UI timeout " + name); }
            task.GetAwaiter().GetResult();
        }
        Field<ComboBox>("inputSource").SelectedIndex = 1;
        Field<TextBox>("filePath").Text = path;
        Invoke("LoadIqFileAsync");
        Require(Field<object?>("receiver") is null && Field<TextBox>("recordFrequency").Text == "283.4875M", "load only, no playback");
        Require(!Field<ComboBox>("sampleRate").Enabled && !Field<Button>("connect").Enabled, "file hardware controls disabled");
        void Handles(Control c) { _ = c.Handle; foreach (Control child in c.Controls) Handles(child); c.PerformLayout(); }
        Handles(form);
        Invoke("EnsureFileReceiverAsync");
        var file = Field<FileReceiver>("receiver");
        Task.Run(async () => { await file.SetPausedAsync(false); await Task.Delay(180); await file.SetPausedAsync(true); }).GetAwaiter().GetResult();
        typeof(MainForm).GetMethod("UpdateFileDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [file]);
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(Environment.CurrentDirectory, "iq-file-check.png"));
        var saved = form.CaptureSettings(); Require(saved.FileInput && saved.LastIqPath == path, "file settings persist");
        Invoke("StopReceiverAsync");
    }

    internal static void SilentAudio()
    {
        Task.Run(async () =>
        {
            using var output = new WaveAudioOutput(0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await output.Ready;
            await output.WriteReliableAsync(new float[9600], timeout.Token);
            await output.SetPausedAsync(true);
            await output.FlushAsync();
            await output.SetPausedAsync(false);
            await output.WriteReliableAsync(new float[1000], timeout.Token);
            await output.DrainAsync(timeout.Token);
            Require(output.Failure is null, "native waveOut pause/flush/restart/drain");
        }).GetAwaiter().GetResult();
        Console.WriteLine("PASS: silent default Windows audio pause/reset/restart and short EOF drain.");
    }

    private sealed class SparseStream(byte[] header, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - Position); buffer[..count].Clear();
            if (Position < header.Length) header.AsSpan((int)Position, Math.Min(count, header.Length - (int)Position)).CopyTo(buffer);
            Position += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeAudio : IAudioOutput
    {
        internal long Samples; internal int Flushes, Drains; internal bool Paused;
        public Exception? Failure => null;
        public float Volume { set { } }
        public Task Ready => Task.CompletedTask;
        public void Write(float[] samples) => Samples += samples.Length;
        public void Clear() { }
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
        public Task SetPausedAsync(bool paused) { Paused = paused; return Task.CompletedTask; }
        public Task DrainAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
