using System.Reflection;
using AfSdr.Dsp;
using AfSignalGenerator;

namespace AfSdr.Checks;

internal static class FineTuneChecks
{
    private static void Require(bool pass, string message) { if (!pass) throw new Exception("Fine Tune: " + message); }
    internal static void Run(string? sampleFolder = null)
    {
        // An independent accumulated phase reference, including a nonzero -> zero step.
        var shifter = new FineFrequencyShifter(250000);
        double phase = 0;
        foreach (double offset in new[] { 100.1, -83.7, 0, 10000.0, -10000.0 })
        {
            shifter.SetOffset(offset);
            for (int n = 0; n < 4567; n++)
            {
                float i = .3f, q = -.2f;
                shifter.Shift(ref i, ref q);
                Require(Math.Abs(i - (.3f * Math.Cos(phase) + -.2f * Math.Sin(phase))) < 1e-7
                    && Math.Abs(q - (-.2f * Math.Cos(phase) - .3f * Math.Sin(phase))) < 1e-7, "sample phase continuous at changes and zero offset");
                phase += 2 * Math.PI * offset / 250000;
            }
        }
        using (var control = new DigitTuningControl(-10000, 10000))
        {
            int changes = 0; control.ValueChanged += (_, _) => changes++;
            control.SelectDigit(3); control.Wheel(120); Require(control.Value == 100, "100 Hz wheel digit");
            control.Adjust(-1); Require(control.Value == 0 && changes == 2, "spin uses same step/event");
            control.SelectDigit(5); control.Wheel(240); Require(control.Value == 2, "1 Hz wheel / multiple notches");
            control.SelectDigit(6); control.Wheel(-120); Require(control.Value == 1.9m, "0.1 Hz precision");
            control.Wheel(60); Require(control.Value == 1.9m, "high resolution partial wheel");
            control.Wheel(60); Require(control.Value == 2, "accumulated partial wheel");
            Require(control.CommitEntry("-99.9") && control.Value == -99.9m, "signed direct input");
            Require(!control.CommitEntry("NaN") && !control.CommitEntry("10000.1") && !control.CommitEntry("0.01") && control.Value == -99.9m, "invalid direct input rejected");
            control.Value = 10000; control.Adjust(1); Require(control.Value == 10000, "upper bound");
            control.Value = 0;
            var surface = Field<Control>(control, "surface");
            int cellWidth = Math.Max(12, (int)Math.Ceiling(surface.Font.SizeInPoints * control.DeviceDpi / 72 * .7));
            // Exercise the real mouse and button handlers, not only their arithmetic helper.
            typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface,
                [new MouseEventArgs(MouseButtons.Left, 1, 3 + 4 * cellWidth + cellWidth / 2, 10, 0)]);
            typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface,
                [new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120)]);
            Require(control.Value == 100 && control.SelectedStep == 100, "left click selects 100 Hz and actual wheel handler adjusts");
            var down = control.Controls[0].Controls.OfType<Button>().Single(b => b.Text == "▼");
            down.PerformClick(); Require(control.Value == 0, "actual spin button handler adjusts selected digit");
        }
        using (var form = new DigitalForm(new(FineFrequencyOffset: 123.4)))
        {
            var fine = Field<DigitTuningControl>(form, "fine");
            var baud = Field<NumericUpDown>(form, "baud");
            baud.Value = 4800; int events = 0;
            form.SettingsChanged += () => { events++; return Task.CompletedTask; };
            fine.SelectDigit(6); fine.Adjust(1);
            Require(events == 1 && form.Settings.FineFrequencyOffset == 123.5 && form.Settings.SymbolRate == 9600, "immediate fine event does not apply draft baud");
            form.RestoreOptions(form.Settings with { FineFrequencyOffset = -50 });
            Require(fine.Value == -50 && events == 1, "rollback/restore does not send another update");
            void Handles(Control c) { _ = c.Handle; foreach (Control child in c.Controls) Handles(child); c.PerformLayout(); }
            Handles(form);
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(Environment.CurrentDirectory, "fine-tune-check.png"));
        }
        string folder = Path.Combine(Path.GetTempPath(), "AF-Fine-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string settingsPath = Path.Combine(folder, "settings.json");
            AfSdr.SettingsStore.Save(settingsPath, new UserSettings { Digital = new(Enabled: true, FineFrequencyOffset: -123.4) });
            var saved = AfSdr.SettingsStore.Load(settingsPath, out _);
            Require(saved.Digital!.FineFrequencyOffset == -123.4 && !saved.Digital.Enabled, "persist value but never auto-start");
            foreach (double offset in new[] { 0.0, 100.0 })
            {
                var result = SignalWriter.Generate(new SignalSettings { Duration = 1.2, FrequencyOffset = offset }, folder);
                foreach (double correction in offset == 0 ? new[] { 0.0 } : new[] { 0.0, 99.0, 100.0, 101.0 })
                {
                    using var reader = new IqWaveReader(result.WavePath);
                    var dsp = new ConstellationProcessor(250000, new(true, FineFrequencyOffset: correction));
                    float[] block;
                    while ((block = reader.Read(2347)).Length != 0) dsp.Process(block);
                    var frame = dsp.Snapshot(0);
                    double rms = Math.Sqrt(frame.Points.Average(p => p.X * p.X + p.Y * p.Y));
                    double evm = Math.Sqrt(frame.Points.Average(p => Math.Pow(Math.Abs(p.X / rms) - Math.Sqrt(.5), 2) + Math.Pow(Math.Abs(p.Y / rms) - Math.Sqrt(.5), 2)));
                    Console.WriteLine($"Fine Tune QPSK: input {offset}, manual {correction}, residual {frame.FrequencyErrorHz:F4} Hz, EVM {evm:P3}");
                    Require(Math.Abs(frame.FrequencyErrorHz - (offset - correction)) < .3 && evm < .06, "known offset sign, residual and four clusters");
                }
                // Change correction on an existing stream. No new processor or data rewind.
                using (var reader = new IqWaveReader(result.WavePath))
                {
                    var dsp = new ConstellationProcessor(250000, new(true));
                    for (int n = 0; n < 10; n++) dsp.Process(reader.Read(10000)); long before = dsp.Snapshot(0).Symbols;
                    dsp.SetFineFrequencyOffset(offset);
                    float[] block; while ((block = reader.Read(1231)).Length > 0) dsp.Process(block);
                    Require(dsp.Snapshot(0).Symbols > before && Math.Abs(dsp.FrequencyErrorHz) < .3, "live correction converges without resetting timing/count");
                }
                Task.Run(() => VerifyFileAsync(result.WavePath)).GetAwaiter().GetResult();
            }
            if (sampleFolder is not null)
            {
                Directory.CreateDirectory(sampleFolder);
                foreach (double offset in new[] { 0.0, 100.0 })
                {
                    string target = Path.Combine(sampleFolder, offset == 0 ? "Golden" : "OffsetPlus100Hz"); Directory.CreateDirectory(target);
                    SignalWriter.Generate(new SignalSettings { FrequencyOffset = offset }, target);
                }
            }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS: Fine Tune phase continuity, digits/wheel/spin/direct input, persistence, Golden/100 Hz and file state preservation.");
    }
    private static async Task VerifyFileAsync(string path)
    {
        var settings = new ReceiveSettings(250000, Digital: new(true));
        var file = new FileReceiver(path, 100000000, settings);
        try
        {
            await file.SetPausedAsync(false);
            for (int n = 0; n < 500 && file.Constellation is null; n++) await Task.Delay(10);
            await file.SetPausedAsync(true);
            Require(file.Constellation is not null && file.Failure is null, "actual file playback pipeline");
            var dsp = Field<ConstellationProcessor>(file, "digital"); var spectrum = file.Spectrum; long position = file.Position;
            var result = await file.UpdateAsync(file.Frequency, settings with { Digital = new(true, FineFrequencyOffset: 100) });
            Require(result == new SettingsChange(false, false, false, false) && ReferenceEquals(dsp, Field<ConstellationProcessor>(file, "digital"))
                && ReferenceEquals(spectrum, file.Spectrum) && position == file.Position && file.Frequency == 100000000,
                "file correction keeps parent tuning, position, FFT and DSP instance");
            await file.UpdateAsync(100118000, settings);
            bool rejected = false;
            try { await file.UpdateAsync(100118000, settings with { Digital = new(true, FineFrequencyOffset: 1000) }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && file.Settings.DigitalOptions.FineFrequencyOffset == 0, "fine-shifted passband outside recording rejected transactionally");
        }
        finally { await file.StopAsync(); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
}
