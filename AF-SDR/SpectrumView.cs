using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AfSdr;

internal sealed class SpectrumView : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float[]? Values { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint CenterFrequency { get; set; } = 80_000_000;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint SampleRate { get; set; } = Receiver.RequestedRate;

    public SpectrumView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(13, 22, 32);
        ForeColor = Color.FromArgb(170, 190, 210);
        Dock = DockStyle.Fill;
        MinimumSize = new Size(480, 240);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        float scale = DeviceDpi / 96f;
        var plot = new RectangleF(62 * scale, 38 * scale, Width - 88 * scale, Height - 90 * scale);
        if (plot.Width <= 0 || plot.Height <= 0) return;
        using var grid = new Pen(Color.FromArgb(38, 53, 69));
        using var center = new Pen(Color.FromArgb(86, 107, 132)) { DashStyle = DashStyle.Dash };
        using var ink = new SolidBrush(ForeColor);
        using var line = new Pen(Color.FromArgb(73, 218, 183), 1.3f * scale);
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("レベル (dBFS / FFT bin)", Font, ink, plot.Left, 10 * scale);
        for (int db = -120; db <= 0; db += 20)
        {
            float y = plot.Top + -db / 120f * plot.Height;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(db.ToString(), Font, ink, 10 * scale, y - 8 * scale);
        }
        for (int i = 0; i <= 8; i++)
        {
            float x = plot.Left + plot.Width * i / 8;
            g.DrawLine(i == 4 ? center : grid, x, plot.Top, x, plot.Bottom);
            double mhz = (CenterFrequency + (i / 8.0 - 0.5) * SampleRate) / 1e6;
            g.DrawString(mhz.ToString("F3"), Font, ink, x, plot.Bottom + 9 * scale, centered);
        }
        g.DrawString("周波数 (MHz)", Font, ink, plot.Left + plot.Width / 2, plot.Bottom + 29 * scale, centered);
        float[]? values = Values;
        if (values is null)
        {
            g.DrawString("接続すると受信スペクトラムを表示します", Font, ink,
                plot.Left + plot.Width / 2, plot.Top + plot.Height / 2, centered);
            return;
        }
        // Preserve narrow peaks when there are more FFT bins than horizontal pixels.
        int columns = Math.Min(values.Length, Math.Max(2, (int)plot.Width));
        var points = new PointF[columns];
        for (int x = 0; x < columns; x++)
        {
            int first = x * values.Length / columns;
            int end = (x + 1) * values.Length / columns;
            float peak = -140;
            for (int bin = first; bin < end; bin++) peak = Math.Max(peak, values[bin]);
            points[x] = new PointF(plot.Left + x * plot.Width / (columns - 1),
                plot.Top + -Math.Clamp(peak, -120, 0) / 120 * plot.Height);
        }
        g.SetClip(plot);
        g.DrawLines(line, points);
        g.ResetClip();
    }
}
