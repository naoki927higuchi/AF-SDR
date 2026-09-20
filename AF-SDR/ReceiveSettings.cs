namespace AfSdr;

internal sealed record ReceiveSettings(uint SampleRate, int? ManualGain = null, int FftSize = Dsp.SpectrumProcessor.DefaultSize)
{
    // RTL-SDR Blog 1.4.0 rtl-sdr.h: 225001..300000 or 900001..3200000.
    internal static readonly uint[] Rates = [250_000, 300_000, 1_000_000, 1_024_000, 1_400_000,
        1_800_000, 2_048_000, 2_400_000, 2_560_000, 2_880_000, 3_200_000];
    internal static bool ValidRate(uint rate) => rate is > 225_000 and <= 300_000 or > 900_000 and <= 3_200_000;
    internal void Validate()
    {
        if (!ValidRate(SampleRate)) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (!Dsp.SpectrumProcessor.SupportedSizes.Contains(FftSize)) throw new ArgumentOutOfRangeException(nameof(FftSize));
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
