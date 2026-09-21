using System.ComponentModel;
using AfSdr.Dsp;
namespace AfSdr;

internal sealed class ConstellationView : Control
{
    private PointF[] points = [];
    private float[] trace = [];
    private DigitalSettings settings = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Caption { get; set; } = "デジタル信号表示 OFF";
    internal ConstellationView()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        BackColor = Color.FromArgb(13, 22, 32); ForeColor = Color.FromArgb(170, 190, 210);
        Dock = DockStyle.Fill;
    }
    internal void Display(PointF[]? values, string caption, DigitalSettings? configuration = null, float[]? waveform = null)
    {
        points = values ?? []; trace = waveform ?? []; settings = configuration ?? new(); Caption = caption; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        using var text = new SolidBrush(ForeColor);
        using var grid = new Pen(Color.FromArgb(50, 66, 85));
        using var reference = new Pen(Color.FromArgb(110, 120, 130));
        using var dot = new SolidBrush(Color.FromArgb(155, 73, 218, 183));
        using var wave = new Pen(Color.FromArgb(73, 218, 183));
        g.DrawString(Caption, Font, text, new RectangleF(10, 8, Width - 20, 60 * scale));
        bool linear = settings.FrequencyMode || settings.Mode == DigitalMode.Ask;
        float side = Math.Min(Width - 100 * scale, Height - 145 * scale);
        if (side < 20) return;
        var plot = linear ? new RectangleF(65 * scale, 85 * scale, Width - 110 * scale, Math.Max(25, (Height - 210 * scale) * 0.42f))
            : new RectangleF((Width - side) / 2, 85 * scale, side, side);
        double left = settings.FrequencyMode ? -settings.Spacing * settings.Tones / 2 : settings.Mode == DigitalMode.Ask ? -0.1 : -2;
        double right = settings.FrequencyMode ? settings.Spacing * settings.Tones / 2 : 2;
        float X(double value) => plot.Left + (float)((value - left) / (right - left)) * plot.Width;
        float Y(double value) => plot.Top + plot.Height * (float)(0.5 - value / 4);
        g.DrawRectangle(grid, plot.X, plot.Y, plot.Width, plot.Height);
        g.DrawLine(grid, plot.Left, Y(0), plot.Right, Y(0));
        g.DrawLine(grid, X(0), plot.Top, X(0), plot.Bottom);
        IEnumerable<System.Numerics.Complex> Guides()
        {
            if (settings.FrequencyMode)
                return Enumerable.Range(0, settings.Tones).Select(k => new System.Numerics.Complex((k - (settings.Tones - 1) / 2.0) * settings.Spacing, 0));
            if (settings.Mode == DigitalMode.Ask)
                return Enumerable.Range(0, settings.AskOrder).Select(k => new System.Numerics.Complex(k / Math.Sqrt((settings.AskOrder - 1) * (2 * settings.AskOrder - 1) / 6.0), 0));
            if (settings.Mode == DigitalMode.Qam)
            {
                int size = (int)Math.Sqrt(settings.QamOrder); double norm = Math.Sqrt(2.0 * (settings.QamOrder - 1) / 3);
                return from i in Enumerable.Range(0, size) from q in Enumerable.Range(0, size)
                    select new System.Numerics.Complex((2 * i - size + 1) / norm, (2 * q - size + 1) / norm);
            }
            if (settings.Mode == DigitalMode.Bpsk) return [new(-1, 0), new(1, 0)];
            if (settings.Mode is DigitalMode.Qpsk or DigitalMode.Pi4Qpsk)
                return Enumerable.Range(0, 4).Select(k => System.Numerics.Complex.FromPolarCoordinates(1, Math.PI / 4 + k * Math.PI / 2));
            return [];
        }
        foreach (var guide in Guides())
        {
            float x = X(guide.Real), y = Y(guide.Imaginary);
            g.DrawLine(reference, x - 4 * scale, y, x + 4 * scale, y);
            g.DrawLine(reference, x, y - 4 * scale, x, y + 4 * scale);
        }
        var state = g.Save(); g.SetClip(plot);
        foreach (var p in points)
            if (float.IsFinite(p.X) && float.IsFinite(p.Y)) g.FillEllipse(dot, X(p.X) - scale, Y(p.Y) - scale, 2 * scale, 2 * scale);
        g.Restore(state);
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = left + (right - left) * tick / 4;
            g.DrawString(value.ToString(settings.FrequencyMode ? "0" : "0.#"), Font, text, X(value) - 12 * scale, plot.Bottom + 2 * scale);
        }
        string axis = settings.FrequencyMode ? "周波数偏移 (Hz) — 中心周波数からの差"
            : settings.Mode == DigitalMode.Ask ? "包絡線振幅 / RMS（0＝OFF）"
            : settings.Mode == DigitalMode.Pi4Qpsk ? "cos(Δφ) / sin(Δφ) — 差動位相"
            : "正規化 I / Q  ±2";
        g.DrawString(axis, Font, text, plot.Left, plot.Bottom + 24 * scale);
        if (!linear) g.DrawString(settings.Mode == DigitalMode.Pi4Qpsk ? "sin Δφ" : "Q", Font, text, plot.Left + plot.Width / 2, plot.Top - 25 * scale);
        else
        {
            var time = new RectangleF(plot.Left, plot.Bottom + 75 * scale, plot.Width, Math.Max(10, Height - plot.Bottom - 117 * scale));
            g.DrawRectangle(grid, time.X, time.Y, time.Width, time.Height);
            g.DrawString(settings.FrequencyMode ? "周波数偏移の時間波形 (Hz)" : "包絡線の時間波形", Font, text, time.Left, time.Top - 23 * scale);
            double low = settings.FrequencyMode ? left : 0, high = right;
            if (trace.Length > 1)
            {
                var waveform = trace.Select((value, n) => new PointF(time.Left + time.Width * n / 127,
                    time.Bottom - time.Height * (float)((value - low) / (high - low)))).ToArray();
                state = g.Save(); g.SetClip(time); g.DrawLines(wave, waveform); g.Restore(state);
            }
            g.DrawString($"{high:0.#}", Font, text, 3 * scale, time.Top);
            g.DrawString($"{low:0.#}", Font, text, 3 * scale, time.Bottom - 18 * scale);
            g.DrawString("過去 ← 約16シンボル → 最新（公称baud換算）", Font, text, time.Left, time.Bottom + 4 * scale);
        }
    }
}
