using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Rakip hafızası (deneysel): ekranın sol üstündeki minimap saniyede 10 kez okunur; görülen rakiplerin
    /// (mor halkalı ajan ikonları) son yeri bölge adıyla tutulur. Oyuna ve belleğine dokunmaz, yalnızca
    /// oyuncunun zaten gördüğü ekran görüntüsünü okur. Raund değişince hafıza sıfırlanır.
    /// Ölçüler 1080p'de ölçüldü; başka çözünürlükte pencere yüksekliğiyle orantılanır.
    /// </summary>
    sealed class MinimapMemory : IDisposable
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        public sealed class Sighting { public string AgentId, Name, Callout; public DateTime Seen; public bool Visible; }

        readonly object lk = new object();
        readonly Dictionary<string, Sighting> memory = new Dictionary<string, Sighting>();
        Thread thread;
        volatile int generation;
        volatile bool roundReset;

        public bool Active => thread != null;
        /// <summary>Valorant şu an önde mi (panel yalnızca o zaman görünür).</summary>
        public volatile bool GameFront;
        /// <summary>Minimap'in ekrandaki yeri (gerçek piksel); panel hemen altına yerleşir. Boşsa henüz kalibre edilmedi.</summary>
        public Rectangle MapRect { get; private set; }
        /// <summary>Son karenin işlenme süresi (ms), tanılama için.</summary>
        public double LastMs { get; private set; }

        public void Start(string mapUrl, List<string> enemyAgents)
        {
            Stop();
            int gen = ++generation;
            lock (lk) memory.Clear();
            thread = new Thread(() => Loop(mapUrl, enemyAgents, gen)) { IsBackground = true, Name = "minimap", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            Log($"başladı · {mapUrl} · rakip ajan {enemyAgents.Count}");
        }

        public void Stop()
        {
            if (thread == null) return;
            generation++;
            thread = null;
            lock (lk) memory.Clear();
            MapRect = Rectangle.Empty;
            GameFront = false;
        }

        /// <summary>Yeni raund: eski yerler artık yanıltıcı.</summary>
        public void NewRound() { roundReset = true; lock (lk) memory.Clear(); }

        public List<Sighting> Snapshot()
        {
            lock (lk) return memory.Values.Select(s => new Sighting { AgentId = s.AgentId, Name = s.Name, Callout = s.Callout, Seen = s.Seen, Visible = s.Visible })
                                          .OrderByDescending(s => s.Visible).ThenByDescending(s => s.Seen).ToList();
        }

        public void Dispose() => Stop();

        static readonly object logLock = new object();

        /// <summary>Yayından bağımsız günlük: logs\hafiza_GGAAYYYY.log (test sırasında yayın açık olmayabilir).</summary>
        public static void Log(string text)
        {
            Diag.Event("HAFIZA", text);
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(Diag.Dir);
                    File.AppendAllText(Path.Combine(Diag.Dir, $"hafiza_{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:HH:mm:ss}  {text}\r\n");
                }
            }
            catch { }
        }

        void Loop(string mapUrl, List<string> enemies, int gen)
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // gerçek piksel (Windows ölçeklemesinden bağımsız)
            Engine eng;
            try { eng = Engine.Create(mapUrl, enemies); }
            catch (Exception e) { Log("hazırlanamadı: " + e.Message); if (gen == generation) thread = null; return; }

            var sw = new Stopwatch();
            Bitmap bmp = null;
            int frames = 0, seenFrames = 0; double totalMs = 0, maxMs = 0; DateTime statAt = DateTime.Now.AddMinutes(1);
            try
            {
                while (gen == generation)
                {
                    sw.Restart();
                    if (roundReset) { roundReset = false; eng.Reset(); }
                    GameFront = GameWindow(out var win);
                    if (GameFront)
                    {
                        float k = win.Height / 1080f;
                        int size = (int)Math.Ceiling(480 * k);
                        if (bmp == null || bmp.Width != size) { bmp?.Dispose(); bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb); }
                        try { using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(win.Left, win.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy); }
                        catch { Thread.Sleep(500); continue; } // kilit ekranı / güvenli masaüstü
                        var now = DateTime.Now;
                        var seen = eng.Process(bmp, k, now);
                        if (eng.Calibrated) MapRect = new Rectangle(win.Left + eng.MapX, win.Top + eng.MapY, eng.MapS, eng.MapS);
                        lock (lk)
                        {
                            if (gen != generation) break;
                            foreach (var s in memory.Values) s.Visible = false;
                            foreach (var kv in seen)
                            {
                                if (!memory.TryGetValue(kv.Key, out var m)) memory[kv.Key] = m = new Sighting { AgentId = kv.Key, Name = eng.NameOf(kv.Key) };
                                m.Callout = kv.Value; m.Seen = now; m.Visible = true;
                            }
                        }
                        LastMs = sw.Elapsed.TotalMilliseconds;
                        frames++; totalMs += LastMs; if (LastMs > maxMs) maxMs = LastMs; if (seen.Count > 0) seenFrames++;
                        if (now >= statAt)
                        {
                            Log($"son 1 dk: {frames} kare · ort {totalMs / Math.Max(1, frames):0.0} ms · en uzun {maxMs:0} ms · rakip görülen kare {seenFrames} · " +
                                (eng.Calibrated ? $"minimap {eng.MapS}px @ {eng.MapX},{eng.MapY}" : "kalibre değil"));
                            frames = seenFrames = 0; totalMs = maxMs = 0; statAt = now.AddMinutes(1);
                        }
                    }
                    int wait = (GameFront ? 100 : 500) - (int)sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch (Exception e) { Log("durdu: " + e.Message); }
            finally { bmp?.Dispose(); }
        }

        static bool GameWindow(out Rectangle r)
        {
            r = Rectangle.Empty;
            var h = GetForegroundWindow();
            GetWindowThreadProcessId(h, out uint pid);
            try { if (!Process.GetProcessById((int)pid).ProcessName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase)) return false; }
            catch { return false; }
            if (!GetWindowRect(h, out var rc) || rc.Bottom - rc.Top < 400) return false;
            r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
            return true;
        }

        /// <summary>Tek maçın görüntü işleme durumu: harita maskesi, kalibrasyon, ajan şablonları, izler.</summary>
        sealed class Engine
        {
            sealed class Tpl { public string Agent; public int D; public float[] R, G, B; public bool[] M; }
            sealed class Track { public float X, Y, Dx, Dy; public DateTime Last; public int Frames; public string Agent; public readonly Dictionary<string, double> Votes = new Dictionary<string, double>(); }

            bool[] mask;                 // harita görselinin zemin maskesi, MaskN x MaskN
            const int MaskN = 512;
            List<KeyValuePair<string, PointF>> callouts;
            string mapKey;
            readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();
            readonly Dictionary<string, string> names = new Dictionary<string, string>();
            List<Tpl> tpls; float tplK;
            readonly List<Track> tracks = new List<Track>();

            public bool Calibrated;
            public int MapX, MapY, MapS;
            bool defending;
            DateTime nextCalib, nextSide;

            public string NameOf(string agent) => names.TryGetValue(agent, out var n) ? n : "?";

            public static Engine Create(string mapUrl, List<string> enemies)
            {
                var map = ValApi.MapOf(mapUrl) ?? throw new InvalidOperationException("harita verisi yok: " + mapUrl);
                if (map.Callouts.Count == 0) throw new InvalidOperationException("bölge adları yok: " + map.Name);
                var e = new Engine { callouts = map.Callouts, mapKey = mapUrl };
                string cache = Path.Combine(Settings.Dir, "cache");
                Directory.CreateDirectory(cache);
                using (var img = new Bitmap(Fetch(map.Minimap, Path.Combine(cache, "minimap_" + mapUrl.Split('/').Last() + ".png")))) e.mask = FloorMask(img);
                foreach (var id in enemies.Where(a => !string.IsNullOrEmpty(a)).Distinct())
                {
                    try
                    {
                        using (var src = Image.FromFile(Fetch(OverlayRenderer.AgentIcon(id), IconFile(id)))) // Maç Odası ile aynı önbellek
                            e.icons[id] = new Bitmap(src);
                        e.names[id] = RiotLocal.Title(ValApi.AgentName(id));
                    }
                    catch { }
                }
                if (e.icons.Count == 0) throw new InvalidOperationException("ajan ikonları indirilemedi");
                e.LoadCalibration();
                return e;
            }

            public static string IconFile(string agent) => Path.Combine(Settings.Dir, "cache", "agent_" + agent + ".png");

            static string Fetch(string url, string file)
            {
                if (!File.Exists(file))
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var wc = new WebClient()) wc.DownloadFile(url, file + ".part");
                    File.Move(file + ".part", file);
                }
                return file;
            }

            public void Reset() { tracks.Clear(); }

            // ---------- Kalibrasyon: harita görseli ekranda nerede, hangi yönde? ----------

            static string CalibFile => Path.Combine(Settings.Dir, "minimap-calib.txt");

            void LoadCalibration()
            {
                try
                {
                    foreach (var line in File.ReadAllLines(CalibFile))
                    {
                        var p = line.Split('\t');
                        if (p.Length == 4 && p[0] == mapKey) { MapS = int.Parse(p[1]); MapX = int.Parse(p[2]); MapY = int.Parse(p[3]); Calibrated = true; }
                    }
                }
                catch { }
            }

            void SaveCalibration()
            {
                try
                {
                    var lines = File.Exists(CalibFile) ? File.ReadAllLines(CalibFile).Where(l => !l.StartsWith(mapKey + "\t")).ToList() : new List<string>();
                    lines.Add($"{mapKey}\t{MapS}\t{MapX}\t{MapY}");
                    File.WriteAllLines(CalibFile, lines);
                }
                catch { }
            }

            static bool Grey(int r, int g, int b)
            {
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                return max - min < 14 && max >= 95 && max <= 175; // minimap zemini: nötr orta gri
            }

            static bool[] FloorMask(Bitmap img)
            {
                using (var small = new Bitmap(MaskN, MaskN, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.NearestNeighbor; g.DrawImage(img, 0, 0, MaskN, MaskN); }
                    var d = small.LockBits(new Rectangle(0, 0, MaskN, MaskN), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new byte[d.Stride * MaskN]; Marshal.Copy(d.Scan0, px, 0, px.Length); small.UnlockBits(d);
                    var m = new bool[MaskN * MaskN];
                    for (int y = 0; y < MaskN; y++)
                        for (int x = 0; x < MaskN; x++) { int i = y * d.Stride + x * 4; m[y * MaskN + x] = px[i + 3] > 200 && Grey(px[i + 2], px[i + 1], px[i]); }
                    return m;
                }
            }

            static bool[] FrameFloor(byte[] px, int st, int W, int H)
            {
                var m = new bool[W * H];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = y * st + x * 3; m[y * W + x] = Grey(px[i + 2], px[i + 1], px[i]); }
                return m;
            }

            /// <summary>Harita maskesi ile ekran zemininin örtüşmesi (0..1). rot: savunma tarafı, harita 180° dönük.</summary>
            double Score(bool[] floor, int W, int H, int s, int ox, int oy, bool rot)
            {
                int tp = 0, fp = 0, fn = 0;
                for (int y = 0; y < s; y += 2)
                    for (int x = 0; x < s; x += 2)
                    {
                        int sx = ox + (rot ? s - 1 - x : x), sy = oy + (rot ? s - 1 - y : y);
                        if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                        bool p = mask[(y * MaskN / s) * MaskN + x * MaskN / s], q = floor[sy * W + sx];
                        if (p && q) tp++; else if (p) fn++; else if (q) fp++;
                    }
                return 2.0 * tp / (2.0 * tp + fp + fn + 1);
            }

            void Calibrate(bool[] floor, int W, int H, float k)
            {
                // 1080p'de ölçülen yerin çevresinde kaba, sonra ince arama; iki yönelim birden.
                int s0 = (int)(384 * k), x0 = (int)(15 * k), y0 = (int)(35 * k);
                double best = 0; int bs = 0, bx = 0, by = 0; bool br = false;
                for (int s = s0 - (int)(60 * k); s <= s0 + (int)(60 * k); s += 6)
                    for (int ox = x0 - (int)(40 * k); ox <= x0 + (int)(40 * k); ox += 6)
                        for (int oy = y0 - (int)(40 * k); oy <= y0 + (int)(40 * k); oy += 6)
                            foreach (bool rot in new[] { false, true })
                            {
                                double sc = Score(floor, W, H, s, ox, oy, rot);
                                if (sc > best) { best = sc; bs = s; bx = ox; by = oy; br = rot; }
                            }
                int cs = bs, cx = bx, cy = by;
                for (int s = cs - 6; s <= cs + 6; s++)
                    for (int ox = cx - 5; ox <= cx + 5; ox++)
                        for (int oy = cy - 5; oy <= cy + 5; oy++)
                        {
                            double sc = Score(floor, W, H, s, ox, oy, br);
                            if (sc > best) { best = sc; bs = s; bx = ox; by = oy; }
                        }
                Log($"kalibrasyon {mapKey}: {bs}px @ {bx},{by} · skor {best:0.00} · {(br ? "savunma" : "saldırı")}");
                if (best < 0.75) return; // harita büyük açık / skor tablosu: sonra yeniden
                MapS = bs; MapX = bx; MapY = by; defending = br; Calibrated = true;
                SaveCalibration();
            }

            void UpdateSide(bool[] floor, int W, int H)
            {
                double a = Score(floor, W, H, MapS, MapX, MapY, false), d = Score(floor, W, H, MapS, MapX, MapY, true);
                if (Math.Max(a, d) > 0.6 && Math.Abs(a - d) > 0.15) defending = d > a;
            }

            // ---------- Tespit ----------

            struct Blob { public int X, Y, W, H; }

            // Rakip halkası: doygun, koyu mor (renk körlüğü + mor rakip ayarında ~#8A40A4). Açık mor saçlar (Clove) elenir.
            static bool Purple(int r, int g, int b)
            {
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 100 || max > 215 || (max - min) * 100 < max * 45) return false;
                if (b < g + 30 || r < g + 25) return false;
                double d = max - min, h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60; if (h < 0) h += 360;
                return h >= 268 && h <= 305;
            }

            List<Blob> Find(byte[] px, int st, int W, int H, float k)
            {
                var hit = new bool[W * H];
                int cxm = MapX + MapS / 2, cym = MapY + MapS / 2, rad = (int)(MapS * 0.53);
                int yA = Math.Max(0, cym - rad), yB = Math.Min(H, cym + rad), xA = Math.Max(0, cxm - rad), xB = Math.Min(W, cxm + rad);
                for (int y = yA; y < yB; y++)
                    for (int x = xA; x < xB; x++) { int i = y * st + x * 3; hit[y * W + x] = Purple(px[i + 2], px[i + 1], px[i]); }

                var res = new List<Blob>(); var seen = new bool[W * H]; var stack = new Stack<int>();
                int minN = (int)(25 * k * k), minS = (int)(12 * k), maxS = (int)(40 * k) + 1, gap = k > 1.4f ? 3 : 2;
                for (int y = yA; y < yB; y++)
                    for (int x = xA; x < xB; x++)
                    {
                        int p = y * W + x;
                        if (!hit[p] || seen[p]) continue;
                        int n = 0, x0 = W, y0 = H, x1 = 0, y1 = 0; stack.Push(p); seen[p] = true;
                        while (stack.Count > 0)
                        {
                            int q = stack.Pop(), qx = q % W, qy = q / W; n++;
                            if (qx < x0) x0 = qx; if (qx > x1) x1 = qx; if (qy < y0) y0 = qy; if (qy > y1) y1 = qy;
                            for (int dy = -gap; dy <= gap; dy++) // portre halkayı bölebilir: küçük boşlukları birleştir
                                for (int dx = -gap; dx <= gap; dx++)
                                {
                                    int nx = qx + dx, ny = qy + dy; if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                                    int r = ny * W + nx; if (hit[r] && !seen[r]) { seen[r] = true; stack.Push(r); }
                                }
                        }
                        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                        if (n < minN || bw < minS || bh < minS || bw > maxS || bh > maxS) continue;
                        // Halka testi: merkezden 8 yöne; ikonun halkası portreyi çevreler, haritadaki mor bariyer çizgileri çevrelemez.
                        float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f; int hits = 0, reach = Math.Max(bw, bh) / 2 + 2;
                        for (int a = 0; a < 8; a++)
                        {
                            double ang = a * Math.PI / 4, ddx = Math.Cos(ang), ddy = Math.Sin(ang);
                            for (int s = 1; s <= reach; s++)
                            {
                                int rx = (int)Math.Round(cx + ddx * s), ry = (int)Math.Round(cy + ddy * s);
                                if (rx < 0 || ry < 0 || rx >= W || ry >= H) break;
                                if (hit[ry * W + rx]) { hits++; break; }
                            }
                        }
                        if (hits >= 6) res.Add(new Blob { X = (int)cx, Y = (int)cy, W = bw, H = bh });
                    }
                return res;
            }

            // ---------- Ajan tanıma: yalnızca bu maçın rakip ajanları arasında ----------

            void BuildTemplates(float k)
            {
                tpls = new List<Tpl>(); tplK = k;
                foreach (var kv in icons)
                    for (int d = (int)Math.Round(15 * k); d <= (int)Math.Round(18 * k); d++)
                        using (var bmp = new Bitmap(d, d, PixelFormat.Format24bppRgb))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.Clear(Color.FromArgb(30, 30, 34)); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(kv.Value, new Rectangle(0, 0, d, d));
                            }
                            var t = new Tpl { Agent = kv.Key, D = d, R = new float[d * d], G = new float[d * d], B = new float[d * d], M = new bool[d * d] };
                            float c = (d - 1) / 2f, rad = d / 2f - 0.8f;
                            for (int y = 0; y < d; y++)
                                for (int x = 0; x < d; x++)
                                {
                                    var p = bmp.GetPixel(x, y); int i = y * d + x;
                                    t.R[i] = p.R; t.G[i] = p.G; t.B[i] = p.B; t.M[i] = (x - c) * (x - c) + (y - c) * (y - c) <= rad * rad;
                                }
                            tpls.Add(t);
                        }
            }

            /// <summary>En iyi ajan, en yakın ikinciyle arasındaki pay (büyük = emin) ve portre merkezi.</summary>
            void Identify(byte[] px, int st, int W, int H, int cx, int cy, float k, out string agent, out double margin, out int ix, out int iy)
            {
                var best = new Dictionary<string, double>(); var at = new Dictionary<string, Point>();
                int span = (int)Math.Round(7 * k);
                foreach (var t in tpls)
                    for (int oy = -span; oy <= span; oy++)
                        for (int ox = -span; ox <= span; ox++)
                        {
                            int x0 = cx + ox - t.D / 2, y0 = cy + oy - t.D / 2;
                            if (x0 < 0 || y0 < 0 || x0 + t.D >= W || y0 + t.D >= H) continue;
                            double s = 0; int n = 0;
                            for (int y = 0; y < t.D; y++)
                                for (int x = 0; x < t.D; x++)
                                {
                                    int i = y * t.D + x; if (!t.M[i]) continue;
                                    int j = (y0 + y) * st + (x0 + x) * 3;
                                    double dr = px[j + 2] - t.R[i], dg = px[j + 1] - t.G[i], db = px[j] - t.B[i];
                                    s += Math.Sqrt(dr * dr + dg * dg + db * db); n++;
                                }
                            s /= n;
                            if (!best.TryGetValue(t.Agent, out var cur) || s < cur) { best[t.Agent] = s; at[t.Agent] = new Point(cx + ox, cy + oy); }
                        }
                var order = best.OrderBy(kv => kv.Value).ToList();
                agent = order.Count > 0 ? order[0].Key : null;
                margin = order.Count > 1 ? order[1].Value - order[0].Value : order.Count == 1 ? 30 : 0;
                ix = agent != null ? at[agent].X : cx; iy = agent != null ? at[agent].Y : cy;
            }

            // ---------- Kare işleme ----------

            /// <summary>Bu karede görülen ve kimliği belli rakipler: ajan → bölge adı.</summary>
            public List<KeyValuePair<string, string>> Process(Bitmap bmp, float k, DateTime now)
            {
                var result = new List<KeyValuePair<string, string>>();
                int W = bmp.Width, H = bmp.Height;
                var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var px = new byte[d.Stride * H]; Marshal.Copy(d.Scan0, px, 0, px.Length); int st = d.Stride; bmp.UnlockBits(d);

                if (!Calibrated)
                {
                    if (now < nextCalib) return result;
                    nextCalib = now.AddSeconds(10); // pahalı (~1 sn): harita büyük açıkken ya da skor tablosundayken tutmazsa seyrek dene
                    Calibrate(FrameFloor(px, st, W, H), W, H, k);
                    if (!Calibrated) return result;
                    nextSide = now.AddSeconds(1.5);
                }
                else if (now >= nextSide) { nextSide = now.AddSeconds(1.5); UpdateSide(FrameFloor(px, st, W, H), W, H); }
                if (tpls == null || Math.Abs(tplK - k) > 0.01f) BuildTemplates(k);

                tracks.RemoveAll(t => (now - t.Last).TotalSeconds > 1.5);
                float near = 30 * k;
                foreach (var b in Find(px, st, W, H, k))
                {
                    var tr = tracks.Where(t => (now - t.Last).TotalSeconds < 0.6).OrderBy(t => Dist(t, b)).FirstOrDefault();
                    if (tr == null || Dist(tr, b) > near) { tr = new Track(); tracks.Add(tr); }
                    // Kimlik ilk karelerde ve sonra arada bir yeniden oylanır; aradaki karelerde son bulunan portre kayması kullanılır.
                    if (tr.Frames < 3 || tr.Frames % 4 == 0 || tr.Agent == null)
                    {
                        Identify(px, st, W, H, b.X, b.Y, k, out var agent, out var margin, out int ix, out int iy);
                        if (agent != null) tr.Votes[agent] = (tr.Votes.TryGetValue(agent, out var v) ? v : 0) + Math.Min(margin, 40);
                        tr.Dx = ix - b.X; tr.Dy = iy - b.Y;
                        var ord = tr.Votes.OrderByDescending(kv => kv.Value).ToList();
                        double second = ord.Count > 1 ? ord[1].Value : 0;
                        tr.Agent = ord.Count > 0 && ord[0].Value >= 15 && ord[0].Value - second >= 10 ? ord[0].Key : null;
                    }
                    tr.X = b.X + tr.Dx; tr.Y = b.Y + tr.Dy; tr.Last = now; tr.Frames++;
                    if (tr.Agent != null) result.Add(new KeyValuePair<string, string>(tr.Agent, CalloutAt(tr.X, tr.Y)));
                }
                return result;
            }

            static float Dist(Track t, Blob b) => (float)Math.Sqrt((t.X - t.Dx - b.X) * (t.X - t.Dx - b.X) + (t.Y - t.Dy - b.Y) * (t.Y - t.Dy - b.Y));

            /// <summary>Ekran pikseli → harita görselinde 0..1 (savunmada 180° dönük) → en yakın bölge.</summary>
            string CalloutAt(float x, float y)
            {
                float u = (x - MapX) / MapS, v = (y - MapY) / MapS;
                if (defending) { u = 1 - u; v = 1 - v; }
                string best = "?"; float bd = float.MaxValue;
                foreach (var c in callouts)
                {
                    float dd = (c.Value.X - u) * (c.Value.X - u) + (c.Value.Y - v) * (c.Value.Y - v);
                    if (dd < bd) { bd = dd; best = c.Key; }
                }
                return best;
            }
        }

        public static string IconFile(string agent) => Engine.IconFile(agent);
    }

    /// <summary>
    /// Rakip hafızası şeridi: minimap'in hemen altında, oyunun üstünde, tıklamaları geçiren yarı saydam pencere.
    /// Yakalamadan dışlanır (WDA_EXCLUDEFROMCAPTURE): yayına ve kayda girmez.
    /// </summary>
    sealed class MemoryPanel : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        static readonly Color Bg = Color.FromArgb(14, 16, 22), Purple = ColorTranslator.FromHtml("#b065d6"), Muted = Color.FromArgb(150, 156, 170);
        readonly float scale;   // gerçek piksel → form pikseli (Windows ölçeklemesi; uygulama DPI'dan habersiz)
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        List<MinimapMemory.Sighting> rows = new List<MinimapMemory.Sighting>();
        const int RowH = 24, PadX = 8;

        public MemoryPanel(float scale)
        {
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; BackColor = Bg; Opacity = 0.86; DoubleBuffered = true;
            Size = new Size(250, RowH);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; // LAYERED | TRANSPARENT (tıklama geçer) | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11);
        }

        /// <summary>mapRect: minimap'in gerçek piksel konumu; şerit hemen altına, aynı sol kenara.</summary>
        public void Set(List<MinimapMemory.Sighting> list, Rectangle mapRect)
        {
            rows = list;
            int w = (int)Math.Max(220, mapRect.Width / scale * 0.8f);
            var loc = new Point((int)(mapRect.Left / scale), (int)((mapRect.Bottom + 6) / scale));
            var size = new Size(w, Math.Max(1, list.Count) * RowH + 6);
            if (Location != loc) Location = loc;
            if (Size != size) Size = size;
            Invalidate();
        }

        Image AgentIcon(string agent)
        {
            if (icons.TryGetValue(agent, out var img)) return img;
            try { using (var src = Image.FromFile(MinimapMemory.IconFile(agent))) img = new Bitmap(src, 40, 40); } catch { img = null; }
            return icons[agent] = img;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Purple)) g.FillRectangle(b, 0, 0, 3, Height);
            using (var name = new Font(L.SansBold, 9.5f))
            using (var small = new Font(L.Sans, 9f))
            {
                int y = 3;
                foreach (var r in rows)
                {
                    var ic = AgentIcon(r.AgentId);
                    if (ic != null)
                    {
                        var rc = new Rectangle(PadX, y + 2, RowH - 4, RowH - 4);
                        using (var path = new GraphicsPath()) { path.AddEllipse(rc); g.SetClip(path); g.DrawImage(ic, rc); g.ResetClip(); }
                        using (var p = new Pen(Purple, 1.5f)) g.DrawEllipse(p, rc);
                    }
                    float x = PadX + RowH + 2;
                    string when = r.Visible ? L.T("şimdi") : Ago(DateTime.Now - r.Seen);
                    var ws = g.MeasureString(when, small);
                    float right = Width - PadX - ws.Width;
                    TextRenderer.DrawText(g, r.Name, name, new Point((int)x, y + 3), Color.White, TextFormatFlags.NoPadding);
                    x += TextRenderer.MeasureText(g, r.Name, name, Size.Empty, TextFormatFlags.NoPadding).Width + 6;
                    Theme.DrawFit(g, r.Callout, small, Color.FromArgb(225, 228, 236), x, y + 4, right - x - 6);
                    using (var b = new SolidBrush(r.Visible ? Purple : Muted)) g.DrawString(when, small, b, right, y + 4);
                    y += RowH;
                }
            }
        }

        static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? L.F("{0} sn", (int)t.TotalSeconds) : L.F("{0} dk", (int)t.TotalMinutes);
    }
}
