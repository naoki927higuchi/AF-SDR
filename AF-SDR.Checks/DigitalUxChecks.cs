using System.Diagnostics;
using System.Reflection;
using AfSdr.Dsp;
using AfSignalGenerator;

namespace AfSdr.Checks;

internal static class DigitalUxChecks
{
    private static void Require(bool pass, string message) { if (!pass) throw new Exception("Digital UX: " + message); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Invoke(Control control, string method, EventArgs args) => typeof(Control).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [args]);
    private static void Handles(Control c) { _ = c.Handle; foreach (Control child in c.Controls) Handles(child); c.PerformLayout(); }
    private static void Until(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.Elapsed.TotalSeconds > 8) throw new Exception("Digital UX wait timed out"); Application.DoEvents(); Thread.Sleep(2); }
    }
    private static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    internal static void Run()
    {
        using (var form = new DigitalForm(new()))
        {
            Handles(form);
            var baud = Field<DigitTuningControl>(form, "baud"); var fine = Field<DigitTuningControl>(form, "fine");
            var mode = Field<ComboBox>(form, "mode"); var apply = Field<Button>(form, "apply");
            Require(baud.Value == 9600 && form.CaptureViewSettings().SymbolCount == 256 && !form.CaptureViewSettings().Trajectory, "initial settings");
            Require(form.PointToClient(baud.PointToScreen(Point.Empty)).Y < form.PointToClient(fine.PointToScreen(Point.Empty)).Y
                && !ReferenceEquals(baud.Parent, apply.Parent), "baud moved out of configuration row above frequency tuning");
            int events = 0; form.SettingsChanged += () => { events++; return Task.CompletedTask; };
            var surface = Field<Control>(baud, "surface");
            int cell = Math.Max(12, (int)Math.Ceiling(surface.Font.SizeInPoints * baud.DeviceDpi / 72 * .7));
            Invoke(surface, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 3 + 6 * cell + cell / 2, 10, 0));
            Invoke(surface, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120));
            Require(form.Settings.SymbolRate == 9610 && events == 1, "absolute baud: clicked ten digit / real wheel event immediately applied");
            var down = baud.Controls[0].Controls.OfType<Button>().Single(b => b.Text == "▼");
            Invoke(down, "OnClick", EventArgs.Empty); Require(form.Settings.SymbolRate == 9600, "baud spin shares digit step");
            baud.SelectDigit(7); baud.Wheel(120); Require(form.Settings.SymbolRate == 9601, "one baud step");
            Require(baud.CommitEntry("9603") && form.Settings.SymbolRate == 9603, "direct baud entry immediate");
            Require(!baud.CommitEntry("9603.1") && !baud.CommitEntry("-1"), "integer absolute baud validation");
            fine.Value = 137.2m;
            mode.SelectedIndex = (int)DigitalMode.Qam;
            baud.Adjust(1);
            Require(form.Settings.Mode == DigitalMode.Qpsk && form.Settings.SymbolRate == 9604, "real-time baud does not apply draft mode");
            baud.Adjust(-1);
            Invoke(apply, "OnClick", EventArgs.Empty);
            Require(form.Settings.Mode == DigitalMode.Qam && form.Settings.QamOrder == 16 && form.Settings.SymbolRate == 9603 && form.Settings.FineFrequencyOffset == 137.2, "Apply QAM preserves both tuning values");
            int before = events;
            Field<ComboBox>(form, "symbolCount").SelectedItem = 64; Field<CheckBox>(form, "trajectory").Checked = true;
            Require(events == before, "appearance changes never invoke receiver settings");
            form.SetSampleRate(250000); Require(form.Settings.SymbolRate == 9603, "rate constraint refresh preserves valid baud");
            Require(!baud.CommitEntry("31251"), "source rate restricts baud entry");
            var invalid = new DigitalSettings(false, DigitalMode.Fsk, 30000, FskOrder: 4, FskSpacing: 46666.6);
            form.RestoreOptions(invalid);
            baud.Value = 30001;
            Require(baud.Value == 30000 && form.Settings.SymbolRate == 30000 && Field<Label>(form, "adjustmentError").Text.Length > 0,
                "invalid baud/channel combination rejected rather than silently changing FSK spacing");
        }
        VerifyDrawing(); VerifyPersistence(); VerifyKnownSignal();
        Console.WriteLine("PASS: absolute baud click/wheel/spin/direct/Apply separation, bounded trajectories, display-only settings, digital window persistence/monitor recovery and known-QPSK live tuning.");
    }
    private static void VerifyDrawing()
    {
        using var view = new ConstellationView { Size = new Size(700, 650) }; _ = view.Handle;
        var points = Enumerable.Range(0, 1024).Select(n => new PointF((float)Math.Cos(n * .21), (float)Math.Sin(n * .21))).ToArray();
        view.Display(points, "trajectory test");
        foreach (int count in DigitalViewSettings.SymbolCounts)
        {
            view.SetPresentation(count, false);
            Require(view.DisplayedPoints.SequenceEqual(points.AsSpan(1024 - count)), "latest N points in chronological order");
            using var off = new Bitmap(view.Width, view.Height); view.DrawToBitmap(off, new Rectangle(Point.Empty, off.Size));
            view.SetPresentation(count, true);
            using var on = new Bitmap(view.Width, view.Height); view.DrawToBitmap(on, new Rectangle(Point.Empty, on.Size));
            int differences = 0;
            for (int y = 0; y < on.Height; y += 2) for (int x = 0; x < on.Width; x += 2) if (on.GetPixel(x, y) != off.GetPixel(x, y)) differences++;
            Require(differences > 20 && view.DisplayedPoints.Length == count, "trajectory visible and uses same bounded point set");
        }
        var clock = Stopwatch.StartNew();
        using var image = new Bitmap(view.Width, view.Height);
        for (int n = 0; n < 100; n++) view.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        Console.WriteLine($"Constellation 1024-point trajectory: {clock.Elapsed.TotalMilliseconds / 100:F2} ms per rendered frame");
    }
    private static void VerifyPersistence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AF-DigitalUX-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.json");
            var settings = new UserSettings { Digital = new(false, DigitalMode.Fsk, 9603, .5, 64, 4, 4, 5300, 137.2),
                DigitalView = new() { WindowBounds = new Rectangle(-1700, 40, 900, 850), SymbolCount = 512, Trajectory = true } };
            SettingsStore.Save(path, settings);
            var loaded = SettingsStore.Load(path, out _); Require(loaded == settings, "all digital settings persist");
            using (var main = new MainForm(path)) Require(main.CaptureSettings().DigitalView == settings.DigitalView, "unopened digital window retains saved geometry");
            using (var form = new DigitalForm(loaded.Digital!, loaded.DigitalView))
            {
                var primary = new Rectangle(0, 0, 1920, 1040); var secondary = new Rectangle(-1920, 0, 1920, 1040);
                form.RestoreWindow([primary, secondary]);
                Require(form.Bounds == settings.DigitalView.WindowBounds && form.CaptureViewSettings() == settings.DigitalView, "restore secondary-monitor position/size and view options");
                Require(form.Settings == settings.Digital && !form.Settings.Enabled, "all analysis settings restored without auto-start");
                form.RestoreWindow([primary]); Require(primary.Contains(form.Bounds), "missing monitor recovers into working area");
                form.Bounds = new Rectangle(120, 60, 1000, 880);
                SettingsStore.Save(path, loaded with { DigitalView = form.CaptureViewSettings() });
            }
            using (var restored = new DigitalForm(loaded.Digital!, SettingsStore.Load(path, out _).DigitalView))
            {
                restored.RestoreWindow([new Rectangle(0, 0, 1920, 1040)]);
                Require(restored.Bounds == new Rectangle(120, 60, 1000, 880), "edited geometry persists across new form instance");
            }
            using (var small = new DigitalForm(loaded.Digital!, loaded.DigitalView))
            {
                var area = new Rectangle(0, 0, 800, 600); small.RestoreWindow([area]);
                Require(area.Contains(small.Bounds), "digital window fits smaller replacement monitor");
            }
            using (var maximized = new DigitalForm(loaded.Digital!, loaded.DigitalView! with { Maximized = true }))
            {
                maximized.RestoreWindow([new Rectangle(0, 0, 1920, 1040)]);
                Require(maximized.WindowState == FormWindowState.Maximized && maximized.CaptureViewSettings().Maximized, "maximize state restored");
            }
            File.WriteAllText(path, "{\"DigitalView\":{\"SymbolCount\":17}}");
            Require(SettingsStore.Load(path, out _).DigitalView!.SymbolCount == 256, "bad count falls back to 256");
            File.WriteAllText(path, "{}"); Require(SettingsStore.Load(path, out _).DigitalView == new DigitalViewSettings(), "old settings compatibility");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void VerifyKnownSignal()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AF-Baud-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var signal = SignalWriter.Generate(new SignalSettings { Duration = 3 }, folder);
            var receive = new ReceiveSettings(250000, Digital: new(true, SymbolRate: 9700));
            var file = new FileReceiver(signal.WavePath, 100000000, receive);
            using var form = new DigitalForm(receive.DigitalOptions);
            try
            {
                Handles(form); form.SetSampleRate(250000);
                Field<CheckBox>(form, "enabled").Checked = true;
                Task pending = Task.CompletedTask;
                form.SettingsChanged += async () => { pending = file.UpdateAsync(file.Frequency, receive with { Digital = form.Settings }); await pending; };
                var baud = Field<DigitTuningControl>(form, "baud"); baud.SelectDigit(6);
                Pump(file.SetPausedAsync(false));
                Until(() => file.Constellation is not null);
                for (int target = 9690; target >= 9600; target -= 10)
                {
                    long previousPosition = file.Position;
                    baud.Wheel(-120); Pump(pending);
                    Require(file.Settings.DigitalOptions.SymbolRate == target && file.Frequency == 100000000 && file.Position >= previousPosition && !file.Paused,
                        "9700 -> 9600 wheel updates live WAV DSP without retuning or rewinding");
                    Until(() => file.Constellation is { Symbols: > 300 });
                    var displayed = file.Constellation!;
                    form.Display(displayed, file.Frequency, file.DigitalFailure, true);
                    foreach (int count in DigitalViewSettings.SymbolCounts)
                    {
                        Field<ComboBox>(form, "symbolCount").SelectedItem = count; Field<CheckBox>(form, "trajectory").Checked = !Field<CheckBox>(form, "trajectory").Checked;
                        Require(Field<ConstellationView>(form, "view").DisplayedPoints.Length == Math.Min(count, displayed.Points.Length), "live appearance uses latest bounded symbols");
                    }
                }
                Until(() => file.Constellation is { Symbols: > 6000 }); Pump(file.SetPausedAsync(true));
                var frame = file.Constellation!;
                double rms = Math.Sqrt(frame.Points.Average(p => p.X * p.X + p.Y * p.Y));
                double evm = Math.Sqrt(frame.Points.Average(p => Math.Pow(Math.Abs(p.X / rms) - Math.Sqrt(.5), 2) + Math.Pow(Math.Abs(p.Y / rms) - Math.Sqrt(.5), 2)));
                Require(evm < .06 && file.DigitalFailure is null && file.Failure is null, "known 9600-baud QPSK returns to four clusters");
                Console.WriteLine($"Live WAV Symbol Rate 9700 -> 9600: final EVM {evm:P3}");
                Field<ComboBox>(form, "symbolCount").SelectedItem = 256; Field<CheckBox>(form, "trajectory").Checked = true;
                form.Display(frame, file.Frequency, null, true);
                using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(Environment.CurrentDirectory, "digital-ux-check.png"));
            }
            finally { Pump(file.StopAsync()); }
        }
        finally { Directory.Delete(folder, true); }
    }
}
