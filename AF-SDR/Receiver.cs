using System.Runtime.InteropServices;
using System.Threading.Channels;
using AfSdr.Dsp;
using AfSdr.Native;
using AfSdr.Audio;

namespace AfSdr;

internal sealed class Receiver
{
    public const uint RequestedRate = 2_048_000;
    private readonly object handleLock = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> blocks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<AudioBlock> audioBlocks = Channel.CreateBounded<AudioBlock>(new BoundedChannelOptions(4)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly record struct AudioBlock(long StartByte, byte[] Data);
    private Task audioProcessing = Task.CompletedTask;
    private WaveAudioOutput? audioOutput;
    private Exception? audioFailure;
    private float volume = 0.3f;
    public Exception? AudioFailure => Volatile.Read(ref audioFailure);
    public float Volume
    {
        set
        {
            Volatile.Write(ref volume, value);
            var output = Volatile.Read(ref audioOutput);
            if (output is not null) output.Volume = value;
        }
    }
    private IntPtr handle;
    private Task reading = Task.CompletedTask, processing = Task.CompletedTask;
    private float[]? spectrum;
    private Exception? failure;
    private long receivedBytes;
    public uint Frequency { get; private set; }
    public uint SampleRate { get; private set; }
    public int[] SupportedGains { get; private set; } = [];
    public int? AppliedGain { get; private set; }
    public float[]? Spectrum => Volatile.Read(ref spectrum);
    public Exception? Failure => Volatile.Read(ref failure);
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);

    public static string[] ListDevices()
    {
        uint count = RtlSdrNative.rtlsdr_get_device_count();
        return Enumerable.Range(0, checked((int)count)).Select(i => $"{i}: {Marshal.PtrToStringAnsi(RtlSdrNative.rtlsdr_get_device_name((uint)i))}").ToArray();
    }

    public Task StartAsync(uint index, uint frequency, ReceiveSettings settings)
    {
        settings.Validate();
        if (settings.FmEnabled) audioProcessing = Task.Run(ProcessAudioAsync);
        processing = Task.Run(async () =>
        {
            var dsp = new SpectrumProcessor(settings.FftSize);
            await foreach (byte[] block in blocks.Reader.ReadAllAsync())
            {
                // Analyze all complete FFT blocks; bounded channel prevents latency buildup.
                for (int offset = 0; offset + dsp.Size * 2 <= block.Length; offset += dsp.Size * 2)
                    Volatile.Write(ref spectrum, dsp.Process(block.AsSpan(offset, dsp.Size * 2)));
            }
        });
        reading = Task.Run(() => Read(index, frequency, settings));
        return ready.Task;
    }

    private async Task ProcessAudioAsync()
    {
        WaveAudioOutput? output = null;
        try
        {
            await ready.Task;
            stop.Token.ThrowIfCancellationRequested();
            var demodulator = new FmDemodulator(SampleRate);
            output = new WaveAudioOutput(Volatile.Read(ref volume));
            Volatile.Write(ref audioOutput, output);
            await output.Ready;
            long expected = 0;
            await foreach (var block in audioBlocks.Reader.ReadAllAsync(stop.Token))
            {
                if (output.Failure is { } error) throw error;
                if (block.StartByte != expected)
                {
                    // Never differentiate phase across missing I/Q; discard old audio and fade in.
                    demodulator = new FmDemodulator(SampleRate);
                    output.Clear();
                }
                expected = block.StartByte + block.Data.Length;
                output.Write(demodulator.Process(block.Data));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref audioFailure, ex); }
        finally
        {
            Volatile.Write(ref audioOutput, null);
            output?.Dispose();
        }
    }

    private void Read(uint index, uint frequency, ReceiveSettings settings)
    {
        RtlSdrNative.ReadCallback callback = (buffer, length, _) =>
        {
            // Exceptions must never escape across the native callback boundary.
            try
            {
                if (stop.IsCancellationRequested) return;
                var copy = new byte[checked((int)length)];
                Marshal.Copy(buffer, copy, 0, copy.Length);
                long endByte = Interlocked.Add(ref receivedBytes, copy.Length);
                blocks.Writer.TryWrite(copy);
                if (settings.FmEnabled && AudioFailure is null) audioBlocks.Writer.TryWrite(new AudioBlock(endByte - copy.Length, copy));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
                RtlSdrNative.rtlsdr_cancel_async(handle);
            }
        };
        try
        {
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_open(out var opened, index), "接続");
            lock (handleLock) handle = opened;
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_sample_rate(handle, settings.SampleRate), "サンプルレート設定");
            int count = RtlSdrNative.rtlsdr_get_tuner_gains(handle, null);
            if (count < 0 || count > 1024) throw new IOException("対応RFゲインを取得できません。");
            if (count > 0)
            {
                var gains = new int[count];
                int returned = RtlSdrNative.rtlsdr_get_tuner_gains(handle, gains);
                if (returned != count) throw new IOException("対応RFゲインの取得件数が一致しません。");
                SupportedGains = gains.Distinct().Order().ToArray();
            }
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_tuner_gain_mode(handle, settings.ManualGain.HasValue ? 1 : 0), "RFゲインモード設定");
            if (settings.ManualGain is int gain)
            {
                if (!SupportedGains.Contains(gain)) throw new IOException("選択したRFゲインはこのデバイスで使用できません。");
                RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_tuner_gain(handle, gain), "RFゲイン設定");
                AppliedGain = RtlSdrNative.rtlsdr_get_tuner_gain(handle);
                if (AppliedGain != gain) throw new IOException("RFゲインの設定値を確認できません。");
            }
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_agc_mode(handle, 0), "ADC AGC設定");
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_center_freq(handle, frequency), "中心周波数設定");
            Frequency = RtlSdrNative.rtlsdr_get_center_freq(handle);
            SampleRate = RtlSdrNative.rtlsdr_get_sample_rate(handle);
            if (Frequency == 0 || SampleRate == 0) throw new IOException("受信設定を取得できません。");
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_reset_buffer(handle), "受信バッファ初期化");
            ready.TrySetResult();
            if (!stop.IsCancellationRequested)
            {
                int result = RtlSdrNative.rtlsdr_read_async(handle, callback, IntPtr.Zero, 8, 32768);
                if (!stop.IsCancellationRequested)
                    throw new IOException($"受信が終了しました (RTL-SDR: {result})。再接続してください。");
            }
        }
        catch (Exception ex) { Volatile.Write(ref failure, ex); ready.TrySetException(ex); }
        finally
        {
            // Close only after read_async and every callback have returned.
            lock (handleLock)
            {
                if (handle != IntPtr.Zero) { RtlSdrNative.rtlsdr_close(handle); handle = IntPtr.Zero; }
            }
            blocks.Writer.TryComplete();
            audioBlocks.Writer.TryComplete();
            GC.KeepAlive(callback);
        }
    }

    public async Task StopAsync()
    {
        stop.Cancel();
        Volume = 0;
        // Retry covers cancellation arriving just before native read_async enters RUNNING.
        while (!reading.IsCompleted)
        {
            lock (handleLock)
                if (handle != IntPtr.Zero) RtlSdrNative.rtlsdr_cancel_async(handle);
            await Task.WhenAny(reading, Task.Delay(25));
        }
        await reading;
        await processing;
        await audioProcessing;
    }
}
