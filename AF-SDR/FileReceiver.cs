using System.Diagnostics;
using AfSdr.Audio;
using AfSdr.Dsp;

namespace AfSdr;

internal sealed class FileReceiver : IReceiver
{
    private readonly IqWaveReader reader;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Func<float, IAudioOutput> createAudio;
    private readonly Task worker;
    private ReceiveSettings settings;
    private SpectrumProcessor fft;
    private FmDemodulator? fm;
    private ConstellationProcessor? digital;
    private IAudioOutput? output;
    private float[] fftInput;
    private int fftCount;
    private double phase;
    private float volume = .3f;
    private volatile bool paused = true, ended, loop, swap, buffering;
    private long position;
    private uint frequency, recordCenter;
    private int displayRevision;
    private float[]? spectrum;
    private ConstellationFrame? constellation;
    private Exception? failure, audioFailure, digitalFailure;
    public uint Frequency => frequency;
    public uint SampleRate => reader.Info.Rate;
    public int[] SupportedGains => [];
    public int? AppliedGain => null;
    public float[]? Spectrum => Volatile.Read(ref spectrum);
    public ConstellationFrame? Constellation => Volatile.Read(ref constellation);
    public Exception? Failure => Volatile.Read(ref failure);
    public Exception? AudioFailure => Volatile.Read(ref audioFailure);
    public Exception? DigitalFailure => Volatile.Read(ref digitalFailure);
    public long ReceivedBytes => Position * reader.Info.BlockAlign;
    public float Volume { set { volume = value; if (output is { } audio) audio.Volume = value; } }
    internal ReceiveSettings Settings => settings;
    internal IqWaveInfo Info => reader.Info;
    internal uint RecordCenter => recordCenter;
    internal long Position => Interlocked.Read(ref position);
    internal int DisplayRevision => Volatile.Read(ref displayRevision);
    internal bool Paused => paused;
    internal bool Ended => ended;
    internal bool Buffering => buffering;
    internal bool Loop { get => loop; set => loop = value; }

    internal FileReceiver(string path, uint center, ReceiveSettings settings, bool swap = false, Func<float, IAudioOutput>? createAudio = null)
    {
        reader = new IqWaveReader(path);
        try
        {
            this.settings = settings with { SampleRate = reader.Info.Rate, ManualGain = null };
            this.settings.Validate(true);
            frequency = recordCenter = center; this.swap = swap;
            ValidateTune(center, this.settings);
            this.createAudio = createAudio ?? (v => new WaveAudioOutput(v));
            fft = new SpectrumProcessor(settings.FftSize, settings.Window);
            fftInput = new float[settings.FftSize * 2];
            worker = Task.Run(RunAsync);
        }
        catch { reader.Dispose(); throw; }
    }

    private void ValidateTune(uint hz, ReceiveSettings next)
    {
        if (next.DigitalOptions.Enabled && Math.Abs((double)hz - recordCenter + next.DigitalOptions.FineFrequencyOffset)
            + next.DigitalOptions.ChannelCutoff > SampleRate / 2.0)
            throw new ArgumentException("手動周波数補正後のデジタル受信帯域が記録帯域を超えています。");
        double half = next.FmEnabled ? next.RxBandwidth / 2.0 : 0;
        if (hz == 0 || Math.Abs((double)hz - recordCenter) + half > SampleRate / 2.0 || (double)hz - recordCenter >= SampleRate / 2.0)
            throw new ArgumentException("選局周波数と復調帯域が記録帯域を超えています。周波数またはRxBWを調整してください。");
    }

    public async Task<SettingsChange> UpdateAsync(uint hz, ReceiveSettings next)
    {
        next = next with { SampleRate = SampleRate, ManualGain = null };
        next.Validate(true);
        await gate.WaitAsync();
        try
        {
            ValidateTune(hz, next);
            bool tune = hz != frequency;
            var change = new SettingsChange(false, next.FftSize != settings.FftSize || next.Window != settings.Window,
                tune || next.FmEnabled != settings.FmEnabled || next.RxBandwidth != settings.RxBandwidth,
                tune || settings.DigitalOptions.RequiresReset(next.DigitalOptions));
            settings = next; frequency = hz;
            if (tune) phase = 0;
            if (change.Spectrum) ResetSpectrum();
            if (change.Audio) { fm = null; audioFailure = null; if (output is not null) await output.FlushAsync(); }
            if (change.Digital) { digital = null; digitalFailure = null; Volatile.Write(ref constellation, null); }
            if (!next.FmEnabled && output is not null) { output.Dispose(); output = null; }
            return change;
        }
        finally { gate.Release(); }
    }

    internal async Task SetPausedAsync(bool value)
    {
        await gate.WaitAsync();
        try
        {
            if (!value && ended) await ResetAtAsync(0);
            if (output is not null) await output.SetPausedAsync(value);
            paused = value;
        }
        finally { gate.Release(); }
    }

    internal async Task SeekAsync(long frame, bool stopPlayback = false)
    {
        await gate.WaitAsync();
        try { if (stopPlayback) paused = true; await ResetAtAsync(frame); }
        finally { gate.Release(); }
    }

    internal async Task ReconfigureAsync(uint center, bool iqSwap)
    {
        if (center == 0) throw new ArgumentOutOfRangeException(nameof(center));
        await gate.WaitAsync();
        try { recordCenter = frequency = center; swap = iqSwap; await ResetAtAsync(Position); }
        finally { gate.Release(); }
    }

    private void ResetSpectrum()
    {
        fft = new SpectrumProcessor(settings.FftSize, settings.Window);
        fftInput = new float[fft.Size * 2]; fftCount = 0;
        Volatile.Write(ref spectrum, null); Interlocked.Increment(ref displayRevision);
    }

    private async Task ResetAtAsync(long frame)
    {
        reader.Seek(frame); Interlocked.Exchange(ref position, frame);
        phase = 0; fm = null; digital = null; audioFailure = digitalFailure = null;
        Volatile.Write(ref constellation, null); ResetSpectrum(); ended = false;
        if (output is not null) { await output.FlushAsync(); await output.SetPausedAsync(paused); }
    }

    // Sequential file DSP supplies every sample to active demodulators. Only FFT display is decimated.
    private async Task RunAsync()
    {
        double deadline = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        long lastDisplay = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (paused) { await Task.Delay(10, stop.Token); deadline = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; continue; }
                int frames = 0;
                await gate.WaitAsync(stop.Token);
                try
                {
                    if (paused) continue;
                    var data = reader.Read((int)Math.Clamp(SampleRate / 100, 1, 32768), swap);
                    frames = data.Length / 2;
                    if (frames == 0)
                    {
                        if (loop) await ResetAtAsync(0);
                        else
                        {
                            if (output is not null) { await output.DrainAsync(stop.Token); await output.SetPausedAsync(true); }
                            ended = paused = true;
                        }
                        continue;
                    }
                    // Keep the newest full FFT window, independently of input read boundaries.
                    if (data.Length >= fftInput.Length) { Array.Copy(data, data.Length - fftInput.Length, fftInput, 0, fftInput.Length); fftCount = fftInput.Length; }
                    else
                    {
                        int keep = Math.Min(fftCount, fftInput.Length - data.Length);
                        Array.Copy(fftInput, fftCount - keep, fftInput, 0, keep);
                        Array.Copy(data, 0, fftInput, keep, data.Length); fftCount = keep + data.Length;
                    }
                    long now = Environment.TickCount64;
                    bool publish = now - lastDisplay >= 40;
                    if (publish && fftCount == fftInput.Length) Volatile.Write(ref spectrum, fft.Process(fftInput));
                    if (settings.FmEnabled || settings.DigitalOptions.Enabled)
                    {
                        var tuned = Translate(data, (double)frequency - recordCenter, SampleRate, ref phase);
                        if (settings.FmEnabled && audioFailure is null)
                        {
                            try
                            {
                                fm ??= new FmDemodulator(SampleRate, settings.RxBandwidth);
                                output ??= createAudio(volume); await output.Ready;
                                output.Volume = volume;
                                await output.WriteReliableAsync(fm.Process(tuned), stop.Token);
                            }
                            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                            catch (Exception ex) { audioFailure = ex; output?.Dispose(); output = null; }
                        }
                        if (settings.DigitalOptions.Enabled && digitalFailure is null)
                        {
                            try { digital ??= new ConstellationProcessor(SampleRate, settings.DigitalOptions); digital.SetFineFrequencyOffset(settings.DigitalOptions.FineFrequencyOffset); digital.Process(tuned); if (publish) Volatile.Write(ref constellation, digital.Snapshot(0)); }
                            catch (Exception ex) { digitalFailure = ex; }
                        }
                    }
                    if (publish) lastDisplay = now;
                    Interlocked.Exchange(ref position, reader.Position);
                }
                finally { gate.Release(); }
                double current = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                deadline += frames / (double)SampleRate;
                buffering = current - deadline > .05;
                if (buffering) deadline = current; // rebase after stalls; absorb small scheduler jitter without cumulative drift
                double wait = deadline - current;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref failure, ex); paused = true; }
    }

    internal static float[] Translate(float[] data, double offset, uint rate, ref double phase)
    {
        if (offset == 0) return data;
        var result = new float[data.Length]; double step = -2 * Math.PI * offset / rate;
        for (int n = 0; n < data.Length; n += 2)
        {
            double c = Math.Cos(phase), s = Math.Sin(phase);
            result[n] = (float)(data[n] * c - data[n + 1] * s);
            result[n + 1] = (float)(data[n] * s + data[n + 1] * c);
            phase = Math.IEEERemainder(phase + step, 2 * Math.PI);
        }
        return result;
    }

    public async Task StopAsync()
    {
        stop.Cancel(); await worker;
        await gate.WaitAsync();
        try { output?.Dispose(); output = null; reader.Dispose(); }
        finally { gate.Release(); }
    }
}
