using System.Runtime.InteropServices;

namespace AfSdr.Native;

// RTL-SDR Blog 1.4.0 include/rtl-sdr.h and rtl-sdr_export.h.
// uint32_t -> uint, int -> int, opaque device pointer -> IntPtr; C ABI.
internal static class RtlSdrNative
{
    private const string Dll = "rtlsdr.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ReadCallback(IntPtr buffer, uint length, IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint rtlsdr_get_device_count();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rtlsdr_get_device_name(uint index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_open(out IntPtr device, uint index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_close(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_set_center_freq(IntPtr device, uint frequency);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint rtlsdr_get_center_freq(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_set_sample_rate(IntPtr device, uint rate);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint rtlsdr_get_sample_rate(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_set_tuner_gain_mode(IntPtr device, int manual);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_set_agc_mode(IntPtr device, int enabled);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_reset_buffer(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_read_async(IntPtr device, ReadCallback callback, IntPtr context, uint bufferCount, uint bufferLength);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtlsdr_cancel_async(IntPtr device);
    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation}に失敗しました (RTL-SDR: {result})。");
    }
}
