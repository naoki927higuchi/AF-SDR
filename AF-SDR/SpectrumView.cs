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
    internal uint? TuneFrequency { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint CenterFrequency { get; set; } = 80_000_000;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint SampleRate { get; set; } = Receiver.RequestedRate;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint RequestedBandwidth { get; set; }
    internal DisplayRange Range => new(SampleRate, RequestedBandwidth);
    private float minimumLevel = -120, maximumLevel = 0;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal uint RxBandwidth { get; set; } = 200_000;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowRxBandwidth { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool FmEnabled { get; set; }

    internal void SetLevels(float lower, float upper)
    {
        waterfall.SetLevels(lower, upper);
        minimumLevel = lower; maximumLevel = upper;
        Invalidate();
    }

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
            return new RectangleF(62 * scale, 60 * scale, Width - 175 * scale,
                Math.Max(40 * scale, (Height - 155 * scale) * 0.52f));
        }
    }

    internal uint? FrequencyAt(Point point)
    {
        RectangleF plot = PlotBounds;
        if (plot.Width <= 0 || (!plot.Contains(point) && !WaterfallBounds.Contains(point))) return null;
        double hz = Range.FrequencyAt(CenterFrequency, (point.X - plot.Left) / plot.Width);
        return hz >= 1 && hz <= uint.MaxValue ? (uint)Math.Round(hz) : null;
    }

    internal RectangleF WaterfallBounds
    {
        get
        {
            float scale = DeviceDpi / 96f;
            var plot = PlotBounds;
            return new RectangleF(plot.Left, plot.Bottom + 76 * scale, plot.Width,
                Math.Max(1, Height - plot.Bottom - 88 * scale));
        }
    }

    internal void DisplayFrame(float[]? frame)
    {
        if (ReferenceEquals(lastFrame, frame)) return;
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
        g.DrawString($"レベル (dBFS / FFT bin)   中心 {FrequencyInput.Format(CenterFrequency)}   RxBW {FrequencyInput.Format(RxBandwidth)} Hz ({(FmEnabled ? "FM ON" : "FM OFF")})", Font, ink, plot.Left, 10 * scale);
        string cursorText = pointer is Point position && FrequencyAt(position) is uint hz
            ? $"カーソル: {hz:N0} Hz  ({hz / 1e6:F6} MHz)  |  左クリックで選局"
            : "スペクトラム／ウォーターフォールを左クリックで選局";
        g.DrawString(cursorText, Font, ink, plot.Left, 32 * scale);
        for (int tick = 0; tick <= 6; tick++)
        {
            float db = maximumLevel - tick * (maximumLevel - minimumLevel) / 6;
            float y = plot.Top + tick * plot.Height / 6;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(db.ToString("0.#"), Font, ink, 6 * scale, y - 8 * scale);
        }
        for (int i = 0; i <= 8; i++)
        {
            float x = plot.Left + plot.Width * i / 8;
            g.DrawLine(i == 4 ? center : grid, x, plot.Top, x, plot.Bottom);
            double mhz = Range.FrequencyAt(CenterFrequency, i / 8.0) / 1e6;
            g.DrawString(mhz.ToString("F3"), Font, ink, x, plot.Bottom + 9 * scale, centered);
        }
        g.DrawString("周波数 (MHz)", Font, ink, plot.Left + plot.Width / 2, plot.Bottom + 29 * scale, centered);
        var water = WaterfallBounds;
        g.DrawString($"ウォーターフォール  ↓ 時間（最新が上）    色: {minimumLevel:0} ～ {maximumLevel:0} dBFS", Font, ink,
            plot.Left, plot.Bottom + 53 * scale);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        waterfall.Draw(g, water, Range);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.DrawRectangle(grid, water.X, water.Y, water.Width, water.Height);
        var bar = new RectangleF(water.Right + 12 * scale, water.Top, 16 * scale, water.Height);
        for (int y = 0; y < (int)bar.Height; y++)
        {
            float db = maximumLevel - y / Math.Max(1, bar.Height - 1) * (maximumLevel - minimumLevel);
            using var brush = new SolidBrush(WaterfallHistory.LevelColor(db, minimumLevel, maximumLevel));
            g.FillRectangle(brush, bar.X, bar.Y + y, bar.Width, 1);
        }
        for (int tick = 0; tick <= 4; tick++)
        {
            float db = maximumLevel - tick * (maximumLevel - minimumLevel) / 4;
            g.DrawString(db.ToString("0.#"), Font, ink, bar.Right + 4 * scale,
                Math.Clamp(bar.Top + tick * bar.Height / 4 - 8 * scale, bar.Top, bar.Bottom - 16 * scale));
        }
        if (ShowRxBandwidth)
        {
            float middle = plot.Left + plot.Width * (float)(0.5 + ((double)(TuneFrequency ?? CenterFrequency) - CenterFrequency) / Range.Bandwidth);
            float halfWidth = plot.Width * RxBandwidth / Range.Bandwidth / 2;
            using var shade = new SolidBrush(Color.FromArgb(22, 92, 162, 245));
            using var boundary = new Pen(Color.FromArgb(155, 194, 245)) { DashStyle = DashStyle.Dash };
            using var tuning = new Pen(Color.FromArgb(100, 190, 250), 1.5f);
            foreach (var area in new[] { plot, water })
            {
                g.SetClip(area);
                if (area == plot) g.FillRectangle(shade, middle - halfWidth, area.Top, halfWidth * 2, area.Height);
                g.DrawLine(tuning, middle, area.Top, middle, area.Bottom);
                if (middle - halfWidth >= area.Left) g.DrawLine(boundary, middle - halfWidth, area.Top, middle - halfWidth, area.Bottom);
                if (middle + halfWidth <= area.Right) g.DrawLine(boundary, middle + halfWidth, area.Top, middle + halfWidth, area.Bottom);
                g.ResetClip();
            }
        }
        if (pointer is Point cursor && FrequencyAt(cursor).HasValue)
        {
            using var marker = new Pen(Color.FromArgb(245, 220, 110)) { DashStyle = DashStyle.Dash };
            g.DrawLine(marker, cursor.X, plot.Top, cursor.X, plot.Bottom);
            g.DrawLine(marker, cursor.X, water.Top, cursor.X, water.Bottom);
        }
        float[]? values = Values;
        if (values is null)
        {
            g.DrawString("接続・再生するとスペクトラムを表示します", Font, ink,
                plot.Left + plot.Width / 2, plot.Top + plot.Height / 2, centered);
            return;
        }
        // Preserve narrow peaks when there are more FFT bins than horizontal pixels.
        double firstBin = Range.FirstBin(values.Length), binWidth = Range.BinWidth(values.Length);
        int columns = Math.Max(2, Math.Min((int)Math.Ceiling(binWidth), (int)plot.Width));
        var points = new PointF[columns];
        for (int x = 0; x < columns; x++)
        {
            int first = Math.Clamp((int)Math.Floor(firstBin + x * binWidth / columns), 0, values.Length - 1);
            int end = Math.Clamp((int)Math.Ceiling(firstBin + (x + 1) * binWidth / columns), first + 1, values.Length);
            float peak = -140;
            for (int bin = first; bin < end; bin++) peak = Math.Max(peak, values[bin]);
            points[x] = new PointF(plot.Left + x * plot.Width / (columns - 1),
                plot.Top + (maximumLevel - Math.Clamp(peak, minimumLevel, maximumLevel)) / (maximumLevel - minimumLevel) * plot.Height);
        }
        g.SetClip(plot);
        g.DrawLines(line, points);
        g.ResetClip();
    }
}
