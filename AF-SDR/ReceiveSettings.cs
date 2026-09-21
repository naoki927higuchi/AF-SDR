namespace AfSdr;

internal sealed record ReceiveSettings(uint SampleRate, int? ManualGain = null, int FftSize = Dsp.SpectrumProcessor.DefaultSize,
    bool FmEnabled = false, Dsp.FftWindow Window = Dsp.FftWindow.Hann, uint RxBandwidth = 200_000, Dsp.DigitalSettings? Digital = null)
{
    internal Dsp.DigitalSettings DigitalOptions => Digital ?? new();
    internal static readonly uint[] RxBandwidths = [100_000, 120_000, 150_000, 180_000, 200_000, 220_000, 240_000];
    // RTL-SDR Blog 1.4.0 rtl-sdr.h: 225001..300000 or 900001..3200000.
    internal static readonly uint[] Rates = [250_000, 300_000, 1_000_000, 1_024_000, 1_400_000,
        1_800_000, 2_048_000, 2_400_000, 2_560_000, 2_880_000, 3_200_000];
    internal static bool ValidRate(uint rate) => rate is > 225_000 and <= 300_000 or > 900_000 and <= 3_200_000;
    internal static int? ResolveInitialGain(int? desired, int[] supported) => desired is int value && supported.Length > 0
        ? supported.MinBy(gain => Math.Abs((long)gain - value)) : null;
    internal void Validate(bool fileInput = false)
    {
        DigitalOptions.Validate(SampleRate);
        if (fileInput ? SampleRate is < 250_000 or > 3_200_000 : !ValidRate(SampleRate)) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (!Dsp.SpectrumProcessor.SupportedSizes.Contains(FftSize)) throw new ArgumentOutOfRangeException(nameof(FftSize));
        if (!Enum.IsDefined(Window)) throw new ArgumentOutOfRangeException(nameof(Window));
        if (!RxBandwidths.Contains(RxBandwidth) || RxBandwidth > SampleRate) throw new ArgumentOutOfRangeException(nameof(RxBandwidth));
    }
}

internal readonly record struct SettingsChange(bool Hardware, bool Spectrum, bool Audio, bool Digital = false)
{
    internal static SettingsChange Between(uint oldFrequency, ReceiveSettings old, uint frequency, ReceiveSettings next)
    {
        bool hardware = oldFrequency != frequency || old.SampleRate != next.SampleRate || old.ManualGain != next.ManualGain;
        return new(hardware, hardware || old.FftSize != next.FftSize || old.Window != next.Window,
            hardware || old.FmEnabled != next.FmEnabled || old.RxBandwidth != next.RxBandwidth,
            hardware || old.DigitalOptions != next.DigitalOptions);
    }
}

internal sealed record RateOption(uint Hertz, string Label)
{
    public override string ToString() => Label;
}

internal sealed record GainOption(int TenthsDb)
{
    public override string ToString() => $"{TenthsDb / 10.0:F1} dB";
}

internal readonly record struct DisplayRange(uint SampleRate, uint RequestedBandwidth)
{
    internal uint Bandwidth => RequestedBandwidth == 0 ? SampleRate : Math.Min(RequestedBandwidth, SampleRate);
    internal double BinWidth(int size) => size * (double)Bandwidth / SampleRate;
    internal double FirstBin(int size) => (size - BinWidth(size)) / 2;
    internal double FrequencyAt(uint center, double fraction) => center + (fraction - 0.5) * Bandwidth;
}
