namespace AfSdr.Native;

internal interface IRtlDevice
{
    void Open(uint index);
    void Close();
    (uint Frequency, uint Rate, int[] Gains, int? Gain) Configure(uint frequency, ReceiveSettings settings);
    int Read(RtlSdrNative.ReadCallback callback);
    void Cancel();
}

internal sealed class RtlDevice : IRtlDevice
{
    private IntPtr handle;
    private ReceiveSettings? applied;
    private uint appliedFrequency;
    private int[] gains = [];
    public void Open(uint index) => RtlSdrNative.Check(RtlSdrNative.rtlsdr_open(out handle, index), "接続");
    public void Close()
    {
        if (handle != IntPtr.Zero) { RtlSdrNative.rtlsdr_close(handle); handle = IntPtr.Zero; }
        applied = null;
    }
    public (uint Frequency, uint Rate, int[] Gains, int? Gain) Configure(uint frequency, ReceiveSettings settings)
    {
        if (applied is null || applied.SampleRate != settings.SampleRate)
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_sample_rate(handle, settings.SampleRate), "サンプルレート設定");
        if (applied is null)
        {
            int count = RtlSdrNative.rtlsdr_get_tuner_gains(handle, null);
            if (count < 0 || count > 1024) throw new IOException("対応RFゲインを取得できません。");
            gains = new int[count];
            if (count > 0 && RtlSdrNative.rtlsdr_get_tuner_gains(handle, gains) != count) throw new IOException("RFゲイン取得件数が一致しません。");
        }
        if (applied is null) settings = settings with { ManualGain = ReceiveSettings.ResolveInitialGain(settings.ManualGain, gains) };
        if (settings.ManualGain is int selected && !gains.Contains(selected)) throw new IOException("RFゲインが非対応です。");
        if (applied is null || applied.ManualGain.HasValue != settings.ManualGain.HasValue)
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_tuner_gain_mode(handle, settings.ManualGain.HasValue ? 1 : 0), "RFゲインモード設定");
        int? gain = null;
        if (settings.ManualGain is int manual)
        {
            if (applied is null || applied.ManualGain != manual)
                RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_tuner_gain(handle, manual), "RFゲイン設定");
            gain = RtlSdrNative.rtlsdr_get_tuner_gain(handle);
            if (gain != manual) throw new IOException("RFゲイン設定値を確認できません。");
        }
        if (applied is null) RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_agc_mode(handle, 0), "ADC AGC設定");
        if (applied is null || appliedFrequency != frequency)
            RtlSdrNative.Check(RtlSdrNative.rtlsdr_set_center_freq(handle, frequency), "中心周波数設定");
        uint actualFrequency = RtlSdrNative.rtlsdr_get_center_freq(handle), actualRate = RtlSdrNative.rtlsdr_get_sample_rate(handle);
        if (actualFrequency == 0 || actualRate == 0) throw new IOException("受信設定を取得できません。");
        RtlSdrNative.Check(RtlSdrNative.rtlsdr_reset_buffer(handle), "受信バッファ初期化");
        applied = settings;
        appliedFrequency = actualFrequency;
        return (actualFrequency, actualRate, gains.Distinct().Order().ToArray(), gain);
    }
    public int Read(RtlSdrNative.ReadCallback callback) => RtlSdrNative.rtlsdr_read_async(handle, callback, IntPtr.Zero, 8, 32768);
    public void Cancel() { if (handle != IntPtr.Zero) RtlSdrNative.rtlsdr_cancel_async(handle); }
}
