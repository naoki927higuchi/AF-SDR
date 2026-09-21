using AfSdr.Dsp;

namespace AfSdr;

internal interface IReceiver
{
    uint Frequency { get; }
    uint SampleRate { get; }
    int[] SupportedGains { get; }
    int? AppliedGain { get; }
    float[]? Spectrum { get; }
    ConstellationFrame? Constellation { get; }
    Exception? Failure { get; }
    Exception? AudioFailure { get; }
    Exception? DigitalFailure { get; }
    long ReceivedBytes { get; }
    float Volume { set; }
    Task<SettingsChange> UpdateAsync(uint frequency, ReceiveSettings settings);
    Task StopAsync();
}

internal static class IqSamples
{
    internal static float[] FromRtl(ReadOnlySpan<byte> source)
    {
        if ((source.Length & 1) != 0) throw new ArgumentException("I/Q samples must be paired.");
        var result = new float[source.Length];
        for (int n = 0; n < result.Length; n++) result[n] = (source[n] - 127.5f) / 128;
        return result;
    }
}
