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

        /// <summary>U, V: minimap kutusu içinde son görülen yer (0..1, ekrandaki yönüyle).</summary>
        public sealed class Sighting { public string AgentId, Name, Callout; public DateTime Seen; public bool Visible; public float U, V; }
        sealed class Seen { public string Agent, Callout; public float U, V; }

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

        /// <summary>Yeni raund: eski yerler artık yanıltıcı. Skor raund *bitince* değişir; ardından birkaç saniye
        /// hayatta kalan rakipler minimap'te görünmeye devam eder. Onlar yeni rauda taşınmasın diye bir süre görmezden gelinir.</summary>
        public void NewRound() { roundReset = true; quietUntil = DateTime.Now.AddSeconds(QuietSeconds).Ticks; lock (lk) memory.Clear(); }
        const int QuietSeconds = 15;
        long quietUntil;

        public List<Sighting> Snapshot()
        {
            lock (lk) return memory.Values.Select(s => new Sighting { AgentId = s.AgentId, Name = s.Name, Callout = s.Callout, Seen = s.Seen, Visible = s.Visible, U = s.U, V = s.V })
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
            Bitmap bmp = null, barBmp = null;
            var bar = new TopBar(eng.Icons);
            var dead = new HashSet<string>();
            var diag = new DiagRecorder();
            int frames = 0, seenFrames = 0, tick = 0; double totalMs = 0, maxMs = 0; DateTime statAt = DateTime.Now.AddMinutes(1);
            try
            {
                while (gen == generation)
                {
                    sw.Restart();
                    if (roundReset) { roundReset = false; eng.Reset(); bar.NewRound(); dead.Clear(); }
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
                        // Üst çubuk saniyede ~3 kez: ölen rakip oradan kalkar.
                        if (tick++ % 3 == 0)
                        {
                            var r = TopBar.Region(win, k);
                            if (barBmp == null || barBmp.Size != r.Size) { barBmp?.Dispose(); barBmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb); }
                            try
                            {
                                using (var g = Graphics.FromImage(barBmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
                                var d = bar.Update(barBmp, k);
                                if (d != null && !d.SetEquals(dead))
                                {
                                    // Birkaç rakip ölüyken çubuk birden 5'e dolduysa yeni raundun satın alma evresi başladı
                                    // (diriltme yalnızca bir kişiyi geri getirir); skor bilgisi gecikse de hafıza temizlenir.
                                    if (d.Count == 0 && dead.Count >= 2)
                                    {
                                        quietUntil = Math.Max(quietUntil, now.AddSeconds(5).Ticks);
                                        Log("çubuk dolu: yeni raund, hafıza temizlendi");
                                    }
                                    dead = d;
                                    Log("ölü: " + (dead.Count == 0 ? "yok" : string.Join(", ", dead.Select(eng.NameOf))));
                                }
                                diag.Bar(barBmp, now);
                            }
                            catch { }
                        }
                        if (tick % 20 == 1) diag.Map(bmp, now);
                        bool quiet = now.Ticks < quietUntil;
                        lock (lk)
                        {
                            if (gen != generation) break;
                            if (quiet) { memory.Clear(); seen.Clear(); }
                            foreach (var a in dead) memory.Remove(a);
                            foreach (var s in memory.Values) s.Visible = false;
                            foreach (var e in seen)
                            {
                                if (dead.Contains(e.Agent)) continue;
                                if (!memory.TryGetValue(e.Agent, out var m)) memory[e.Agent] = m = new Sighting { AgentId = e.Agent, Name = eng.NameOf(e.Agent) };
                                m.Callout = e.Callout; m.U = e.U; m.V = e.V; m.Seen = now; m.Visible = true;
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
            finally { bmp?.Dispose(); barBmp?.Dispose(); }
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
            int rot;                     // harita görselinin ekrandaki dönüşü, saat yönünde çeyrek tur (taraf değişince +2)
            DateTime nextCalib, nextSide;
            int calibFails;

            public string NameOf(string agent) => names.TryGetValue(agent, out var n) ? n : "?";
            public Dictionary<string, Bitmap> Icons => icons;

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
                        if (p.Length >= 4 && p[0] == mapKey)
                        {
                            MapS = int.Parse(p[1]); MapX = int.Parse(p[2]); MapY = int.Parse(p[3]);
                            rot = p.Length > 4 ? int.Parse(p[4]) : 0; Calibrated = true;
                        }
                    }
                }
                catch { }
            }

            /// <summary>Başka bir haritanın ölçüsü: minimap kutusu haritadan bağımsız, önce orada denenir.</summary>
            static bool CalibrationHint(out int s, out int x, out int y)
            {
                s = x = y = 0;
                try
                {
                    var p = File.ReadAllLines(CalibFile).Select(l => l.Split('\t')).LastOrDefault(a => a.Length >= 4);
                    if (p == null) return false;
                    s = int.Parse(p[1]); x = int.Parse(p[2]); y = int.Parse(p[3]);
                    return true;
                }
                catch { return false; }
            }

            void SaveCalibration()
            {
                try
                {
                    var lines = File.Exists(CalibFile) ? File.ReadAllLines(CalibFile).Where(l => !l.StartsWith(mapKey + "\t")).ToList() : new List<string>();
                    lines.Add($"{mapKey}\t{MapS}\t{MapX}\t{MapY}\t{rot}");
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

            /// <summary>Harita maskesi ile ekran zemininin örtüşmesi (0..1). q: saat yönünde çeyrek tur
            /// (bazı haritaların görseli oyundakine göre 90° dönük; savunma tarafı ayrıca 180°).</summary>
            double Score(bool[] floor, int W, int H, int s, int ox, int oy, int q)
            {
                int tp = 0, fp = 0, fn = 0;
                for (int y = 0; y < s; y += 2)
                    for (int x = 0; x < s; x += 2)
                    {
                        int rx = q == 0 ? x : q == 1 ? s - 1 - y : q == 2 ? s - 1 - x : y;
                        int ry = q == 0 ? y : q == 1 ? x : q == 2 ? s - 1 - y : s - 1 - x;
                        int sx = ox + rx, sy = oy + ry;
                        if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                        bool p = mask[(y * MaskN / s) * MaskN + x * MaskN / s], f = floor[sy * W + sx];
                        if (p && f) tp++; else if (p) fn++; else if (f) fp++;
                    }
                return 2.0 * tp / (2.0 * tp + fp + fn + 1);
            }

            void Calibrate(bool[] floor, int W, int H, float k)
            {
                double best = 0; int bs = 0, bx = 0, by = 0, bq = 0;
                void Search(int sA, int sB, int xA, int xB, int yA, int yB, int step, IEnumerable<int> qs)
                {
                    foreach (int q in qs.ToList())
                        for (int s = sA; s <= sB; s += step)
                            for (int ox = xA; ox <= xB; ox += step)
                                for (int oy = yA; oy <= yB; oy += step)
                                {
                                    double sc = Score(floor, W, H, s, ox, oy, q);
                                    if (sc > best) { best = sc; bs = s; bx = ox; by = oy; bq = q; }
                                }
                }
                var all = Enumerable.Range(0, 4);
                // Önce başka haritada bulunan kutu (minimap yeri haritaya göre değişmez): ~0,2 sn.
                bool hinted = CalibrationHint(out int hs, out int hx, out int hy) && Math.Abs(hs - 384 * k) < 60 * k; // çözünürlük değiştiyse geçersiz
                if (hinted) Search(hs - 6, hs + 6, hx - 6, hx + 6, hy - 6, hy + 6, 3, all);
                // Tutmazsa 1080p'de ölçülen yerin çevresinde kaba arama (~3 sn). Orada zemin hiç yoksa minimap ekranda değildir.
                if (best < 0.75 && !(hinted && best < 0.3))
                {
                    int s0 = (int)(384 * k), x0 = (int)(15 * k), y0 = (int)(35 * k);
                    Search(s0 - (int)(60 * k), s0 + (int)(60 * k), x0 - (int)(40 * k), x0 + (int)(40 * k), y0 - (int)(40 * k), y0 + (int)(40 * k), 6, all);
                }
                Search(bs - 3, bs + 3, bx - 3, bx + 3, by - 3, by + 3, 1, new[] { bq });
                Log($"kalibrasyon {mapKey}: {bs}px @ {bx},{by} · {bq * 90}° · skor {best:0.00}");
                if (best < 0.75) return; // harita büyük açık / skor tablosu: sonra yeniden
                MapS = bs; MapX = bx; MapY = by; rot = bq; Calibrated = true;
                SaveCalibration();
            }

            void UpdateSide(bool[] floor, int W, int H)
            {
                int other = (rot + 2) % 4;
                double a = Score(floor, W, H, MapS, MapX, MapY, rot), d = Score(floor, W, H, MapS, MapX, MapY, other);
                if (d > 0.6 && d - a > 0.15) rot = other;
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

            /// <summary>Bu karede görülen ve kimliği belli rakipler: ajan, bölge adı, minimap içindeki yeri (0..1).</summary>
            public List<Seen> Process(Bitmap bmp, float k, DateTime now)
            {
                var result = new List<Seen>();
                int W = bmp.Width, H = bmp.Height;
                var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var px = new byte[d.Stride * H]; Marshal.Copy(d.Scan0, px, 0, px.Length); int st = d.Stride; bmp.UnlockBits(d);

                if (!Calibrated)
                {
                    if (now < nextCalib) return result;
                    Calibrate(FrameFloor(px, st, W, H), W, H, k);
                    // Pahalı: harita büyük açıkken ya da skor tablosundayken tutmazsa giderek seyrekleşir (10 sn → 60 sn).
                    if (!Calibrated) { nextCalib = now.AddSeconds(Math.Min(60, 10 << Math.Min(3, calibFails++))); return result; }
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
                    if (tr.Agent != null) result.Add(new Seen { Agent = tr.Agent, Callout = CalloutAt(tr.X, tr.Y), U = (tr.X - MapX) / MapS, V = (tr.Y - MapY) / MapS });
                }
                return result;
            }

            static float Dist(Track t, Blob b) => (float)Math.Sqrt((t.X - t.Dx - b.X) * (t.X - t.Dx - b.X) + (t.Y - t.Dy - b.Y) * (t.Y - t.Dy - b.Y));

            /// <summary>Ekran pikseli → harita görselinde 0..1 (ekrandaki dönüş geri alınır) → en yakın bölge.</summary>
            string CalloutAt(float x, float y)
            {
                float a = (x - MapX) / MapS, b = (y - MapY) / MapS, u, v;
                switch (rot)
                {
                    case 1: u = b; v = 1 - a; break;
                    case 2: u = 1 - a; v = 1 - b; break;
                    case 3: u = 1 - b; v = a; break;
                    default: u = a; v = b; break;
                }
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

        /// <summary>
        /// Deneme sürümü tanı kaydı: maç boyunca saniyede bir üst çubuk (yalnızca rakip portreleri, oyuncu adı yok) ve
        /// 2 saniyede bir minimap karesi logs\hafiza-kayit\&lt;maç&gt;\ altına yazılır; hatalar sonradan kare kare incelenebilsin.
        /// Yalnızca son 3 maç tutulur (maç başına ~15 MB).
        /// </summary>
        sealed class DiagRecorder
        {
            const int Keep = 3;
            static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            string dir; bool failed; DateTime nextBar;

            string Dir()
            {
                if (dir != null || failed) return dir;
                try
                {
                    string root = Path.Combine(Diag.Dir, "hafiza-kayit");
                    Directory.CreateDirectory(root);
                    foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d).Skip(Keep - 1))
                        try { Directory.Delete(old, true); } catch { }
                    dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(dir);
                }
                catch { failed = true; dir = null; }
                return dir;
            }

            public void Bar(Bitmap bmp, DateTime now)
            {
                if (now < nextBar) return;
                nextBar = now.AddSeconds(1);
                var d = Dir(); if (d == null) return;
                try { bmp.Save(Path.Combine(d, $"c_{now:HHmmss_fff}.png"), ImageFormat.Png); } catch { }
            }

            public void Map(Bitmap bmp, DateTime now)
            {
                var d = Dir(); if (d == null) return;
                try
                {
                    using (var p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                        bmp.Save(Path.Combine(d, $"m_{now:HHmmss_fff}.jpg"), Jpeg, p);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Üst orta skor çubuğu, rakip tarafı (sağ yarı): canlı rakiplerin portreleri ortadan başlayarak yan yana durur,
        /// ölen kalkar ve kalanlar kayar.
        /// Kaç kişi canlı: yuvadaki kenar yoğunluğu (portre var/yok). Kim: 5'i de canlıyken alınan portre kopyalarıyla sırası bozulmadan eşleme.
        /// Hangi sıra hangi ajan: portrenin renk dağılımı ile ajan ikonununki (çubuk çizimi API ikonundan farklı piksel-art,
        /// şekil eşleşmesi Miks/Reyna gibi ajanları karıştırıyordu), maç boyunca biriken farkla tek tek atama.
        /// Ölçüler 1080p'de, ekran ortasına göre; başka çözünürlükte pencere yüksekliğiyle orantılanır.
        /// </summary>
        sealed class TopBar
        {
            const int N = 5, Bins = 4;                   // renk dağılımı: kanal başına 4 → 64 kutu
            const double MaxSame = 45;                   // aynı portrenin iki karesi arasındaki fark bundan küçük kalır

            readonly List<string> agents;
            readonly List<double[]> iconHist;
            byte[][] slots; int cw, ch;                // 5'i de canlıyken portre kopyaları (gerçek piksel)
            readonly double[,] cost = new double[N, N];  // yuva × ajan, birikmiş fark
            int costN, fullReads;
            string[] order;                              // yuva → ajan (güvenilir olduğunda)
            string pendingKey; int pendingRuns;
            HashSet<string> reported = new HashSet<string>();

            public TopBar(Dictionary<string, Bitmap> agentIcons)
            {
                agents = agentIcons.Keys.Take(N).ToList();
                iconHist = agents.Select(a => IconHist(agentIcons[a])).ToList();
            }

            static int Bin(int r, int g, int b) => (r * Bins / 256) * Bins * Bins + (g * Bins / 256) * Bins + b * Bins / 256;

            /// <summary>İkonun çubukta görünen üst %80'i (alt kenar kesik), opak pikseller.</summary>
            static double[] IconHist(Bitmap b)
            {
                var h = new double[Bins * Bins * Bins]; double n = 0; int step = Math.Max(1, b.Width / 256);
                for (int y = 0; y < b.Height * 8 / 10; y += step)
                    for (int x = 0; x < b.Width; x += step)
                    {
                        var c = b.GetPixel(x, y); if (c.A < 230) continue;
                        h[Bin(c.R, c.G, c.B)]++; n++;
                    }
                for (int i = 0; i < h.Length; i++) h[i] /= Math.Max(1, n);
                return h;
            }

            /// <summary>Yuvadaki portre pikselleri: şerit renginden (portrelerin iki yanındaki boşluk) belirgin farklı olanlar.</summary>
            static double[] SlotHist(byte[] px, int st, int s, float k)
            {
                int x0 = (int)((8 + 66.5 * s) * k), step = Math.Max(1, (int)Math.Round(k));
                var br = new List<int>(); var bg = new List<int>(); var bb = new List<int>();
                for (int y = (int)(32 * k); y < (int)(66 * k); y += 2 * step)
                    foreach (int x in new[] { x0 - (int)(6 * k), x0 + (int)(50 * k) })
                    {
                        int i = y * st + x * 3; bb.Add(px[i]); bg.Add(px[i + 1]); br.Add(px[i + 2]);
                    }
                int mr = Median(br), mg = Median(bg), mb = Median(bb);
                var h = new double[Bins * Bins * Bins]; double n = 0;
                for (int y = (int)(30 * k); y < (int)(69 * k); y += step)
                    for (int x = x0; x < x0 + (int)(44 * k); x += step)
                    {
                        int i = y * st + x * 3, r = px[i + 2], g = px[i + 1], b = px[i];
                        if (Math.Abs(r - mr) + Math.Abs(g - mg) + Math.Abs(b - mb) < 45) continue;
                        h[Bin(r, g, b)]++; n++;
                    }
                for (int i = 0; i < h.Length; i++) h[i] /= Math.Max(1, n);
                return h;
            }

            static int Median(List<int> v) { v.Sort(); return v[v.Count / 2]; }

            /// <summary>Bhattacharyya uzaklığı (0 = aynı dağılım, 1 = hiç örtüşmüyor).</summary>
            static double HistDist(double[] p, double[] q) { double s = 0; for (int i = 0; i < p.Length; i++) s += Math.Sqrt(p[i] * q[i]); return Math.Sqrt(Math.Max(0, 1 - s)); }

            /// <summary>Okunacak ekran bölgesi: ekran ortasının 200 px sağından 360 px, üstten 90 px (1080p).</summary>
            public static Rectangle Region(Rectangle win, float k) =>
                new Rectangle(win.Left + win.Width / 2 + (int)(200 * k), win.Top, (int)(360 * k), (int)(90 * k));

            public void NewRound()
            {
                slots = null; pendingKey = null; pendingRuns = 0; reported = new HashSet<string>();
                for (int i = 0; i < N; i++) for (int j = 0; j < N; j++) cost[i, j] *= 0.5; // sıra maç boyunca aynı kalır; yine de taze kanıt ağır bassın
                costN /= 2;
            }

            /// <summary>Ölü rakipler (aynı sonuç ~1 sn sürdüyse); emin değilse null.</summary>
            public HashSet<string> Update(Bitmap bmp, float k)
            {
                var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var px = new byte[d.Stride * bmp.Height]; Marshal.Copy(d.Scan0, px, 0, px.Length); int st = d.Stride; bmp.UnlockBits(d);

                var on = new bool[N];
                for (int i = 0; i < N; i++)
                {
                    double e = Edges(px, st, i, k);
                    if (e >= 40 && e < 100) return null; // ne dolu ne boş: geçiş, efekt
                    on[i] = e >= 100;
                }
                int n = 0; while (n < N && on[n]) n++;
                for (int i = n; i < N; i++) if (on[i]) return null; // canlılar art arda gelir; değilse görüntü bozuk (harita, efekt)
                if (n == 0) return null;                            // çubuk yok: raund arası, skor tablosu

                cw = (int)(44 * k); ch = (int)(41 * k);
                int[] alive;
                if (n == N)
                {
                    var cur = Enumerable.Range(0, N).Select(i => Crop(px, st, i, k)).ToArray();
                    // Raund ortasında boş bir yuva yanlışlıkla dolu okunursa kopyalar bozulmasın: yalnızca her portre eskisine hâlâ benziyorsa yenilenir.
                    if (slots != null && Enumerable.Range(0, N).Any(i => Diff(cur[i], slots[i], k) > MaxSame)) return null;
                    slots = cur;
                    if (fullReads++ % 6 == 0) LearnOrder(px, st, k); // ~2 sn'de bir (saniyede 3 okuma)
                    alive = Enumerable.Range(0, N).ToArray();
                }
                else
                {
                    if (slots == null) return null;
                    var cur = Enumerable.Range(0, n).Select(i => Crop(px, st, i, k)).ToArray();
                    var diff = new double[n, N];
                    for (int j = 0; j < n; j++) for (int s = 0; s < N; s++) diff[j, s] = Diff(cur[j], slots[s], k);
                    double best = double.MaxValue, second = double.MaxValue; int[] bs = null;
                    foreach (var sub in Subsets(n))
                    {
                        double c = 0; for (int j = 0; j < n; j++) c += diff[j, sub[j]];
                        if (c < best) { second = best; best = c; bs = sub; } else if (c < second) second = c;
                    }
                    if ((second - best) / n < 25) return null; // iki açıklama birbirine yakın: bekle
                    alive = bs;
                }
                if (order == null) return null;

                var deadSet = new HashSet<string>(Enumerable.Range(0, N).Where(i => !alive.Contains(i) && order[i] != null).Select(i => order[i]));
                string key = string.Join(",", deadSet.OrderBy(x => x));
                if (key != pendingKey) { pendingKey = key; pendingRuns = 1; return null; }
                // Ölüm ~1 sn tutarlılıkla kabul edilir; birinin dirilmesi (yalnızca Sage yapabilir) ~3 sn ister,
                // kısa süreli yanlış okumalar ölüleri haritaya geri getirmesin.
                bool revive = reported.Any(a => !deadSet.Contains(a));
                if (++pendingRuns < (revive ? 9 : 3)) return null;
                reported = deadSet;
                return deadSet;
            }

            static IEnumerable<int[]> Subsets(int n)
            {
                for (int m = 0; m < 1 << N; m++)
                {
                    var s = Enumerable.Range(0, N).Where(i => (m >> i & 1) == 1).ToArray();
                    if (s.Length == n) yield return s;
                }
            }

            /// <summary>Yuvanın portre alanında komşu pikseller arası ortalama fark: piksel-art portre 130..200,
            /// boş yarı saydam şerit 0..30 (açık zeminli haritalarda bile; portre altı çizgisi orada zayıf kalıyordu).</summary>
            static double Edges(byte[] px, int st, int slot, float k)
            {
                int x0 = (int)((14 + 66.5 * slot) * k), y0 = (int)(33 * k), w = (int)(32 * k), h = Math.Max(1, (int)Math.Round(k)); double s = 0; int n = 0;
                for (int y = y0; y < y0 + w; y += h)
                    for (int x = x0; x < x0 + w; x += h)
                    {
                        int i = y * st + x * 3, r = i + h * 3, d = i + h * st;
                        s += Math.Abs(px[i] - px[r]) + Math.Abs(px[i + 1] - px[r + 1]) + Math.Abs(px[i + 2] - px[r + 2])
                           + Math.Abs(px[i] - px[d]) + Math.Abs(px[i + 1] - px[d + 1]) + Math.Abs(px[i + 2] - px[d + 2]);
                        n++;
                    }
                return s / n;
            }

            byte[] Crop(byte[] px, int st, int slot, float k)
            {
                int x0 = (int)((8 + 66.5 * slot) * k), y0 = (int)(28 * k); var c = new byte[cw * ch * 3];
                for (int y = 0; y < ch; y++) Array.Copy(px, (y0 + y) * st + x0 * 3, c, y * cw * 3, cw * 3);
                return c;
            }

            /// <summary>İki portre kopyası arasındaki ortalama fark; ±2 px kaymaya izin verir.</summary>
            double Diff(byte[] a, byte[] b, float k)
            {
                double best = double.MaxValue; int m = 3, step = k > 1.4f ? 2 : 1;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        double s = 0; int n = 0;
                        for (int y = m; y < ch - m; y += step)
                            for (int x = m; x < cw - m; x += step)
                            {
                                int i = (y * cw + x) * 3, j = ((y + dy) * cw + x + dx) * 3;
                                s += Math.Abs(a[i] - b[j]) + Math.Abs(a[i + 1] - b[j + 1]) + Math.Abs(a[i + 2] - b[j + 2]); n++;
                            }
                        if (s / n < best) best = s / n;
                    }
                return best;
            }

            /// <summary>5'i de canlıyken: her yuvanın renk dağılımını ajan ikonlarınınkiyle karşılaştır, birikmiş farkla en iyi atamayı bul.
            /// Üç kayıtlı maçta doğru ajan 0,12..0,29, en yakın yanlış en az 0,40 uzaklıkta.</summary>
            void LearnOrder(byte[] px, int st, float k)
            {
                for (int s = 0; s < N; s++)
                {
                    var h = SlotHist(px, st, s, k);
                    for (int a = 0; a < agents.Count; a++) cost[s, a] += HistDist(h, iconHist[a]);
                }
                costN++;

                // En iyi ve ikinci en iyi atama (5! = 120); fark yeterince açıksa sıra kabul edilir.
                // 5'ten az ajan bilinirse boş kalan yuvaların dizilişi maliyeti değiştirmez: aynı atama ikinci sayılmaz.
                double b1 = double.MaxValue, b2 = double.MaxValue; int[] bp = null;
                foreach (var p in Perms(Enumerable.Range(0, N).ToArray(), 0))
                {
                    double c = 0; for (int a = 0; a < agents.Count; a++) c += cost[p[a], a];
                    bool same = bp != null && Enumerable.Range(0, agents.Count).All(a => p[a] == bp[a]);
                    if (same) continue;
                    if (c < b1) { b2 = b1; b1 = c; bp = (int[])p.Clone(); } else if (c < b2) b2 = c;
                }
                bool sure = costN >= 2 && (b2 - b1) / costN >= 0.15;
                if (!sure) return;
                var o = new string[N];
                for (int a = 0; a < agents.Count; a++) o[bp[a]] = agents[a];
                if (order == null || !o.SequenceEqual(order))
                {
                    order = o;
                    Log("çubuk sırası: " + string.Join(", ", o.Select(x => x == null ? "?" : RiotLocal.Title(ValApi.AgentName(x)))) + $" · pay {(b2 - b1) / costN:0.00}");
                }
            }

            static IEnumerable<int[]> Perms(int[] a, int i)
            {
                if (i == a.Length) { yield return a; yield break; }
                for (int j = i; j < a.Length; j++)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    foreach (var p in Perms(a, i + 1)) yield return p;
                    (a[i], a[j]) = (a[j], a[i]);
                }
            }
        }
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

        internal static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? L.F("{0} sn", (int)t.TotalSeconds) : L.F("{0} dk", (int)t.TotalMinutes);
    }

    /// <summary>
    /// Rakip hafızası, harita üstünde: minimap'in tam üstüne oturan, piksel başına saydam katman. Şu an görünmeyen
    /// rakiplerin ikonu son görüldükleri yerde, kesik halka ve geçen süreyle durur; eskidikçe soluklaşır.
    /// Şu an görülen rakip çizilmez (oyun zaten gösteriyor). Tıklama geçer, yayına/kayda girmez.
    /// </summary>
    sealed class MemoryMarks : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref Blend pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

        static readonly Color Purple = ColorTranslator.FromHtml("#b065d6");
        readonly float scale;   // gerçek piksel → form pikseli
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        string drawn;

        public MemoryMarks(float scale)
        {
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11);
        }

        /// <summary>Çizilecek bir şey var mı (şu an görünmeyen, yeri bilinen rakip).</summary>
        public static bool Any(List<MinimapMemory.Sighting> list) => list.Any(s => !s.Visible);

        public void Set(List<MinimapMemory.Sighting> list, Rectangle mapRect)
        {
            var rc = new Rectangle((int)(mapRect.Left / scale), (int)(mapRect.Top / scale), Math.Max(1, (int)(mapRect.Width / scale)), Math.Max(1, (int)(mapRect.Height / scale)));
            var marks = list.Where(s => !s.Visible).OrderBy(s => s.Seen).ToList(); // en yeni en üstte
            // Saniyede birden sık değişmez; aynıysa yeniden çizme.
            string key = rc + "|" + string.Join(";", marks.Select(s => $"{s.AgentId}{s.U:0.000}{s.V:0.000}{(int)(DateTime.Now - s.Seen).TotalSeconds}"));
            if (key == drawn && Visible) return;
            drawn = key;
            using (var bmp = Render(marks, rc.Size)) Push(bmp, rc.Location);
        }

        Bitmap Render(List<MinimapMemory.Sighting> marks, Size size)
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font(L.SansBold, 6.25f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float d = Math.Max(12, size.Width * 0.046f); // oyunun ikonundan biraz küçük (~18 px @ 1080p)
                foreach (var s in marks)
                {
                    var age = DateTime.Now - s.Seen;
                    // İlk 5 sn tam, sonra 40 sn'ye kadar yavaşça %50'ye iner.
                    float a = (float)Math.Max(0.5, 1 - Math.Max(0, age.TotalSeconds - 5) / 70);
                    float cx = s.U * size.Width, cy = s.V * size.Height;
                    var rc = new RectangleF(cx - d / 2, cy - d / 2, d, d);
                    using (var bg = new SolidBrush(Color.FromArgb((int)(220 * a), 14, 16, 22))) g.FillEllipse(bg, RectangleF.Inflate(rc, 1.5f, 1.5f));
                    var ic = AgentIcon(s.AgentId);
                    if (ic != null)
                        using (var path = new GraphicsPath())
                        using (var ia = new ImageAttributes())
                        {
                            path.AddEllipse(rc); g.SetClip(path);
                            ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                            g.DrawImage(ic, Rectangle.Round(rc), 0, 0, ic.Width, ic.Height, GraphicsUnit.Pixel, ia);
                            g.ResetClip();
                        }
                    using (var p = new Pen(Color.FromArgb((int)(255 * a), Purple), 1.5f) { DashPattern = new[] { 2.2f, 1.4f } }) g.DrawEllipse(p, rc);
                    // Geçen süre: ikonun altında küçük hap
                    string t = MemoryPanel.Ago(age);
                    var ts = g.MeasureString(t, font);
                    var pill = new RectangleF(cx - ts.Width / 2 - 1, rc.Bottom + 1, ts.Width + 2, ts.Height - 1);
                    if (pill.Bottom > size.Height) pill.Y = rc.Top - pill.Height - 1; // alt kenarda: üstüne
                    using (var bg = new SolidBrush(Color.FromArgb((int)(200 * a), 14, 16, 22))) g.FillRectangle(bg, pill);
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), 235, 236, 242))) g.DrawString(t, font, b, pill.X + 1, pill.Y - 0.5f);
                }
            }
            return bmp;
        }

        Image AgentIcon(string agent)
        {
            if (icons.TryGetValue(agent, out var img)) return img;
            try { using (var src = Image.FromFile(MinimapMemory.IconFile(agent))) img = new Bitmap(src, 64, 64); } catch { img = null; }
            return icons[agent] = img;
        }

        /// <summary>Piksel başına saydamlıkla pencereyi günceller (UpdateLayeredWindow, önceden çarpılmış ARGB).</summary>
        void Push(Bitmap bmp, Point at)
        {
            if (!IsHandleCreated) CreateHandle();
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hb);
                var size = bmp.Size; var src = Point.Empty;
                var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
                if (Bounds != new Rectangle(at, size)) Bounds = new Rectangle(at, size);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (hb != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hb); }
                DeleteDC(mem);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var i in icons.Values) i?.Dispose();
            base.Dispose(disposing);
        }
    }
}
