using System.Reflection;
using System.Text.Json;
using AfSdr.Dsp;

namespace AfSdr.Checks;
internal static class PersistenceChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AF-SDR-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Require(SettingsStore.Load(path, out _).Frequency == 80_000_000, "Missing settings defaults");
            var settings = new UserSettings
            {
                WindowBounds = new Rectangle(-1500, 120, 1100, 850), Maximized = true,
                Frequency = 78_400_001, SampleRate = 1_024_000, ManualGain = true, Gain = 197,
                DisplayBandwidth = 500_000, FftSize = 16384, Window = FftWindow.BlackmanHarris,
                LevelLower = -90, LevelUpper = -20, RxBandwidth = 150_000, ShowRxBandwidth = false, Volume = 47, Digital = new DigitalSettings(false, DigitalMode.Bpsk, 19200, 0.5)
            };
            SettingsStore.Save(path, settings);
            Require(SettingsStore.Load(path, out _) == settings, "Every setting survives JSON round trip");
            SettingsStore.Save(path, settings with { Volume = 48 });
            Require(SettingsStore.Load(path, out _) == settings with { Volume = 48 }, "Atomic replacement of existing settings");
            Require(Directory.GetFiles(directory).Length == 1, "No temporary file left after save");
            using (var form = new MainForm(path))
            {
                var captured = form.CaptureSettings();
                Require(captured with { WindowBounds = settings.WindowBounds, Maximized = true, Volume = 47 } == settings, "UI restores all reception and display controls");
                Require(!Field<CheckBox>(form, "fmEnabled").Checked && Field<object?>(form, "receiver") is null, "Restored startup cannot receive or play audio");
                Field<TextBox>(form, "frequency").Text = "unfinished!";
                Require(form.CaptureSettings().Frequency == settings.Frequency, "Invalid frequency text retains last valid frequency");
                Field<TrackBar>(form, "volume").Value = 63;
                typeof(MainForm).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(form,
                    [form, new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                Require(SettingsStore.Load(path, out _).Volume == 63, "Normal closing saves current controls");
            }
            var negativeMonitor = new Rectangle(-1920, 0, 1920, 1080);
            var primary = new Rectangle(0, 0, 1920, 1040);
            Require(SettingsStore.FitWindow(settings.WindowBounds, [primary, negativeMonitor], new Size(1000, 800)) == settings.WindowBounds, "Keep valid negative monitor coordinates");
            var fit = SettingsStore.FitWindow(settings.WindowBounds, [primary], new Size(1000, 800));
            Require(primary.Contains(fit), "Removed monitor moves window into current working area");
            var small = new Rectangle(0, 0, 800, 600);
            Require(small.Contains(SettingsStore.FitWindow(new Rectangle(100, 100, 2000, 1500), [small], new Size(1000, 800))), "Small screen keeps title bar and whole window accessible");
            Require(ReceiveSettings.ResolveInitialGain(200, [0, 197, 496]) == 197 && ReceiveSettings.ResolveInitialGain(197, []) is null,
                "Saved RF gain maps to nearest supported value or auto");
            File.WriteAllText(path, "{broken json");
            Require(SettingsStore.Load(path, out var error) == new UserSettings() && error is not null, "Corrupt file falls back safely");
            File.WriteAllText(path, JsonSerializer.Serialize(settings with { SampleRate = 42, FftSize = 7, Window = (FftWindow)999, LevelLower = 0, LevelUpper = -20, RxBandwidth = 1, Volume = 200 }));
            var normalized = SettingsStore.Load(path, out _);
            Require(normalized.SampleRate == Receiver.RequestedRate && normalized.FftSize == 4096 && normalized.Window == FftWindow.Hann && normalized.LevelLower == -120 && normalized.LevelUpper == 0 && normalized.RxBandwidth == 200000 && normalized.Volume == 100,
                "Out-of-range saved values are normalized");
            File.WriteAllText(path, "{\"SchemaVersion\":999}");
            Require(SettingsStore.Load(path, out error) == new UserSettings() && error is not null, "Unknown schema defaults");
            Console.WriteLine("PASS: settings persistence, replacement, corruption/range recovery, safe UI restore, monitor changes and RF gain mapping.");
        }
        finally
        {
            // This unique test-owned directory contains only the settings file and possible temporary files.
            if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Require(bool success, string message) { if (!success) throw new Exception("FAIL: " + message); }
}
