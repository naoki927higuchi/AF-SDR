using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AfSdr;
using AfSdr.Dsp;
using AfSdr.Native;

namespace AfSdr.Checks;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }

    private static void Run(string[] args)
    {
        ConstellationChecks.Run();
        ModulationChecks.Run();
        WaterfallStabilityChecks.Run();
        SettingsChecks.RunAsync().GetAwaiter().GetResult();
        FmChecks.Run();
        if (args.Contains("--audio-smoke"))
        {
            FmChecks.SilentOutputSmoke();
            IqWaveChecks.SilentAudio();
            args = args.Where(a => a != "--audio-smoke").ToArray();
        }
        VerifyFrequencyInput();
        VerifyFftSizes();
        VerifyReceiveSettings();
        foreach (int bin in new[] { -1500, -317, 233, 1600 })
        {
            float[] spectrum = new SpectrumProcessor().Process(Tone(bin));
            int peak = Array.IndexOf(spectrum, spectrum.Max());
            Require(peak == SpectrumProcessor.DefaultSize / 2 + bin, $"FFT frequency sign / shift: {bin}");
            Require(Math.Abs(spectrum[peak] - 20 * Math.Log10(0.5)) < 0.15, "Tone level ~ -6.02 dBFS");
            Require(spectrum[(peak + 100) % spectrum.Length] < -65, "Hann off-tone rejection");
        }
        byte[] dc = Enumerable.Repeat((byte)170, SpectrumProcessor.DefaultSize * 2).ToArray();
        Require(new SpectrumProcessor().Process(dc).All(v => float.IsFinite(v) && v <= -120), "DC removal / finite silence");
        var changing = new SpectrumProcessor();
        changing.Process(Tone(200));
        float[] next = [];
        for (int i = 0; i < 40; i++) next = changing.Process(Tone(-500));
        Require(Array.IndexOf(next, next.Max()) == SpectrumProcessor.DefaultSize / 2 - 500, "Spectrum follows changing input");
        var clock = Stopwatch.StartNew();
        var dsp = new SpectrumProcessor();
        byte[] tone = Tone(300);
        for (int i = 0; i < 500; i++) dsp.Process(tone);
        Console.WriteLine($"500 FFT blocks: {clock.ElapsedMilliseconds} ms (one second of 2.048 MS/s)");

        // Load dependencies and verify every declared export; never enumerate or open hardware.
        string nativePath = Path.Combine(AppContext.BaseDirectory, "rtlsdr.dll");
        IntPtr library = NativeLibrary.Load(nativePath);
        try
        {
            foreach (MethodInfo method in typeof(RtlSdrNative).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                if (method.GetCustomAttribute<DllImportAttribute>() is not null)
                    Require(NativeLibrary.TryGetExport(library, method.Name, out _), "Native export: " + method.Name);
        }
        finally { NativeLibrary.Free(library); }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        PersistenceChecks.Run();
        string? iqOriginal = null;
        int iqArgument = Array.IndexOf(args, "--iq-wave");
        if (iqArgument >= 0) { iqOriginal = args[iqArgument + 1]; args = args.Where((_, n) => n != iqArgument && n != iqArgument + 1).ToArray(); }
        IqWaveChecks.Run(iqOriginal);
        string? goldenFolder = null;
        int goldenArgument = Array.IndexOf(args, "--generator-golden");
        if (goldenArgument >= 0) { goldenFolder = args[goldenArgument + 1]; args = args.Where((_, n) => n != goldenArgument && n != goldenArgument + 1).ToArray(); }
        GeneratorChecks.Run(goldenFolder);
        string? fineFolder = null;
        int fineArgument = Array.IndexOf(args, "--fine-tune-samples");
        if (fineArgument >= 0) { fineFolder = args[fineArgument + 1]; args = args.Where((_, n) => n != fineArgument && n != fineArgument + 1).ToArray(); }
        FineTuneChecks.Run(fineFolder);
        using var form = new MainForm(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        CreateHandles(form);
        VerifySettingsControls(form);
        VerifyWaterfall();
        var view = (SpectrumView)form.Controls[0].Controls.OfType<SpectrumView>().Single();
        for (int i = 0; i < 330; i++) view.DisplayFrame(new SpectrumProcessor().Process(Tone(100 + i * 2)));
        VerifyPointer(view);
        view.RequestedBandwidth = 1_000_000;
        VerifyPointer(view);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        if (args.Length > 0) bitmap.Save(Path.GetFullPath(args[0]));
        using (var digital = new DigitalForm(new DigitalSettings()))
        {
            CreateHandles(digital);
            ((CheckBox)typeof(DigitalForm).GetField("enabled", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(digital)!).Checked = true;
            digital.Display(ConstellationChecks.Preview, 435_000_000, null, true);
            using var digitalBitmap = new Bitmap(digital.Width, digital.Height);
            digital.DrawToBitmap(digitalBitmap, new Rectangle(Point.Empty, digitalBitmap.Size));
            if (args.Length > 0) digitalBitmap.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "constellation-check.png"));
        }
        foreach (var preview in ModulationChecks.Previews)
        {
            using var digital = new DigitalForm(preview.Settings);
            CreateHandles(digital);
            ((CheckBox)typeof(DigitalForm).GetField("enabled", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(digital)!).Checked = true;
            Require(digital.Settings == preview.Settings, "Mode-specific UI settings round trip");
            digital.Display(preview.Frame, 435_000_000, null, true);
            using var digitalBitmap = new Bitmap(digital.Width, digital.Height);
            digital.DrawToBitmap(digitalBitmap, new Rectangle(Point.Empty, digitalBitmap.Size));
            if (args.Length > 0) digitalBitmap.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,
                $"{preview.Settings.Mode}-{preview.Settings.QamOrder}-{preview.Settings.AskOrder}-{preview.Settings.FskOrder}-{preview.Settings.FskSpacing}-check.png"));
        }
        using var plot = new SpectrumView { Size = new Size(1050, 480), Values = new SpectrumProcessor().Process(Tone(233)) };
        using var plotted = new Bitmap(plot.Width, plot.Height);
        plot.DrawToBitmap(plotted, new Rectangle(Point.Empty, plotted.Size));
        if (args.Length > 1) plotted.Save(Path.GetFullPath(args[1]));
        Console.WriteLine("PASS: FM demodulation/audio buffer, receive settings, display bandwidth/cropping, SI input, FFT, native DLL exports, WinForms, waterfall and click tuning. No RTL-SDR hardware opened.");
    }

    private static void VerifyReceiveSettings()
    {
        foreach (uint rate in ReceiveSettings.Rates) new ReceiveSettings(rate).Validate();
        foreach (uint invalid in new uint[] { 0, 225_000, 300_001, 900_000, 3_200_001 })
            Require(!ReceiveSettings.ValidRate(invalid), "Reject unsupported sample rate");
        foreach (uint rate in ReceiveSettings.Rates)
        {
            var full = new DisplayRange(rate, 0);
            Require(full.Bandwidth == rate && full.FirstBin(4096) == 0 && full.BinWidth(4096) == 4096, "Full FFT span");
            var zoom = new DisplayRange(rate, 100_000);
            Require(zoom.FrequencyAt(80_000_000, 0) == 79_950_000 && zoom.FrequencyAt(80_000_000, 1) == 80_050_000,
                "Zoom axis endpoints independent of sample rate");
            Require(Math.Abs(zoom.FirstBin(4096) + zoom.BinWidth(4096) / 2 - 2048) < 1e-8, "Zoom remains centered");
            Require(new DisplayRange(rate, rate + 1000).Bandwidth == rate, "Display span limited to acquired bandwidth");
        }
    }

    private static void VerifySettingsControls(MainForm form)
    {
        ComboBox Field(string name) => (ComboBox)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var rates = Field("sampleRate");
        var spans = Field("bandwidth");
        var gain = Field("rfGain");
        var view = form.Controls[0].Controls.OfType<SpectrumView>().Single();
        var commit = typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < rates.Items.Count; i++)
        {
            rates.SelectedIndex = i;
            commit.Invoke(rates, [EventArgs.Empty]);
            uint rate = ((RateOption)rates.SelectedItem!).Hertz;
            Require(view.SampleRate == rate, "Disconnected rate updates axis preview");
            Require(spans.Items.Cast<RateOption>().All(o => o.Hertz <= rate), "Dropdown excludes excessive spans");
        }
        rates.SelectedIndex = Array.IndexOf(ReceiveSettings.Rates, Receiver.RequestedRate);
        commit.Invoke(rates, [EventArgs.Empty]);
        spans.SelectedIndex = spans.Items.Count - 1;
        Require(view.Range.Bandwidth == 50_000, "Display dropdown changes view span");
        spans.SelectedIndex = 0;
        typeof(MainForm).GetMethod("PopulateGains", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [new int[] { 0, 77, 197, 496 }, (int?)77]);
        Require(gain.Items.Cast<GainOption>().Select(g => g.TenthsDb).SequenceEqual(new[] { 0, 77, 197, 496 }), "Gain choices match device list");
        Require(((GainOption)gain.SelectedItem!).TenthsDb == 77 && new GainOption(77).ToString().Contains("7.7"), "Gain tenths dB conversion and selection");
        gain.Items.Clear();
    }

    private static void VerifyFrequencyInput()
    {
        (string Input, uint Expected)[] cases = [
            ("78.4M", 78_400_000), ("8400k", 8_400_000), ("100M", 100_000_000), ("500k", 500_000),
            ("8400K", 8_400_000), ("78.4m", 78_400_000), ("78.4 MHz", 78_400_000),
            (" 500 KHZ ", 500_000), ("1g", 1_000_000_000), ("78400000", 78_400_000),
            ("78,400,000 Hz", 78_400_000), ("0.000001M", 1), ("4294967295", uint.MaxValue)];
        foreach (var item in cases)
        {
            Require(FrequencyInput.TryParse(item.Input, out uint hz, out _) && hz == item.Expected,
                "Parse " + item.Input);
            Require(FrequencyInput.TryParse(FrequencyInput.Format(hz), out uint roundtrip, out _) && roundtrip == hz,
                "Click/normalized Hz roundtrip");
        }
        foreach (string invalid in new[] { "", "M", "0", "-1M", "4294967296", "4.3G", "1.5", "0.0001k",
                     "78,4M", "1,00", "1e6", "NaN", "100MM", "78.4M garbage", new string('9', 100) })
            Require(!FrequencyInput.TryParse(invalid, out _, out _), "Reject " + invalid);
        var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Require(FrequencyInput.TryParse("78.4M", out uint hz, out _) && hz == 78_400_000, "Culture-independent decimal point");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
    }

    private static void VerifyPointer(SpectrumView view)
    {
        RectangleF bounds = view.PlotBounds;
        var middle = new Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + 20));
        double tolerance = view.Range.Bandwidth / bounds.Width;
        Require(Math.Abs((double)view.FrequencyAt(middle)!.Value - view.CenterFrequency) <= tolerance, "Center cursor frequency");
        var quarter = new Point((int)(bounds.Left + bounds.Width / 4), middle.Y);
        Require(Math.Abs((double)view.FrequencyAt(quarter)!.Value - (view.CenterFrequency - view.Range.Bandwidth / 4)) <= tolerance, "Offset cursor frequency");
        Require(view.FrequencyAt(Point.Empty) is null, "Ignore axis margins");
        uint? selected = null;
        view.FrequencySelected += hz => selected = hz;
        MethodInfo click = typeof(SpectrumView).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        view.CanTune = true;
        click.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, quarter.X, quarter.Y, 0)]);
        Require(selected == view.FrequencyAt(quarter), "Left click requests cursor frequency");
        var waterfallPoint = new Point(quarter.X, (int)(view.WaterfallBounds.Top + 10));
        Require(view.FrequencyAt(waterfallPoint) == view.FrequencyAt(quarter), "Waterfall and spectrum tuning match");
        selected = null;
        click.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, waterfallPoint.X, waterfallPoint.Y, 0)]);
        Require(selected == view.FrequencyAt(quarter), "Waterfall left click tunes station");
        selected = null;
        click.Invoke(view, [new MouseEventArgs(MouseButtons.Right, 1, middle.X, middle.Y, 0)]);
        Require(selected is null, "Right click ignored");
        view.CanTune = false;
        click.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, middle.X, middle.Y, 0)]);
        Require(selected is null, "Busy/disconnected click ignored");
        typeof(SpectrumView).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [new MouseEventArgs(MouseButtons.None, 0, quarter.X, quarter.Y, 0)]);
    }

    private static void VerifyWaterfall()
    {
        using var history = new WaterfallHistory();
        for (int i = 0; i < WaterfallHistory.Capacity + 5; i++)
            history.Add(Enumerable.Repeat(-100f, SpectrumProcessor.DefaultSize).ToArray());
        history.Add(Enumerable.Repeat(-20f, SpectrumProcessor.DefaultSize).ToArray());
        Require(history.Count == WaterfallHistory.Capacity, "Waterfall history bounded");
        using var bitmap = new Bitmap(SpectrumProcessor.DefaultSize, WaterfallHistory.Capacity);
        using var graphics = Graphics.FromImage(bitmap);
        history.Draw(graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        Require(bitmap.GetPixel(100, 0).ToArgb() == WaterfallHistory.LevelColor(-20).ToArgb(), "Newest row at top after ring wrap");
        Require(bitmap.GetPixel(100, 1).ToArgb() == WaterfallHistory.LevelColor(-100).ToArgb(), "Older row below newest");
        history.Clear();
        graphics.Clear(Color.Black);
        history.Draw(graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        Require(history.Count == 0 && bitmap.GetPixel(100, 0).ToArgb() == Color.Black.ToArgb(), "Retune clears history");
        var bands = Enumerable.Repeat(-100f, SpectrumProcessor.DefaultSize).ToArray();
        Array.Fill(bands, -20f, 1536, 1024);
        history.Add(bands);
        history.Draw(graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height), new DisplayRange(2_048_000, 512_000));
        Require(bitmap.GetPixel(100, 0).ToArgb() == WaterfallHistory.LevelColor(-20).ToArgb()
            && bitmap.GetPixel(4000, 0).ToArgb() == WaterfallHistory.LevelColor(-20).ToArgb(), "Waterfall crops same centered FFT band as axis");
    }

    private static void VerifyFftSizes()
    {
        using var history = new WaterfallHistory();
        foreach (int size in SpectrumProcessor.SupportedSizes.Concat(SpectrumProcessor.SupportedSizes.Reverse()))
        {
            new ReceiveSettings(2_048_000, null, size).Validate();
            foreach (int bin in new[] { -233, 317 })
            {
                float[] result = new SpectrumProcessor(size).Process(Tone(bin, size));
                Require(result.Length == size && Array.IndexOf(result, result.Max()) == size / 2 + bin, "Variable FFT sign/size");
                Require(Math.Abs(result.Max() - 20 * Math.Log10(0.5)) < 0.15, "Variable FFT amplitude normalization");
            }
            history.Add(Enumerable.Repeat(-20f, size).ToArray());
            Require(history.Count <= 2, "FFT resize resets waterfall history");
            using var bitmap = new Bitmap(640, 300);
            using var graphics = Graphics.FromImage(bitmap);
            history.Draw(graphics, new RectangleF(0, 0, 640, 300), new DisplayRange(2_048_000, 500_000));
            Require(bitmap.GetPixel(320, 0).ToArgb() == WaterfallHistory.LevelColor(-20).ToArgb(), "Resized waterfall rendering");
        }
        bool rejected = false;
        try { _ = new SpectrumProcessor(3000); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "Invalid FFT size rejected");
    }

    private static byte[] Tone(int bin, int size = SpectrumProcessor.DefaultSize)
    {
        var iq = new byte[size * 2];
        for (int i = 0; i < size; i++)
        {
            double phase = 2 * Math.PI * bin * i / size;
            iq[2 * i] = (byte)Math.Round(127.5 + 64 * Math.Cos(phase));
            iq[2 * i + 1] = (byte)Math.Round(127.5 + 64 * Math.Sin(phase));
        }
        return iq;
    }
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }

    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls) CreateHandles(child);
        control.PerformLayout();
    }
}
