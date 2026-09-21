using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AfSignalGenerator;

internal sealed record TruthPoint(long Frame, double TimeSeconds, double CarrierOffsetHz, double ActualBaud,
    double SymbolCoordinate, double CarrierJitterHz, double BaudJitterPpm, double CarrierPhaseRadians);
internal sealed record GenerationResult(string WavePath, string JsonPath, long Frames, double SignalDbfs, double? MeasuredSnrDb, double Peak, string Sha256);
internal sealed class Moments
{
    private double sum, squares;
    private long count;
    internal void Add(double x) { sum += x; squares += x * x; count++; }
    internal double Mean => sum / Math.Max(1, count);
    internal double Rms => Math.Sqrt(squares / Math.Max(1, count));
}

internal static class SignalWriter
{
    internal const string Algorithm = "AFSG-1.0.0";
    internal static GenerationResult Generate(SignalSettings parameters, string folder, IProgress<int>? progress = null, CancellationToken token = default)
    {
        var s = parameters with { };
        var errors = s.Validate(); if (errors.Length != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        Directory.CreateDirectory(folder);
        long frames = s.Frames, traceStep = Math.Max(1, (frames + 999) / 1000);
        var trace = new List<TruthPoint>(); var fj = new Moments(); var bj = new Moments();
        double sum = 0, maxRaw = 0, minBaud = double.MaxValue, maxBaud = 0;
        var engine = new SignalEngine(s);
        double phase = engine.ResolvedPhaseDegrees, timing = engine.ResolvedTimingSymbols;
        // First deterministic pass measures the actual clean waveform RMS, including shaping and clock warping.
        for (long n = 0; n < frames; n++)
        {
            if ((n & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report((int)(40 * n / frames)); }
            var value = engine.Next(); double p = value.Value.Real * value.Value.Real + value.Value.Imaginary * value.Value.Imaginary;
            sum += p; maxRaw = Math.Max(maxRaw, p); fj.Add(value.CarrierJitterHz); bj.Add(value.BaudJitterPpm);
            minBaud = Math.Min(minBaud, value.Baud); maxBaud = Math.Max(maxBaud, value.Baud);
            if (n % traceStep == 0 || n == frames - 1) trace.Add(new(n, n / (double)s.SampleRate, value.CarrierHz, value.Baud, value.Coordinate, value.CarrierJitterHz, value.BaudJitterPpm, value.CarrierPhase));
        }
        if (sum <= 1e-30 || !double.IsFinite(sum)) throw new InvalidDataException("信号電力が0または非有限です。系列・Durationを確認してください。");
        double power = Math.Pow(10, s.LevelDbfs / 10), gain = Math.Sqrt(power / (sum / frames));
        double sigma = s.Awgn ? Math.Sqrt(power / Math.Pow(10, s.SnrDb / 10) / 2) : 0;
        string stem = $"AFSG_{s.Modulation}_{s.Fc}Hz_seed{s.Seed}", output = Path.Combine(folder, stem + ".wav");
        for (int number = 1; File.Exists(output) || File.Exists(Path.ChangeExtension(output, ".json")); number++) output = Path.Combine(folder, stem + $"_{number}.wav");
        string metadata = Path.ChangeExtension(output, ".json");
        string tmpWave = output + "." + Guid.NewGuid().ToString("N") + ".tmp", tmpJson = tmpWave + ".json";
        bool wavePublished = false, bothPublished = false;
        double noisePower = 0, peak = 0; long overRange = 0;
        try
        {
            engine = new SignalEngine(s); var noise = new RandomSource(s.Seed ^ 0x9b05688c2b3e6c1fUL);
            using (var stream = new FileStream(tmpWave, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                WriteHeader(writer, s.SampleRate, frames);
                for (long n = 0; n < frames; n++)
                {
                    if ((n & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(40 + (int)(55 * n / frames)); }
                    Complex signal = engine.Next().Value * gain;
                    double ni = sigma == 0 ? 0 : noise.Gaussian() * sigma, nq = sigma == 0 ? 0 : noise.Gaussian() * sigma;
                    noisePower += ni * ni + nq * nq;
                    float i = (float)(signal.Real + ni), q = (float)(signal.Imaginary + nq);
                    if (!float.IsFinite(i) || !float.IsFinite(q)) throw new InvalidDataException("出力が非有限です。");
                    peak = Math.Max(peak, Math.Max(Math.Abs(i), Math.Abs(q)));
                    if (Math.Abs(i) > 1 || Math.Abs(q) > 1) overRange++;
                    writer.Write(i); writer.Write(q); // float32 LE, ch1=I, ch2=Q; never clip
                }
                writer.Flush(); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            string hash;
            using (var stream = File.OpenRead(tmpWave))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[131072]; int count;
                while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
                hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            }
            double? measuredSnr = sigma == 0 ? null : 10 * Math.Log10(power / (noisePower / frames));
            var document = new
            {
                Schema = "AF-SignalGenerator.IqTestVector", SchemaVersion = 1, GeneratorVersion = "1.0.0", Algorithm,
                Parameters = s,
                Wave = new { Container = "RF64/WAVE", Format = "IEEE_FLOAT", BitsPerSample = 32, Channels = 2, ChannelOrder = "I,Q", ComplexConvention = "I+jQ", SampleRateHz = s.SampleRate, CenterFrequencyHz = s.Fc, Frames = frames, DurationSeconds = frames / (double)s.SampleRate, Sha256 = hash },
                Resolved = new { InitialCarrierPhaseDegrees = phase, TimingOffsetSymbols = timing, SymbolCoordinateAtStart = 2 * SignalEngine.Radius + timing, CleanGain = gain },
                Models = new
                {
                    Carrier = "f(t)=FrequencyOffset+FrequencyDrift*t+FrequencyJitter*j_c(t) Hz; phase integrated by trapezoid at Fs, no RF Fc oscillation in baseband",
                    Clock = "b(t)=Baud*(1+1e-6*(BaudOffsetPpm+BaudDriftPpmPerSecond*t+BaudJitterPpm*j_b(t))); symbol coordinate integrates b(t); positive timing advances signal",
                    Jitter = "Independent unit Gaussian knots; random knot origin; h=(1-cos(pi*fraction))/2; j=((1-h)*g0+h*g1)/sqrt((1-h)^2+h^2). C1 continuous, ensemble mean 0 / RMS 1; finite realization need not have exact zero mean or target RMS. KnotMs is correlation scale, NOT -3dB bandwidth.",
                    Level = "10log10(mean(I_clean^2+Q_clean^2)); 0 dBFS = unit complex RMS. Two-pass exact clean RMS before AWGN; no clipping, no output AGC.",
                    Noise = "Independent Gaussian I,Q at Fs; sigma^2=Pclean/(2*10^(SNR/10)); SNR is full sampled-band power, NOT Eb/N0 or post-filter SNR.",
                    Pulse = s.UsesRrc ? "RRC +/-12 symbols, 2048 phases/symbol linear table interpolation, random prehistory, no start-up zero padding" : s.Modulation is Modulation.GMSK or Modulation.ASK ? "Gaussian-filtered NRZ, +/-12 symbols; BT=user for GMSK, BT=0.5 for ASK/OOK" : "Continuous-phase rectangular frequency pulses (FSK/MSK)",
                    Fsk = "FSK tones=(code-(M-1)/2)*FskSpacing Hz; fixed spacing independent of baud error. MSK/GMSK deviation=instantaneous baud/4 (h=0.5). Rectangular FSK/MSK pulses integrated across actual symbol boundaries; GMSK uses trapezoid integration. Phase never resets per symbol.",
                    Data = "SplitMix64 domain-separated streams, Box-Muller Gaussian; PRBS15 x^15+x^14+1 / PRBS23 x^23+x^18+1, seed low bits, zero replaced by 1; details in SIGNAL-MODEL.md"
                },
                Measurements = new { CleanPowerDbfs = 10 * Math.Log10(sum / frames * gain * gain), CleanPeakMagnitude = Math.Sqrt(maxRaw) * gain, MeasuredSnrDb = measuredSnr, PeakComponent = peak, FramesBeyondUnitComponent = overRange, CarrierJitterMeanHz = fj.Mean, CarrierJitterRmsHz = fj.Rms, BaudJitterMeanPpm = bj.Mean, BaudJitterRmsPpm = bj.Rms, MinActualBaud = minBaud, MaxActualBaud = maxBaud },
                TruthSampleStride = traceStep, Truth = trace
            };
            File.WriteAllText(tmpJson, JsonSerializer.Serialize(document, SettingsStore.Json));
            token.ThrowIfCancellationRequested();
            File.Move(tmpWave, output); wavePublished = true;
            File.Move(tmpJson, metadata); bothPublished = true;
            progress?.Report(100);
            return new(output, metadata, frames, s.LevelDbfs, measuredSnr, peak, hash);
        }
        finally
        {
            if (File.Exists(tmpWave)) File.Delete(tmpWave);
            if (File.Exists(tmpJson)) File.Delete(tmpJson);
            if (wavePublished && !bothPublished) File.Delete(output);
        }
    }
    internal static void WriteHeader(BinaryWriter w, uint rate, long frames)
    {
        ulong bytes = checked((ulong)frames * 8);
        void Four(string text) => w.Write(Encoding.ASCII.GetBytes(text));
        Four("RF64"); w.Write(uint.MaxValue); Four("WAVE");
        Four("ds64"); w.Write(28u); w.Write(checked(bytes + 86)); w.Write(bytes); w.Write((ulong)frames); w.Write(0u);
        Four("fmt "); w.Write(18u); w.Write((ushort)3); w.Write((ushort)2); w.Write(rate); w.Write(checked(rate * 8)); w.Write((ushort)8); w.Write((ushort)32); w.Write((ushort)0);
        Four("fact"); w.Write(4u); w.Write((uint)Math.Min(frames, uint.MaxValue));
        Four("data"); w.Write(uint.MaxValue);
    }
}
