using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Ekranın küçük bir bölgesine oturan, kendi borusuyla ffmpeg'e giden katman. Kareler ham BGRA olarak gider:
    /// PNG sıkıştırması kare başına 3-36 ms CPU yiyordu, ham kopya 1 ms'nin altında (ffmpeg tarafı ikisinde de aynı).
    /// </summary>
    abstract class Layer : IDisposable
    {
        public readonly int X, Y, W, H, Fps;
        /// <summary>En son çizilen kare: W×H ham BGRA (düz alfa).</summary>
        public byte[] Current { get; private set; }
        protected readonly float s;   // 1080p'ye göre ölçek
        readonly Timer timer;
        int ticking;
        protected readonly DateTime born = DateTime.Now;
        /// <summary>ffmpeg kareyi bu kat büyütüp bindirir: tam ekran katman yarı çözünürlükte çizilir (PNG'si 4 kat ucuz).</summary>
        public int Up { get; protected set; } = 1;
        /// <summary>
        /// ffmpeg katmanı yalnızca Shown iken bindirir ve tam hızda okur; gizliyken saniyede 4 kez okur (boş katmanı
        /// her karede çekmek, üç katmanda çekirdeğin ~%19'unu yiyordu). Shown varsayılan olarak karenin boş olup
        /// olmamasından çıkar; Sparse katman (chat eğlencesi) onu kendisi yönetir.
        /// </summary>
        public virtual bool Sparse => false;
        public bool Shown { get; private set; }
        public event Action<bool> ShownChanged;

        /// <summary>run false: önizleme için; zamanlayıcı ve PNG yok, Render ile doğrudan çizilir.</summary>
        protected Layer(int x, int y, int w, int h, int fps, float scale, bool run = true)
        {
            X = x; Y = y; W = Math.Max(2, w); H = Math.Max(2, h); Fps = fps; s = scale;
            Current = new byte[W * H * 4];
            if (run) timer = new Timer(_ => Tick(), null, 0, 1000 / fps);
        }

        protected void SetShown(bool v)
        {
            if (v == Shown) return;
            Shown = v;
            ShownChanged?.Invoke(v);
        }

        /// <summary>Önizleme penceresi: katmanı verilen yüzeye çizer.</summary>
        public void Render(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Draw(g, (DateTime.Now - born).TotalSeconds);
        }

        protected float P(float v) => v * s;

        /// <summary>Yeniden çizilmesi gerekiyorsa true (animasyon sürüyorsa her karede).</summary>
        protected abstract bool NeedsRender();
        protected abstract void Draw(Graphics g, double t);

        void Tick()
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                if (!NeedsRender()) return;
                var raw = Encode();
                Current = raw;
                // Önce yeni kare, sonra bildirim: bindirme açıldığında ffmpeg eski boş kareyi değil yenisini okur.
                if (!Sparse) SetShown(!Empty(raw));
            }
            catch (Exception e) { Streamer.Log("katman: " + e.Message); }
            finally { ticking = 0; }
        }

        byte[] Encode()
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    Draw(g, (DateTime.Now - born).TotalSeconds);
                }
                return Raw(bmp);
            }
        }

        /// <summary>32 bit ARGB bitmap → satır boşluğu olmadan ham BGRA (ffmpeg rawvideo bgra ile birebir).</summary>
        internal static byte[] Raw(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var buf = new byte[w * h * 4];
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == w * 4) System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                else for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, buf, y * w * 4, w * 4);
            }
            finally { bmp.UnlockBits(d); }
            return buf;
        }

        static bool Empty(byte[] raw)
        {
            for (int i = 3; i < raw.Length; i += 4) if (raw[i] != 0) return false;
            return true;
        }

        public virtual void Dispose() => timer?.Dispose();

        // ---------- ortak çizim yardımcıları ----------

        internal static Color A(Color c, double a) => Color.FromArgb((int)Math.Max(0, Math.Min(255, a * c.A)), c.R, c.G, c.B);

        internal static void DrawImage(Graphics g, Image img, RectangleF r, double alpha)
        {
            if (img == null || alpha <= 0.01) return;
            // GDI+ resmi aynı anda iki iş parçacığında çizilemez; ikonlar katmanlar arasında paylaşılıyor.
            lock (img)
            {
                if (alpha >= 0.99) { g.DrawImage(img, r); return; }
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
                    g.DrawImage(img, Rectangle.Round(r), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                }
            }
        }

        /// <summary>Ortalı yazı; maxW verilirse uzun çeviri o genişliğe sığacak kadar küçülür (dikey merkez korunur).</summary>
        internal static void TextCentered(Graphics g, string text, Font f, Color c, float cx, float y, float maxW = float.MaxValue)
        {
            var ff = maxW < float.MaxValue ? Theme.Fit(g, text, f, maxW) : f;
            var size = g.MeasureString(text, ff);
            using (var br = new SolidBrush(c)) g.DrawString(text, ff, br, cx - size.Width / 2, y + (f.GetHeight(g) - ff.GetHeight(g)) / 2);
            if (ff != f) ff.Dispose();
        }

        internal static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Yazı tipi ve ikonlar: katmanlar arasında paylaşılır; ikonlar arka planda iner, çizim hiç beklemez.</summary>
    static class Assets
    {
        static FontFamily family;
        static FontStyle style;
        static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
        static readonly HashSet<string> loading = new HashSet<string>();

        public static Font Display(float px)
        {
            if (family == null) family = L.DisplayFamily(out style);
            return new Font(family, Math.Max(1, px), style, GraphicsUnit.Pixel);
        }

        public static Font Ui(float px, bool bold = false) => new Font(bold ? L.SansBold : L.Sans, Math.Max(1, px), FontStyle.Regular, GraphicsUnit.Pixel);

        /// <summary>Hazırsa ikonu verir; değilse indirmeyi başlatıp null döner (sonraki karede gelir).</summary>
        public static Image Icon(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (icons)
            {
                if (icons.TryGetValue(url, out var img)) return img;
                if (!loading.Add(url)) return null;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Image img = null;
                try
                {
                    var dir = Path.Combine(Settings.Dir, "cache");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, Math.Abs(url.GetHashCode()).ToString("x") + ".png");
                    if (!File.Exists(file))
                        using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "Mozilla/5.0"; wc.DownloadFile(url, file); }
                    using (var src = Image.FromFile(file)) img = new Bitmap(src);
                }
                catch { }
                lock (icons) { icons[url] = img; loading.Remove(url); }
            });
            return null;
        }
    }

    /// <summary>
    /// Minimap'in üstüne oturan radar rozeti: haritayı yayından gizler (stream sniper yerini göremez).
    /// Dönen radar taraması, RR halkası, yörüngede parçacıklar; ortada sırayla rank → bugün → son maç.
    /// RR değişince yeşil/kırmızı şok dalgası.
    /// </summary>
    sealed class RadarWidget : Layer
    {
        static readonly Color Win = ColorTranslator.FromHtml("#3ddc97"), Loss = ColorTranslator.FromHtml("#ff5a6e");
        static readonly Color Muted = ColorTranslator.FromHtml("#9aa3b2"), Accent = ColorTranslator.FromHtml("#ff4655");
        const double PageSec = 7, FadeSec = 0.5;

        readonly object lk = new object();
        ValoState state;
        double shownRr = -1;
        DateTime popAt = DateTime.MinValue; int popDelta;
        public Func<bool> Hidden = () => false;
        bool wasHidden;
        public string PlayerName = "";
        /// <summary>Canlı maç bilgisi (Riot presence): maçtayken skor sayfası eklenir.</summary>
        public Func<LiveSnap> Live = () => null;

        public RadarWidget(int size, float scale) : this((int)(10 * scale), (int)(10 * scale), size, scale) { }
        public RadarWidget(int x, int y, int size, float scale) : base(x, y, size, size, 30, scale) { }

        public void SetState(ValoState st) { lock (lk) state = st; }
        public void Pop(int delta) { lock (lk) { popDelta = delta; popAt = DateTime.Now; } }

        protected override bool NeedsRender()
        {
            bool h = Hidden();
            if (h && wasHidden) return false; // gizliyken boş kare bir kez çizilir
            wasHidden = h;
            return true;
        }

        protected override void Draw(Graphics g, double t)
        {
            if (Hidden()) return;
            ValoState st; double popAge; int pop;
            lock (lk) { st = state; popAge = (DateTime.Now - popAt).TotalSeconds; pop = popDelta; }

            float S = W, c = S / 2, R = S * 0.36f;
            var tier = st != null && st.TierId > 0 ? Tiers.Color(st.TierId) : Accent;

            // Taban: haritayı örten koyu daire ve ince ızgara (sabit: bir kez çizilip saklanır), üstünde radar taraması.
            g.DrawImageUnscaled(Base(), 0, 0);
            // Radar taraması: arkası sönen dilimler (4 sn'de bir tur). Yarıçapı dairenin içinde kaldığı için kırpma gerekmez.
            float sweep = (float)(t * 90 % 360);
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.HighSpeed; // ince dilimlerde kenar yumuşatma görünmez, maliyeti büyük
            for (int i = 0; i < 16; i++)
                using (var br = new SolidBrush(A(tier, 0.30 * (1 - i / 16.0))))
                    g.FillPie(br, c - R * 1.25f, c - R * 1.25f, R * 2.5f, R * 2.5f, sweep - i * 5f - 5f, 5.4f);
            g.SmoothingMode = sm;
            using (var lead = new Pen(A(tier, 0.75), P(1.6f)))
            {
                double a = sweep * Math.PI / 180;
                g.DrawLine(lead, c, c, c + (float)Math.Cos(a) * R * 1.25f, c + (float)Math.Sin(a) * R * 1.25f);
            }
            using (var pen = new Pen(A(tier, 0.35), 1.5f)) g.DrawEllipse(pen, 1, 1, S - 2, S - 2);
            // Pusula kadranı: kenarda 72 çentik, her 6'da biri uzun; yavaşça döner.
            float bezel = (float)(t * 6);
            using (var thin = new Pen(Color.FromArgb(50, 255, 255, 255), P(1.2f)))
            using (var thick = new Pen(A(tier, 0.85), P(2.4f)))
                for (int i = 0; i < 72; i++)
                {
                    double a = (bezel + i * 5) * Math.PI / 180;
                    bool major = i % 6 == 0;
                    float r1 = S * (major ? 0.445f : 0.465f), r2 = S * 0.485f;
                    float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                    g.DrawLine(major ? thick : thin, c + cs * r1, c + sn * r1, c + cs * r2, c + sn * r2);
                }

            // Merkez disk + nefes alan dış parıltı.
            float breathe = (float)(0.5 + 0.5 * Math.Sin(t * 2.2));
            for (int i = 3; i >= 1; i--)
                using (var br = new SolidBrush(A(tier, 0.05 * breathe)))
                    g.FillEllipse(br, c - R - P(4 * i), c - R - P(4 * i), (R + P(4 * i)) * 2, (R + P(4 * i)) * 2);
            using (var br = new SolidBrush(Color.FromArgb(235, 11, 13, 19))) g.FillEllipse(br, c - R, c - R, R * 2, R * 2);

            // Dönen kesikli halkalar (biri saat yönünde, biri tersine).
            DashedRing(g, c, R * 1.13f, (float)(t * 24), A(tier, 0.55), P(1.6f), new[] { 2f, 5f });
            DashedRing(g, c, R * 0.80f, (float)(-t * 36), A(Color.White, 0.16), P(1.2f), new[] { 1f, 7f });

            // RR halkası: değer değişince yumuşakça dolar/boşalır.
            double target = st != null && st.TierId > 0 ? Math.Min(100, Math.Max(0, st.Rr)) : 100 * (0.5 + 0.5 * Math.Sin(t * 0.8));
            shownRr = shownRr < 0 ? target : shownRr + (target - shownRr) * 0.08;
            float rw = S * 0.024f;
            using (var track = new Pen(Color.FromArgb(38, 255, 255, 255), rw)) g.DrawEllipse(track, c - R, c - R, R * 2, R * 2);
            float sweepRr = (float)(360 * shownRr / 100);
            if (sweepRr > 0.5f)
            {
                using (var arc = new Pen(tier, rw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, c - R, c - R, R * 2, R * 2, -90, sweepRr);
                double ea = (-90 + sweepRr) * Math.PI / 180;
                float ex = c + (float)Math.Cos(ea) * R, ey = c + (float)Math.Sin(ea) * R;
                for (int i = 3; i >= 0; i--)
                    using (var br = new SolidBrush(i == 0 ? Color.White : A(tier, 0.22)))
                        g.FillEllipse(br, ex - rw * (0.5f + i * 0.45f), ey - rw * (0.5f + i * 0.45f), rw * (1 + i * 0.9f), rw * (1 + i * 0.9f));
            }

            // Yörüngede üç parçacık.
            for (int i = 0; i < 3; i++)
            {
                double a = (t * (50 + i * 17) + i * 120) * Math.PI / 180;
                float pr = R * 1.13f, px = c + (float)Math.Cos(a) * pr, py = c + (float)Math.Sin(a) * pr, d = P(3.2f);
                using (var glow = new SolidBrush(A(tier, 0.25))) g.FillEllipse(glow, px - d * 1.8f, py - d * 1.8f, d * 3.6f, d * 3.6f);
                using (var dot = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.FillEllipse(dot, px - d / 2, py - d / 2, d, d);
            }

            // RR değişimi: şok dalgası + ortada büyük +/-.
            if (pop != 0 && popAge < 1.6)
            {
                var pc = pop > 0 ? Win : Loss;
                for (int w = 0; w < 2; w++)
                {
                    double age = popAge - w * 0.25;
                    if (age < 0 || age > 1.2) continue;
                    float rr = R * (1f + (float)age * 0.55f);
                    using (var pen = new Pen(A(pc, 0.9 * (1 - age / 1.2)), P(4 - w * 1.5f))) g.DrawEllipse(pen, c - rr, c - rr, rr * 2, rr * 2);
                }
            }

            // Orta içerik: sayfalar arasında yumuşak geçiş.
            var pages = Pages(st);
            int page = (int)(t / PageSec) % pages.Count;
            double inPage = t % PageSec;
            double alpha = Math.Min(1, Math.Min(inPage / FadeSec, (PageSec - inPage) / FadeSec));
            if (pop != 0 && popAge < 3.0)
            {
                using (var f = Assets.Display(S * 0.16f))
                    TextCentered(g, (pop > 0 ? "+" : "−") + Math.Abs(pop), f, A(pop > 0 ? Win : Loss, Math.Min(1, (3.0 - popAge) / 0.4)), c, c - S * 0.12f);
                using (var f = Assets.Display(S * 0.055f)) TextCentered(g, "RR", f, Muted, c, c + S * 0.06f);
            }
            else pages[pages.Count == 1 ? 0 : page](g, c, S, tier, pages.Count == 1 ? 1 : alpha, t);
        }

        Bitmap baseImg;

        /// <summary>Koyu daire + ızgara: her karede yeniden çizmek radarın en pahalı kısmıydı.</summary>
        Bitmap Base()
        {
            if (baseImg != null) return baseImg;
            float S = W;
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new RectangleF(1, 1, S - 2, S - 2);
                path.AddEllipse(box);
                using (var br = new LinearGradientBrush(box, Color.FromArgb(252, 18, 21, 30), Color.FromArgb(252, 9, 10, 15), 60f)) g.FillPath(br, path);
                g.SetClip(path);
                using (var grid = new Pen(Color.FromArgb(14, 255, 255, 255), 1))
                    for (float v = S / 8; v < S; v += S / 8) { g.DrawLine(grid, v, 0, v, S); g.DrawLine(grid, 0, v, S, v); }
            }
            return baseImg = bmp;
        }

        public override void Dispose() { base.Dispose(); baseImg?.Dispose(); }

        delegate void PageDraw(Graphics g, float c, float S, Color tier, double alpha, double t);

        List<PageDraw> Pages(ValoState st)
        {
            var list = new List<PageDraw>();
            var live = Live();
            if (live != null && live.InGame && live.Map != "") list.Add(LiveScore);
            if (st == null || st.TierId <= 0) { list.Add(Logo); return list; }
            list.Add(Rank);
            if (st.Wins + st.Losses > 0) list.Add(Session);
            if (st.Last != null) list.Add(LastMatch);
            return list;

            void LiveScore(Graphics g, float c, float S, Color tier, double a, double t)
            {
                // Canlı noktası: yanıp söner
                float blink = (float)(0.5 + 0.5 * Math.Sin(t * 5));
                // Rozet yuvarlak: yazılar o yükseklikteki kirişe sığacak kadar (uzun çevirilerde küçülerek).
                using (var f0 = Assets.Display(S * 0.05f))
                {
                    var f = Theme.Fit(g, L.T("CANLI SKOR"), f0, S * 0.6f);
                    float w = g.MeasureString(L.T("CANLI SKOR"), f).Width;
                    using (var b = new SolidBrush(A(Accent, a * (0.4 + 0.6 * blink)))) g.FillEllipse(b, c - w / 2 - S * 0.035f, c - S * 0.175f, S * 0.025f, S * 0.025f);
                    TextCentered(g, L.T("CANLI SKOR"), f, A(Muted, a), c + S * 0.01f, c - S * 0.19f + (f0.GetHeight(g) - f.GetHeight(g)) / 2);
                    if (f != f0) f.Dispose();
                }
                using (var f = Assets.Display(S * 0.15f))
                {
                    string al = live.Ally.ToString(), en = live.Enemy.ToString(), dash = "–";
                    float aw = g.MeasureString(al, f).Width, dw = g.MeasureString(dash, f).Width, ew = g.MeasureString(en, f).Width;
                    float x = c - (aw + dw + ew) / 2 + S * 0.03f, y = c - S * 0.13f;
                    using (var b1 = new SolidBrush(A(Win, a))) g.DrawString(al, f, b1, x, y);
                    using (var b2 = new SolidBrush(A(Muted, a))) g.DrawString(dash, f, b2, x + aw - S * 0.03f, y);
                    using (var b3 = new SolidBrush(A(Loss, a))) g.DrawString(en, f, b3, x + aw + dw - S * 0.06f, y);
                }
                using (var f = Assets.Display(S * 0.06f)) TextCentered(g, live.Map.ToUpper(L.Culture), f, A(Color.White, a * 0.9), c, c + S * 0.08f, S * 0.7f);
                using (var f = Assets.Ui(S * 0.04f, true)) TextCentered(g, L.F("{0}. ROUND", live.Ally + live.Enemy + 1), f, A(Muted, a), c, c + S * 0.155f, S * 0.6f);
            }
            void Rank(Graphics g, float c, float S, Color tier, double a, double t)
            {
                float bob = (float)Math.Sin(t * 1.6) * S * 0.008f, sz = S * 0.30f;
                DrawImage(g, Assets.Icon(OverlayRenderer.TierIcon(st.TierId)), new RectangleF(c - sz / 2, c - S * 0.21f + bob, sz, sz), a);
                using (var f = Assets.Display(S * 0.058f)) TextCentered(g, st.TierName.ToUpper(L.Culture), f, A(tier, a), c, c + S * 0.09f, S * 0.72f);
                using (var f = Assets.Display(S * 0.05f)) TextCentered(g, st.Rr + " RR", f, A(Color.White, a * 0.85), c, c + S * 0.16f);
            }
            void Session(Graphics g, float c, float S, Color tier, double a, double t)
            {
                using (var f = Assets.Display(S * 0.05f)) TextCentered(g, L.T("BUGÜN"), f, A(Muted, a), c, c - S * 0.19f, S * 0.6f);
                using (var f = Assets.Display(S * 0.13f))
                {
                    string w = st.Wins.ToString(), l = st.Losses.ToString(), dash = " – ";
                    float ww = g.MeasureString(w, f).Width, dw = g.MeasureString(dash, f).Width, lw = g.MeasureString(l, f).Width;
                    float x = c - (ww + dw + lw) / 2 + S * 0.01f, y = c - S * 0.12f;
                    using (var b1 = new SolidBrush(A(Win, a))) g.DrawString(w, f, b1, x, y);
                    using (var b2 = new SolidBrush(A(Muted, a))) g.DrawString(dash, f, b2, x + ww - S * 0.02f, y);
                    using (var b3 = new SolidBrush(A(Loss, a))) g.DrawString(l, f, b3, x + ww + dw - S * 0.04f, y);
                }
                string net = (st.NetRr > 0 ? "+" : st.NetRr < 0 ? "−" : "±") + Math.Abs(st.NetRr) + " RR" + (st.Streak >= 2 ? $" · ×{st.Streak}" : "");
                using (var f = Assets.Display(S * 0.06f)) TextCentered(g, net, f, A(st.NetRr > 0 ? Win : st.NetRr < 0 ? Loss : Muted, a), c, c + S * 0.06f);
                Sparkline(g, st, c, S, a);
            }
            void LastMatch(Graphics g, float c, float S, Color tier, double a, double t)
            {
                var m = st.Last;
                var col = m.Result == "W" ? Win : m.Result == "L" ? Loss : Muted;
                float sz = S * 0.24f, y = c - S * 0.23f;
                using (var ring = new Pen(A(col, a), P(2.5f))) g.DrawEllipse(ring, c - sz / 2 - P(3), y - P(3), sz + P(6), sz + P(6));
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(c - sz / 2, y, sz, sz);
                    g.SetClip(clip);
                    DrawImage(g, Assets.Icon(OverlayRenderer.AgentIcon(m.AgentId)), new RectangleF(c - sz / 2, y, sz, sz), a);
                    g.ResetClip();
                }
                string res = (m.Result == "W" ? L.T("ZAFER") : m.Result == "L" ? L.T("YENİLGİ") : L.T("BERABERE")) + " " + m.Score;
                using (var f = Assets.Display(S * 0.06f)) TextCentered(g, res, f, A(col, a), c, c + S * 0.045f, S * 0.75f);
                using (var f = Assets.Display(S * 0.048f)) TextCentered(g, m.Kda, f, A(Color.White, a * 0.9), c, c + S * 0.12f, S * 0.7f);
                using (var f = Assets.Ui(S * 0.036f)) TextCentered(g, m.Map, f, A(Muted, a), c, c + S * 0.18f, S * 0.6f);
            }
        }

        /// <summary>Günün RR grafiği: her maçtan sonraki toplam değişim (soldan sağa eskiden yeniye).</summary>
        static void Sparkline(Graphics g, ValoState st, float c, float S, double a)
        {
            var ch = st.SessionMatches.Select(m => m.RrChange).Reverse().ToList();
            if (ch.Count < 2) return;
            var pts = new List<float> { 0 };
            foreach (var d in ch) pts.Add(pts[pts.Count - 1] + d);
            float min = Math.Min(0, pts.Min()), max = Math.Max(0, pts.Max()), span = Math.Max(1, max - min);
            float w = S * 0.40f, h = S * 0.075f, x0 = c - w / 2, y0 = c + S * 0.15f;
            var line = pts.Select((v, i) => new PointF(x0 + w * i / (pts.Count - 1), y0 + h - h * (v - min) / span)).ToArray();
            float zero = y0 + h - h * (0 - min) / span;
            using (var pen = new Pen(Color.FromArgb((int)(60 * a), 255, 255, 255), 1) { DashPattern = new[] { 2f, 3f } }) g.DrawLine(pen, x0, zero, x0 + w, zero);
            var col = pts[pts.Count - 1] >= 0 ? Win : Loss;
            using (var pen = new Pen(A(col, a), Math.Max(1.5f, S * 0.008f)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line);
            var last = line[line.Length - 1];
            using (var b = new SolidBrush(A(Color.White, a))) g.FillEllipse(b, last.X - S * 0.009f, last.Y - S * 0.009f, S * 0.018f, S * 0.018f);
        }

        void Logo(Graphics g, float c, float S, Color tier, double a, double t)
        {
            using (var f = Assets.Display(S * 0.11f))
            {
                float w1 = g.MeasureString("VALO", f).Width, w2 = g.MeasureString("LIVE", f).Width;
                float x = c - (w1 + w2 - S * 0.03f) / 2, y = c - S * 0.08f;
                using (var b = new SolidBrush(Color.White)) g.DrawString("VALO", f, b, x, y);
                using (var b = new SolidBrush(Accent)) g.DrawString("LIVE", f, b, x + w1 - S * 0.03f, y);
            }
            if (PlayerName != "") using (var f = Assets.Display(S * 0.045f)) TextCentered(g, PlayerName.ToUpper(L.Culture), f, Muted, c, c + S * 0.07f);
        }

        static void DashedRing(Graphics g, float c, float r, float angle, Color col, float width, float[] dash)
        {
            var state = g.Save();
            g.TranslateTransform(c, c);
            g.RotateTransform(angle);
            using (var pen = new Pen(col, width) { DashPattern = dash }) g.DrawEllipse(pen, -r, -r, r * 2, r * 2);
            g.Restore(state);
        }
    }

    /// <summary>
    /// Yayında chat kutusu: Kick emote'ları resim olarak, kullanıcı adları kendi renginde. Yeni mesaj
    /// alttan kayarak girer, 45 sn sonra söner. Yalnızca değişince/animasyonda çizilir.
    /// </summary>
    sealed class ChatLayer : Layer
    {
        const int MaxLines = 8;
        const double LifeSec = 45, InSec = 0.28, OutSec = 1.0;
        static readonly Regex Emote = new Regex(@"\[emote:(\d+):([^\]]*)\]");

        sealed class Item { public ChatMessage M; public DateTime At; }
        readonly List<Item> items = new List<Item>();
        readonly object lk = new object();
        bool dirty = true, wasAnimating;

        public ChatLayer(float scale)
            : base((int)(14 * scale), (int)(590 * scale), (int)(420 * scale), (int)(300 * scale), 30, scale) { }

        public void Add(ChatMessage m)
        {
            foreach (Match e in Emote.Matches(m.Text)) Assets.Icon(EmoteUrl(e.Groups[1].Value)); // önden indir
            lock (lk)
            {
                items.Add(new Item { M = m, At = DateTime.Now });
                while (items.Count > MaxLines + 4) items.RemoveAt(0);
                dirty = true;
            }
        }

        static string EmoteUrl(string id) => $"https://files.kick.com/emotes/{id}/fullsize";

        protected override bool NeedsRender()
        {
            var now = DateTime.Now;
            lock (lk)
            {
                items.RemoveAll(i => (now - i.At).TotalSeconds > LifeSec);
                bool animating = items.Any(i => { double a = (now - i.At).TotalSeconds; return a < InSec || a > LifeSec - OutSec; })
                                 || items.Any(i => Emote.IsMatch(i.M.Text) && (now - i.At).TotalSeconds < 5); // emote resmi gelince
                bool need = dirty || animating || wasAnimating;
                wasAnimating = animating; dirty = false;
                return need;
            }
        }

        protected override void Draw(Graphics g, double t)
        {
            List<Item> list;
            lock (lk) list = items.ToList();
            var now = DateTime.Now;
            float pad = P(10), lineH = P(25), y = H;
            using (var nameFont = Assets.Ui(P(17), true))
            using (var textFont = Assets.Ui(P(17)))
            {
                for (int i = list.Count - 1; i >= 0 && y > 0; i--)
                {
                    var it = list[i];
                    double age = (now - it.At).TotalSeconds;
                    double alpha = Math.Min(1, Math.Min(age / InSec, (LifeSec - age) / OutSec));
                    var lines = Layout(g, it.M, nameFont, textFont, W - pad * 2 - P(8));
                    float h = lines.Count * lineH + P(8);
                    float slide = age < InSec ? (float)((1 - age / InSec) * P(18)) : 0;
                    y -= h + P(5);
                    var box = new RectangleF(0, y + slide, W, h);
                    using (var path = Rounded(box, P(7)))
                    using (var br = new SolidBrush(Color.FromArgb((int)(165 * alpha), 10, 12, 18))) g.FillPath(br, path);
                    var accent = it.M.Broadcaster ? ColorTranslator.FromHtml("#ff4655") : it.M.Mod ? ColorTranslator.FromHtml("#3ddc97") : it.M.Sub ? ColorTranslator.FromHtml("#ffb547") : Color.Empty;
                    if (accent != Color.Empty) using (var br = new SolidBrush(A(accent, alpha))) g.FillRectangle(br, box.Left, box.Top + P(5), P(3), h - P(10));
                    float ly = box.Top + P(4);
                    foreach (var line in lines)
                    {
                        foreach (var tok in line)
                        {
                            if (tok.Img != null)
                            {
                                var img = Assets.Icon(tok.Img);
                                float ih = lineH - P(3), iw = ih;
                                if (img != null) lock (img) iw = ih * img.Width / Math.Max(1, img.Height);
                                DrawImage(g, img, new RectangleF(pad + tok.X, ly + P(1), iw, ih), alpha);
                            }
                            else using (var br = new SolidBrush(A(tok.Color, alpha))) g.DrawString(tok.Text, tok.Font, br, pad + tok.X, ly + P(2), StringFormat.GenericTypographic);
                        }
                        ly += lineH;
                    }
                }
            }
        }

        sealed class Tok { public string Text, Img; public Font Font; public Color Color; public float X, W; }

        /// <summary>Kelime kelime satırlara böler; emote'lar satır içinde resim olarak yer alır.</summary>
        List<List<Tok>> Layout(Graphics g, ChatMessage m, Font nameFont, Font textFont, float maxW)
        {
            var toks = new List<Tok>();
            Color nameCol;
            try { nameCol = string.IsNullOrEmpty(m.Color) ? Color.White : ColorTranslator.FromHtml(m.Color); } catch { nameCol = Color.White; }
            toks.Add(new Tok { Text = m.User + ":", Font = nameFont, Color = nameCol });
            int last = 0;
            foreach (Match e in Emote.Matches(m.Text))
            {
                AddWords(toks, m.Text.Substring(last, e.Index - last), textFont);
                toks.Add(new Tok { Img = EmoteUrl(e.Groups[1].Value) });
                last = e.Index + e.Length;
            }
            AddWords(toks, m.Text.Substring(last), textFont);

            float space = P(5), ih = P(22);
            var lines = new List<List<Tok>> { new List<Tok>() };
            float x = 0;
            foreach (var t in toks)
            {
                if (t.Img != null) { var img = Assets.Icon(t.Img); if (img != null) lock (img) t.W = ih * img.Width / Math.Max(1, img.Height); else t.W = ih; }
                else t.W = g.MeasureString(t.Text, t.Font, PointF.Empty, StringFormat.GenericTypographic).Width;
                if (x > 0 && x + t.W > maxW) { lines.Add(new List<Tok>()); x = 0; if (lines.Count > 3) break; }
                t.X = x; lines[lines.Count - 1].Add(t);
                x += t.W + space;
            }
            return lines.Take(3).ToList();
        }

        static void AddWords(List<Tok> toks, string text, Font f)
        {
            foreach (var w in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                toks.Add(new Tok { Text = w.Length > 40 ? w.Substring(0, 40) + "…" : w, Font = f, Color = Color.FromArgb(240, 240, 242, 248) });
        }
    }
}
