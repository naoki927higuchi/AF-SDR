using System.ComponentModel;

namespace AfSdr;

internal sealed class ConstellationView : Control
{
    private PointF[] points = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Caption { get; set; } = "コンスタレーション OFF";
    internal ConstellationView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(13, 22, 32);
        ForeColor = Color.FromArgb(170, 190, 210);
        Dock = DockStyle.Fill;
    }
    internal void Display(PointF[]? values, string caption)
    {
        points = values ?? [];
        Caption = caption;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        using var text = new SolidBrush(ForeColor);
        using var grid = new Pen(Color.FromArgb(50, 66, 85));
        using var dot = new SolidBrush(Color.FromArgb(155, 73, 218, 183));
        g.DrawString(Caption, Font, text, new RectangleF(10, 8, Width - 20, 60 * scale));
        float side = Math.Min(Width - 60 * scale, Height - 120 * scale);
        if (side < 20) return;
        var plot = new RectangleF((Width - side) / 2, 80 * scale, side, side);
        g.DrawRectangle(grid, plot.X, plot.Y, side, side);
        g.DrawLine(grid, plot.Left, plot.Top + side / 2, plot.Right, plot.Top + side / 2);
        g.DrawLine(grid, plot.Left + side / 2, plot.Top, plot.Left + side / 2, plot.Bottom);
        g.DrawString("I", Font, text, plot.Right + 4, plot.Top + side / 2);
        g.DrawString("Q", Font, text, plot.Left + side / 2, plot.Top - 22 * scale);
        var state = g.Save();
        g.SetClip(plot);
        foreach (var p in points)
            if (float.IsFinite(p.X) && float.IsFinite(p.Y))
                g.FillEllipse(dot, plot.Left + side * (0.5f + p.X / 4) - scale,
                    plot.Top + side * (0.5f - p.Y / 4) - scale, 2 * scale, 2 * scale);
        g.Restore(state);
        g.DrawString("正規化 I/Q  ±2", Font, text, plot.Left, plot.Bottom + 8 * scale);
    }
}
