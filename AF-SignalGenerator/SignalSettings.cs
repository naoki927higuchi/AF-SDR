using System.Text.Json;
using System.Text.Json.Serialization;

namespace AfSignalGenerator;

internal enum Modulation { BPSK, QPSK, PSK8, QAM, Pi4QPSK, ASK, FSK, MSK, GMSK }
internal enum DataPattern { RandomBits, PRBS15, PRBS23 }
internal enum AmplitudeMode { OOK, ASK }

internal sealed record SignalSettings
{
    public Modulation Modulation { get; set; } = Modulation.QPSK;
    public uint Fc { get; set; } = 100_000_000;
    public double Baud { get; set; } = 9600;
    public double RrcAlpha { get; set; } = .35;
    public uint SampleRate { get; set; } = 250000;
    public double LevelDbfs { get; set; } = -20;
    public double Duration { get; set; } = 10;
    public DataPattern DataPattern { get; set; } = DataPattern.RandomBits;
    public uint Seed { get; set; } = 1;
    public int QamOrder { get; set; } = 16;
    public int FskTones { get; set; } = 2;
    public double FskSpacing { get; set; } = 4800;
    public double Bt { get; set; } = .3;
    public AmplitudeMode AmplitudeMode { get; set; } = AmplitudeMode.OOK;
    public int AskOrder { get; set; } = 4;
    public double FrequencyOffset { get; set; }
    public double FrequencyDrift { get; set; }
    public double FrequencyJitter { get; set; }
    public double CarrierKnotMs { get; set; } = 100;
    public double InitialPhaseDegrees { get; set; }
    public bool RandomPhase { get; set; }
    public double BaudOffsetPpm { get; set; }
    public double BaudDriftPpmPerSecond { get; set; }
    public double BaudJitterPpm { get; set; }
    public double BaudKnotMs { get; set; } = 100;
    public double TimingOffsetSymbols { get; set; }
    public bool RandomTiming { get; set; }
    public bool Awgn { get; set; }
    public double SnrDb { get; set; } = 30;
    [JsonIgnore] public bool UsesRrc => Modulation is Modulation.BPSK or Modulation.QPSK or Modulation.PSK8 or Modulation.QAM or Modulation.Pi4QPSK;
    [JsonIgnore] public long Frames => checked((long)Math.Round(Duration * SampleRate, MidpointRounding.AwayFromZero));

    internal double HalfBandwidth(double baud) => UsesRrc ? (1 + RrcAlpha) * baud / 2
        : Modulation == Modulation.FSK ? (FskTones - 1) * FskSpacing / 2 + baud
        : Modulation is Modulation.MSK or Modulation.GMSK ? 1.5 * baud : 1.5 * baud;
    internal string[] Validate()
    {
        var errors = new List<string>();
        void Range(double v, double min, double max, string label) { if (!double.IsFinite(v) || v < min || v > max) errors.Add($"{label}: {min}～{max}を指定してください。"); }
        Range(Fc, 1, uint.MaxValue, "Fc [Hz]"); Range(SampleRate, 250000, 3200000, "Sample Rate [S/s]");
        Range(Baud, 100, 100000, "Baud"); Range(RrcAlpha, 0, 1, "RRC α"); Range(Duration, .01, 3600, "Duration [s]");
        Range(LevelDbfs, -100, 0, "Signal Level [dBFS]"); Range(Bt, .1, 1, "GMSK BT"); Range(FskSpacing, 1, 500000, "FSK間隔 [Hz]");
        Range(FrequencyOffset, -1600000, 1600000, "Frequency Offset"); Range(FrequencyDrift, -100000, 100000, "Frequency Drift");
        Range(FrequencyJitter, 0, 100000, "Frequency Jitter RMS"); Range(CarrierKnotMs, 1, 10000, "Carrier Jitter相関時間 [ms]");
        Range(InitialPhaseDegrees, -360, 360, "Initial Phase [deg]"); Range(TimingOffsetSymbols, 0, 1, "Timing Offset [symbol]");
        Range(BaudOffsetPpm, -100000, 100000, "Baud Offset [ppm]"); Range(BaudDriftPpmPerSecond, -100000, 100000, "Baud Drift [ppm/s]");
        Range(BaudJitterPpm, 0, 100000, "Baud Jitter RMS [ppm]"); Range(BaudKnotMs, 1, 10000, "Baud Jitter相関時間 [ms]");
        Range(SnrDb, -20, 100, "SNR [dB]");
        if (!Enum.IsDefined(Modulation) || !Enum.IsDefined(DataPattern) || !Enum.IsDefined(AmplitudeMode)) errors.Add("変調方式またはデータ系列が不正です。");
        if (QamOrder is not (16 or 64 or 256) || FskTones is not (2 or 4 or 8) || AskOrder is not (2 or 4 or 8)) errors.Add("多値数が非対応です。");
        if (errors.Count > 0) return errors.ToArray();
        double end = BaudOffsetPpm + BaudDriftPpmPerSecond * Duration;
        double slow = Baud * (1 + (Math.Min(BaudOffsetPpm, end) - 6 * BaudJitterPpm) * 1e-6);
        double fast = Baud * (1 + (Math.Max(BaudOffsetPpm, end) + 6 * BaudJitterPpm) * 1e-6);
        if (slow <= 0 || fast > SampleRate / 8.0) errors.Add("クロック誤差・ドリフト・6σジッターを含むBaudには、最低8 samples/symbolが必要です。Baudまたは誤差を下げてください。");
        double carrier = Math.Max(Math.Abs(FrequencyOffset), Math.Abs(FrequencyOffset + FrequencyDrift * Duration)) + 6 * FrequencyJitter;
        if (carrier + HalfBandwidth(fast) >= SampleRate * .45) errors.Add("周波数偏差・ドリフト・6σジッターと信号帯域がSample Rateの安全帯域（±0.45 Fs）を超えます。");
        if (Fc - carrier - HalfBandwidth(fast) < 1 || Fc + carrier + HalfBandwidth(fast) > uint.MaxValue) errors.Add("Fcと信号帯域がAF-SDRの周波数範囲外です。");
        return errors.ToArray();
    }
}

internal sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public SignalSettings Parameters { get; init; } = new();
    public string InputFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string OutputFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AF-SignalGenerator");
    internal AppSettings Golden() => this with { Parameters = new SignalSettings() };
}

internal static class SettingsStore
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AF-SignalGenerator", "settings.json");
    internal static AppSettings Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("設定JSONが大きすぎます。");
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("空の設定です。");
            if (value.SchemaVersion != 1 || value.Parameters is null || value.InputFolder is null || value.OutputFolder is null) throw new InvalidDataException("設定の形式が非対応です。");
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { error = "設定を復元できません: " + ex.Message; return new(); }
    }
    internal static void Save(string path, AppSettings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
