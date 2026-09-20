using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AfSdr.Dsp;

namespace AfSdr;

// UI-thread-owned ring bitmap: one new scan line per fresh displayed FFT frame.
internal sealed class WaterfallHistory : IDisposable
{
    private static readonly Color[] Stops = [Color.FromArgb(8, 13, 26), Color.FromArgb(22, 39, 106),
        Color.FromArgb(0, 146, 190), Color.FromArgb(69, 215, 140),
        Color.FromArgb(255, 217, 66), Color.FromArgb(255, 80, 38)];
    internal const int Capacity = 300;
    private readonly Bitmap bitmap = new(SpectrumProcessor.Size, Capacity, PixelFormat.Format32bppArgb);
    private readonly int[] row = new int[SpectrumProcessor.Size];
    private int head;
    internal int Count { get; private set; }

    internal void Add(float[] values)
    {
        if (values.Length != row.Length) throw new ArgumentException("Unexpected FFT size.");
        head = (head + Capacity - 1) % Capacity;
        for (int i = 0; i < row.Length; i++) row[i] = LevelColor(values[i]).ToArgb();
        var data = bitmap.LockBits(new Rectangle(0, head, row.Length, 1), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(row, 0, data.Scan0, row.Length); }
        finally { bitmap.UnlockBits(data); }
        Count = Math.Min(Count + 1, Capacity);
    }

    internal static Color LevelColor(float db)
    {
        float value = Math.Clamp((db + 110) / 100, 0, 1) * (Stops.Length - 1);
        int index = Math.Min((int)value, Stops.Length - 2);
        float part = value - index;
        Color a = Stops[index], b = Stops[index + 1];
        return Color.FromArgb((int)(a.R + (b.R - a.R) * part),
            (int)(a.G + (b.G - a.G) * part), (int)(a.B + (b.B - a.B) * part));
    }

    internal void Draw(Graphics graphics, RectangleF target, DisplayRange? range = null)
    {
        if (Count == 0) return;
        int first = Math.Min(Count, Capacity - head);
        float rowHeight = target.Height / Capacity;
        float sourceX = (float)(range?.FirstBin(bitmap.Width) ?? 0);
        float sourceWidth = (float)(range?.BinWidth(bitmap.Width) ?? bitmap.Width);
        graphics.DrawImage(bitmap, new RectangleF(target.X, target.Y, target.Width, first * rowHeight),
            new RectangleF(sourceX, head, sourceWidth, first), GraphicsUnit.Pixel);
        int rest = Count - first;
        if (rest > 0)
            graphics.DrawImage(bitmap, new RectangleF(target.X, target.Y + first * rowHeight, target.Width, rest * rowHeight),
                new RectangleF(sourceX, 0, sourceWidth, rest), GraphicsUnit.Pixel);
    }

    internal void Clear() { Count = 0; head = 0; }
    public void Dispose() => bitmap.Dispose();
}
