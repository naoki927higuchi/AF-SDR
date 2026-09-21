using System.Drawing.Drawing2D;
namespace AfSdr.Checks;
internal static class WaterfallStabilityChecks
{
    internal static void Run()
    {
        using var history = new WaterfallHistory();
        float Level(int n) => -110 + n % 17 * 6;
        for (int n = 0; n < 300; n++) history.Add(Enumerable.Repeat(Level(n), 1024).ToArray());
        using var image = new Bitmap(333, 210);
        using var reference = new Bitmap(333, 210);
        using var ordered = new Bitmap(1, 300);
        using var g = Graphics.FromImage(image);
        using var r = Graphics.FromImage(reference);
        g.InterpolationMode = r.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = r.PixelOffsetMode = PixelOffsetMode.Half;
        var target = new RectangleF(0, 1.37f, 333, 202.3f);
        for (int frame = 0; frame < 301; frame++)
        {
            g.Clear(Color.Magenta); r.Clear(Color.Magenta);
            history.Draw(g, target);
            for (int y = 0; y < 300; y++) ordered.SetPixel(0, y, WaterfallHistory.LevelColor(Level(299 + frame - y)));
            r.DrawImage(ordered, target, new RectangleF(0, 0, 1, 300), GraphicsUnit.Pixel);
            for (int y = 2; y < 202; y++)
                if (image.GetPixel(100, y) != reference.GetPixel(100, y))
                    throw new Exception($"Waterfall differs from contiguous reference at ring frame {frame}, row {y} (fractional scaling).");
            history.Add(Enumerable.Repeat(Level(300 + frame), 1024).ToArray());
        }
        Console.WriteLine("PASS: waterfall matches contiguous reference across full ring wrap at fractional scale.");
    }
}
