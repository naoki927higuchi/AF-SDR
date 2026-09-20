using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AfSdr;

internal sealed class SpectrumView : Control
{
    private readonly WaterfallHistory waterfall = new();
    private Point? pointer;
    private float[]? lastFrame;
    internal event Action<uint>? FrequencySelected;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool CanTune { get; set; }
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
        MinimumSize = new Size(480, 360);
    }

    internal RectangleF PlotBounds
    {
        get
        {
            float scale = DeviceDpi / 96f;
            return new RectangleF(62 * scale, 60 * scale, Width - 88 * scale,
                Math.Max(40 * scale, (Height - 155 * scale) * 0.52f));
        }
    }

    internal uint? FrequencyAt(Point point)
    {
        RectangleF plot = PlotBounds;
        if (plot.Width <= 0 || !plot.Contains(point)) return null;
        double hz = CenterFrequency + ((point.X - plot.Left) / plot.Width - 0.5) * SampleRate;
        return hz >= 1 && hz <= uint.MaxValue ? (uint)Math.Round(hz) : null;
    }

    internal void DisplayFrame(float[]? frame)
    {
        Values = frame;
        if (frame is not null && !ReferenceEquals(lastFrame, frame)) waterfall.Add(frame);
        lastFrame = frame;
        Invalidate();
    }

    internal void Clear()
    {
        Values = lastFrame = null;
        pointer = null;
        waterfall.Clear();
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        pointer = e.Location;
        Cursor = FrequencyAt(e.Location).HasValue ? Cursors.Cross : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        pointer = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (CanTune && Values is not null && e.Button == MouseButtons.Left && FrequencyAt(e.Location) is uint hz)
            FrequencySelected?.Invoke(hz);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) waterfall.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        float scale = DeviceDpi / 96f;
        var plot = PlotBounds;
        if (plot.Width <= 0 || plot.Height <= 0) return;
        using var grid = new Pen(Color.FromArgb(38, 53, 69));
        using var center = new Pen(Color.FromArgb(86, 107, 132)) { DashStyle = DashStyle.Dash };
        using var ink = new SolidBrush(ForeColor);
        using var line = new Pen(Color.FromArgb(73, 218, 183), 1.3f * scale);
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("レベル (dBFS / FFT bin)", Font, ink, plot.Left, 10 * scale);
        string cursorText = pointer is Point position && FrequencyAt(position) is uint hz
            ? $"カーソル: {hz:N0} Hz  ({hz / 1e6:F6} MHz)  |  左クリックで中心に設定"
            : "スペクトラム上で周波数を確認 / 左クリックで中心周波数を変更";
        g.DrawString(cursorText, Font, ink, plot.Left, 32 * scale);
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
        var water = new RectangleF(plot.Left, plot.Bottom + 76 * scale, plot.Width,
            Math.Max(1, Height - plot.Bottom - 88 * scale));
        g.DrawString("ウォーターフォール  ↓ 時間（最新が上）    弱 −110 → −10 dBFS 強", Font, ink,
            plot.Left, plot.Bottom + 53 * scale);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        waterfall.Draw(g, water);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.DrawRectangle(grid, water.X, water.Y, water.Width, water.Height);
        if (pointer is Point cursor && FrequencyAt(cursor).HasValue)
        {
            using var marker = new Pen(Color.FromArgb(245, 220, 110)) { DashStyle = DashStyle.Dash };
            g.DrawLine(marker, cursor.X, plot.Top, cursor.X, plot.Bottom);
            g.DrawLine(marker, cursor.X, water.Top, cursor.X, water.Bottom);
        }
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
