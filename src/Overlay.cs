using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Yayına gömülen katmanı ekran çözünürlüğünde şeffaf bir PNG olarak çizer ve en güncelini
    /// bellekte tutar (<see cref="Current"/>); Streamer bunu bir pipe ile ffmpeg'e akıtır.
    /// Görüntü yalnızca içerik değişince (veya animasyon sırasında) yeniden çizilir, boştayken CPU harcamaz.
    /// Yerleşim Valorant HUD'ını kapatmayacak şekilde: rank paneli minimap'in
    /// altında solda, maç sonu bandı kill feed'in altında sağda.
    /// </summary>
    sealed class OverlayRenderer : IDisposable
    {
        sealed class Banner { public string Title, Sub, IconUrl; public Color Color; }

        static readonly Color Panel = Color.FromArgb(228, 14, 16, 22);
        static readonly Color Accent = ColorTranslator.FromHtml("#ff4655");
        static readonly Color Win = ColorTranslator.FromHtml("#3ddc97");
        static readonly Color Loss = ColorTranslator.FromHtml("#ff5a6e");
        static readonly Color Muted = ColorTranslator.FromHtml("#9aa3b2");
        const int BannerMs = 6500, SlideMs = 450;

        /// <summary>En son çizilen katman: W×H ham BGRA.</summary>
        public byte[] Current { get; private set; }
        /// <summary>Panelde hiçbir şey yok (oyun sırasında çoğu zaman). Boş tam ekran katmanı her karede
        /// karıştırmak ffmpeg'de çekirdeğin ~%25'ini yiyordu: Streamer boşken karıştırmayı kapatır.</summary>
        public bool Blank { get; private set; }
        public event Action<bool> BlankChanged;
        readonly int W, H;
        readonly float s;
        readonly FontFamily family;
        readonly FontStyle style;
        readonly Timer timer;
        readonly object lk = new object();
        readonly Queue<Banner> queue = new Queue<Banner>();
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        readonly string cacheDir;
        readonly Bitmap canvas;
        readonly object canvasLk = new object();

        ValoState state;
        public string PlayerName = "";
        Banner banner;
        DateTime bannerStart, popStart;
        int popDelta;
        bool cover, summary, dirty = true;
        int lastPhase = -1;

        public bool Cover { get => cover; set { lock (lk) { cover = value; if (value) summary = false; dirty = true; } } }
        public bool Summary { get => summary; set { lock (lk) { summary = value; if (value) cover = false; dirty = true; } } }
        /// <summary>Seçili uygulama önde değil: ekran örtülü, "birazdan dönüyorum" kartı gösterilir.</summary>
        public bool Privacy { get => privacy; set { lock (lk) { if (privacy != value) { privacy = value; dirty = true; } } } }
        public string PrivacyApp = "Valorant";
        bool privacy;
        /// <summary>Menüde/sırada: bekleme ekranı (sıra sayacı, oturum, son maç). QueueSince null = sırada değil.</summary>
        public bool Break { get => brk; set { lock (lk) { if (brk != value) { brk = value; dirty = true; } } } }
        public DateTime? QueueSince { get => queueSince; set { lock (lk) { if (queueSince != value) { queueSince = value; dirty = true; } } } }
        bool brk;
        DateTime? queueSince;
        /// <summary>Radar rozeti açıkken aynı bilgiyi gösteren rank paneli gizlenir (ekran kalabalıklaşmasın).</summary>
        public bool RankPanel { get => rankPanel; set { lock (lk) { if (rankPanel != value) { rankPanel = value; dirty = true; } } } }
        bool rankPanel = true;

        public OverlayRenderer(int width, int height)
        {
            W = width; H = height; s = height / 1080f;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            cacheDir = Path.Combine(Settings.Dir, "cache");
            Directory.CreateDirectory(cacheDir);

            family = L.DisplayFamily(out style);
            canvas = new Bitmap(W, H, PixelFormat.Format32bppPArgb); // önceden çarpılmış: çizim hızlı, Raw düz alfaya çevirir

            Render();
            timer = new Timer(_ => Tick(), null, 66, 66);
        }

        /// <summary>Kareyi okuyan var mı (yayın/test açık). Yokken menüdeki bekleme ekranı bile saniyede bir
        /// tam ekran çiziliyordu (çizim ~20 ms): boşta çekirdeğin ~%3'ü.</summary>
        public Func<bool> Needed = () => true;

        /// <summary>Yayın başlarken: bekletilen çizimi hemen yap, Streamer güncel Current ve Blank'i okusun.</summary>
        public void Refresh()
        {
            lock (lk) dirty = false;
            try { Render(); } catch (Exception e) { Streamer.Log("overlay: " + e.Message); }
        }

        public void SetState(ValoState st) { lock (lk) { state = st; dirty = true; } }
        public void PopRr(int delta) { lock (lk) { popDelta = delta; popStart = DateTime.Now; dirty = true; } }

        public void ShowBanner(string title, string sub, Color color, string iconUrl)
        {
            lock (lk) queue.Enqueue(new Banner { Title = title, Sub = sub, Color = color, IconUrl = iconUrl });
        }

        public static string TierIcon(int id) => $"https://media.valorant-api.com/competitivetiers/03621f52-342b-cf4e-4f86-9350a49c6d04/{id}/largeicon.png";
        public static string AgentIcon(string id) => $"https://media.valorant-api.com/agents/{id}/displayicon.png";

        int ticking;

        void Tick()
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try { TickCore(); } finally { ticking = 0; }
        }

        void TickCore()
        {
            lock (lk)
            {
                var now = DateTime.Now;
                if (banner == null && queue.Count > 0) { banner = queue.Dequeue(); bannerStart = now; dirty = true; }
                int phase = 0;
                if (banner != null)
                {
                    double t = (now - bannerStart).TotalMilliseconds;
                    if (t > BannerMs) { banner = null; dirty = true; }
                    else if (t < SlideMs || t > BannerMs - SlideMs) dirty = true;
                    else phase = 1;
                }
                if (popDelta != 0 && (now - popStart).TotalSeconds > 4) { popDelta = 0; dirty = true; }
                if ((cover || summary || privacy || brk) && now.Millisecond < 70) dirty = true; // saniyede bir "canlı" noktası
                if (phase != lastPhase) { lastPhase = phase; dirty = true; }
                if (!dirty || !Needed()) return; // yayın kapalıyken çizim bekler (dirty kalır), açılınca Refresh tazeler
                dirty = false;
            }
            try { Render(); } catch (Exception e) { Streamer.Log("overlay: " + e.Message); }
        }

        Font F(float px) => new Font(family, px * s, style, GraphicsUnit.Pixel);
        Font Small(float px) => new Font(L.SansBold, px * s, FontStyle.Regular, GraphicsUnit.Pixel);
        float X(float v) => v * s;

        void Render()
        {
            ValoState st; Banner b; double bt; int pop; bool cov, sum, priv, br; DateTime? qs;
            lock (lk) { st = state; b = banner; bt = (DateTime.Now - bannerStart).TotalMilliseconds; pop = popDelta; cov = cover; sum = summary; priv = privacy; br = brk; qs = queueSince; }

            lock (canvasLk) // Refresh ve zamanlayıcı aynı anda çizebilir: tuval tek
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                if (cov) DrawCover(g, st);
                else if (sum) DrawSummary(g, st);
                else if (br) { DrawBreak(g, st, qs); if (b != null) DrawBanner(g, b, bt); } // maç sonucu bandı bekleme ekranında da görünsün
                else if (priv) DrawPrivacy(g, st);
                else
                {
                    if (st != null && st.TierId > 0 && rankPanel) DrawRankPanel(g, st, pop);
                    if (b != null) DrawBanner(g, b, bt);
                }
                bool blank = !cov && !sum && !priv && !br && !(st != null && st.TierId > 0 && rankPanel) && b == null;

                Current = Layer.Raw(canvas); // ham BGRA: 1080p PNG sıkıştırması kare başına ~36 ms tutuyordu
                // Önce yeni kare, sonra bildirim: karıştırma açıldığında ffmpeg eski boş kareyi değil yenisini görür.
                if (blank != Blank) { Blank = blank; BlankChanged?.Invoke(blank); }
            }
        }

        // ---------- Parçalar ----------

        GraphicsPath Cut(RectangleF r, float cut)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] {
                new PointF(r.Left, r.Top), new PointF(r.Right, r.Top),
                new PointF(r.Right, r.Bottom - cut), new PointF(r.Right - cut, r.Bottom), new PointF(r.Left, r.Bottom) });
            return p;
        }

        void DrawPanel(Graphics g, RectangleF r, Color edge, float edgeW = 4)
        {
            using (var path = Cut(r, X(14)))
            using (var br = new SolidBrush(Panel)) g.FillPath(br, path);
            using (var br = new SolidBrush(edge)) g.FillRectangle(br, r.Left, r.Top, X(edgeW), r.Height);
        }

        void Text(Graphics g, string text, Font f, Color c, float x, float y)
        {
            using (var br = new SolidBrush(c)) g.DrawString(text, f, br, x, y);
        }

        void DrawRankPanel(Graphics g, ValoState st, int pop)
        {
            var r = new RectangleF(X(18), X(440), X(320), X(132));
            DrawPanel(g, r, Accent);
            var tier = Tiers.Color(st.TierId);
            var icon = Icon(TierIcon(st.TierId));
            if (icon != null) g.DrawImage(icon, r.Left + X(16), r.Top + X(14), X(64), X(64));

            float x = r.Left + X(94);
            using (var f = F(24)) Theme.DrawFit(g, st.TierName.ToUpper(L.Culture), f, tier, x, r.Top + X(12), r.Right - x - X(10));
            using (var bg = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(bg, x, r.Top + X(48), X(200), X(7));
            using (var fill = new LinearGradientBrush(new PointF(x, 0), new PointF(x + X(200), 0), tier, Color.White))
                g.FillRectangle(fill, x, r.Top + X(48), X(200) * Math.Min(100, st.Rr) / 100f, X(7));
            using (var f = Small(15)) Text(g, st.Rr + " RR", f, Muted, x, r.Top + X(60));

            // Galibiyet/mağlubiyet harfleri dilin "{0}G  {1}M" çevirisinden (G/M, W/L, V/D…); öğeler ölçülerek yan yana dizilir.
            float y = r.Top + X(94), nx;
            string fmt = L.T("{0}G  {1}M");
            string winMark = Between(fmt, "{0}", "{1}").Trim(), lossMark = After(fmt, "{1}").Trim();
            using (var f = F(22))
            {
                float wx = r.Left + X(16);
                float tight = winMark.Length > 1 ? 0 : X(4); // tek harf sayıya yapışık, kısaltma ("поб.") bir boşluk ötede
                Text(g, st.Wins.ToString(), f, Win, wx, y); wx += g.MeasureString(st.Wins.ToString(), f).Width - tight;
                Text(g, winMark, f, Muted, wx, y); wx += g.MeasureString(winMark, f).Width + X(4);
                float lx = Math.Max(r.Left + X(62), wx);
                Text(g, st.Losses.ToString(), f, Loss, lx, y); lx += g.MeasureString(st.Losses.ToString(), f).Width - tight;
                Text(g, lossMark, f, Muted, lx, y);
                nx = Math.Max(r.Left + X(112), lx + g.MeasureString(lossMark, f).Width + X(8));
            }
            using (var f = F(17))
            {
                string net = (st.NetRr > 0 ? "+" : st.NetRr < 0 ? "−" : "±") + Math.Abs(st.NetRr) + " RR";
                Text(g, net, f, st.NetRr > 0 ? Win : st.NetRr < 0 ? Loss : Muted, nx, y + X(4));
                float sx = Math.Max(r.Left + X(210), nx + g.MeasureString(net, f).Width + X(8));
                if (st.Streak >= 2) Theme.DrawFit(g, L.T("SERİ") + " ×" + st.Streak, f, ColorTranslator.FromHtml("#ffb547"), sx, y + X(4), r.Right - sx - X(8));
            }

            if (pop != 0)
                using (var f = F(30))
                    Text(g, (pop > 0 ? "+" : "−") + Math.Abs(pop), f, pop > 0 ? Win : Loss, r.Right + X(10), r.Top + X(30));
        }

        void DrawBanner(Graphics g, Banner b, double t)
        {
            double k = t < SlideMs ? t / SlideMs : t > BannerMs - SlideMs ? (BannerMs - t) / SlideMs : 1;
            k = 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, k)), 3);
            float w = X(540), h = X(112);
            float x = W - (float)(k * (w + X(20)));
            var r = new RectangleF(x, X(330), w, h);
            DrawPanel(g, r, b.Color, 8);
            float tx = r.Left + X(26);
            var icon = b.IconUrl != null ? Icon(b.IconUrl) : null;
            if (icon != null) { g.DrawImage(icon, tx, r.Top + X(18), X(76), X(76)); tx += X(92); }
            using (var f = F(44)) Theme.DrawFit(g, b.Title, f, b.Color, tx, r.Top + X(10), r.Right - tx - X(18));
            using (var f = Small(18)) Theme.DrawFit(g, b.Sub, f, Color.White, tx + X(2), r.Top + X(70), r.Right - tx - X(20));
        }

        static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal), j = s.IndexOf(b, StringComparison.Ordinal);
            return i < 0 || j < i ? "" : s.Substring(i + a.Length, j - i - a.Length);
        }

        static string After(string s, string a) { int i = s.IndexOf(a, StringComparison.Ordinal); return i < 0 ? "" : s.Substring(i + a.Length); }

        void FullBackground(Graphics g, string kicker)
        {
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, W, H), ColorTranslator.FromHtml("#0b0d12"), ColorTranslator.FromHtml("#22101a"), 35f))
                g.FillRectangle(br, 0, 0, W, H);
            using (var br = new SolidBrush(Color.FromArgb(18, Accent)))
                for (int i = -4; i < 14; i++)
                    g.FillPolygon(br, new[] { new PointF(X(i * 220), H), new PointF(X(i * 220 + 90), H), new PointF(X(i * 220 + 90 + 700), 0), new PointF(X(i * 220 + 700), 0) });
            using (var br = new SolidBrush(Accent)) g.FillRectangle(br, 0, 0, W, X(6));
            bool blink = DateTime.Now.Second % 2 == 0;
            using (var br = new SolidBrush(blink ? Accent : Color.FromArgb(90, Accent))) g.FillEllipse(br, X(60), X(62), X(16), X(16));
            using (var f = F(20)) Text(g, kicker, f, Color.White, X(88), X(54));
        }

        void Centered(Graphics g, string text, Font f, Color c, float y)
        {
            var ff = Theme.Fit(g, text, f, W - X(160)); // uzun çeviri ekran kenarına taşmasın
            var size = g.MeasureString(text, ff);
            Text(g, text, ff, c, (W - size.Width) / 2, y + (f.GetHeight(g) - ff.GetHeight(g)) / 2);
            if (ff != f) ff.Dispose();
        }

        void DrawCover(Graphics g, ValoState st)
        {
            FullBackground(g, L.T("CANLI") + " · " + PlayerName.ToUpper(L.Culture));
            using (var f = F(30)) Centered(g, L.T("SNIPER KORUMASI AKTİF"), f, Accent, X(330));
            using (var f = F(96)) Centered(g, L.T("AJAN SEÇİMİ"), f, Color.White, X(380));
            using (var f = Small(26)) Centered(g, L.T("Sıra / ajan seçimi gizleniyor — maç başlayınca dönüyoruz"), f, Muted, X(510));
            if (st != null && st.TierId > 0) DrawStatsRow(g, st, X(640));
        }

        void DrawPrivacy(Graphics g, ValoState st)
        {
            FullBackground(g, L.T("CANLI") + " · " + PlayerName.ToUpper(L.Culture));
            using (var f = F(30)) Centered(g, L.T("KISA BİR ARA"), f, Accent, X(330));
            using (var f = F(96)) Centered(g, L.T("BİRAZDAN DÖNÜYORUM"), f, Color.White, X(380));
            using (var f = Small(26)) Centered(g, L.F("{0} açılınca yayın kendiliğinden devam eder", PrivacyApp), f, Muted, X(510));
            if (st != null && st.TierId > 0) DrawStatsRow(g, st, X(640));
        }

        void DrawBreak(Graphics g, ValoState st, DateTime? queueSince)
        {
            FullBackground(g, L.T("CANLI") + " · " + PlayerName.ToUpper(L.Culture));
            if (queueSince is DateTime q)
            {
                var t = DateTime.Now - q;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                using (var f = F(30)) Centered(g, L.T("SIRA BEKLENİYOR"), f, Accent, X(230));
                using (var f = F(150)) Centered(g, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}", f, Color.White, X(270));
                using (var f = Small(24)) Centered(g, L.T("Maç bulununca yayın kendiliğinden oyuna döner"), f, Muted, X(460));
            }
            else
            {
                using (var f = F(30)) Centered(g, L.T("LOBİDE"), f, Accent, X(260));
                using (var f = F(96)) Centered(g, L.T("SIRADAKİ MAÇ BİRAZDAN"), f, Color.White, X(310));
                using (var f = Small(24)) Centered(g, L.T("Sıraya girilince sayaç başlar"), f, Muted, X(440));
            }
            if (st == null) return;
            if (st.TierId > 0) DrawStatsRow(g, st, X(540));
            var last = st.Last;
            if (last != null)
            {
                var res = last.Result == "W" ? Win : last.Result == "L" ? Loss : Muted;
                var r = new RectangleF((W - X(760)) / 2, X(730), X(760), X(130));
                DrawPanel(g, r, res, 6);
                var icon = Icon(AgentIcon(last.AgentId));
                if (icon != null) g.DrawImage(icon, r.Left + X(26), r.Top + X(17), X(96), X(96));
                float tw = r.Width - X(160);
                using (var f = Small(18)) Theme.DrawFit(g, L.T("SON MAÇ") + " · " + (last.Result == "W" ? L.T("ZAFER") : last.Result == "L" ? L.T("YENİLGİ") : L.T("BERABERE")), f, res, r.Left + X(140), r.Top + X(18), tw);
                using (var f = F(40)) Theme.DrawFit(g, $"{last.Agent} · {last.Kda}", f, Color.White, r.Left + X(140), r.Top + X(42), tw);
                using (var f = Small(18)) Theme.DrawFit(g, $"{last.Map} {last.Score} · HS {L.Pct(last.Hs)}", f, Muted, r.Left + X(142), r.Top + X(94), tw);
            }
        }

        void DrawSummary(Graphics g, ValoState st)
        {
            FullBackground(g, L.T("YAYIN ÖZETİ") + " · " + PlayerName.ToUpper(L.Culture));
            using (var f = F(96)) Centered(g, L.T("GG, TEŞEKKÜRLER!"), f, Color.White, X(250));
            if (st == null) return;
            DrawStatsRow(g, st, X(420));
            var best = st.SessionMatches.OrderByDescending(m => m.K).FirstOrDefault();
            if (best != null)
            {
                var r = new RectangleF((W - X(760)) / 2, X(640), X(760), X(130));
                DrawPanel(g, r, ColorTranslator.FromHtml("#ffb547"), 6);
                var icon = Icon(AgentIcon(best.AgentId));
                if (icon != null) g.DrawImage(icon, r.Left + X(26), r.Top + X(17), X(96), X(96));
                float tw = r.Width - X(160);
                using (var f = Small(18)) Theme.DrawFit(g, L.T("GÜNÜN MAÇI"), f, ColorTranslator.FromHtml("#ffb547"), r.Left + X(140), r.Top + X(18), tw);
                using (var f = F(40)) Theme.DrawFit(g, $"{best.Agent} · {best.Kda}", f, Color.White, r.Left + X(140), r.Top + X(42), tw);
                using (var f = Small(18)) Theme.DrawFit(g, $"{best.Map} {best.Score} · HS {L.Pct(best.Hs)}", f, Muted, r.Left + X(142), r.Top + X(94), tw);
            }
        }

        void DrawStatsRow(Graphics g, ValoState st, float y)
        {
            int games = st.Wins + st.Losses;
            var cells = new List<Tuple<string, string, Color>>
            {
                Tuple.Create("RANK", st.TierName.ToUpper(L.Culture), Tiers.Color(st.TierId)),
                Tuple.Create(L.T("OTURUM"), L.F("{0}G  {1}M", st.Wins, st.Losses), Color.White),
                Tuple.Create("NET RR", (st.NetRr > 0 ? "+" : st.NetRr < 0 ? "−" : "±") + Math.Abs(st.NetRr), st.NetRr > 0 ? Win : st.NetRr < 0 ? Loss : Muted),
                Tuple.Create(L.T("KAZANMA"), games > 0 ? L.Pct(100 * st.Wins / games) : "—", Color.White),
            };
            float cw = X(300), gap = X(16), x = (W - (cw * 4 + gap * 3)) / 2;
            foreach (var c in cells)
            {
                var r = new RectangleF(x, y, cw, X(150));
                DrawPanel(g, r, Accent);
                using (var f = Small(17)) Theme.DrawFit(g, c.Item1, f, Muted, r.Left + X(24), r.Top + X(22), cw - X(40));
                float vx = r.Left + X(22);
                if (c.Item1 == "RANK")
                {
                    var icon = Icon(TierIcon(st.TierId));
                    if (icon != null) { g.DrawImage(icon, vx, r.Top + X(56), X(64), X(64)); vx += X(74); }
                }
                using (var f = F(c.Item2.Length > 9 ? 30 : 44)) Theme.DrawFit(g, c.Item2, f, c.Item3, vx, r.Top + X(60), r.Right - vx - X(18), 0.5f);
                x += cw + gap;
            }
        }

        /// <summary>İkonları bir kez indirip diske önbellekler; ağ hatasında null döner.</summary>
        Image Icon(string url)
        {
            if (string.IsNullOrEmpty(url) || url.EndsWith("//displayicon.png")) return null;
            lock (icons)
            {
                if (icons.TryGetValue(url, out var img)) return img;
                img = null;
                try
                {
                    var file = Path.Combine(cacheDir, Math.Abs(url.GetHashCode()).ToString("x") + ".png");
                    if (!File.Exists(file)) using (var wc = new WebClient()) wc.DownloadFile(url, file);
                    using (var src = Image.FromFile(file)) img = new Bitmap(src);
                }
                catch { }
                icons[url] = img;
                return img;
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            lock (icons) foreach (var i in icons.Values) i?.Dispose();
            lock (canvasLk) canvas.Dispose();
        }
    }
}
