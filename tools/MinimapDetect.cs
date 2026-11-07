using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Detect.exe <klasör> <hata-ayıklama-çıktı-klasörü>
// Prototip: rakip moru (ton ~285°) maskesi -> bağlı bileşenler -> ikon büyüklüğündeki lekeler.
static class Detect
{
    public struct Blob { public int X, Y, N, W, H; }

    static bool Purple(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 100 || max > 215 || (max - min) * 100 < max * 45) return false; // rakip halkası koyu ve doygun; açık mor saçlar elenir
        if (b < g + 30 || r < g + 25) return false;            // mor: R ve B, G'den belirgin yüksek
        // ton 270-305 arası: B en büyük ya da R'ye çok yakın
        double h = Hue(r, g, b);
        return h >= 268 && h <= 305;
    }

    static bool Teal(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 90 || max - min < 50) return false;
        double h = Hue(r, g, b);
        return h >= 155 && h <= 185;
    }

    static double Hue(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)); double d = max - min;
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60; return h < 0 ? h + 360 : h;
    }

    public static List<Blob> Rejected = new List<Blob>();

    public static List<Blob> Find(Bitmap bmp, int top = 40)
    {
        int W = bmp.Width, H = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var px = new byte[data.Stride * H]; Marshal.Copy(data.Scan0, px, 0, px.Length);
        int stride = data.Stride; bmp.UnlockBits(data);

        var mask = new bool[W * H];
        for (int y = top; y < H; y++)
            for (int x = 0; x < W; x++) { int i = y * stride + x * 3; mask[y * W + x] = Purple(px[i + 2], px[i + 1], px[i]); }

        var blobs = new List<Blob>(); var seen = new bool[W * H]; var stack = new Stack<int>();
        for (int p = 0; p < mask.Length; p++)
        {
            if (!mask[p] || seen[p]) continue;
            int n = 0, sx = 0, sy = 0, x0 = W, y0 = H, x1 = 0, y1 = 0; stack.Push(p); seen[p] = true;
            var pts = new List<int>();
            while (stack.Count > 0)
            {
                int q = stack.Pop(), qx = q % W, qy = q / W; n++; sx += qx; sy += qy; pts.Add(q);
                if (qx < x0) x0 = qx; if (qx > x1) x1 = qx; if (qy < y0) y0 = qy; if (qy > y1) y1 = qy;
                // 8-komşuluk, 1 piksellik boşlukları da birleştir (portre halkayı böler)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = qx + dx, ny = qy + dy; if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                        int r = ny * W + nx; if (mask[r] && !seen[r]) { seen[r] = true; stack.Push(r); }
                    }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (n >= 20 && bw <= 40 && bh <= 40 && x0 > 20 && x1 < 420) Rejected.Add(new Blob { X = (x0 + x1) / 2, Y = (y0 + y1) / 2, N = n, W = bw, H = bh });
            if (n < 25 || bw < 12 || bh < 12 || bw > 40 || bh > 40) continue;
            // Halka testi: ikonun mor çerçevesi merkezin etrafında her yöne yayılır; bariyer çizgileri yayılmaz.
            // Işın testi: merkezden 8 yöne yürü; halka portreyi çevrelediği için en az 7 yönde mora çarpmalı.
            float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f; int hits = 0, reach = Math.Max(bw, bh) / 2 + 2;
            for (int k = 0; k < 8; k++)
            {
                double ang = k * Math.PI / 4, ddx = Math.Cos(ang), ddy = Math.Sin(ang);
                for (int s = 1; s <= reach; s++)
                {
                    int rx = (int)Math.Round(cx + ddx * s), ry = (int)Math.Round(cy + ddy * s);
                    if (rx < 0 || ry < 0 || rx >= W || ry >= H) break;
                    if (mask[ry * W + rx]) { hits++; break; }
                }
            }
            if (hits < 6) continue;
            // Mor saçlı takım arkadaşı (ör. Clove): portresi mor ama çevresinde turkuaz halka var.
            int teal = 0, ring = 0, rr = Math.Max(bw, bh) / 2;
            for (int dy = -rr - 4; dy <= rr + 4; dy++)
                for (int dx = -rr - 4; dx <= rr + 4; dx++)
                {
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < rr - 1 || dist > rr + 4) continue;
                    int qx = (int)cx + dx, qy = (int)cy + dy; if (qx < 0 || qy < 0 || qx >= W || qy >= H) continue;
                    int i = qy * stride + qx * 3; ring++;
                    if (Teal(px[i + 2], px[i + 1], px[i])) teal++;
                }
            if (teal * 4 > ring) continue;
            blobs.Add(new Blob { X = (int)cx, Y = (int)cy, N = n, W = bw, H = bh });
        }
        return blobs;
    }

    static void Main(string[] a)
    {
        var files = Directory.GetFiles(a[0], "*.png").OrderBy(f => f).ToArray();
        Directory.CreateDirectory(a[1]);
        var sw = new Stopwatch(); var times = new List<double>(); int withEnemy = 0; var hist = new int[8];
        for (int i = 0; i < files.Length; i++)
        {
            using (var bmp = new Bitmap(files[i]))
            using (var b24 = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format24bppRgb))
            {
                Rejected.Clear();
                sw.Restart(); var bl = Find(b24); sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds);
                // Reddedilen lekeler: sabit L bariyeri (252,195) dışında kalanlar olası kaçırmalardır.
                var miss = Rejected.Where(r => !(Math.Abs(r.X - 252) < 6 && Math.Abs(r.Y - 195) < 6) && !bl.Any(b => Math.Abs(b.X - r.X) < 6 && Math.Abs(b.Y - r.Y) < 6)).ToList();
                if (miss.Count > 0 && i > 5)
                    using (var g = Graphics.FromImage(b24))
                    {
                        foreach (var b in miss) g.DrawRectangle(Pens.Red, b.X - b.W / 2 - 3, b.Y - b.H / 2 - 3, b.W + 6, b.H + 6);
                        foreach (var b in bl) g.DrawRectangle(Pens.Lime, b.X - b.W / 2 - 3, b.Y - b.H / 2 - 3, b.W + 6, b.H + 6);
                        Directory.CreateDirectory(Path.Combine(a[1], "miss"));
                        b24.Save(Path.Combine(a[1], "miss", i.ToString("D3") + ".png"), ImageFormat.Png);
                    }
                if (i == 162 || i == 198 || i == 294 || i == 378)
                    Console.WriteLine("#{0}: {1}", i, string.Join("  ", bl.Select(b => $"({b.X},{b.Y}) n={b.N} {b.W}x{b.H}")));
                hist[Math.Min(bl.Count, 7)]++; if (bl.Count > 0) withEnemy++;
                if (bl.Count > 0 && i % 6 == 0)
                    using (var g = Graphics.FromImage(b24))
                    {
                        foreach (var b in bl) g.DrawRectangle(Pens.Lime, b.X - b.W / 2 - 3, b.Y - b.H / 2 - 3, b.W + 6, b.H + 6);
                        b24.Save(Path.Combine(a[1], i.ToString("D3") + "_" + bl.Count + ".png"), ImageFormat.Png);
                    }
            }
        }
        times.Sort();
        Console.WriteLine("kare: {0}, rakip goruldugu kare: {1}", files.Length, withEnemy);
        Console.WriteLine("kare basina rakip sayisi dagilimi: " + string.Join(" ", hist.Select((c, k) => k + ":" + c)));
        Console.WriteLine("sure (ms) medyan {0:0.00}, p95 {1:0.00}, max {2:0.00}", times[times.Count / 2], times[(int)(times.Count * .95)], times[times.Count - 1]);
    }
}
