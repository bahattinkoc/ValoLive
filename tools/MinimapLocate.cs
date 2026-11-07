using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

// Locate.exe <örnek-klasörü> <ref-klasörü> <çıktı-klasörü> <ajan1,ajan2,...>
// Tespit -> taraf (harita yönelimi) -> 5 aday arasında ajan -> ikon merkezi -> callout. Hata ayıklama görselleri üretir.
static class Locate
{
    // 1920x1080, minimap boyutu 1: harita görseli ekranda bu karede (Calib ile ölçüldü)
    const int MapX = 15, MapY = 35, MapS = 384;

    sealed class Tpl { public string Name; public int D; public float[] R, G, B; public bool[] M; }
    sealed class Callout { public string Name; public float U, V; }

    static List<Tpl> tpls = new List<Tpl>();
    static List<Callout> callouts = new List<Callout>();
    static bool[] mapMask; // MapS x MapS, saldırı yönelimi

    static void Load(string refDir, string[] agents)
    {
        foreach (var name in agents)
            using (var src = new Bitmap(Path.Combine(refDir, "agents", name + ".png")))
                for (int d = 15; d <= 18; d++)
                    using (var bmp = new Bitmap(d, d, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(30, 30, 34)); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, d, d));
                        }
                        var t = new Tpl { Name = name, D = d, R = new float[d * d], G = new float[d * d], B = new float[d * d], M = new bool[d * d] };
                        float c = (d - 1) / 2f, rad = d / 2f - 0.8f;
                        for (int y = 0; y < d; y++)
                            for (int x = 0; x < d; x++)
                            {
                                var p = bmp.GetPixel(x, y); int k = y * d + x;
                                t.R[k] = p.R; t.G[k] = p.G; t.B[k] = p.B; t.M[k] = (x - c) * (x - c) + (y - c) * (y - c) <= rad * rad;
                            }
                        tpls.Add(t);
                    }

        // Harita verisi: dönüşüm katsayıları + callout'lar (valorant-api, tr-TR)
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var map = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(Path.Combine(refDir, "bind.json")));
        double xm = Convert.ToDouble(map["xMultiplier"]), ym = Convert.ToDouble(map["yMultiplier"]), xa = Convert.ToDouble(map["xScalarToAdd"]), ya = Convert.ToDouble(map["yScalarToAdd"]);
        foreach (Dictionary<string, object> c in (object[])map["callouts"])
        {
            var loc = (Dictionary<string, object>)c["location"];
            double gx = Convert.ToDouble(loc["x"]), gy = Convert.ToDouble(loc["y"]);
            callouts.Add(new Callout { Name = c["superRegionName"] + "/" + c["regionName"], U = (float)(gy * xm + xa), V = (float)(gx * ym + ya) });
        }

        var mm = Calib.MapMask(new Bitmap(Path.Combine(refDir, "bind.png")), MapS);
        mapMask = new bool[MapS * MapS];
        for (int y = 0; y < MapS; y++) for (int x = 0; x < MapS; x++) mapMask[y * MapS + x] = mm[x, y] > 0;
    }

    /// <summary>Saldırı tarafında harita düz, savunmada 180° dönük. Hangisi zemine daha iyi oturuyor?</summary>
    static bool Defending(Bitmap fr)
    {
        var floor = Calib.FrameFloor(fr, out int W, out int H);
        double[] s = new double[2];
        for (int r = 0; r < 2; r++)
        {
            int tp = 0, fp = 0, fn = 0;
            for (int y = 0; y < MapS; y += 2)
                for (int x = 0; x < MapS; x += 2)
                {
                    int sx = MapX + (r == 1 ? MapS - 1 - x : x), sy = MapY + (r == 1 ? MapS - 1 - y : y);
                    bool p = mapMask[y * MapS + x], q = floor[sy * W + sx];
                    if (p && q) tp++; else if (p) fn++; else if (q) fp++;
                }
            s[r] = 2.0 * tp / (2.0 * tp + fp + fn + 1);
        }
        return s[1] > s[0];
    }

    /// <summary>Ekran pikseli -> harita görselinde 0..1 (taraf dönüşü dahil) -> en yakın callout.</summary>
    static string CalloutAt(float px, float py, bool def, out float u, out float v)
    {
        u = (px - MapX) / MapS; v = (py - MapY) / MapS;
        if (def) { u = 1 - u; v = 1 - v; }
        float uu = u, vv = v;
        return callouts.OrderBy(c => (c.U - uu) * (c.U - uu) + (c.V - vv) * (c.V - vv)).First().Name;
    }

    static Tuple<string, double, double, int, int> Identify(byte[] px, int st, int W, int H, int cx, int cy)
    {
        var best = new Dictionary<string, Tuple<double, int, int>>();
        foreach (var t in tpls)
            for (int oy = -7; oy <= 7; oy++)
                for (int ox = -7; ox <= 7; ox++)
                {
                    int x0 = cx + ox - t.D / 2, y0 = cy + oy - t.D / 2;
                    if (x0 < 0 || y0 < 0 || x0 + t.D >= W || y0 + t.D >= H) continue;
                    double s = 0; int n = 0;
                    for (int y = 0; y < t.D; y++)
                        for (int x = 0; x < t.D; x++)
                        {
                            int k = y * t.D + x; if (!t.M[k]) continue;
                            int i = (y0 + y) * st + (x0 + x) * 3;
                            double dr = px[i + 2] - t.R[k], dg = px[i + 1] - t.G[k], db = px[i] - t.B[k];
                            s += Math.Sqrt(dr * dr + dg * dg + db * db); n++;
                        }
                    s /= n;
                    if (!best.TryGetValue(t.Name, out var cur) || s < cur.Item1) best[t.Name] = Tuple.Create(s, cx + ox, cy + oy);
                }
        var order = best.OrderBy(kv => kv.Value.Item1).ToList();
        var top = order[0];
        double margin = order.Count > 1 ? order[1].Value.Item1 - top.Value.Item1 : 99;
        return Tuple.Create(top.Key, top.Value.Item1, margin, top.Value.Item2, top.Value.Item3);
    }

    static void Main(string[] a)
    {
        Load(a[1], a[3].Split(','));
        Directory.CreateDirectory(a[2]);
        var files = Directory.GetFiles(a[0], "*.png").OrderBy(f => f).ToArray();
        var swF = new System.Diagnostics.Stopwatch(); var tFind = new List<double>(); var tId = new List<double>(); var tSide = new List<double>();
        int saved = 0;
        for (int i = 6; i < files.Length; i++)
            using (var raw = new Bitmap(files[i]))
            using (var fr = raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), PixelFormat.Format24bppRgb))
            {
                swF.Restart();
                // Yalnızca minimap dairesinin içi (HUD'daki büyük portreler dışarıda kalır)
                var found = Detect.Find(fr).Where(b => Math.Pow(b.X - (MapX + MapS / 2), 2) + Math.Pow(b.Y - (MapY + MapS / 2), 2) < 200 * 200).ToList();
                swF.Stop(); tFind.Add(swF.Elapsed.TotalMilliseconds);
                if (found.Count == 0) continue;

                swF.Restart(); bool def = Defending(fr); swF.Stop(); tSide.Add(swF.Elapsed.TotalMilliseconds);
                int W = fr.Width, H = fr.Height;
                var d = fr.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var px = new byte[d.Stride * H]; Marshal.Copy(d.Scan0, px, 0, px.Length); int st = d.Stride; fr.UnlockBits(d);

                var labels = new List<Tuple<int, int, string>>();
                foreach (var b in found)
                {
                    swF.Restart(); var id = Identify(px, st, W, H, b.X, b.Y); swF.Stop(); tId.Add(swF.Elapsed.TotalMilliseconds);
                    string where = CalloutAt(id.Item4, id.Item5, def, out float u, out float v);
                    Console.WriteLine("#{0,3} {1}  {2,-8} fark={3:0} pay={4:0}  -> {5}", i, def ? "SAV" : "SAL", id.Item1, id.Item2, id.Item3, where);
                    labels.Add(Tuple.Create(id.Item4, id.Item5, id.Item1 + " · " + where));
                }
                if (saved < 12 && i % 3 == 0)
                {
                    using (var g = Graphics.FromImage(fr))
                    using (var f = new Font("Segoe UI", 8, FontStyle.Bold))
                    {
                        // callout noktaları (küçük, soluk) + tespitler
                        foreach (var c in callouts)
                        {
                            float cu = def ? 1 - c.U : c.U, cv = def ? 1 - c.V : c.V;
                            g.FillEllipse(Brushes.Yellow, MapX + cu * MapS - 2, MapY + cv * MapS - 2, 4, 4);
                        }
                        foreach (var l in labels)
                        {
                            g.DrawEllipse(Pens.Lime, l.Item1 - 9, l.Item2 - 9, 18, 18);
                            g.FillRectangle(new SolidBrush(Color.FromArgb(190, 0, 0, 0)), l.Item1 + 10, l.Item2 - 8, g.MeasureString(l.Item3, f).Width, 15);
                            g.DrawString(l.Item3, f, Brushes.White, l.Item1 + 10, l.Item2 - 8);
                        }
                        g.DrawString(def ? "SAVUNMA" : "SALDIRI", f, Brushes.Yellow, 4, 460);
                    }
                    fr.Save(Path.Combine(a[2], i.ToString("D3") + ".png"), ImageFormat.Png); saved++;
                }
            }
        Func<List<double>, string> med = l => l.Count == 0 ? "-" : l.OrderBy(x => x).ElementAt(l.Count / 2).ToString("0.0");
        Console.WriteLine("sure (ms) tespit medyan {0}, taraf {1}, ajan tanima/ikon {2}", med(tFind), med(tSide), med(tId));
    }
}
