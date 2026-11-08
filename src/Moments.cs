using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;

namespace ValoLive
{
    /// <summary>
    /// Yayın anları: maçta olan şeyleri yakalayıp yayın katmanına animasyon olarak verir. Oyuna dokunmaz; ekranda zaten
    /// görünen üst skor çubuğunu (iki taraftaki canlı portreler) ve Riot istemcisinin kendi skorunu okur.
    /// Çubuktan: clutch (takımında yalnızca sen kaldın), ilk kan, kusursuz raund, son rakip.
    /// Skordan: galibiyet serisi, geri dönüş, maç sayısı (bizde/rakipte), uzatma, devre arası.
    /// </summary>
    sealed class MomentWatch : IDisposable
    {
        public enum Phase { None, Live, Won, Lost }
        public enum Kind
        {
            FirstBlood, Flawless, LastEnemy, Streak, Comeback, MatchPoint, EnemyMatchPoint, Overtime, HalfTime,
            // Maç sonu (Riot maç detayından): sırayla oynatılan "maçın anları"
            Ace, FourK, NinjaDefuse, FallDeath, EnemyFell, KnifeKill, SpikeDeath, SelfKill, FriendlyFire,
        }

        /// <summary>Clutch'ın anlık durumu; her değişimde yeni nesne (okuyan kilit tutmaz).</summary>
        public sealed class View
        {
            public Phase Phase;
            public int Start, Left;                    // clutch başında ve şu an canlı rakip
            public DateTime Since, ResultAt, FallenAt; // FallenAt: sen öldün, sonuç (skor) bekleniyor
            public DateTime[] DownAt = new DateTime[0]; // rakip düştükçe (pip patlaması)
            public View Clone() => (View)MemberwiseClone();
        }

        /// <summary>Kısa süreli an (sırayla gösterilir). At: gösterilmeye başladığı an.</summary>
        public sealed class Moment
        {
            public Kind Kind; public int N, Ally, Enemy;
            public DateTime Queued, At;
            public double Duration = 3.4, Keep = 8; // Keep: sırada en çok bekleme (sn); daha geç kalan atlanır
            public bool Mine;                       // ninja defuse: imhayı sen mi yaptın
        }

        const int ReadMs = 250, Agree = 3;     // ~0,75 sn aynı okuma
        const double ShowResultSec = 4.5;

        readonly Func<LiveSnap> snap;
        readonly Thread thread;
        volatile bool stop;
        public volatile View Current = new View();
        readonly object lk = new object();
        readonly Queue<Moment> queue = new Queue<Moment>();
        Moment shown;

        public MomentWatch(Func<LiveSnap> snap)
        {
            this.snap = snap;
            thread = new Thread(Loop) { IsBackground = true, Name = "anlar", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        public void Dispose() => stop = true;

        static void Log(string text) => Diag.Event("ANLAR", text);

        /// <summary>Şu an gösterilecek an: clutch sürerken bekler, bitenin yerine sıradakini alır, bayatlayanı atar.</summary>
        public Moment Showing(DateTime now)
        {
            lock (lk)
            {
                if (shown != null && (now - shown.At).TotalSeconds > shown.Duration) shown = null;
                if (shown == null && Current.Phase == Phase.None)
                    while (queue.Count > 0)
                    {
                        var m = queue.Dequeue();
                        if ((now - m.Queued).TotalSeconds > m.Keep) continue;
                        m.At = now; shown = m; break;
                    }
                return shown;
            }
        }

        void Push(Kind kind, int n = 0, int ally = 0, int enemy = 0)
        {
            lock (lk) queue.Enqueue(new Moment { Kind = kind, N = n, Ally = ally, Enemy = enemy, Queued = DateTime.Now });
            Log($"an: {kind} {n} {ally}-{enemy}");
        }

        /// <summary>Maç bitti, Riot detayı geldi: maçın anları sırayla (en fazla 6, en iyiler önce).</summary>
        public void PushHighlights(PostMatch pm)
        {
            var list = pm.Highlights.Take(6).ToList();
            lock (lk)
                foreach (var h in list)
                    queue.Enqueue(new Moment { Kind = h.Kind, N = h.N, Mine = h.Mine, Queued = DateTime.Now, Keep = 120 });
            if (list.Count > 0) Log("maçın anları: " + string.Join(", ", list.Select(h => $"{h.Kind} {h.N}")));
        }

        // Raund içi bayraklar (çubuktan) ve maç boyu skor geçmişi.
        bool roundLive, firstBlood, flawless, lastEnemy;
        int streak, maxDeficit; bool comebackShown; string lastMp = "";

        void Loop()
        {
            MinimapMemory.PerMonitorDpi();
            string self = null; List<KeyValuePair<string, double[]>> allies = null; DateTime retry = DateTime.MinValue;
            Bitmap allyBmp = null, enemyBmp = null;
            string pendKey = null; int pendRuns = 0;
            int lastAlly = -1, lastEnemy = -1, doneScore = -1;
            DateTime lastRead = DateTime.Now;
            while (!stop)
            {
                try
                {
                    var now = DateTime.Now;
                    var s = snap();
                    if (s == null || !s.InGame)
                    {
                        if (lastAlly >= 0) { lock (lk) { queue.Clear(); shown = null; } }
                        self = null; allies = null; lastAlly = lastEnemy = doneScore = -1; Set(new View());
                        roundLive = false; streak = maxDeficit = 0; comebackShown = false; lastMp = "";
                        Thread.Sleep(1000); continue;
                    }
                    var v = Current;
                    if (lastAlly >= 0 && (s.Ally != lastAlly || s.Enemy != lastEnemy))
                    {
                        bool weWon = s.Ally > lastAlly;
                        // Skor raund bitince değişir: süren clutch'ın sonucu (patlama/imha da dahil).
                        if (v.Phase == Phase.Live) Finish(v, weWon, now);
                        ScoreMoments(s, weWon);
                        roundLive = false;
                    }
                    lastAlly = s.Ally; lastEnemy = s.Enemy;
                    int score = s.Ally + s.Enemy;
                    v = Current;
                    if ((v.Phase == Phase.Won || v.Phase == Phase.Lost) && (now - v.ResultAt).TotalSeconds > ShowResultSec) Set(v = new View());
                    if (v.Phase == Phase.Live && (now - lastRead).TotalSeconds > 30) { Log("okunamadı, bant kapandı"); Set(v = new View()); }

                    if (allies == null && now >= retry)
                    {
                        retry = now.AddSeconds(15);
                        var st = new RiotLocal().Read();
                        var me = st.Players.FirstOrDefault(p => p.IsSelf);
                        if (st.Phase == "ingame" && me != null)
                        {
                            var list = new List<KeyValuePair<string, double[]>>();
                            foreach (var p in st.Players.Where(p => p.Ally && !string.IsNullOrEmpty(p.AgentId)))
                                try { using (var b = MinimapMemory.AgentBitmap(p.AgentId)) list.Add(new KeyValuePair<string, double[]>(p.AgentId, MinimapMemory.TopBar.IconHist(b))); }
                                catch { }
                            if (list.Any(kv => kv.Key == me.AgentId)) { self = me.AgentId; allies = list; Log($"hazır · takım ajanı {list.Count}"); }
                        }
                    }
                    if (allies == null || !MinimapMemory.GameWindow(out var win)) { Thread.Sleep(500); continue; }

                    float k = win.Height / 1080f;
                    var er = MinimapMemory.TopBar.Region(win, k);
                    // Kendi takımın sol yarıda, aynalanmış dizilişle: aynı bölge ortanın solunda, okunduktan sonra yatay çevrilir.
                    var ar = new Rectangle(win.Left + win.Width / 2 - (int)(560 * k), er.Top, er.Width, er.Height);
                    int a = Count(ref allyBmp, ar, k, true, out var px, out int stride);
                    int e = Count(ref enemyBmp, er, k, false, out _, out _);
                    bool mine = a == 1 && IsMe(px, stride, k, self, allies);
                    string key = $"{a}/{e}/{mine}";
                    if (key != pendKey) { pendKey = key; pendRuns = 1; }
                    else if (++pendRuns == Agree && a >= 0 && e >= 0)
                    {
                        lastRead = now;
                        Step(Current, a, e, mine, score, ref doneScore, now);
                        BarMoments(a, e);
                    }
                }
                catch (Exception ex) { Log("hata: " + ex.Message); Thread.Sleep(2000); }
                Thread.Sleep(ReadMs);
            }
            allyBmp?.Dispose(); enemyBmp?.Dispose();
        }

        /// <summary>Çubuktan anlar. Raund, satın alma evresinde çubuk iki tarafta da 5'e dolunca başlar
        /// (uygulama maç ortasında açıldıysa ilk tam raunda dek susar).</summary>
        void BarMoments(int a, int e)
        {
            if (a == 5 && e == 5) { roundLive = true; firstBlood = flawless = lastEnemy = false; return; }
            if (!roundLive) return;
            if (!firstBlood && (a < 5 || e < 5)) { firstBlood = true; if (a == 5) Push(Kind.FirstBlood); }
            if (!flawless && a == 5 && e == 0) { flawless = true; Push(Kind.Flawless); }
            if (!lastEnemy && e == 1 && a >= 2) { lastEnemy = true; Push(Kind.LastEnemy, a); }
        }

        /// <summary>Raund bitti (skor değişti): seri, geri dönüş, devre arası, uzatma, maç sayısı.</summary>
        void ScoreMoments(LiveSnap s, bool weWon)
        {
            int target = Target(s.Queue);
            if (target == 0) return; // ölüm maçı vb.: raund yok
            streak = weWon ? Math.Max(0, streak) + 1 : Math.Min(0, streak) - 1;
            maxDeficit = Math.Max(maxDeficit, s.Enemy - s.Ally);
            int total = s.Ally + s.Enemy, a = s.Ally, e = s.Enemy;

            if (total == target - 1 && a < target && e < target) Push(Kind.HalfTime, 0, a, e);
            if (target == 13 && a == 12 && e == 12) Push(Kind.Overtime, 0, a, e);
            if (weWon && streak >= 3) Push(Kind.Streak, streak, a, e);
            if (weWon && a == e && maxDeficit >= 4 && !comebackShown) { comebackShown = true; Push(Kind.Comeback, maxDeficit, a, e); }

            // Maç sayısı: normalde hedefin bir eksiği; uzatmada (12-12 sonrası) bir önde olan.
            string mp = "";
            bool ot = target == 13 && a >= 12 && e >= 12;
            if (ot) mp = a == e + 1 ? "biz" : e == a + 1 ? "rakip" : "";
            else if (a == target - 1 && e < target - 1) mp = "biz";
            else if (e == target - 1 && a < target - 1) mp = "rakip";
            if (mp != "" && mp + total != lastMp) Push(mp == "biz" ? Kind.MatchPoint : Kind.EnemyMatchPoint, 0, a, e);
            if (mp != "") lastMp = mp + total;
        }

        /// <summary>Kazanmak için gereken raund (devre arası bunun bir eksiğinde). Raundsuz modlarda 0.</summary>
        internal static int Target(string queue)
        {
            switch (queue)
            {
                case "swiftplay": return 5;
                case "spikerush": return 4;
                case "deathmatch": case "hurm": case "ggteam": case "newmap": return 0;
                default: return 13; // rekabetçi, derecesiz, premier, özel
            }
        }

        /// <summary>Kabul edilmiş okuma (a: takımında canlı, e: rakipte canlı, mine: kalan tek kişi sen misin).</summary>
        void Step(View v, int a, int e, bool mine, int score, ref int doneScore, DateTime now)
        {
            switch (v.Phase)
            {
                case Phase.None:
                    // Raund başına bir clutch: sonucu gösterilen raundda yeniden başlamaz.
                    if (a == 1 && mine && e >= 1 && score != doneScore)
                    {
                        doneScore = score;
                        Set(new View { Phase = Phase.Live, Start = e, Left = e, Since = now, DownAt = Enumerable.Repeat(DateTime.MinValue, e).ToArray() });
                        Log($"clutch başladı · 1v{e}");
                    }
                    break;
                case Phase.Live:
                    if (a >= 2 || (a == 1 && !mine)) { Set(new View()); Log("clutch iptal (diriltme)"); break; }
                    if (a == 0 && e >= 1 && v.FallenAt == DateTime.MinValue) { var n = v.Clone(); n.FallenAt = now; Set(n); Log("clutch: düştün"); break; }
                    if (a == 1 && e < v.Left)
                    {
                        var n = v.Clone(); n.DownAt = (DateTime[])v.DownAt.Clone();
                        for (int i = e; i < v.Left && i < n.DownAt.Length; i++) n.DownAt[i] = now;
                        n.Left = e; Set(n);
                        if (e == 0) Finish(n, true, now);
                    }
                    break;
            }
        }

        void Finish(View v, bool won, DateTime now)
        {
            var n = v.Clone();
            n.Phase = won ? Phase.Won : Phase.Lost; n.ResultAt = now;
            if (won) n.Left = 0;
            Set(n);
            Log("clutch " + (won ? "kazandı" : "kaybetti") + $" · 1v{v.Start}");
        }

        void Set(View v) => Current = v;

        static int Count(ref Bitmap bmp, Rectangle r, float k, bool flip, out byte[] px, out int stride)
        {
            if (bmp == null || bmp.Size != r.Size) { bmp?.Dispose(); bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb); }
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            if (flip) bmp.RotateFlip(RotateFlipType.RotateNoneFlipX);
            px = MinimapMemory.TopBar.Pixels(bmp, out stride);
            return MinimapMemory.TopBar.Alive(px, stride, k);
        }

        /// <summary>Kalan tek portre en çok senin ajanına mı benziyor (takımdaki diğer ajanlardan belirgin farkla).
        /// Kayıtlı maçlarda doğru ajan 0,08..0,37, en yakın yanlış en az 0,40 uzaklıkta kaldı.</summary>
        static bool IsMe(byte[] px, int stride, float k, string self, List<KeyValuePair<string, double[]>> allies)
        {
            var h = MinimapMemory.TopBar.SlotHist(px, stride, 0, k, true);
            var r = allies.Select(kv => new { kv.Key, D = MinimapMemory.TopBar.HistDist(h, kv.Value) }).OrderBy(x => x.D).ToList();
            return r[0].Key == self && r[0].D < 0.42 && (r.Count < 2 || r[1].D - r[0].D >= 0.08);
        }
    }

    /// <summary>
    /// Yayında anlar katmanı: ekranın alt ortasında. Clutch bandı (nabız, EKG, altın patlama ya da "F" yağmuru) önceliklidir;
    /// diğer anlar sırayla, her biri kendi efektiyle: ilk kan (kılıç izi + kan damlaları), kusursuz (parlama + yıldızlar),
    /// son rakip (kilitlenen nişangâh), seri (alevler), geri dönüş (yükselen oklar), maç sayısı (işaret dalgaları / siren),
    /// uzatma (şimşekler), devre arası (iki takım rengi yer değiştirir).
    /// </summary>
    sealed class MomentLayer : Layer
    {
        static readonly Color Red = ColorTranslator.FromHtml("#ff4655"), Gold = ColorTranslator.FromHtml("#ffcf5a");
        static readonly Color Muted = ColorTranslator.FromHtml("#9aa3b2"), Bg = Color.FromArgb(12, 14, 20);
        static readonly Color Cyan = ColorTranslator.FromHtml("#5ce1e6"), Orange = ColorTranslator.FromHtml("#ff9f43");
        static readonly Color Green = ColorTranslator.FromHtml("#3ddc97"), Purple = ColorTranslator.FromHtml("#a970ff"), Blue = ColorTranslator.FromHtml("#4f8dff");
        static readonly Color Teal = ColorTranslator.FromHtml("#2ee6a6"), Sky = ColorTranslator.FromHtml("#8ab4ff");
        const double InSec = 0.4, OutSec = 0.6;

        readonly MomentWatch watch;
        MomentWatch.Moment moment;
        bool wasVisible;
        public Func<bool> Hidden = () => false;

        public MomentLayer(MomentWatch watch, int screenW, int screenH, float scale)
            : base((screenW - (int)(900 * scale)) / 2, screenH - (int)(430 * scale), (int)(900 * scale), (int)(300 * scale), 30, scale)
        { this.watch = watch; }

        protected override bool NeedsRender()
        {
            moment = watch.Showing(DateTime.Now);
            bool visible = (watch.Current.Phase != MomentWatch.Phase.None || moment != null) && !Hidden();
            bool need = visible || wasVisible; // kapanınca bir kez boş kare
            wasVisible = visible;
            return need;
        }

        protected override void Draw(Graphics g, double t)
        {
            if (Hidden()) return;
            var v = watch.Current;
            if (v.Phase != MomentWatch.Phase.None) RenderClutch(g, v, DateTime.Now, W, H, s);
            else if (moment != null) RenderMoment(g, moment, DateTime.Now, W, H, s);
        }

        static double Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return 1 - Math.Pow(1 - x, 3); }

        /// <summary>Lub-dub: iki ardışık vuruş (0..1).</summary>
        static double Beat(double ph) => Math.Exp(-Math.Pow(ph / 0.05, 2)) + 0.55 * Math.Exp(-Math.Pow((ph - 0.17) / 0.05, 2));

        /// <summary>EKG dalgası (yukarı +): sivri QRS ve küçük T dalgası.</summary>
        static double Ecg(double ph)
        {
            if (ph < 0.03) return ph / 0.03;
            if (ph < 0.06) return 1 - 1.45 * (ph - 0.03) / 0.03;
            if (ph < 0.10) return -0.45 + 0.45 * (ph - 0.06) / 0.04;
            return 0.22 * Math.Exp(-Math.Pow((ph - 0.28) / 0.06, 2));
        }

        static RectangleF Box(int W, int H, float s, double aIn) =>
            new RectangleF(W / 2f - 270 * s, H - 18 * s - 96 * s + (float)((1 - aIn) * 40 * s), 540 * s, 96 * s);

        static GraphicsPath Body(RectangleF box, float sk)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] { new PointF(box.Left + sk, box.Top), new PointF(box.Right, box.Top), new PointF(box.Right - sk, box.Bottom), new PointF(box.Left, box.Bottom) });
            return p;
        }

        /// <summary>Arka parıltı (katmanlı elipsler).</summary>
        static void Glow(Graphics g, RectangleF box, Color c, double alpha, double extra, float s)
        {
            for (int i = 4; i >= 1; i--)
            {
                float grow = s * 10 * i + (float)(extra * s * 16);
                using (var br = new SolidBrush(A(c, alpha * (0.07 + 0.07 * extra))))
                    g.FillEllipse(br, box.Left - grow * 1.6f, box.Top - grow, box.Width + grow * 3.2f, box.Height + grow * 2);
            }
        }

        /// <summary>Ortak kart: eğik koyu gövde, solda renk şeridi, giriş flaşı; inside: gövdeye kırpılmış ek çizim.</summary>
        static void Card(Graphics g, RectangleF box, Color accent, double alpha, double age, double fade, float s, double edge = 0, Action<GraphicsPath> inside = null)
        {
            using (var body = Body(box, 18 * s))
            {
                using (var br = new SolidBrush(Color.FromArgb((int)(232 * alpha), Bg))) g.FillPath(br, body);
                var st = g.Save();
                g.SetClip(body);
                using (var br = new SolidBrush(A(accent, alpha))) g.FillRectangle(br, box.Left, box.Top, 30 * s, box.Height);
                inside?.Invoke(body);
                if (age < 0.35) using (var br = new SolidBrush(Color.FromArgb((int)(200 * (1 - age / 0.35) * fade), 255, 255, 255))) g.FillRectangle(br, box);
                g.Restore(st);
                using (var pen = new Pen(A(accent, alpha * (0.55 + 0.45 * edge)), 2 * s)) g.DrawPath(pen, body);
            }
        }

        static SizeF Text(Graphics g, string text, Font f, Color c, float x, float y, float scale = 1)
        {
            var sz = g.MeasureString(text, f);
            var st = g.Save();
            g.TranslateTransform(x + sz.Width / 2, y + sz.Height / 2);
            g.ScaleTransform(scale, scale);
            using (var br = new SolidBrush(c)) g.DrawString(text, f, br, -sz.Width / 2, -sz.Height / 2);
            g.Restore(st);
            return sz;
        }

        /// <summary>Clutch bandını çizer (önizleme için dışarıdan da çağrılır).</summary>
        public static void RenderClutch(Graphics g, MomentWatch.View v, DateTime now, int W, int H, float s)
        {
            float P(float x) => x * s;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double age = (now - v.Since).TotalSeconds;
            bool won = v.Phase == MomentWatch.Phase.Won, lost = v.Phase == MomentWatch.Phase.Lost;
            bool fallen = v.FallenAt != DateTime.MinValue;
            double rt = won || lost ? (now - v.ResultAt).TotalSeconds : 0;
            double fadeOut = won || lost ? Math.Min(1, Math.Max(0, (4.5 - rt) / OutSec)) : 1;
            double aIn = Ease(age / InSec), alpha = aIn * fadeOut;
            if (alpha <= 0.01) return;

            var accent = won ? Gold : lost ? Muted : fallen ? Color.FromArgb(150, 110, 116) : Red;
            int left = Math.Max(0, v.Left);
            double bpm = 70 + 20 * Math.Max(1, left), beatLen = 60 / bpm;
            double pulse = won || lost || fallen ? 0 : Beat(age % beatLen);

            var box = Box(W, H, s, aIn);
            float bh = box.Height, cx = W / 2f;
            Glow(g, box, accent, alpha, pulse, s);
            Card(g, box, accent, alpha, age, fadeOut, s, pulse);

            // Sol: büyük "1v3" (kaybedince "F"), nabızla hafifçe büyür.
            string big = lost ? "F" : "1v" + v.Start;
            using (var f = Assets.Display(P(62)))
            {
                var sz = g.MeasureString(big, f);
                Text(g, big, f, A(won ? Gold : Color.White, alpha * (fallen && !won ? 0.6 : 1)), box.Left + P(44), box.Top + (bh - sz.Height) / 2 + P(2), (float)(1 + 0.06 * pulse));
            }

            // Sağ bölüm: başlık, pip'ler, EKG.
            float rx = box.Left + P(210), rw = box.Right - P(30) - rx;
            using (var pen = new Pen(A(Color.White, alpha * 0.15), P(1))) g.DrawLine(pen, rx - P(18), box.Top + P(16), rx - P(22), box.Bottom - P(16));
            using (var f = Assets.Display(P(26)))
            {
                var sz = Text(g, won ? "CLUTCH!" : "CLUTCH", f, A(accent, alpha), rx, box.Top + P(10), won ? (float)(1 + 0.45 * Math.Exp(-rt * 7)) : 1);
                if (lost) using (var pen = new Pen(A(Muted, alpha), P(3))) g.DrawLine(pen, rx, box.Top + P(10) + sz.Height / 2, rx + sz.Width, box.Top + P(10) + sz.Height / 2);
            }
            // Rakip pip'leri: canlılar dolu kırmızı, düşenler çarpı; düşerken halka patlar.
            float pr = P(7.5f), pgap = P(22), px0 = box.Right - P(34) - (v.Start - 1) * pgap, py = box.Top + P(26);
            for (int i = 0; i < v.Start; i++)
            {
                float x = px0 + i * pgap;
                double since = i < v.DownAt.Length && v.DownAt[i] != DateTime.MinValue ? (now - v.DownAt[i]).TotalSeconds : 99;
                if (since < 0.7)
                {
                    float rr = pr + (float)(since * P(60));
                    using (var pen = new Pen(A(Red, alpha * (1 - since / 0.7)), P(3))) g.DrawEllipse(pen, x - rr, py - rr, rr * 2, rr * 2);
                }
                if (i < left)
                    using (var br = new SolidBrush(A(Red, alpha))) g.FillEllipse(br, x - pr, py - pr, pr * 2, pr * 2);
                else
                {
                    using (var pen = new Pen(A(Muted, alpha * 0.5), P(1.5f))) g.DrawEllipse(pen, x - pr, py - pr, pr * 2, pr * 2);
                    using (var pen = new Pen(A(won ? Gold : Color.White, alpha), P(2.4f))) { float d = pr * 0.7f; g.DrawLine(pen, x - d, py - d, x + d, py + d); g.DrawLine(pen, x - d, py + d, x + d, py - d); }
                }
            }

            // EKG: son 2 sn soldan sağa kayar; düşüş ya da yenilgiden sonrası (o andan daha yakın geçmiş) düz.
            float ey = box.Bottom - P(26), eh = P(17);
            const int N = 120; const double Window = 2.0;
            var pts = new PointF[N];
            double flatSince = fallen ? (now - v.FallenAt).TotalSeconds : lost ? rt : -1;
            for (int i = 0; i < N; i++)
            {
                double back = (1 - i / (double)(N - 1)) * Window, tau = age - back;
                double val = tau < 0 || back < flatSince ? 0 : Ecg(tau % beatLen);
                pts[i] = new PointF(rx + rw * i / (N - 1), ey - (float)(val * eh));
            }
            using (var lg = new LinearGradientBrush(new RectangleF(rx - 1, ey - eh * 2, rw + 2, eh * 4), Color.FromArgb(0, accent), A(accent, alpha), 0f))
            using (var pen = new Pen(lg, P(2.2f)) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            var head = pts[N - 1];
            using (var br = new SolidBrush(A(Color.White, alpha))) g.FillEllipse(br, head.X - P(3), head.Y - P(3), P(6), P(6));

            if (won) Burst(g, cx, box.Top + bh / 2, rt, s);
            if (lost) FRain(g, rt, W, H, s, fadeOut);
        }

        /// <summary>Kısa anı çizer (önizleme için dışarıdan da çağrılır).</summary>
        public static void RenderMoment(Graphics g, MomentWatch.Moment m, DateTime now, int W, int H, float s)
        {
            float P(float x) => x * s;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double age = (now - m.At).TotalSeconds;
            double fade = Math.Min(1, Math.Max(0, (m.Duration - age) / OutSec)), aIn = Ease(age / InSec), alpha = aIn * fade;
            if (alpha <= 0.01) return;
            string score = $"{m.Ally}-{m.Enemy}";
            Color accent; string title, sub, big = null;
            switch (m.Kind)
            {
                case MomentWatch.Kind.FirstBlood: accent = Red; title = L.T("İLK KAN"); sub = L.T("ilk öldürme bizden"); break;
                case MomentWatch.Kind.Flawless: accent = Cyan; title = L.T("KUSURSUZ"); sub = L.T("kimse düşmeden raund alındı"); break;
                case MomentWatch.Kind.LastEnemy: accent = Orange; title = L.T("SON RAKİP"); sub = L.T("rakipte tek kişi kaldı"); big = m.N + "v1"; break;
                case MomentWatch.Kind.Ace: accent = Gold; title = "ACE"; sub = L.T("bir raundda 5 öldürme"); break;
                case MomentWatch.Kind.FourK: accent = Orange; title = "4K"; sub = L.T("bir raundda 4 öldürme"); break;
                case MomentWatch.Kind.NinjaDefuse:
                    accent = Teal; title = L.T("NİNJA DEFUSE");
                    sub = m.Mine ? L.F("{0} rakip ayaktayken imha ettin", m.N) : L.F("takım arkadaşın {0} rakip ayaktayken imha etti", m.N); break;
                case MomentWatch.Kind.FallDeath: accent = Sky; title = L.T("YER ÇEKİMİ KAZANDI"); sub = L.F("{0} kez düşerek öldün", m.N); break;
                case MomentWatch.Kind.EnemyFell: accent = Sky; title = L.T("RAKİP UÇURUMDAN"); sub = L.F("{0} rakip düşerek öldü", m.N); break;
                case MomentWatch.Kind.KnifeKill: accent = Cyan; title = L.T("BIÇAK"); sub = L.F("{0} bıçak öldürmesi", m.N); break;
                case MomentWatch.Kind.SpikeDeath: accent = Orange; title = L.T("SPIKE'IN DİBİNDE"); sub = L.F("spike patlamasında {0} kez öldün", m.N); break;
                case MomentWatch.Kind.SelfKill: accent = Purple; title = L.T("KENDİ KENDİNE"); sub = L.F("kendi yeteneğinle {0} kez öldün", m.N); break;
                case MomentWatch.Kind.FriendlyFire: accent = Red; title = L.T("DOST ATEŞİ"); sub = L.F("takım arkadaşın seni {0} kez vurdu", m.N); break;
                case MomentWatch.Kind.Streak: accent = Orange; title = L.T("SERİ"); sub = L.F("{0} raund üst üste", m.N); big = "×" + m.N; break;
                case MomentWatch.Kind.Comeback: accent = Green; title = L.T("GERİ DÖNÜŞ"); sub = L.F("{0} raund geriden eşitlendi", m.N) + " · " + score; break;
                case MomentWatch.Kind.MatchPoint: accent = Gold; title = L.T("MAÇ SAYISI"); sub = L.T("bir raund kaldı") + " · " + score; break;
                case MomentWatch.Kind.EnemyMatchPoint: accent = Red; title = L.T("RAKİP MAÇ SAYISINDA"); sub = L.T("bu raund kritik") + " · " + score; break;
                case MomentWatch.Kind.Overtime: accent = Purple; title = L.T("UZATMA"); sub = L.T("iki farkla kazanan alır") + " · " + score; break;
                default: accent = Green; title = L.T("DEVRE ARASI"); sub = L.T("taraflar değişiyor") + " · " + score; break;
            }

            // Kartın sallanması (geri dönüş) ve siren yanıp sönmesi (rakip maç sayısı).
            var box = Box(W, H, s, aIn);
            if ((m.Kind == MomentWatch.Kind.Comeback || m.Kind == MomentWatch.Kind.FriendlyFire || m.Kind == MomentWatch.Kind.FourK) && age < 0.5)
                box.Offset((float)(Math.Sin(age * 70) * P(6) * (1 - age / 0.5)), 0);
            double edge = 0.5 + 0.5 * Math.Sin(age * 6);
            // Düşerek ölme: kart yukarıdan düşüp yere çarpar, iki kez seker, sonra yamuk kalır.
            float tilt = 0;
            if (m.Kind == MomentWatch.Kind.FallDeath)
            {
                double drop = Bounce(age / 0.9);
                box.Offset(0, (float)(-(1 - drop) * P(170)));
                tilt = (float)(age < 0.9 ? 0 : Math.Min(1, (age - 0.9) / 0.25) * -4);
            }
            var tiltState = g.Save();
            if (tilt != 0) { g.TranslateTransform(box.Left + box.Width / 2, box.Top + box.Height / 2); g.RotateTransform(tilt); g.TranslateTransform(-(box.Left + box.Width / 2), -(box.Top + box.Height / 2)); }

            // ---- Kartın arkasındaki efektler ----
            switch (m.Kind)
            {
                case MomentWatch.Kind.EnemyMatchPoint:
                {
                    bool redSide = (int)(age * 3) % 2 == 0;
                    var lb = new RectangleF(box.Left - P(120), box.Top - P(30), box.Width / 2 + P(120), box.Height + P(60));
                    var rb = new RectangleF(box.Left + box.Width / 2, box.Top - P(30), box.Width / 2 + P(120), box.Height + P(60));
                    using (var br = new SolidBrush(A(redSide ? Red : Blue, alpha * 0.35))) g.FillEllipse(br, lb);
                    using (var br = new SolidBrush(A(redSide ? Blue : Red, alpha * 0.12))) g.FillEllipse(br, rb);
                    break;
                }
                case MomentWatch.Kind.MatchPoint:
                    // İşaret feneri: kart merkezinden 0,8 sn'de bir genişleyen halkalar.
                    for (int i = 0; i < 3; i++)
                    {
                        double ph = (age + i * 0.8 / 3) % 0.8 / 0.8;
                        float rx = (float)(box.Width * (0.55 + ph * 0.6)), ry = (float)(box.Height * (0.6 + ph * 1.3));
                        using (var pen = new Pen(A(Gold, alpha * (1 - ph) * 0.8), P(3))) g.DrawEllipse(pen, W / 2f - rx, box.Top + box.Height / 2 - ry, rx * 2, ry * 2);
                    }
                    break;
                case MomentWatch.Kind.Streak: Flames(g, box, age, alpha, m.N, s); break;
                case MomentWatch.Kind.Comeback: Chevrons(g, box, age, alpha, H, s, false); break;
                case MomentWatch.Kind.EnemyFell: Chevrons(g, box, age, alpha, H, s, true); break;
                case MomentWatch.Kind.Overtime: Lightning(g, box, age, alpha, W, s); break;
                case MomentWatch.Kind.SpikeDeath: Explosion(g, box, age, alpha, s); break;
                case MomentWatch.Kind.NinjaDefuse: Smoke(g, box, age, alpha, s); break;
                case MomentWatch.Kind.FallDeath: SpeedLines(g, box, age, alpha, s); break;
            }
            Glow(g, box, accent, alpha, m.Kind == MomentWatch.Kind.MatchPoint ? edge : 0.3, s);

            Card(g, box, accent, alpha, age, fade, s, edge, body =>
            {
                if (m.Kind == MomentWatch.Kind.Flawless)
                {
                    // Parlama: eğik beyaz şerit kartın üstünden iki kez geçer.
                    for (int pass = 0; pass < 2; pass++)
                    {
                        double p = (age - 0.3 - pass * 1.1) / 0.7; if (p < 0 || p > 1) continue;
                        float x = box.Left - P(80) + (float)(p * (box.Width + P(160)));
                        using (var sweep = new GraphicsPath())
                        {
                            sweep.AddPolygon(new[] { new PointF(x, box.Top), new PointF(x + P(40), box.Top), new PointF(x + P(10), box.Bottom), new PointF(x - P(30), box.Bottom) });
                            using (var br = new SolidBrush(A(Color.White, alpha * 0.35))) g.FillPath(br, sweep);
                        }
                    }
                }
                if (m.Kind == MomentWatch.Kind.FriendlyFire)
                    // Uyarı şeritleri: kartın içinde kayan sarı-siyah çapraz bantlar.
                    using (var br = new SolidBrush(A(Color.FromArgb(255, 200, 40), alpha * 0.16)))
                        for (float x = box.Left - P(80) + (float)(age * P(60) % P(40)); x < box.Right; x += P(40))
                            g.FillPolygon(br, new[] { new PointF(x, box.Bottom), new PointF(x + P(20), box.Bottom), new PointF(x + P(60), box.Top), new PointF(x + P(40), box.Top) });
                if (m.Kind == MomentWatch.Kind.HalfTime)
                {
                    // İki takım rengi: kartın iki yarısı, ortadaki çizgi kayarak yer değiştirir.
                    double p = Ease((age - 0.3) / 0.9);
                    float mid = box.Left + box.Width / 2;
                    var leftC = p < 0.5 ? Green : Red; var rightC = p < 0.5 ? Red : Green;
                    float slide = (float)(Math.Sin(p * Math.PI) * box.Width * 0.5);
                    using (var br = new SolidBrush(A(leftC, alpha * 0.18))) g.FillRectangle(br, box.Left, box.Top, mid - box.Left, box.Height);
                    using (var br = new SolidBrush(A(rightC, alpha * 0.18))) g.FillRectangle(br, mid, box.Top, box.Right - mid, box.Height);
                    using (var pen = new Pen(A(Color.White, alpha * 0.6 * Math.Sin(p * Math.PI)), P(3))) g.DrawLine(pen, mid - slide + P(10), box.Top, mid - slide - P(10), box.Bottom);
                    using (var pen = new Pen(A(Color.White, alpha * 0.6 * Math.Sin(p * Math.PI)), P(3))) g.DrawLine(pen, mid + slide + P(10), box.Top, mid + slide - P(10), box.Bottom);
                }
            });

            // ---- Kart içeriği ----
            float tx = box.Left + P(50);
            if (big != null)
            {
                using (var f = Assets.Display(P(56)))
                {
                    var sz = g.MeasureString(big, f);
                    float pop = (float)(1 + 0.35 * Math.Exp(-age * 6));
                    Text(g, big, f, A(m.Kind == MomentWatch.Kind.Streak ? Gold : Color.White, alpha), tx, box.Top + (box.Height - sz.Height) / 2 + P(2), pop);
                    if (m.Kind == MomentWatch.Kind.LastEnemy) Reticle(g, tx + sz.Width * 0.82f, box.Top + box.Height / 2, age, alpha, s);
                    tx += sz.Width + P(22);
                    using (var pen = new Pen(A(Color.White, alpha * 0.15), P(1))) g.DrawLine(pen, tx - P(10), box.Top + P(16), tx - P(14), box.Bottom - P(16));
                }
            }
            float maxW = box.Right - P(30) - tx;
            using (var f0 = Assets.Display(P(38)))
            {
                var f = Theme.Fit(g, title, f0, maxW);
                Text(g, title, f, A(accent, alpha), tx, box.Top + P(12), (float)(1 + 0.25 * Math.Exp(-age * 8)));
                if (f != f0) f.Dispose();
            }
            using (var f0 = Assets.Ui(P(17), true))
            {
                var f = Theme.Fit(g, sub, f0, maxW);
                using (var br = new SolidBrush(A(Color.FromArgb(225, 230, 238), alpha))) g.DrawString(sub, f, br, tx + P(2), box.Bottom - P(36));
                if (f != f0) f.Dispose();
            }
            if (m.Kind == MomentWatch.Kind.Flawless)
                for (int i = 0; i < 5; i++) // beş kalkan: beşi de ayakta
                {
                    float x = box.Right - P(40) - i * P(20), y = box.Top + P(22);
                    double pop = Ease((age - 0.25 - (4 - i) * 0.08) / 0.2);
                    using (var br = new SolidBrush(A(Cyan, alpha * pop))) g.FillPolygon(br, Shield(x, y, P(7) * (float)pop));
                }

            if (m.Kind == MomentWatch.Kind.Ace || m.Kind == MomentWatch.Kind.FourK)
                for (int i = 0; i < m.N; i++) // kafatası yerine öldürme pip'leri: sırayla patlayarak gelir
                {
                    float x = box.Right - P(40) - (m.N - 1 - i) * P(22), y = box.Top + P(24);
                    double pop = Ease((age - 0.2 - i * 0.12) / 0.18), r = P(8) * (float)pop * (1 + 0.6 * Math.Exp(-Math.Max(0, age - 0.2 - i * 0.12) * 10));
                    using (var br = new SolidBrush(A(accent, alpha * pop))) g.FillEllipse(br, x - (float)r, y - (float)r, (float)r * 2, (float)r * 2);
                }

            // ---- Kartın önündeki efektler ----
            switch (m.Kind)
            {
                case MomentWatch.Kind.FirstBlood: Slash(g, box, age, alpha, s); break;
                case MomentWatch.Kind.Flawless: Sparkles(g, box, age, alpha, s); break;
                case MomentWatch.Kind.Ace: if (age > 0.85) Burst(g, W / 2f, box.Top + box.Height / 2, age - 0.85, s); break;
                case MomentWatch.Kind.KnifeKill: Claws(g, box, age, alpha, s); break;
                case MomentWatch.Kind.NinjaDefuse: Shuriken(g, box, age, alpha, W, s); break;
                case MomentWatch.Kind.SelfKill: Dizzy(g, box, age, alpha, s); break;
            }
            g.Restore(tiltState);
        }

        /// <summary>Yere çarpıp seken düşüş (0..1 → 0..1; 1 = yerde).</summary>
        static double Bounce(double x)
        {
            x = Math.Max(0, Math.Min(1, x));
            if (x < 0.55) return Math.Pow(x / 0.55, 2);
            if (x < 0.8) { double u = (x - 0.675) / 0.125; return 1 - 0.18 * (1 - u * u); }
            double v = (x - 0.9) / 0.1; return 1 - 0.05 * (1 - v * v);
        }

        /// <summary>Düşme: kartın üstünde aşağı akan hız çizgileri (düşüş sürerken).</summary>
        static void SpeedLines(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            if (age > 0.6) return;
            var rnd = new Random(13);
            for (int i = 0; i < 12; i++)
            {
                float x = box.Left + 30 * s + (float)rnd.NextDouble() * (box.Width - 60 * s), len = (float)(30 + rnd.NextDouble() * 50) * s;
                float y = box.Top - 20 * s - (float)rnd.NextDouble() * 60 * s;
                using (var pen = new Pen(A(Color.White, alpha * 0.5 * (1 - age / 0.6)), 2 * s)) g.DrawLine(pen, x, y - len, x, y);
            }
        }

        /// <summary>Spike: kartın arkasında turuncu patlama halkaları ve yükselen gri duman.</summary>
        static void Explosion(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            float cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
            if (age < 0.25) using (var br = new SolidBrush(Color.FromArgb((int)(220 * (1 - age / 0.25)), 255, 240, 200))) g.FillEllipse(br, cx - 300 * s, cy - 140 * s, 600 * s, 280 * s);
            for (int i = 0; i < 3; i++)
            {
                double a = age - i * 0.12; if (a < 0 || a > 0.9) continue;
                float r = (float)((80 + a * 420) * s);
                using (var br = new SolidBrush(A(i == 0 ? Color.FromArgb(255, 220, 120) : Orange, alpha * 0.35 * (1 - a / 0.9)))) g.FillEllipse(br, cx - r, cy - r * 0.5f, r * 2, r);
            }
            var rnd = new Random(17);
            for (int i = 0; i < 14; i++)
            {
                double d = rnd.NextDouble() * 0.4, t = age - d; if (t < 0) continue;
                float x = box.Left + (float)rnd.NextDouble() * box.Width, y = box.Top - (float)(t * (40 + rnd.NextDouble() * 50) * s);
                float r = (float)((14 + t * 30) * s);
                using (var br = new SolidBrush(A(Color.FromArgb(120, 120, 128), alpha * 0.45 * Math.Max(0, 1 - t / 2.5)))) g.FillEllipse(br, x - r, y - r, r * 2, r * 2);
            }
        }

        /// <summary>Ninja: kartın iki ucundan yayılan duman bulutları.</summary>
        static void Smoke(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            var rnd = new Random(19);
            for (int i = 0; i < 18; i++)
            {
                bool leftSide = i % 2 == 0;
                float x0 = leftSide ? box.Left + 10 * s : box.Right - 10 * s, y0 = box.Top + (float)rnd.NextDouble() * box.Height;
                double ang = (leftSide ? Math.PI : 0) + (rnd.NextDouble() - 0.5) * 1.6, sp = (30 + rnd.NextDouble() * 60) * s;
                float x = x0 + (float)(Math.Cos(ang) * sp * age), y = y0 + (float)(Math.Sin(ang) * sp * age) - (float)(age * 12 * s);
                float r = (float)((16 + age * 26 + rnd.NextDouble() * 10) * s);
                using (var br = new SolidBrush(A(Color.FromArgb(150, 165, 160), alpha * 0.55 * Math.Max(0, 1 - age / 3.2)))) g.FillEllipse(br, x - r, y - r, r * 2, r * 2);
            }
        }

        /// <summary>Ninja: kartın üstünden dönerek geçip sağ köşeye saplanan yıldız (shuriken).</summary>
        static void Shuriken(Graphics g, RectangleF box, double age, double alpha, int W, float s)
        {
            double p = Ease(age / 0.55);
            float x = (float)(-40 * s + p * (box.Right - 34 * s + 40 * s)), y = box.Top + 4 * s - (float)(Math.Sin(p * Math.PI) * 50 * s);
            float rot = (float)(age < 0.55 ? age * 1400 : 0.55 * 1400 + 15), r = 15 * s;
            var st = g.Save();
            g.TranslateTransform(x, y); g.RotateTransform(rot);
            var pts = new List<PointF>();
            for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; float rr = i % 2 == 0 ? r : r * 0.32f; pts.Add(new PointF((float)Math.Cos(a) * rr, (float)Math.Sin(a) * rr)); }
            using (var br = new SolidBrush(A(Color.FromArgb(215, 222, 232), alpha))) g.FillPolygon(br, pts.ToArray());
            using (var br = new SolidBrush(A(Bg, alpha))) g.FillEllipse(br, -r * 0.18f, -r * 0.18f, r * 0.36f, r * 0.36f);
            g.Restore(st);
        }

        /// <summary>Bıçak: sırayla açılan üç paralel pençe izi.</summary>
        static void Claws(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            float bx = box.Right - 110 * s, by = box.Top + 14 * s;
            for (int i = 0; i < 3; i++)
            {
                double p = Ease((age - 0.2 - i * 0.07) / 0.14); if (p <= 0) continue;
                var a = new PointF(bx + i * 22 * s, by); var b = new PointF(bx + i * 22 * s + 46 * s, by + 66 * s);
                var e = new PointF(a.X + (b.X - a.X) * (float)p, a.Y + (b.Y - a.Y) * (float)p);
                using (var glow = new Pen(A(Cyan, alpha * 0.4), 9 * s) { StartCap = LineCap.Round, EndCap = LineCap.Triangle }) g.DrawLine(glow, a, e);
                using (var core = new Pen(A(Color.White, alpha), 2.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Triangle }) g.DrawLine(core, a, e);
            }
        }

        /// <summary>Kendi kendine: kartın üstünde dönen baş dönmesi yıldızları.</summary>
        static void Dizzy(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            float cx = box.Left + 130 * s, cy = box.Top - 18 * s, rx = 95 * s, ry = 18 * s;
            for (int i = 0; i < 5; i++)
            {
                double a = age * 4 + i * Math.PI * 2 / 5;
                float x = cx + (float)Math.Cos(a) * rx, y = cy + (float)Math.Sin(a) * ry, r = (float)((11 + 3 * Math.Sin(a)) * s);
                var pts = new List<PointF>();
                for (int j = 0; j < 10; j++) { double b = -Math.PI / 2 + j * Math.PI / 5; float rr = j % 2 == 0 ? r : r * 0.45f; pts.Add(new PointF(x + (float)Math.Cos(b) * rr, y + (float)Math.Sin(b) * rr)); }
                using (var br = new SolidBrush(A(i % 2 == 0 ? Gold : Color.White, alpha * (0.6 + 0.4 * Math.Sin(a))))) g.FillPolygon(br, pts.ToArray());
            }
        }

        static PointF[] Shield(float x, float y, float r) => new[]
        {
            new PointF(x - r, y - r), new PointF(x + r, y - r), new PointF(x + r, y + r * 0.2f), new PointF(x, y + r * 1.3f), new PointF(x - r, y + r * 0.2f),
        };

        /// <summary>İlk kan: kartı çapraz kesen kılıç izi, ardından yerçekimiyle düşen kan damlaları.</summary>
        static void Slash(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            var a = new PointF(box.Left - 30 * s, box.Bottom + 6 * s); var b = new PointF(box.Right + 30 * s, box.Top - 8 * s);
            double p = Math.Min(1, age / 0.22);
            var e = new PointF(a.X + (b.X - a.X) * (float)p, a.Y + (b.Y - a.Y) * (float)p);
            double trail = age < 0.22 ? 1 : Math.Max(0, 1 - (age - 0.22) / 0.5);
            using (var glow = new Pen(A(Red, alpha * 0.5 * trail), 12 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(glow, a, e);
            using (var core = new Pen(A(Color.White, alpha * trail), 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(core, a, e);
            using (var scar = new Pen(A(Red, alpha * 0.55), 2 * s)) g.DrawLine(scar, a, e);
            if (age < 0.15) return;
            double t = age - 0.15;
            var rnd = new Random(3);
            for (int i = 0; i < 46; i++)
            {
                double u = rnd.NextDouble(), vx = (rnd.NextDouble() - 0.3) * 160 * s, vy = -(60 + rnd.NextDouble() * 200) * s, size = (2.5 + rnd.NextDouble() * 5) * s;
                float x0 = a.X + (b.X - a.X) * (float)u, y0 = a.Y + (b.Y - a.Y) * (float)u;
                float x = x0 + (float)(vx * t), y = y0 + (float)(vy * t + 0.5 * 900 * s * t * t);
                using (var br = new SolidBrush(A(i % 5 == 0 ? Color.FromArgb(150, 20, 30) : Red, alpha * Math.Max(0, 1 - t / 1.6))))
                    g.FillEllipse(br, x - (float)size / 2, y - (float)size * 0.7f, (float)size, (float)size * 1.4f);
            }
        }

        /// <summary>Kusursuz: kartın çevresinde göz kırpan dört köşeli yıldızlar.</summary>
        static void Sparkles(Graphics g, RectangleF box, double age, double alpha, float s)
        {
            var rnd = new Random(5);
            for (int i = 0; i < 16; i++)
            {
                float x = box.Left - 40 * s + (float)rnd.NextDouble() * (box.Width + 80 * s), y = box.Top - 50 * s + (float)rnd.NextDouble() * (box.Height + 70 * s);
                double ph = rnd.NextDouble() * Math.PI * 2, tw = Math.Max(0, Math.Sin(age * 5 + ph));
                float r = (float)((5 + rnd.NextDouble() * 9) * s * tw);
                if (r < 0.5f) continue;
                var pts = new[] { new PointF(x, y - r), new PointF(x + r * 0.22f, y - r * 0.22f), new PointF(x + r, y), new PointF(x + r * 0.22f, y + r * 0.22f),
                                  new PointF(x, y + r), new PointF(x - r * 0.22f, y + r * 0.22f), new PointF(x - r, y), new PointF(x - r * 0.22f, y - r * 0.22f) };
                using (var br = new SolidBrush(A(i % 3 == 0 ? Cyan : Color.White, alpha * tw))) g.FillPolygon(br, pts);
            }
        }

        /// <summary>Son rakip: büyükten küçülüp dönerek hedefe kilitlenen nişangâh.</summary>
        static void Reticle(Graphics g, float cx, float cy, double age, double alpha, float s)
        {
            double p = Ease(age / 0.45);
            float r = (float)((110 - 80 * p) * s);
            float rot = (float)((1 - p) * 180 + age * 20);
            bool locked = age > 0.45;
            var c = locked && (int)(age * 6) % 2 == 0 ? Color.White : Orange;
            var st = g.Save();
            g.TranslateTransform(cx, cy); g.RotateTransform(rot);
            using (var pen = new Pen(A(c, alpha * (0.4 + 0.6 * p)), 2.2f * s))
            {
                g.DrawEllipse(pen, -r, -r, r * 2, r * 2);
                for (int i = 0; i < 4; i++)
                {
                    g.DrawLine(pen, 0, -r - 10 * s, 0, -r + 8 * s);
                    g.RotateTransform(90);
                }
            }
            g.Restore(st);
        }

        /// <summary>Seri: kartın üst kenarından yükselen alevler; seri uzadıkça daha yoğun.</summary>
        static void Flames(Graphics g, RectangleF box, double age, double alpha, int n, float s)
        {
            int count = Math.Min(90, 30 + n * 12);
            var rnd = new Random(9);
            for (int i = 0; i < count; i++)
            {
                double life = 0.6 + rnd.NextDouble() * 0.6, off = rnd.NextDouble() * life;
                float x0 = box.Left + 20 * s + (float)rnd.NextDouble() * (box.Width - 40 * s);
                double t = (age + off) % life, p = t / life;
                float x = x0 + (float)(Math.Sin((age + i) * 7) * 6 * s), y = box.Top + 10 * s - (float)(p * (60 + n * 10) * s);
                float r = (float)((1 - p) * (8 + rnd.NextDouble() * 8) * s);
                var c = p < 0.3 ? Color.FromArgb(255, 240, 160) : p < 0.6 ? Gold : p < 0.8 ? Orange : Red;
                using (var br = new SolidBrush(A(c, alpha * (1 - p) * Math.Min(1, age / 0.3)))) g.FillEllipse(br, x - r, y - r * 1.4f, r * 2, r * 2.8f);
            }
        }

        /// <summary>Geri dönüş: kartın iki yanından yukarı akan yeşil oklar.</summary>
        static void Chevrons(Graphics g, RectangleF box, double age, double alpha, int H, float s, bool down)
        {
            var c = down ? Sky : Green;
            foreach (float x in new[] { box.Left - 50 * s, box.Right + 50 * s })
                for (int i = 0; i < 4; i++)
                {
                    double p = (age * 0.9 + i / 4.0) % 1;
                    float span = box.Bottom - 10 * s, y = down ? 10 * s + (float)(p * span) : box.Bottom - (float)(p * span), w = 22 * s, d = down ? -1 : 1;
                    using (var pen = new Pen(A(c, alpha * Math.Sin(p * Math.PI)), 5 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(x - w, y + d * w * 0.6f), new PointF(x, y - d * w * 0.2f), new PointF(x + w, y + d * w * 0.6f) });
                }
        }

        /// <summary>Uzatma: yukarıdan karta inen, her ~0,09 sn'de yeniden çakan şimşekler.</summary>
        static void Lightning(Graphics g, RectangleF box, double age, double alpha, int W, float s)
        {
            int frame = (int)(age / 0.09);
            var rnd = new Random(frame * 31 + 1);
            for (int b = 0; b < 3; b++)
            {
                if (rnd.NextDouble() < 0.35) continue; // aralıklı çakma
                float x = box.Left + (float)rnd.NextDouble() * box.Width, y = 0;
                var pts = new List<PointF> { new PointF(x, y) };
                while (y < box.Top)
                {
                    y += (float)(12 + rnd.NextDouble() * 18) * s; x += (float)(rnd.NextDouble() - 0.5) * 36 * s;
                    pts.Add(new PointF(x, Math.Min(y, box.Top)));
                }
                using (var glow = new Pen(A(Purple, alpha * 0.45), 8 * s) { LineJoin = LineJoin.Round }) g.DrawLines(glow, pts.ToArray());
                using (var core = new Pen(A(Color.White, alpha * 0.95), 2 * s) { LineJoin = LineJoin.Round }) g.DrawLines(core, pts.ToArray());
            }
        }

        /// <summary>Altın patlama: şok dalgası + yerçekimiyle düşen parçacıklar (aynı tohum: kareler arası tutarlı).</summary>
        static void Burst(Graphics g, float cx, float cy, double rt, float s)
        {
            if (rt < 0.8)
                for (int w = 0; w < 2; w++)
                {
                    double a = rt - w * 0.15; if (a < 0) continue;
                    float r = (float)(s * (60 + a * 620));
                    using (var pen = new Pen(A(Gold, 0.9 * (1 - a / 0.8)), s * (5 - w * 2))) g.DrawEllipse(pen, cx - r, cy - r * 0.45f, r * 2, r * 0.9f);
                }
            if (rt > 2.2) return;
            var rnd = new Random(7);
            for (int i = 0; i < 90; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, sp = (240 + rnd.NextDouble() * 520) * s, size = (3 + rnd.NextDouble() * 5) * s;
                var col = (i % 3) == 0 ? Color.White : (i % 3) == 1 ? Gold : Red;
                float x = cx + (float)(Math.Cos(ang) * sp * rt), y = cy + (float)(Math.Sin(ang) * sp * 0.6 * rt + 0.5 * 700 * s * rt * rt);
                using (var br = new SolidBrush(A(col, Math.Max(0, 1 - rt / 2.2)))) g.FillRectangle(br, x - (float)size / 2, y - (float)size / 2, (float)size, (float)size);
            }
        }

        /// <summary>"F" yağmuru: farklı boy ve hızlarda yukarıdan düşen F'ler.</summary>
        static void FRain(Graphics g, double rt, int W, int H, float s, double fade)
        {
            var rnd = new Random(11);
            for (int i = 0; i < 34; i++)
            {
                float x = (float)(rnd.NextDouble() * W), size = (float)((20 + rnd.NextDouble() * 30) * s);
                double sp = (140 + rnd.NextDouble() * 160) * s, delay = rnd.NextDouble() * 1.2, rot = rnd.NextDouble() * 30 - 15;
                double tt = rt - delay; if (tt < 0) continue;
                float y = (float)(-size + sp * tt);
                if (y > H) continue;
                var st = g.Save();
                g.TranslateTransform(x, y); g.RotateTransform((float)rot);
                using (var f = Assets.Display(size))
                using (var br = new SolidBrush(A(i % 4 == 0 ? Red : Color.White, 0.85 * fade * Math.Min(1, tt / 0.2)))) g.DrawString("F", f, br, 0, 0);
                g.Restore(st);
            }
        }
    }
}
