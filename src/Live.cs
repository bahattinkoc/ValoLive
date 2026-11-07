using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>Riot istemcisinin kendi durum bilgisinden (presence) anlık maç durumu.</summary>
    sealed class LiveSnap
    {
        public bool InGame;
        public string State = "";                 // MENUS, PREGAME, INGAME ("" = Riot istemcisi kapalı)
        public bool Queueing;                     // sıra aranıyor
        public DateTime QueueSince;               // sıraya giriş (yerel saat)
        public string MapUrl = "", Map = "", Queue = "";
        public int Ally, Enemy;
    }

    /// <summary>Maç sonu özeti: Riot maç detayından, kendi oyunun.</summary>
    sealed class PostMatch
    {
        public string MatchId, MapUrl, Map, AgentId, Result, Queue;
        public int Ally, Enemy, K, D, A, Acs, Hs, FirstBloods, Plants, Defuses, Rounds;
        public List<PointF> Kills = new List<PointF>(), Deaths = new List<PointF>();   // oyun koordinatı
    }

    /// <summary>
    /// Canlı bilgi: her 3 sn'de kendi presence kaydın okunur (maçta mısın, harita, skor). Maç bitince
    /// (INGAME → menü) Riot maç detayı hazır olana dek beklenir, sonra maç sonu özeti çıkarılır.
    /// Oyuna dokunmaz; yalnızca istemcinin zaten arkadaşlarına yayınladığı bilgiyi ve kendi maç geçmişini okur.
    /// </summary>
    sealed class LiveInfo : IDisposable
    {
        readonly RiotLocal riot = new RiotLocal();
        readonly Timer timer;
        int busy;
        string lastMatch;      // bilinen son maç (yeni maç gelince özet çıkarılır)
        DateTime waitUntil = DateTime.MinValue;

        public LiveSnap Snap { get; private set; } = new LiveSnap();
        public PostMatch Last { get; private set; }
        public event Action<LiveSnap> Changed;
        public event Action<PostMatch> PostMatchReady;

        public LiveInfo() { timer = new Timer(_ => Tick(), null, 500, 3000); }

        void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var p = riot.Presence();
                var s = new LiveSnap();
                if (p != null)
                {
                    s.State = S(J(p, "matchPresenceData", "sessionLoopState"));
                    s.InGame = s.State == "INGAME";
                    s.Queueing = S(J(p, "partyPresenceData", "partyState")) == "MATCHMAKING";
                    // "2026.11.06-12.06.53" biçiminde, UTC.
                    if (DateTime.TryParseExact(S(J(p, "partyPresenceData", "queueEntryTime")), "yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var q))
                        s.QueueSince = q.ToLocalTime();
                    s.MapUrl = S(J(p, "matchPresenceData", "matchMap"));
                    s.Queue = S(J(p, "queueId"));
                    s.Ally = I(J(p, "partyOwnerMatchScoreAllyTeam"));
                    s.Enemy = I(J(p, "partyOwnerMatchScoreEnemyTeam"));
                    s.Map = s.MapUrl == "" ? "" : riot.MapNameOf(s.MapUrl);
                }
                var old = Snap;
                Snap = s;
                if (old.State != s.State || old.Queueing != s.Queueing || old.InGame != s.InGame || old.Ally != s.Ally || old.Enemy != s.Enemy || old.MapUrl != s.MapUrl) Changed?.Invoke(s);
                if (lastMatch == null) lastMatch = LatestMatchId() ?? "";
                if (old.InGame && !s.InGame) waitUntil = DateTime.Now.AddMinutes(3); // maç bitti: detay birkaç sn-dk içinde düşer
                if (DateTime.Now < waitUntil && DateTime.Now.Second % 9 < 3) CheckNewMatch();
            }
            catch { } // Riot istemcisi kapalı: sessizce bekle
            finally { busy = 0; }
        }

        string LatestMatchId()
        {
            var h = (J(riot.History(riot.SelfPuuid, 1), "History") as object[]) ?? new object[0];
            return h.Length > 0 ? S(J(h[0], "MatchID")) : null;
        }

        void CheckNewMatch()
        {
            var id = LatestMatchId();
            if (id == null || id == lastMatch) return;
            lastMatch = id;
            waitUntil = DateTime.MinValue;
            var pm = Build(riot, riot.MatchDetails(id), riot.SelfPuuid);
            if (pm == null) return;
            Last = pm;
            PostMatchReady?.Invoke(pm);
        }

        /// <summary>Maç detayından bir oyuncunun özeti ve öldürdüğü/öldüğü yerler.</summary>
        public static PostMatch Build(RiotLocal riot, object d, string me)
        {
            var players = (J(d, "players") as object[]) ?? new object[0];
            var mine = players.FirstOrDefault(p => S(J(p, "subject")) == me);
            if (mine == null) return null;
            string team = S(J(mine, "teamId"));
            var teams = (J(d, "teams") as object[]) ?? new object[0];
            var my = teams.FirstOrDefault(t => S(J(t, "teamId")) == team);
            var other = teams.FirstOrDefault(t => S(J(t, "teamId")) != team);
            var pm = new PostMatch
            {
                MatchId = S(J(d, "matchInfo", "matchId")),
                MapUrl = S(J(d, "matchInfo", "mapId")),
                Queue = S(J(d, "matchInfo", "queueID")),
                AgentId = S(J(mine, "characterId")),
                K = I(J(mine, "stats", "kills")), D = I(J(mine, "stats", "deaths")), A = I(J(mine, "stats", "assists")),
                Rounds = Math.Max(1, I(J(mine, "stats", "roundsPlayed"))),
                Ally = I(J(my, "roundsWon")), Enemy = I(J(other, "roundsWon")),
            };
            pm.Map = riot.MapNameOf(pm.MapUrl);
            pm.Acs = I(J(mine, "stats", "score")) / pm.Rounds;
            pm.Result = J(my, "won") is bool w && w ? "W" : J(other, "won") is bool l && l ? "L" : "D";
            int hs = 0, all = 0;
            foreach (var r in (J(d, "roundResults") as object[]) ?? new object[0])
            {
                if (S(J(r, "firstBloodPlayer")) == me) pm.FirstBloods++;
                if (S(J(r, "bombPlanter")) == me) pm.Plants++;
                if (S(J(r, "bombDefuser")) == me) pm.Defuses++;
                foreach (var ps in (J(r, "playerStats") as object[]) ?? new object[0])
                    if (S(J(ps, "subject")) == me)
                        foreach (var dm in (J(ps, "damage") as object[]) ?? new object[0])
                        { int h = I(J(dm, "headshots")); hs += h; all += h + I(J(dm, "bodyshots")) + I(J(dm, "legshots")); }
            }
            pm.Hs = all > 0 ? 100 * hs / all : 0;
            foreach (var k in (J(d, "kills") as object[]) ?? new object[0])
            {
                if (S(J(k, "killer")) == me)
                {
                    // Öldürdüğün anda nerede durduğun (ölenin değil): playerLocations içinde kendi kaydın.
                    var at = ((J(k, "playerLocations") as object[]) ?? new object[0]).FirstOrDefault(pl => S(J(pl, "subject")) == me);
                    if (at != null) pm.Kills.Add(new PointF(I(J(at, "location", "x")), I(J(at, "location", "y"))));
                }
                if (S(J(k, "victim")) == me)
                    pm.Deaths.Add(new PointF(I(J(k, "victimLocation", "x")), I(J(k, "victimLocation", "y"))));
            }
            return pm;
        }

        public void Dispose() => timer.Dispose();
    }

    /// <summary>
    /// Maç sonu kartı: minimap üzerinde öldürdüğün (yeşil) ve öldüğün (kırmızı) yerler + maçın özeti.
    /// Maç bitip Riot detayı gelince 25 sn görünür, kayarak girip çıkar.
    /// </summary>
    sealed class PostMatchLayer : Layer
    {
        static readonly Color Win = ColorTranslator.FromHtml("#3ddc97"), Loss = ColorTranslator.FromHtml("#ff5a6e");
        static readonly Color Muted = ColorTranslator.FromHtml("#9aa3b2"), Accent = ColorTranslator.FromHtml("#ff4655");
        const double ShowSec = 25, SlideSec = 0.5;

        readonly object lk = new object();
        PostMatch pm;
        ValApi.Map map;
        DateTime shownAt = DateTime.MinValue;
        bool drawnHidden;
        public Func<bool> Hidden = () => false;

        public PostMatchLayer(int x, int y, float scale) : base(x, y, (int)(440 * scale), (int)(610 * scale), 30, scale) { }

        public void Show(PostMatch m)
        {
            var mp = ValApi.MapOf(m.MapUrl);   // ağ: çizim iş parçacığında beklenmesin diye burada
            if (mp != null) Assets.Icon(mp.Minimap);
            Assets.Icon(OverlayRenderer.AgentIcon(m.AgentId));
            Thread.Sleep(1500);                // görseller insin
            lock (lk) { pm = m; map = mp; shownAt = DateTime.Now; drawnHidden = false; }
        }

        double Age() { lock (lk) return (DateTime.Now - shownAt).TotalSeconds; }

        protected override bool NeedsRender()
        {
            bool visible = Age() < ShowSec && !Hidden();
            if (visible) { drawnHidden = false; return true; }
            if (drawnHidden) return false;
            drawnHidden = true;                 // bir kez boş kare
            return true;
        }

        protected override void Draw(Graphics g, double t)
        {
            PostMatch m; ValApi.Map mp; double age;
            lock (lk) { m = pm; mp = map; age = (DateTime.Now - shownAt).TotalSeconds; }
            if (m == null || age >= ShowSec || Hidden()) return;
            double a = Math.Min(1, Math.Min(age / SlideSec, (ShowSec - age) / SlideSec));
            g.TranslateTransform((float)((1 - a) * P(60)), 0);
            Render(g, m, mp, a, W, H, s);
        }

        /// <summary>Kartı çizer (test için dışarıdan da çağrılır).</summary>
        public static void Render(Graphics g, PostMatch m, ValApi.Map mp, double a, int W, int H, float s)
        {
            float P(float v) => v * s;
            var res = m.Result == "W" ? Win : m.Result == "L" ? Loss : Muted;
            var box = new RectangleF(0, 0, W - 1, H - 1);
            using (var b = new SolidBrush(Color.FromArgb((int)(235 * a), 12, 14, 20))) g.FillRectangle(b, box);
            using (var b = new SolidBrush(A(res, a))) g.FillRectangle(b, 0, 0, P(5), H);

            // Başlık: sonuç + skor + harita
            var icon = Assets.Icon(OverlayRenderer.AgentIcon(m.AgentId));
            if (icon != null) lock (icon) g.DrawImage(icon, P(18), P(16), P(54), P(54));
            using (var f = Assets.Display(P(30)))
                Theme.DrawFit(g, (m.Result == "W" ? L.T("ZAFER") : m.Result == "L" ? L.T("YENİLGİ") : L.T("BERABERE")) + $"  {m.Ally}-{m.Enemy}", f, A(res, a), P(82), P(14), W - P(96));
            using (var f = Assets.Ui(P(15), true)) Theme.DrawFit(g, m.Map.ToUpper(L.Culture) + " · " + L.T("MAÇ ÖZETİ"), f, A(Muted, a), P(84), P(50), W - P(98));

            // Minimap + noktalar
            float mx = P(20), my = P(84), ms = W - P(40);
            var mini = mp != null ? Assets.Icon(mp.Minimap) : null;
            using (var b = new SolidBrush(Color.FromArgb((int)(28 * a), 255, 255, 255))) g.FillRectangle(b, mx, my, ms, ms);
            if (mini != null)
                lock (mini)
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)(0.85 * a) });
                        g.DrawImage(mini, Rectangle.Round(new RectangleF(mx, my, ms, ms)), 0, 0, mini.Width, mini.Height, GraphicsUnit.Pixel, ia);
                    }
            if (mp != null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                void Dots(List<PointF> pts, Color c, bool cross)
                {
                    foreach (var pt in pts)
                    {
                        var u = mp.ToMinimap(pt.X, pt.Y);
                        if (u.X < 0 || u.X > 1 || u.Y < 0 || u.Y > 1) continue;
                        float px = mx + u.X * ms, py = my + u.Y * ms, r = P(5.5f);
                        using (var glow = new SolidBrush(A(c, 0.30 * a))) g.FillEllipse(glow, px - r * 1.9f, py - r * 1.9f, r * 3.8f, r * 3.8f);
                        if (cross)
                            using (var pen = new Pen(A(c, a), P(2.6f))) { g.DrawLine(pen, px - r, py - r, px + r, py + r); g.DrawLine(pen, px - r, py + r, px + r, py - r); }
                        else
                            using (var br = new SolidBrush(A(c, a))) using (var pen = new Pen(A(Color.Black, 0.6 * a), P(1.2f))) { g.FillEllipse(br, px - r, py - r, r * 2, r * 2); g.DrawEllipse(pen, px - r, py - r, r * 2, r * 2); }
                    }
                }
                Dots(m.Deaths, Loss, true);
                Dots(m.Kills, Win, false);
            }

            // İstatistik satırı
            float sy = my + ms + P(14);
            var stats = new[]
            {
                ("K/D/A", $"{m.K}/{m.D}/{m.A}"), ("ACS", m.Acs.ToString()), ("HS", L.Pct(m.Hs)), (L.T("İLK KAN"), m.FirstBloods.ToString()),
            };
            float cw = (W - P(40)) / stats.Length;
            for (int i = 0; i < stats.Length; i++)
            {
                float cx = P(20) + i * cw;
                using (var f = Assets.Ui(P(12), true)) Theme.DrawFit(g, stats[i].Item1, f, A(Muted, a), cx, sy, cw - P(6));
                using (var f = Assets.Display(P(24))) Theme.DrawFit(g, stats[i].Item2, f, A(Color.White, a), cx, sy + P(16), cw - P(8));
            }
            // Açıklama satırı: öğeler yan yana ve ölçülerek; kartın genişliğini aşarsa hepsi birlikte küçülür.
            string kills = L.T("öldürdüğün yerler"), deaths = L.T("öldüğün yerler"), plants = m.Plants + m.Defuses > 0 ? L.F("kurma {0} · imha {1}", m.Plants, m.Defuses) : "";
            using (var f0 = Assets.Ui(P(12), true))
            {
                float need = P(14) + g.MeasureString(kills, f0).Width + P(26) + g.MeasureString(deaths, f0).Width + (plants != "" ? P(16) + g.MeasureString(plants, f0).Width : 0);
                var f = need > W - P(40) ? new Font(f0.FontFamily, f0.Size * Math.Max(0.7f, (W - P(40)) / need), f0.Style, f0.Unit) : f0;
                float lx = P(20), ly = H - P(30);
                using (var br = new SolidBrush(A(Win, a))) g.FillEllipse(br, lx, ly + P(3), P(10), P(10));
                Draw(g, kills, f, A(Muted, a), lx + P(14), ly);
                lx += P(14) + g.MeasureString(kills, f).Width + P(16);
                using (var pen = new Pen(A(Loss, a), P(2.2f))) { float cx = lx, cy = ly + P(8); g.DrawLine(pen, cx - P(5), cy - P(5), cx + P(5), cy + P(5)); g.DrawLine(pen, cx - P(5), cy + P(5), cx + P(5), cy - P(5)); }
                Draw(g, deaths, f, A(Muted, a), lx + P(10), ly);
                lx += P(10) + g.MeasureString(deaths, f).Width + P(16);
                if (plants != "") Draw(g, plants, f, A(Muted, a), lx, ly);
                if (f != f0) f.Dispose();
            }
        }

        static void Draw(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }
    }
}
