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
        VerifyFrequencyInput();
        foreach (int bin in new[] { -1500, -317, 233, 1600 })
        {
            float[] spectrum = new SpectrumProcessor().Process(Tone(bin));
            int peak = Array.IndexOf(spectrum, spectrum.Max());
            Require(peak == SpectrumProcessor.Size / 2 + bin, $"FFT frequency sign / shift: {bin}");
            Require(Math.Abs(spectrum[peak] - 20 * Math.Log10(0.5)) < 0.15, "Tone level ~ -6.02 dBFS");
            Require(spectrum[(peak + 100) % spectrum.Length] < -65, "Hann off-tone rejection");
        }
        byte[] dc = Enumerable.Repeat((byte)170, SpectrumProcessor.Size * 2).ToArray();
        Require(new SpectrumProcessor().Process(dc).All(v => float.IsFinite(v) && v <= -120), "DC removal / finite silence");
        var changing = new SpectrumProcessor();
        changing.Process(Tone(200));
        float[] next = [];
        for (int i = 0; i < 40; i++) next = changing.Process(Tone(-500));
        Require(Array.IndexOf(next, next.Max()) == SpectrumProcessor.Size / 2 - 500, "Spectrum follows changing input");
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
        using var form = new MainForm();
        CreateHandles(form);
        VerifyWaterfall();
        var view = (SpectrumView)form.Controls[0].Controls.OfType<SpectrumView>().Single();
        for (int i = 0; i < 330; i++) view.DisplayFrame(new SpectrumProcessor().Process(Tone(100 + i * 2)));
        VerifyPointer(view);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        if (args.Length > 0) bitmap.Save(Path.GetFullPath(args[0]));
        using var plot = new SpectrumView { Size = new Size(1050, 480), Values = new SpectrumProcessor().Process(Tone(233)) };
        using var plotted = new Bitmap(plot.Width, plot.Height);
        plot.DrawToBitmap(plotted, new Rectangle(Point.Empty, plotted.Size));
        if (args.Length > 1) plotted.Save(Path.GetFullPath(args[1]));
        Console.WriteLine("PASS: SI frequency input, DSP, native DLL exports, WinForms, waterfall ring/clear, cursor mapping and click tuning. No hardware opened.");
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
        double tolerance = view.SampleRate / bounds.Width;
        Require(Math.Abs((double)view.FrequencyAt(middle)!.Value - view.CenterFrequency) <= tolerance, "Center cursor frequency");
        var quarter = new Point((int)(bounds.Left + bounds.Width / 4), middle.Y);
        Require(Math.Abs((double)view.FrequencyAt(quarter)!.Value - (view.CenterFrequency - view.SampleRate / 4)) <= tolerance, "Offset cursor frequency");
        Require(view.FrequencyAt(Point.Empty) is null, "Ignore axis margins");
        uint? selected = null;
        view.FrequencySelected += hz => selected = hz;
        MethodInfo click = typeof(SpectrumView).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        view.CanTune = true;
        click.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, quarter.X, quarter.Y, 0)]);
        Require(selected == view.FrequencyAt(quarter), "Left click requests cursor frequency");
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
            history.Add(Enumerable.Repeat(-100f, SpectrumProcessor.Size).ToArray());
        history.Add(Enumerable.Repeat(-20f, SpectrumProcessor.Size).ToArray());
        Require(history.Count == WaterfallHistory.Capacity, "Waterfall history bounded");
        using var bitmap = new Bitmap(SpectrumProcessor.Size, WaterfallHistory.Capacity);
        using var graphics = Graphics.FromImage(bitmap);
        history.Draw(graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        Require(bitmap.GetPixel(100, 0).ToArgb() == WaterfallHistory.LevelColor(-20).ToArgb(), "Newest row at top after ring wrap");
        Require(bitmap.GetPixel(100, 1).ToArgb() == WaterfallHistory.LevelColor(-100).ToArgb(), "Older row below newest");
        history.Clear();
        graphics.Clear(Color.Black);
        history.Draw(graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        Require(history.Count == 0 && bitmap.GetPixel(100, 0).ToArgb() == Color.Black.ToArgb(), "Retune clears history");
    }

    private static byte[] Tone(int bin)
    {
        var iq = new byte[SpectrumProcessor.Size * 2];
        for (int i = 0; i < SpectrumProcessor.Size; i++)
        {
            double phase = 2 * Math.PI * bin * i / SpectrumProcessor.Size;
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
