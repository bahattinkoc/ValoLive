using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Calib.exe <harita.png> <kare.png> [rot180=0|1]
// Minimap görselinin (1024x1024, saydam zemin) ekrandaki ölçek ve konumunu, gri zemin maskelerini örtüştürerek bulur.
static class Calib
{
    public static bool[] FrameFloor(Bitmap b, out int W, out int H)
    {
        W = b.Width; H = b.Height;
        var d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var px = new byte[d.Stride * H]; Marshal.Copy(d.Scan0, px, 0, px.Length); int st = d.Stride; b.UnlockBits(d);
        var m = new bool[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * st + x * 3; int r = px[i + 2], g = px[i + 1], bl = px[i];
                int max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
                m[y * W + x] = max - min < 14 && max >= 95 && max <= 175; // minimap zemini: nötr orta gri
            }
        return m;
    }

    // Harita görselinden zemin maskesi, n x n'e küçültülmüş (opak ve gri)
    public static float[,] MapMask(Bitmap map, int n)
    {
        var o = new float[n, n];
        using (var small = new Bitmap(map, n, n))
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var c = small.GetPixel(x, y);
                    int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                    o[x, y] = c.A > 200 && max - min < 14 && max >= 95 && max <= 175 ? 1 : 0;
                }
        return o;
    }

    // ölçek (harita kenarı piksel), sol-üst köşe -> örtüşme skoru (F1 benzeri)
    public static double Score(bool[] floor, int W, int H, Bitmap map, int size, int ox, int oy, bool rot)
    {
        var mm = MapMask(map, size);
        int tp = 0, fp = 0, fn = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = ox + (rot ? size - 1 - x : x), sy = oy + (rot ? size - 1 - y : y);
                if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                bool a = mm[x, y] > 0, b = floor[sy * W + sx];
                if (a && b) tp++; else if (a) fn++; else if (b) fp++;
            }
        return 2.0 * tp / (2.0 * tp + fp + fn);
    }

    static void Main(string[] a)
    {
        using (var map = new Bitmap(a[0]))
        using (var raw = new Bitmap(a[1]))
        using (var fr = raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), PixelFormat.Format24bppRgb))
        {
            bool rot = a.Length > 2 && a[2] == "1";
            var floor = FrameFloor(fr, out int W, out int H);
            double best = 0; int bs = 0, bx = 0, by = 0;
            // kaba arama
            for (int s = 300; s <= 440; s += 8)
                for (int ox = -20; ox <= 140; ox += 4)
                    for (int oy = 0; oy <= 140; oy += 4)
                    {
                        if (ox + s < 200 || oy + s < 200) continue;
                        double sc = ScoreFast(floor, W, H, map, s, ox, oy, rot);
                        if (sc > best) { best = sc; bs = s; bx = ox; by = oy; }
                    }
            // ince arama
            int s0 = bs, x0 = bx, y0 = by;
            for (int s = s0 - 8; s <= s0 + 8; s++)
                for (int ox = x0 - 5; ox <= x0 + 5; ox++)
                    for (int oy = y0 - 5; oy <= y0 + 5; oy++)
                    {
                        double sc = ScoreFast(floor, W, H, map, s, ox, oy, rot);
                        if (sc > best) { best = sc; bs = s; bx = ox; by = oy; }
                    }
            Console.WriteLine("boyut={0} sol={1} ust={2} skor={3:0.000} rot={4}", bs, bx, by, best, rot);
        }
    }

    static readonly System.Collections.Generic.Dictionary<int, float[,]> masks = new System.Collections.Generic.Dictionary<int, float[,]>();
    static double ScoreFast(bool[] floor, int W, int H, Bitmap map, int size, int ox, int oy, bool rot)
    {
        if (!masks.TryGetValue(size, out var mm)) masks[size] = mm = MapMask(map, size);
        int tp = 0, fp = 0, fn = 0;
        for (int y = 0; y < size; y += 2)
            for (int x = 0; x < size; x += 2)
            {
                int sx = ox + (rot ? size - 1 - x : x), sy = oy + (rot ? size - 1 - y : y);
                if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                bool p = mm[x, y] > 0, q = floor[sy * W + sx];
                if (p && q) tp++; else if (p) fn++; else if (q) fp++;
            }
        return 2.0 * tp / (2.0 * tp + fp + fn + 1);
    }
}
