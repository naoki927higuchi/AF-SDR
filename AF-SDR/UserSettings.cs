using System.Text.Json;
using AfSdr.Dsp;

namespace AfSdr;

internal sealed record UserSettings
{
    public bool FileInput { get; init; }
    public string LastIqPath { get; init; } = "";
    public int SchemaVersion { get; init; } = 1;
    public Rectangle WindowBounds { get; init; }
    public bool Maximized { get; init; }
    public uint Frequency { get; init; } = 80_000_000;
    public uint SampleRate { get; init; } = Receiver.RequestedRate;
    public bool ManualGain { get; init; }
    public int Gain { get; init; } = 200;
    public uint DisplayBandwidth { get; init; }
    public int FftSize { get; init; } = SpectrumProcessor.DefaultSize;
    public FftWindow Window { get; init; } = FftWindow.Hann;
    public decimal LevelLower { get; init; } = -120;
    public decimal LevelUpper { get; init; }
    public uint RxBandwidth { get; init; } = 200_000;
    public bool ShowRxBandwidth { get; init; } = true;
    public DigitalViewSettings? DigitalView { get; init; } = new();
    public DigitalSettings? Digital { get; init; } = new();
    public int Volume { get; init; } = 30;

    internal UserSettings Validated()
    {
        var defaults = new UserSettings();
        uint rate = ReceiveSettings.Rates.Contains(SampleRate) ? SampleRate : defaults.SampleRate;
        bool levelsValid = LevelLower >= -160 && LevelLower <= -5 && LevelUpper >= -115 && LevelUpper <= 20 && LevelUpper - LevelLower >= 5;
        return this with
        {
            Frequency = Frequency == 0 ? defaults.Frequency : Frequency,
            SampleRate = rate,
            Gain = Gain is >= -1000 and <= 1000 ? Gain : defaults.Gain,
            DisplayBandwidth = DisplayBandwidth < rate && new uint[] { 0, 2_000_000, 1_000_000, 500_000, 250_000, 100_000, 50_000 }.Contains(DisplayBandwidth) ? DisplayBandwidth : 0,
            FftSize = SpectrumProcessor.SupportedSizes.Contains(FftSize) ? FftSize : defaults.FftSize,
            Window = Enum.IsDefined(Window) ? Window : defaults.Window,
            LevelLower = levelsValid ? LevelLower : defaults.LevelLower,
            LevelUpper = levelsValid ? LevelUpper : defaults.LevelUpper,
            RxBandwidth = ReceiveSettings.RxBandwidths.Contains(RxBandwidth) ? RxBandwidth : defaults.RxBandwidth,
            Volume = Math.Clamp(Volume, 0, 100),
            Digital = (Digital ?? new()).Normalize(rate),
            DigitalView = (DigitalView ?? new()).Validated()
        };
    }
}

internal sealed record DigitalViewSettings
{
    internal static readonly int[] SymbolCounts = [64, 128, 256, 512, 1024];
    public Rectangle WindowBounds { get; init; }
    public bool Maximized { get; init; }
    public int SymbolCount { get; init; } = 256;
    public bool Trajectory { get; init; }
    internal DigitalViewSettings Validated() => this with { SymbolCount = SymbolCounts.Contains(SymbolCount) ? SymbolCount : 256 };
}

internal static class SettingsStore
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AF-SDR", "settings.json");
    internal static UserSettings Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("設定ファイルが大きすぎます。");
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
            if (settings is null || settings.SchemaVersion != 1) throw new InvalidDataException("設定ファイルの形式が非対応です。");
            return settings.Validated();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            error = "保存設定を読み込めないため初期値で起動しました。";
            return new();
        }
    }

    internal static void Save(string path, UserSettings settings)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings.Validated(), new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Fit normal bounds into one current working area (including negative-coordinate monitors).
    internal static Rectangle FitWindow(Rectangle saved, IReadOnlyList<Rectangle> areas, Size minimum)
    {
        if (areas.Count == 0) throw new ArgumentException("No working areas.");
        bool valid = saved.Width > 0 && saved.Height > 0 && Math.Abs((long)saved.X) < 100000 && Math.Abs((long)saved.Y) < 100000 && saved.Width < 100000 && saved.Height < 100000;
        if (!valid) saved = Rectangle.Empty;
        long Overlap(Rectangle a) => (long)Math.Max(0, Rectangle.Intersect(saved, a).Width) * Math.Max(0, Rectangle.Intersect(saved, a).Height);
        var area = areas.OrderByDescending(Overlap).First();
        int width = Math.Clamp(valid ? saved.Width : 1280, Math.Min(minimum.Width, area.Width), area.Width);
        int height = Math.Clamp(valid ? saved.Height : 960, Math.Min(minimum.Height, area.Height), area.Height);
        int x = valid && Overlap(area) > 0 ? Math.Clamp(saved.X, area.Left, area.Right - width) : area.Left + (area.Width - width) / 2;
        int y = valid && Overlap(area) > 0 ? Math.Clamp(saved.Y, area.Top, area.Bottom - height) : area.Top + (area.Height - height) / 2;
        return new Rectangle(x, y, width, height);
    }
}
