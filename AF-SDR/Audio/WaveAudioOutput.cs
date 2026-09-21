using System.Runtime.InteropServices;

namespace AfSdr.Audio;

internal interface IAudioOutput : IDisposable
{
    Exception? Failure { get; }
    float Volume { set; }
    void Write(float[] samples);
    void Clear();
    Task Ready { get; }
}

// WinMM WAVE_MAPPER selects the system playback device. No global mixer volume changes.
internal sealed class WaveAudioOutput : IAudioOutput
{
    private readonly AudioBuffer buffer = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private float volume;
    public Exception? Failure => Volatile.Read(ref failure);
    public float Volume { set => Volatile.Write(ref volume, Math.Clamp(value, 0, 1)); }
    public void Write(float[] samples) => buffer.Write(samples);
    public void Clear() => buffer.Clear();
    public Task Ready => ready.Task;

    internal WaveAudioOutput(float volume)
    {
        Volume = volume;
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run()
    {
        IntPtr device = IntPtr.Zero;
        var headers = new List<(IntPtr Header, IntPtr Data)>();
        uint size = (uint)Marshal.SizeOf<WaveHeader>();
        try
        {
            var format = new WaveFormat { Format = 1, Channels = 1, Rate = 48000, BytesPerSecond = 96000, BlockAlign = 2, Bits = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0), "既定の音声デバイスを開く");
            for (int n = 0; n < 3; n++)
            {
                IntPtr data = Marshal.AllocHGlobal(1920);
                IntPtr header = Marshal.AllocHGlobal((int)size);
                headers.Add((header, data));
                Marshal.StructureToPtr(new WaveHeader { Data = data, Length = 1920 }, header, false);
                Check(waveOutPrepareHeader(device, header, size), "音声バッファの準備");
            }
            ready.TrySetResult();
            var pcm = new short[960]; // three 20 ms device buffers
            while (!stop.IsCancellationRequested)
            {
                foreach (var item in headers)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(item.Header);
                    if ((header.Flags & 0x10) != 0) continue; // WHDR_INQUEUE: native driver owns memory
                    buffer.Read(pcm, Volatile.Read(ref volume));
                    Marshal.Copy(pcm, 0, item.Data, pcm.Length);
                    Check(waveOutWrite(device, item.Header, size), "音声出力");
                }
                stop.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex) { Volatile.Write(ref failure, ex); ready.TrySetException(ex); }
        finally
        {
            if (device != IntPtr.Zero) waveOutReset(device);
            foreach (var item in headers)
            {
                // Never free memory still owned by a failing driver.
                uint result = device == IntPtr.Zero ? 0 : waveOutUnprepareHeader(device, item.Header, size);
                if (result == 0) { Marshal.FreeHGlobal(item.Header); Marshal.FreeHGlobal(item.Data); }
                else Volatile.Write(ref failure, new IOException($"音声バッファ解放に失敗しました ({result})。"));
            }
            if (device != IntPtr.Zero) waveOutClose(device);
        }
    }

    public void Dispose() { stop.Cancel(); worker.GetAwaiter().GetResult(); stop.Dispose(); }
    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new IOException($"{operation}に失敗しました (WinMM: {result})。");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Format, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
}
