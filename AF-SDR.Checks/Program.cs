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
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        if (args.Length > 0) bitmap.Save(Path.GetFullPath(args[0]));
        using var plot = new SpectrumView { Size = new Size(1050, 480), Values = new SpectrumProcessor().Process(Tone(233)) };
        using var plotted = new Bitmap(plot.Width, plot.Height);
        plot.DrawToBitmap(plotted, new Rectangle(Point.Empty, plotted.Size));
        if (args.Length > 1) plotted.Save(Path.GetFullPath(args[1]));
        Console.WriteLine("PASS: DSP, native DLL exports, WinForms and spectrum rendering. No hardware opened.");
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
