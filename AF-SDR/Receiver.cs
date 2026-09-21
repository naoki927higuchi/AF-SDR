using System.Runtime.InteropServices;
using System.Threading.Channels;
using AfSdr.Dsp;
using AfSdr.Native;
using AfSdr.Audio;

namespace AfSdr;

internal sealed class Receiver : IReceiver
{
    public const uint RequestedRate = 2_048_000;
    private sealed record State(uint Frequency, uint Rate, ReceiveSettings Settings, int SpectrumRevision, int AudioRevision, int DigitalRevision);
    private sealed record Block(long StartByte, float[] Data, State State);
    private sealed record SpectrumResult(float[] Values, int Revision);
    private readonly IRtlDevice device;
    private readonly Func<float, IAudioOutput> createAudio;
    private readonly SemaphoreSlim operations = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Block> blocks = Channel.CreateBounded<Block>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<Block> audioBlocks = Channel.CreateBounded<Block>(new BoundedChannelOptions(4)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<Block> digitalBlocks = Channel.CreateBounded<Block>(new BoundedChannelOptions(8)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private sealed record DigitalResult(ConstellationFrame Frame, int Revision);
    private DigitalResult? digitalResult;
    private Exception? digitalFailure;
    private Task digitalProcessing = Task.CompletedTask;
    public ConstellationFrame? Constellation => Volatile.Read(ref digitalResult) is { } result && result.Revision == Volatile.Read(ref state)?.DigitalRevision ? result.Frame : null;
    public Exception? DigitalFailure => Volatile.Read(ref digitalFailure);
    private State? state;
    private SpectrumResult? spectrum;
    private Task reading = Task.CompletedTask, processing = Task.CompletedTask, audioProcessing = Task.CompletedTask;
    private IAudioOutput? audioOutput;
    private Exception? failure, audioFailure;
    private float volume = 0.3f;
    private int streamStopping;
    private long receivedBytes;
    private bool opened;
    public uint Frequency => Volatile.Read(ref state)?.Frequency ?? 0;
    public uint SampleRate => Volatile.Read(ref state)?.Rate ?? 0;
    public int[] SupportedGains { get; private set; } = [];
    public int? AppliedGain { get; private set; }
    public float[]? Spectrum
    {
        get
        {
            var current = Volatile.Read(ref state);
            var result = Volatile.Read(ref spectrum);
            return result?.Revision == current?.SpectrumRevision ? result?.Values : null;
        }
    }
    public Exception? Failure => Volatile.Read(ref failure);
    public Exception? AudioFailure => Volatile.Read(ref audioFailure);
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    internal int SpectrumRevision => Volatile.Read(ref state)?.SpectrumRevision ?? 0;
    internal int DigitalRevision => Volatile.Read(ref state)?.DigitalRevision ?? 0;
    internal int AudioRevision => Volatile.Read(ref state)?.AudioRevision ?? 0;
    public float Volume
    {
        set
        {
            Volatile.Write(ref volume, value);
            var output = Volatile.Read(ref audioOutput);
            if (output is not null) output.Volume = value;
        }
    }
    internal Receiver(IRtlDevice? device = null, Func<float, IAudioOutput>? createAudio = null)
    {
        this.device = device ?? new RtlDevice();
        this.createAudio = createAudio ?? (volume => new WaveAudioOutput(volume));
    }
    public static string[] ListDevices()
    {
        uint count = RtlSdrNative.rtlsdr_get_device_count();
        return Enumerable.Range(0, checked((int)count)).Select(i => $"{i}: {Marshal.PtrToStringAnsi(RtlSdrNative.rtlsdr_get_device_name((uint)i))}").ToArray();
    }

    public async Task StartAsync(uint index, uint frequency, ReceiveSettings settings)
    {
        settings.Validate();
        await operations.WaitAsync();
        try
        {
            if (opened || stop.IsCancellationRequested) throw new InvalidOperationException("受信セッションは再利用できません。");
            await Task.Run(() =>
            {
                device.Open(index); opened = true;
                var actual = device.Configure(frequency, settings);
                SupportedGains = actual.Gains; AppliedGain = actual.Gain;
                Volatile.Write(ref state, new State(actual.Frequency, actual.Rate, settings with { ManualGain = actual.Gain }, 1, 1, 1));
            });
            processing = Task.Run(ProcessSpectrumAsync);
            audioProcessing = Task.Run(ProcessAudioAsync);
            digitalProcessing = Task.Run(ProcessDigitalAsync);
            StartStream();
        }
        catch { device.Close(); opened = false; throw; }
        finally { operations.Release(); }
    }

    public async Task<SettingsChange> UpdateAsync(uint frequency, ReceiveSettings settings)
    {
        settings.Validate();
        await operations.WaitAsync();
        try
        {
            var previous = state ?? throw new InvalidOperationException("未接続です。");
            if (!opened || stop.IsCancellationRequested) throw new InvalidOperationException("受信は終了しています。");
            var change = SettingsChange.Between(previous.Frequency, previous.Settings, frequency, settings);
            uint actualFrequency = previous.Frequency, actualRate = previous.Rate;
            if (change.Audio)
            {
                var output = Volatile.Read(ref audioOutput);
                if (output is not null) { output.Volume = 0; output.Clear(); }
            }
            if (change.Hardware)
            {
                await StopStreamAsync();
                var actual = await Task.Run(() => device.Configure(frequency, settings));
                actualFrequency = actual.Frequency; actualRate = actual.Rate;
                SupportedGains = actual.Gains; AppliedGain = actual.Gain;
            }
            var next = new State(actualFrequency, actualRate, settings,
                previous.SpectrumRevision + (change.Spectrum ? 1 : 0), previous.AudioRevision + (change.Audio ? 1 : 0), previous.DigitalRevision + (change.Digital ? 1 : 0));
            Volatile.Write(ref state, next);
            if (change.Audio)
            {
                Volatile.Write(ref audioFailure, null);
                audioBlocks.Writer.TryWrite(new Block(ReceivedBytes, [], next));
            }
            if (change.Digital)
            {
                Volatile.Write(ref digitalFailure, null);
                digitalBlocks.Writer.TryWrite(new Block(ReceivedBytes, [], next));
            }
            if (change.Hardware) StartStream();
            return change;
        }
        catch (Exception ex) { Volatile.Write(ref failure, ex); throw; }
        finally { operations.Release(); }
    }

    private async Task ProcessSpectrumAsync()
    {
        SpectrumProcessor? dsp = null;
        int revision = -1;
        try
        {
            await foreach (var block in blocks.Reader.ReadAllAsync(stop.Token))
            {
                if (block.State.SpectrumRevision != Volatile.Read(ref state)!.SpectrumRevision) continue;
                if (revision != block.State.SpectrumRevision)
                {
                    dsp = new SpectrumProcessor(block.State.Settings.FftSize, block.State.Settings.Window);
                    revision = block.State.SpectrumRevision;
                }
                for (int offset = 0; offset + dsp!.Size * 2 <= block.Data.Length; offset += dsp.Size * 2)
                    Volatile.Write(ref spectrum, new SpectrumResult(dsp.Process(block.Data.AsSpan(offset, dsp.Size * 2)), revision));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref failure, ex); }
    }

    private async Task ProcessAudioAsync()
    {
        IAudioOutput? output = null;
        FmDemodulator? demodulator = null;
        int revision = -1, failedRevision = -1;
        long expected = 0;
        try
        {
            await foreach (var block in audioBlocks.Reader.ReadAllAsync(stop.Token))
            {
                var current = Volatile.Read(ref state)!;
                if (block.State.AudioRevision != current.AudioRevision || failedRevision == current.AudioRevision) continue;
                try
                {
                    if (!current.Settings.FmEnabled)
                    {
                        Volatile.Write(ref audioOutput, null);
                        output?.Dispose(); output = null; demodulator = null;
                        revision = current.AudioRevision;
                        continue;
                    }
                    if (output?.Failure is { } error) throw error;
                    if (revision != current.AudioRevision || block.StartByte != expected)
                    {
                        demodulator = new FmDemodulator(current.Rate, current.Settings.RxBandwidth);
                        output?.Clear();
                        revision = current.AudioRevision;
                    }
                    if (output is null)
                    {
                        output = createAudio(Volatile.Read(ref volume));
                        Volatile.Write(ref audioOutput, output);
                        await output.Ready;
                    }
                    expected = block.StartByte + block.Data.Length;
                    if (block.Data.Length == 0) continue;
                    var samples = demodulator!.Process(block.Data);
                    if (revision == Volatile.Read(ref state)!.AudioRevision)
                    {
                        output.Volume = Volatile.Read(ref volume);
                        output.Write(samples);
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref audioFailure, ex);
                    Volatile.Write(ref audioOutput, null);
                    output?.Dispose(); output = null; demodulator = null;
                    failedRevision = current.AudioRevision;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Volatile.Write(ref audioOutput, null); output?.Dispose(); }
    }

    private async Task ProcessDigitalAsync()
    {
        ConstellationProcessor? dsp = null;
        int revision = -1, failedRevision = -1, discontinuities = 0;
        long expected = 0, lastPublished = 0;
        try
        {
            await foreach (var block in digitalBlocks.Reader.ReadAllAsync(stop.Token))
            {
                if (block.State.DigitalRevision != Volatile.Read(ref state)!.DigitalRevision || block.State.DigitalRevision == failedRevision) continue;
                if (!block.State.Settings.DigitalOptions.Enabled) { dsp = null; continue; }
                try
                {
                    if (revision != block.State.DigitalRevision || expected != block.StartByte)
                    {
                        if (revision == block.State.DigitalRevision) discontinuities++; else discontinuities = 0;
                        dsp = new ConstellationProcessor(block.State.Rate, block.State.Settings.DigitalOptions);
                        revision = block.State.DigitalRevision;
                        Volatile.Write(ref digitalResult, null);
                    }
                    expected = block.StartByte + block.Data.Length;
                    dsp!.SetFineFrequencyOffset(Volatile.Read(ref state)!.Settings.DigitalOptions.FineFrequencyOffset);
                    dsp.Process(block.Data);
                    long now = Environment.TickCount64;
                    if (now - lastPublished >= 40)
                    {
                        Volatile.Write(ref digitalResult, new DigitalResult(dsp.Snapshot(discontinuities), revision));
                        lastPublished = now;
                    }
                }
                catch (Exception ex)
                {
                    failedRevision = block.State.DigitalRevision;
                    dsp = null;
                    Volatile.Write(ref digitalResult, null);
                    Volatile.Write(ref digitalFailure, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void StartStream()
    {
        Volatile.Write(ref streamStopping, 0);
        reading = Task.Run(() =>
        {
            RtlSdrNative.ReadCallback callback = (pointer, length, _) =>
            {
                try
                {
                    if (Volatile.Read(ref streamStopping) != 0 || stop.IsCancellationRequested) return;
                    var current = Volatile.Read(ref state)!;
                    var data = new byte[checked((int)length)];
                    Marshal.Copy(pointer, data, 0, data.Length);
                    long end = Interlocked.Add(ref receivedBytes, data.Length);
                    var block = new Block(end - data.Length, IqSamples.FromRtl(data), current);
                    blocks.Writer.TryWrite(block);
                    audioBlocks.Writer.TryWrite(block);
                    if (current.Settings.DigitalOptions.Enabled) digitalBlocks.Writer.TryWrite(block);
                }
                catch (Exception ex) { Volatile.Write(ref failure, ex); device.Cancel(); }
            };
            try
            {
                if (Volatile.Read(ref streamStopping) != 0) return;
                int result = device.Read(callback);
                if (Volatile.Read(ref streamStopping) == 0 && !stop.IsCancellationRequested)
                    throw new IOException($"受信が終了しました (RTL-SDR: {result})。再接続してください。");
            }
            catch (Exception ex) { Volatile.Write(ref failure, ex); }
            finally { GC.KeepAlive(callback); }
        });
    }

    private async Task StopStreamAsync()
    {
        Volatile.Write(ref streamStopping, 1);
        while (!reading.IsCompleted)
        {
            device.Cancel();
            await Task.WhenAny(reading, Task.Delay(25));
        }
        await reading;
    }

    public async Task StopAsync()
    {
        await operations.WaitAsync();
        try
        {
            stop.Cancel(); Volume = 0;
            await StopStreamAsync();
            blocks.Writer.TryComplete(); audioBlocks.Writer.TryComplete(); digitalBlocks.Writer.TryComplete();
            await Task.WhenAll(processing, audioProcessing, digitalProcessing);
            if (opened) { device.Close(); opened = false; }
        }
        finally { operations.Release(); }
    }
}
