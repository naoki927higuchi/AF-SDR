using System.Runtime.InteropServices;
using System.Threading.Channels;
using AfSdr.Dsp;
using AfSdr.Native;

namespace AfSdr;

internal sealed class Receiver
{
    public const uint RequestedRate = 2_048_000;
    private readonly object handleLock = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> blocks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr handle;
    private Task reading = Task.CompletedTask, processing = Task.CompletedTask;
    private float[]? spectrum;
    private Exception? failure;
    private long receivedBytes;
    public uint Frequency { get; private set; }
    public uint SampleRate { get; private set; }
    public float[]? Spectrum => Volatile.Read(ref spectrum);
    public Exception? Failure => Volatile.Read(ref failure);
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);

    public static string[] ListDevices()
    {
        uint count = RtlSdrNative.rtlsdr_get_device_count();
        return Enumerable.Range(0, checked((int)count)).Select(i => $"{i}: {Marshal.PtrToStringAnsi(RtlSdrNative.rtlsdr_get_device_name((uint)i))}").ToArray();
    }

    public Task StartAsync(uint index, uint frequency)
    {
        processing = Task.Run(async () =>
        {
            var dsp = new SpectrumProcessor();
            await foreach (byte[] block in blocks.Reader.ReadAllAsync())
            {
                // Analyze all complete FFT blocks; bounded channel prevents latency buildup.
                for (int offset = 0; offset + SpectrumProcessor.Size * 2 <= block.Length; offset += SpectrumProcessor.Size * 2)
                    Volatile.Write(ref spectrum, dsp.Process(block.AsSpan(offset, SpectrumProcessor.Size * 2)));
            }
        });
        reading = Task.Run(() => Read(index, frequency));
        return ready.Task;
    }

    private void Read(uint index, uint frequency)
    {
        RtlSdrNative.ReadCallback callback = (buffer, length, _) =>
        {
            // Exceptions must never escape across the native callback boundary.
            try
            {
                if (stop.IsCancellationRequested) return;
                var copy = new byte[checked((int)length)];
                Marshal.Copy(buffer, copy, 0, copy.Length);
                Interlocked.Add(ref receivedBytes, copy.Length);
                blocks.Writer.TryWrite(copy);
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
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_sample_rate(handle, RequestedRate), "サンプルレート設定");
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_tuner_gain_mode(handle, 0), "自動ゲイン設定");
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
            GC.KeepAlive(callback);
        }
    }

    public async Task StopAsync()
    {
        stop.Cancel();
        // Retry covers cancellation arriving just before native read_async enters RUNNING.
        while (!reading.IsCompleted)
        {
            lock (handleLock)
                if (handle != IntPtr.Zero) RtlSdrNative.rtlsdr_cancel_async(handle);
            await Task.WhenAny(reading, Task.Delay(25));
        }
        await reading;
        await processing;
    }
}
