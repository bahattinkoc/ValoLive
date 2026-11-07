using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using static ValoLive.ValorantTracker;

namespace ValoLive
{
    /// <summary>Bir oyuncunun tek maçtaki oyun stili özeti (maç detayından). Kendi analizin için diske de yazılır.</summary>
    sealed class PMatch
    {
        public string Id, MapUrl, Queue, Agent, Result;
        public long Start;
        public int Rounds, Kills, Deaths, FirstKills, FirstDeaths, FirstDeathLost, Traded, Ults, OpKills, SheriffKills, Hs, Shots;
        public int AtkRounds, AtkWon, DefRounds, DefWon, AtkDmg, DefDmg;
        public Dictionary<string, int> Plants = new Dictionary<string, int>();   // kurduğu bölge → kaç kez
        public List<int[]> KillSpots = new List<int[]>(), DeathSpots = new List<int[]>(); // {x, y, atak ise 1}
        public Dictionary<string, int[]> Guns = new Dictionary<string, int[]>(); // "Vandal"/"Phantom" → {round, kill, kafa, isabet}
    }

    /// <summary>Maç detayını oyuncu bazında çözümler: ilk düellolar, trade, silah, taraf ve konumlar.</summary>
    static class Scout
    {
        // Oyun stili okunabilen modlar (round yapısı normal olanlar).
        public static readonly HashSet<string> Queues = new HashSet<string> { "competitive", "unrated", "swiftplay", "premier", "newmap" };

        const string Operator = "A03B24D3-4319-996D-0F8C-94BBFBA1DFC7", Sheriff = "E336C6B8-418D-9340-D77F-7A9E4CFE0702";
        const string Vandal = "9C82E19D-4575-0200-1A81-3EACF00CF872", Phantom = "EE8E8D15-496B-07AC-E5F6-8FAE5D4C7B1A";
        const int TradeMs = 5000; // öldüğün andan sonra 5 sn içinde katilin vuruluyorsa "trade edildin"

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Roundun atak takımı: kazanan takım ve rolünden çıkar ("" = bilinmiyor).</summary>
        static string Attackers(object round)
        {
            string w = S(J(round, "winningTeam")), role = S(J(round, "winningTeamRole"));
            if (w == "" || role == "") return "";
            return role == "Attacker" ? w : w == "Red" ? "Blue" : "Red";
        }

        public static PMatch Analyze(object d, string puuid)
        {
            var players = (J(d, "players") as object[]) ?? new object[0];
            var me = players.FirstOrDefault(p => S(J(p, "subject")) == puuid);
            if (me == null) return null;
            var teamOf = players.ToDictionary(p => S(J(p, "subject")), p => S(J(p, "teamId")));
            string team = teamOf[puuid];
            var teams = (J(d, "teams") as object[]) ?? new object[0];
            var mine = teams.FirstOrDefault(t => S(J(t, "teamId")) == team);
            bool won = J(mine, "won") is bool b && b, anyWon = teams.Any(t => J(t, "won") is bool x && x);

            var pm = new PMatch
            {
                Id = S(J(d, "matchInfo", "matchId")), MapUrl = S(J(d, "matchInfo", "mapId")), Queue = S(J(d, "matchInfo", "queueID")),
                Start = Convert.ToInt64(J(d, "matchInfo", "gameStartMillis") ?? 0L), Agent = S(J(me, "characterId")),
                Result = won ? "W" : anyWon ? "L" : "D",
                Rounds = I(J(me, "stats", "roundsPlayed")), Kills = I(J(me, "stats", "kills")), Deaths = I(J(me, "stats", "deaths")),
                Ults = I(J(me, "stats", "abilityCasts", "ultimateCasts")),
            };

            var atkOf = new Dictionary<int, bool>();
            var roundWon = new Dictionary<int, bool>();
            foreach (var r in (J(d, "roundResults") as object[]) ?? new object[0])
            {
                int n = I(J(r, "roundNum"));
                string att = Attackers(r);
                bool atk = att == team, win = S(J(r, "winningTeam")) == team;
                roundWon[n] = win;
                if (att != "") atkOf[n] = atk;
                if (S(J(r, "bombPlanter")) == puuid && S(J(r, "plantSite")) != "")
                {
                    string site = S(J(r, "plantSite"));
                    pm.Plants[site] = (pm.Plants.TryGetValue(site, out int c) ? c : 0) + 1;
                }
                var ps = ((J(r, "playerStats") as object[]) ?? new object[0]).FirstOrDefault(p => S(J(p, "subject")) == puuid);
                if (ps == null) continue;
                int dmg = 0, hs = 0, shots = 0;
                foreach (var dm in (J(ps, "damage") as object[]) ?? new object[0])
                {
                    dmg += I(J(dm, "damage"));
                    int h = I(J(dm, "headshots"));
                    hs += h; shots += h + I(J(dm, "bodyshots")) + I(J(dm, "legshots"));
                }
                pm.Hs += hs; pm.Shots += shots;
                if (att != "") { if (atk) { pm.AtkRounds++; if (win) pm.AtkWon++; pm.AtkDmg += dmg; } else { pm.DefRounds++; if (win) pm.DefWon++; pm.DefDmg += dmg; } }
                string gun = S(J(ps, "economy", "weapon"));
                string key = Eq(gun, Vandal) ? "Vandal" : Eq(gun, Phantom) ? "Phantom" : null;
                if (key != null)
                {
                    if (!pm.Guns.TryGetValue(key, out var gs)) pm.Guns[key] = gs = new int[4];
                    gs[0]++; gs[1] += ((J(ps, "kills") as object[]) ?? new object[0]).Length; gs[2] += hs; gs[3] += shots;
                }
            }

            var kills = ((J(d, "kills") as object[]) ?? new object[0]).OrderBy(k => I(J(k, "round"))).ThenBy(k => I(J(k, "roundTime"))).ToList();
            foreach (var round in kills.GroupBy(k => I(J(k, "round"))))
            {
                var list = round.ToList();
                bool atk = atkOf.TryGetValue(round.Key, out var a) && a;
                var first = list[0];
                if (S(J(first, "killer")) == puuid) pm.FirstKills++;
                if (S(J(first, "victim")) == puuid) { pm.FirstDeaths++; if (roundWon.TryGetValue(round.Key, out var w) && !w) pm.FirstDeathLost++; }
                foreach (var k in list)
                {
                    string killer = S(J(k, "killer")), victim = S(J(k, "victim"));
                    if (killer == puuid)
                    {
                        string item = S(J(k, "finishingDamage", "damageItem"));
                        if (Eq(item, Operator)) pm.OpKills++;
                        if (Eq(item, Sheriff)) pm.SheriffKills++;
                        var at = ((J(k, "playerLocations") as object[]) ?? new object[0]).FirstOrDefault(pl => S(J(pl, "subject")) == puuid);
                        if (at != null) pm.KillSpots.Add(new[] { I(J(at, "location", "x")), I(J(at, "location", "y")), atk ? 1 : 0 });
                    }
                    if (victim == puuid)
                    {
                        pm.DeathSpots.Add(new[] { I(J(k, "victimLocation", "x")), I(J(k, "victimLocation", "y")), atk ? 1 : 0 });
                        int t = I(J(k, "roundTime"));
                        // Trade: katilin, takım arkadaşlarından biri tarafından kısa süre içinde vurulması.
                        if (list.Any(k2 => S(J(k2, "victim")) == killer && I(J(k2, "roundTime")) >= t && I(J(k2, "roundTime")) - t <= TradeMs
                                           && teamOf.TryGetValue(S(J(k2, "killer")), out var kt) && kt == team))
                            pm.Traded++;
                    }
                }
            }
            return pm;
        }

        public sealed class Tag { public string Text, Note; public Color Color; }

        /// <summary>Oyuncunun tracker.gg profilini tarayıcıda açar.</summary>
        public static void OpenProfile(string name, string tag)
        {
            if (string.IsNullOrEmpty(name)) return;
            try { System.Diagnostics.Process.Start("https://tracker.gg/valorant/profile/riot/" + Uri.EscapeDataString(name + "#" + tag) + "/overview"); }
            catch (Exception e) { Diag.Event("ODA", "profil açılamadı: " + e.Message); }
        }

        static readonly Color Orange = ColorTranslator.FromHtml("#ffb547"), Blue = ColorTranslator.FromHtml("#7aa2ff"),
            Pink = ColorTranslator.FromHtml("#ff5a6e"), Green = ColorTranslator.FromHtml("#3ddc97"), Purple = ColorTranslator.FromHtml("#c890f5");

        static int Pct(int a, int b) => b > 0 ? (int)Math.Round(100.0 * a / b) : 0;

        /// <summary>
        /// Oyun stili etiketleri, en belirgini başta. Bir oyuncunun bir roundda ilk kanı alma/ilk ölme ihtimali ortalama
        /// ~%10 (10 oyuncu); eşikler bunun belirgin üstü. Az veri varsa (40 round altı) etiket verilmez.
        /// </summary>
        public static List<Tag> Tags(List<PMatch> ms)
        {
            var tags = new List<Tag>();
            int r = ms.Sum(m => m.Rounds), k = ms.Sum(m => m.Kills), d = ms.Sum(m => m.Deaths);
            if (r < 40) return tags;
            int fk = Pct(ms.Sum(m => m.FirstKills), r), fd = Pct(ms.Sum(m => m.FirstDeaths), r);
            int op = Pct(ms.Sum(m => m.OpKills), k), sh = Pct(ms.Sum(m => m.SheriffKills), k);
            int tr = Pct(ms.Sum(m => m.Traded), d), hs = Pct(ms.Sum(m => m.Hs), ms.Sum(m => m.Shots));
            if (op >= 20 && k >= 20) tags.Add(new Tag { Text = L.T("OP'ÇU"), Color = Purple, Note = L.F("Öldürmelerinin büyük kısmı Operator ile (%{0}). Uzun açıları kuru peek'leme, flaşla ya da başka yerden gir.", op) });
            if (fk >= 15) tags.Add(new Tag { Text = L.T("GİRİŞÇİ"), Color = Orange, Note = L.F("Roundu o açıyor: ilk kan %{0}", fk) + (fd >= 15 ? L.F(", ilk ölüm %{0}", fd) : "") + L.T(". İlk temas genelde ondan gelir; açıyı önceden tut, kuru peek verme.") });
            else if (fd >= 15) tags.Add(new Tag { Text = L.T("AGRESİF"), Color = Pink, Note = L.F("Sık ilk ölüyor (roundların %{0}). Erken peek atar; sabırlı bekle, sana gelir.", fd) });
            else if (fk + fd <= 12) tags.Add(new Tag { Text = L.T("SABIRLI"), Color = Blue, Note = L.F("İlk düelloya nadiren giriyor (%{0}). Geç dönüş/lurk bekle, arkanı kontrol et.", fk + fd) });
            if (d >= 20 && tr < 12) tags.Add(new Tag { Text = L.T("YALNIZ"), Color = Blue, Note = L.F("Öldüğünde nadiren trade ediliyor (%{0}). Takımından uzakta, tek başına oynuyor.", tr) });
            if (sh >= 10 && k >= 20) tags.Add(new Tag { Text = "SHERIFF", Color = Orange, Note = L.F("Sheriff ile çok öldürüyor (öldürmelerinin %{0}). Eco roundunda da tek atabilir.", sh) });
            if (hs >= 30) tags.Add(new Tag { Text = L.T("KAFACI"), Color = Green, Note = L.F("Kafa isabeti yüksek (%{0}). Düz düelloya değil, fayda ile gir.", hs) });
            return tags;
        }
    }

    /// <summary>Minimap üzerinde nokta yoğunluğu (ısı haritası) çizimi; atak turuncu, defans mavi.</summary>
    static class Heat
    {
        public static readonly Color Atk = ColorTranslator.FromHtml("#ffb547"), Def = ColorTranslator.FromHtml("#7aa2ff");

        /// <summary>side: 0 hepsi, 1 yalnız atak, 2 yalnız defans.</summary>
        public static void Draw(Graphics g, ValApi.Map mp, RectangleF box, IEnumerable<int[]> spots, int side, bool cross)
        {
            using (var b = new SolidBrush(Color.FromArgb(255, 22, 25, 33))) g.FillRectangle(b, box);
            var mini = mp != null ? Assets.Icon(mp.Minimap) : null;
            if (mini != null)
                lock (mini)
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.75f });
                        g.DrawImage(mini, Rectangle.Round(box), 0, 0, mini.Width, mini.Height, GraphicsUnit.Pixel, ia);
                    }
            if (mp == null) return;
            var pts = spots.Where(s => side == 0 || (side == 1) == (s[2] == 1)).ToList();
            // Önce geniş soluk halkalar (yoğunluk), sonra küçük net noktalar.
            foreach (var s in pts)
            {
                var u = mp.ToMinimap(s[0], s[1]);
                if (u.X < 0 || u.X > 1 || u.Y < 0 || u.Y > 1) continue;
                float px = box.X + u.X * box.Width, py = box.Y + u.Y * box.Height, r = box.Width / 28;
                using (var glow = new SolidBrush(Color.FromArgb(55, s[2] == 1 ? Atk : Def))) g.FillEllipse(glow, px - r, py - r, r * 2, r * 2);
            }
            foreach (var s in pts)
            {
                var u = mp.ToMinimap(s[0], s[1]);
                if (u.X < 0 || u.X > 1 || u.Y < 0 || u.Y > 1) continue;
                float px = box.X + u.X * box.Width, py = box.Y + u.Y * box.Height, r = 3.2f;
                var c = s[2] == 1 ? Atk : Def;
                if (cross)
                    using (var pen = new Pen(c, 2f)) { g.DrawLine(pen, px - r, py - r, px + r, py + r); g.DrawLine(pen, px - r, py + r, px + r, py - r); }
                else
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, px - r, py - r, r * 2, r * 2);
            }
        }
    }

    /// <summary>Koyu, yakalamadan dışlanmış, elle çizilen pencere tabanı (oyuncu ve kendi analiz pencereleri).</summary>
    class DarkForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        protected static readonly Color Bg = Theme.Bg, Card = Theme.Card, Line = Theme.Line, Txt = Theme.Txt, Mut = Theme.Mut, Red = Theme.Red, Win = ColorTranslator.FromHtml("#3ddc97"), Loss = ColorTranslator.FromHtml("#ff5a6e"),
            Warn = ColorTranslator.FromHtml("#ffb547");

        // Bekleyen görseller (minimap, ajan ikonu) inince kendini yeniler.
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 700 };
        protected readonly List<KeyValuePair<RectangleF, Action>> clicks = new List<KeyValuePair<RectangleF, Action>>();

        public DarkForm()
        {
            BackColor = Bg; ForeColor = Txt;
            Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            refresh.Tick += (s, e) => Invalidate();
            refresh.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11); // yayına/kayda girmez
            Theme.DarkTitle(this);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var hit = clicks.FirstOrDefault(c => c.Key.Contains(e.Location)).Value;
            if (hit != null) { hit(); Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = clicks.Any(c => c.Key.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { refresh.Dispose(); base.OnFormClosed(e); }

        protected static void Draw(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }

        /// <summary>Kutuya satır kırarak yazar; uzun çeviri kutuya sığmazsa yazı tipi %25'e kadar küçülür.</summary>
        protected static void Wrap(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            var ff = f;
            while (g.MeasureString(s, ff, (int)r.Width).Height > r.Height + 1 && ff.Size - 0.25f >= f.Size * 0.75f)
            {
                var next = new Font(f.FontFamily, ff.Size - 0.25f, f.Style, f.Unit);
                if (ff != f) ff.Dispose();
                ff = next;
            }
            using (var b = new SolidBrush(c)) g.DrawString(s, ff, b, r);
            if (ff != f) ff.Dispose();
        }

        /// <summary>Bölüm başlığı + ince çizgi; altındaki içeriğin y'sini döner.</summary>
        protected static float Section(Graphics g, string text, float x, float y, float w)
        {
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, x, y + 3, 4, 13);
            using (var f = Theme.Display(15)) Theme.DrawFit(g, Theme.Upper(text), f, Txt, x + 10, y, w - 10);
            using (var b = new SolidBrush(Line)) g.FillRectangle(b, x, y + 22, w, 1);
            return y + 32;
        }

        /// <summary>Büyük sayı + altında açıklama (sayı kutusu); açıklama kutu genişliğine (w) sığdırılır.</summary>
        protected static void Stat(Graphics g, string value, string label, Color c, float x, float y, float w)
        {
            using (var f = new Font(L.SansBold, 17f)) Draw(g, value, f, c, x, y);
            using (var f = new Font(L.Sans, 8.5f)) Theme.DrawFit(g, label, f, Mut, x + 2, y + 32, w - 8);
        }

        /// <summary>"Hepsi · Atak · Defans" seçici; tıklanınca side değişir. Sağ kenarının x'ini döner.</summary>
        protected float SideSwitch(Graphics g, float x, float y, int side, Action<int> set)
        {
            string[] names = { L.T("Hepsi"), L.T("Atak"), L.T("Defans") };
            using (var f = new Font(L.SansBold, 8.5f))
                for (int i = 0; i < 3; i++)
                {
                    var sz = g.MeasureString(names[i], f);
                    var r = new RectangleF(x, y, sz.Width + 14, 22);
                    using (var b = new SolidBrush(i == side ? Color.FromArgb(70, i == 1 ? Heat.Atk : i == 2 ? Heat.Def : Txt) : Card)) g.FillRectangle(b, r);
                    Draw(g, names[i], f, i == side ? Txt : Mut, x + 7, y + 3);
                    int k = i;
                    clicks.Add(new KeyValuePair<RectangleF, Action>(r, () => set(k)));
                    x += r.Width + 4;
                }
            return x;
        }

        protected static float Pill(Graphics g, string s, Font f, Color bg, Color fg, float x, float y)
        {
            var sz = g.MeasureString(s, f);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, x, y, sz.Width + 6, sz.Height);
            Draw(g, s, f, fg, x + 3, y);
            return x + sz.Width + 12;
        }

        protected static int Pct(int a, int b) => b > 0 ? (int)Math.Round(100.0 * a / b) : 0;
    }

    /// <summary>Maç Odası'nda bir oyuncuya tıklayınca: oyun stili, sayılar ve bu haritada nereden öldürdüğü.</summary>
    sealed class ScoutForm : DarkForm
    {
        readonly Func<string, List<PMatch>> data;   // puuid → çözümlenmiş maçları (yükleniyorsa eksik)
        readonly Func<string, bool> loading;
        LobbyPlayer player;
        string mapUrl;
        int side;

        public ScoutForm(Func<string, List<PMatch>> data, Func<string, bool> loading)
        {
            this.data = data; this.loading = loading;
            Text = L.T("ValoLive — Oyuncu Analizi");
            ClientSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
        }

        public void ShowPlayer(LobbyPlayer p, string map)
        {
            player = p; mapUrl = map; side = 0;
            Text = "ValoLive — " + p.Name + " #" + p.Tag;
            if (!Visible) Show(); else Activate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            clicks.Clear();
            if (player == null) return;
            var ms = data(player.Puuid) ?? new List<PMatch>();
            bool busy = loading(player.Puuid);

            // ---- Sol sütun: kim, stil, sayılar ----
            const float Lx = 20, LW = 400;
            var icon = Assets.Icon(OverlayRenderer.AgentIcon(player.AgentId));
            if (icon != null) lock (icon) g.DrawImage(icon, Lx, 18, 48, 48);
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, 0, 0, ClientSize.Width, 3);
            using (var f = Theme.Display(24)) Draw(g, player.Name ?? "…", f, Txt, Lx + 60, 14);
            using (var f = new Font(L.Sans, 9f))
            {
                string who = "#" + player.Tag + "  ·  " + (player.Ally ? L.T("takım arkadaşın") : L.T("rakip")) + "  ·  ";
                Draw(g, who, f, Mut, Lx + 62, 44);
                // Profil bağlantısı (tracker.gg)
                var lx = Lx + 62 + g.MeasureString(who, f).Width - 4;
                float lw;
                using (var lf = new Font(L.SansBold, 9f)) { Draw(g, L.T("Profili aç ↗"), lf, Red, lx, 44); lw = g.MeasureString(L.T("Profili aç ↗"), lf).Width; }
                var p = player;
                clicks.Add(new KeyValuePair<RectangleF, Action>(new RectangleF(lx, 42, lw, 20), () => Scout.OpenProfile(p.Name, p.Tag)));
                string sub = ms.Count == 0 ? (busy ? L.T("maçları inceleniyor…") : L.T("incelenecek maç bulunamadı"))
                    : L.F("son {0} maç · {1} round incelendi", ms.Count, ms.Sum(m => m.Rounds)) + (busy ? L.T(" · devam ediyor…") : "");
                Theme.DrawFit(g, sub, f, busy ? Warn : Mut, Lx + 62, 62, Lx + LW - (Lx + 62));
            }

            float y = Section(g, L.T("OYUN STİLİ"), Lx, 96, LW);
            var tags = Scout.Tags(ms);
            if (tags.Count == 0)
                using (var f = new Font(L.Sans, 9.5f))
                    Wrap(g, ms.Sum(m => m.Rounds) < 40 ? L.T("Stil çıkarmak için yeterli round yok (en az 40).") : L.T("Belirgin bir alışkanlık yok — dengeli oynuyor."), f, Mut, new RectangleF(Lx, y, LW, 40));
            else
                using (var tf = new Font(L.SansBold, 8.5f))
                using (var nf = new Font(L.Sans, 9f))
                {
                    // Not sütunu en geniş etiketin hemen sağından başlar (etiket adları dile göre uzar).
                    float nx = Math.Max(82, tags.Take(3).Max(t => g.MeasureString(t.Text, tf).Width) + 14);
                    foreach (var t in tags.Take(3))
                    {
                        Pill(g, t.Text, tf, t.Color, Bg, Lx, y + 1);
                        Wrap(g, t.Note, nf, Txt, new RectangleF(Lx + nx, y, LW - nx, 44));
                        y += 46;
                    }
                }

            y = Math.Max(y, 96 + 30 + 3 * 46);
            y = Section(g, L.T("SAYILAR"), Lx, y + 4, LW);
            int r = ms.Sum(m => m.Rounds), k = ms.Sum(m => m.Kills), d = ms.Sum(m => m.Deaths);
            var cells = new[]
            {
                (L.Pct(Pct(ms.Sum(m => m.FirstKills), r)), L.T("ilk kan / round")),
                (L.Pct(Pct(ms.Sum(m => m.FirstDeaths), r)), L.T("ilk ölüm / round")),
                (L.Pct(Pct(ms.Sum(m => m.Traded), d)), L.T("ölümü trade edilen")),
                (L.Pct(Pct(ms.Sum(m => m.Hs), ms.Sum(m => m.Shots))), L.T("kafa isabeti")),
                (d > 0 ? ((double)k / d).ToString("0.00") : "—", "K/D"),
                (ms.Count > 0 ? ((double)ms.Sum(m => m.Ults) / ms.Count).ToString("0.0") : "—", L.T("ulti / maç")),
            };
            for (int i = 0; i < cells.Length; i++)
                Stat(g, ms.Count > 0 ? cells[i].Item1 : "—", cells[i].Item2, Txt, Lx + (i % 3) * (LW / 3), y + (i / 3) * 62, LW / 3);

            // ---- Sağ sütun: bu haritada nereden öldürüyor ----
            const float R = 450, RW = 390;
            var mp = ValApi.MapOf(mapUrl ?? "");
            var here = ms.Where(m => string.Equals(m.MapUrl, mapUrl, StringComparison.OrdinalIgnoreCase)).ToList();
            float ry = Section(g, (mp?.Name?.ToUpper(L.Culture) ?? L.T("HARİTA")) + L.F(" · BU HARİTADA {0} MAÇI", here.Count), R, 18, RW);
            if (here.Count == 0)
            {
                using (var f = new Font(L.Sans, 9.5f))
                    Wrap(g, busy ? L.T("Maçları inceleniyor…") : L.T("Son maçlarında bu haritayı oynamamış. Yukarıdaki stil yine de geçerli."), f, Mut, new RectangleF(R, ry, RW, 60));
                return;
            }
            float sx = SideSwitch(g, R, ry, side, v => side = v) + 8;
            using (var f = new Font(L.Sans, 8.5f)) Theme.DrawFit(g, L.T("noktalar: öldürdüğü anda durduğu yer"), f, Mut, sx, ry + 3, R + RW - sx);
            var box = new RectangleF(R + 20, ry + 32, 350, 350);
            Heat.Draw(g, mp, box, here.SelectMany(m => m.KillSpots), side, false);
            var plants = here.SelectMany(m => m.Plants).GroupBy(p => p.Key).Select(gr => new { Site = gr.Key, N = gr.Sum(x => x.Value) }).OrderByDescending(x => x.N).ToList();
            using (var f = new Font(L.Sans, 9f))
                Theme.DrawFit(g, plants.Count == 0 ? L.T("Bu haritada bomba kurmamış.") : L.T("Kurduğu bölge: ") + string.Join(" · ", plants.Select(p => $"{p.Site} ×{p.N}")), f, Txt, R, box.Bottom + 10, RW);
        }
    }

    /// <summary>
    /// Kendi analizin: son rekabetçi maçlarında nerede öldüğün, round açılışları, tilt, haritalar ve silahlar.
    /// Çözümlenen maçlar diske yazılır (analysis.json); sonraki açılışta yalnızca yeni maçlar indirilir.
    /// </summary>
    sealed class AnalysisForm : DarkForm
    {
        const int Count = 40;
        static readonly string FilePath = Path.Combine(Settings.Dir, "analysis.json");

        sealed class FileModel { public string Self; public List<PMatch> Matches = new List<PMatch>(); }

        readonly RiotLocal riot = new RiotLocal();
        readonly object lk = new object();
        List<PMatch> matches = new List<PMatch>();
        string status = L.T("Hazırlanıyor…");
        volatile bool closed;
        string map;
        bool mapPicked;   // elle seçilene kadar en çok oynanan harita
        int side;

        static AnalysisForm open;

        /// <summary>Tek pencere: açıksa öne getirir.</summary>
        public static void Open()
        {
            if (open == null || open.IsDisposed) { open = new AnalysisForm(); open.Show(); }
            else { if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal; open.Activate(); }
        }

        AnalysisForm()
        {
            Text = L.T("ValoLive — Kendi Analizim");
            ClientSize = new Size(1000, 740);
            StartPosition = FormStartPosition.CenterScreen;
            new Thread(Fetch) { IsBackground = true, Name = "self-analysis" }.Start();
        }

        void Fetch()
        {
            try
            {
                FileModel file = null;
                try { if (File.Exists(FilePath)) file = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<FileModel>(File.ReadAllText(FilePath)); } catch { }
                // Riot maç listesini sayfa başına en fazla 20 verir.
                var ids = new List<string>();
                for (int start = 0; start < Count; start += 20)
                {
                    var page = riot.CompetitiveHistory(start, start + 20, out int total);
                    ids.AddRange(page.Select(x => x.Key));
                    if (page.Count < 20 || start + 20 >= total) break;
                }
                var known = file != null && file.Self == riot.SelfPuuid ? file.Matches.Where(m => ids.Contains(m.Id)).ToDictionary(m => m.Id) : new Dictionary<string, PMatch>();
                lock (lk) matches = known.Values.ToList();
                Repaint();
                var missing = ids.Where(id => !known.ContainsKey(id)).ToList();
                for (int i = 0; i < missing.Count && !closed; i++)
                {
                    lock (lk) status = L.F("Maçlar inceleniyor {0}/{1}…", i + 1, missing.Count);
                    Repaint();
                    object d = null;
                    for (int attempt = 0; d == null && attempt < 4 && !closed; attempt++)
                    {
                        try { d = riot.MatchDetails(missing[i]); }
                        catch (WebException e) when ((int)((e.Response as HttpWebResponse)?.StatusCode ?? 0) == 429) { Thread.Sleep(5000 * (attempt + 1)); }
                    }
                    var pm = d != null ? Scout.Analyze(d, riot.SelfPuuid) : null;
                    if (pm != null) lock (lk) matches.Add(pm);
                    if (i % 5 == 4) Save();
                }
                Save();
                lock (lk) status = "";
            }
            catch (Exception e) { lock (lk) status = L.T("Riot istemcisine ulaşılamadı: ") + e.Message; }
            Repaint();
        }

        void Save()
        {
            try
            {
                FileModel f;
                lock (lk) f = new FileModel { Self = riot.SelfPuuid, Matches = matches.ToList() };
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(f));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Diag.Event("ANALİZ", "kaydedilemedi: " + e.Message); }
        }

        void Repaint() { if (!closed && IsHandleCreated) try { BeginInvoke((Action)Invalidate); } catch { } }

        protected override void OnFormClosed(FormClosedEventArgs e) { closed = true; base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            clicks.Clear();
            List<PMatch> ms; string st;
            lock (lk) { ms = matches.OrderByDescending(m => m.Start).ToList(); st = status; }

            using (var b = new SolidBrush(Red)) g.FillRectangle(b, 0, 0, ClientSize.Width, 3);
            using (var f = Theme.Display(28))
            {
                Theme.SplitTitle(L.T("KENDİ ANALİZİM"), out var head, out var tail);
                if (head != "") Draw(g, head, f, Txt, 16, 8);
                Draw(g, tail, f, Red, head == "" ? 16 : 16 + g.MeasureString(head, f).Width - 6, 8);
            }
            using (var f = new Font(L.Sans, 9f))
                Theme.DrawFit(g, (ms.Count > 0 ? L.F("son {0} rekabetçi maç", ms.Count) : "") + (st != "" ? (ms.Count > 0 ? "  ·  " : "") + st : "") + "  ·  " + L.T("bu pencere yayına girmez"), f, st != "" ? Warn : Mut, 20, 46, ClientSize.Width - 40);
            if (ms.Count == 0) return;

            // ---- Sol: ölüm haritası ----
            var maps = ms.GroupBy(m => m.MapUrl).OrderByDescending(gr => gr.Count()).ToList();
            if (!mapPicked || !maps.Any(gr => gr.Key == map)) map = maps[0].Key;
            float y = Section(g, L.T("NEREDE ÖLÜYORSUN"), 20, 80, 450);
            float x = 20;
            using (var f = new Font(L.SansBold, 8.5f))
                foreach (var gr in maps.Take(8))
                {
                    string name = ValApi.MapOf(gr.Key)?.Name ?? "…";
                    string label = $"{name} {gr.Count()}";
                    var sz = g.MeasureString(label, f);
                    if (x + sz.Width + 14 > 470) { x = 20; y += 26; }
                    var r = new RectangleF(x, y, sz.Width + 14, 22);
                    using (var b = new SolidBrush(gr.Key == map ? Color.FromArgb(70, Red) : Card)) g.FillRectangle(b, r);
                    Draw(g, label, f, gr.Key == map ? Txt : Mut, x + 7, y + 3);
                    string key = gr.Key;
                    clicks.Add(new KeyValuePair<RectangleF, Action>(r, () => { map = key; mapPicked = true; }));
                    x += r.Width + 4;
                }
            y += 32;
            SideSwitch(g, 20, y, side, v => side = v);
            var here = ms.Where(m => m.MapUrl == map).ToList();
            var mp = ValApi.MapOf(map);
            var box = new RectangleF(30, y + 32, 430, 430);
            Heat.Draw(g, mp, box, here.SelectMany(m => m.DeathSpots), side, true);
            int hr = here.Sum(m => m.AtkRounds) + here.Sum(m => m.DefRounds);
            using (var f = new Font(L.Sans, 9f))
                Theme.DrawFit(g, L.F("{0} maç · %{1} kazanma · atak round %{2} · defans round %{3}", here.Count, Pct(here.Count(m => m.Result == "W"), here.Count), Pct(here.Sum(m => m.AtkWon), here.Sum(m => m.AtkRounds)), Pct(here.Sum(m => m.DefWon), here.Sum(m => m.DefRounds))),
                    f, Txt, 20, box.Bottom + 10, 460);

            // ---- Sağ: round açılışı, tilt, haritalar, silahlar ----
            const float R = 510, RW = 470;
            int rounds = ms.Sum(m => m.Rounds), deaths = ms.Sum(m => m.Deaths), fd = ms.Sum(m => m.FirstDeaths);
            y = Section(g, L.T("ROUND AÇILIŞI"), R, 80, RW);
            int fdp = Pct(fd, rounds), fkp = Pct(ms.Sum(m => m.FirstKills), rounds), tr = Pct(ms.Sum(m => m.Traded), deaths), lost = Pct(ms.Sum(m => m.FirstDeathLost), fd);
            Stat(g, L.Pct(fdp), L.T("ilk ölen sensin"), fdp >= 14 ? Loss : Txt, R, y, 118);
            Stat(g, L.Pct(fkp), L.T("ilk kan senden"), fkp >= 14 ? Win : Txt, R + 120, y, 118);
            Stat(g, L.Pct(tr), L.T("ölümün trade edildi"), tr < 14 ? Warn : Txt, R + 240, y, 118);
            Stat(g, L.Pct(lost), L.T("ilk öldüğün round kayıp"), Txt, R + 360, y, RW - 360);
            string open = fdp >= 14 ? L.T("Roundu sık sık ilk ölerek açıyorsun (ortalama ~%10). İlk teması takım arkadaşına ver ya da fayda ile gir.")
                : tr < 14 ? L.T("Öldüğünde takımın seni çoğunlukla geri alamıyor: yalnız kalıyorsun. Bir arkadaşınla aynı bölgeden oyna.")
                : L.T("Round açılışların dengeli.");
            using (var f = new Font(L.Sans, 9f)) Wrap(g, open, f, Mut, new RectangleF(R, y + 54, RW, 40));

            y = Section(g, L.T("TİLT"), R, y + 96, RW);
            Tilt(ms, out int afterN, out int afterW, out int lateN, out int lateW);
            int all = Pct(ms.Count(m => m.Result == "W"), ms.Count);
            Stat(g, L.Pct(all), L.F("genel kazanma ({0})", ms.Count), Txt, R, y, 158);
            Stat(g, afterN >= 3 ? L.Pct(Pct(afterW, afterN)) : "—", L.F("2 kayıptan sonra ({0})", afterN), afterN >= 3 && Pct(afterW, afterN) < all - 10 ? Loss : Txt, R + 160, y, 158);
            Stat(g, lateN >= 3 ? L.Pct(Pct(lateW, lateN)) : "—", L.F("oturumun 4.+ maçı ({0})", lateN), lateN >= 3 && Pct(lateW, lateN) < all - 10 ? Loss : Txt, R + 320, y, RW - 320);
            string tilt = afterN < 3 ? L.T("2 kayıp serisinden sonra yeterli maç yok; örnek arttıkça netleşir.")
                : Pct(afterW, afterN) < all - 10 ? L.T("İki kayıptan sonra belirgin düşüyorsun: o noktada kısa bir mola iyi gelir.")
                : L.T("Kayıp serisinden sonra düşüş görünmüyor — tilt'e dayanıklısın.");
            using (var f = new Font(L.Sans, 9f)) Wrap(g, tilt, f, Mut, new RectangleF(R, y + 54, RW, 40));

            y = Section(g, L.T("HARİTALAR"), R, y + 96, RW);
            using (var hf = new Font(L.Sans, 8.5f))
            using (var f = new Font(L.Sans, 9f))
            {
                string[] head = { L.T("harita"), L.T("maç"), L.T("kazanma"), L.T("atak"), L.T("defans"), L.T("hasar/round") };
                float[] col = { 0, 130, 180, 250, 310, 380 };
                for (int i = 0; i < head.Length; i++) Theme.DrawFit(g, head[i], hf, Mut, R + col[i], y, (i + 1 < col.Length ? col[i + 1] : RW) - col[i] - 4);
                y += 20;
                foreach (var gr in maps.Take(5))
                {
                    var l = gr.ToList();
                    int wr = Pct(l.Count(m => m.Result == "W"), l.Count), ar = Pct(l.Sum(m => m.AtkWon), l.Sum(m => m.AtkRounds)), dr = Pct(l.Sum(m => m.DefWon), l.Sum(m => m.DefRounds));
                    int adr = l.Sum(m => m.Rounds) > 0 ? (l.Sum(m => m.AtkDmg) + l.Sum(m => m.DefDmg)) / l.Sum(m => m.Rounds) : 0;
                    Theme.DrawFit(g, ValApi.MapOf(gr.Key)?.Name ?? "…", f, Txt, R + col[0], y, col[1] - 4);
                    Draw(g, l.Count.ToString(), f, Mut, R + col[1], y);
                    Draw(g, L.Pct(wr), f, wr >= 50 ? Win : Loss, R + col[2], y);
                    Draw(g, L.Pct(ar), f, ar >= 50 ? Txt : Mut, R + col[3], y);
                    Draw(g, L.Pct(dr), f, dr >= 50 ? Txt : Mut, R + col[4], y);
                    Draw(g, adr.ToString(), f, Mut, R + col[5], y);
                    y += 22;
                }
            }

            y = Section(g, L.T("SİLAH"), R, y + 12, RW);
            using (var f = new Font(L.Sans, 9f))
                foreach (var gun in new[] { "Vandal", "Phantom" })
                {
                    var s = new int[4];
                    foreach (var m in ms) if (m.Guns != null && m.Guns.TryGetValue(gun, out var gs)) for (int i = 0; i < 4; i++) s[i] += gs[i];
                    string line = s[0] == 0 ? L.F("{0}: kullanılmamış", gun)
                        : L.F("{0}: {1} round · round başı {2:0.00} öldürme · kafa %{3}", gun, s[0], (double)s[1] / s[0], Pct(s[2], s[3]));
                    Theme.DrawFit(g, line, f, s[0] == 0 ? Mut : Txt, R, y, RW);
                    y += 22;
                }

            // Saate göre: maçın başladığı yerel saat dilimi.
            y = Section(g, L.T("SAATE GÖRE"), R, y + 12, RW);
            var slots = new[] { (L.T("Sabah"), 6, 12), (L.T("Öğlen"), 12, 18), (L.T("Akşam"), 18, 24), (L.T("Gece"), 0, 6) };
            for (int i = 0; i < slots.Length; i++)
            {
                var l = ms.Where(m => { int h = DateTimeOffset.FromUnixTimeMilliseconds(m.Start).LocalDateTime.Hour; return h >= slots[i].Item2 && h < slots[i].Item3; }).ToList();
                int wr = Pct(l.Count(m => m.Result == "W"), l.Count);
                var c = l.Count < 3 ? Mut : wr >= all + 8 ? Win : wr <= all - 8 ? Loss : Txt;
                Stat(g, l.Count == 0 ? "—" : L.Pct(wr), $"{slots[i].Item1} ({l.Count})", c, R + i * 118, y, 116);
            }
        }

        /// <summary>
        /// Maçlar zaman sırasıyla oturumlara bölünür (arada 3 saatten fazla boşluk = yeni oturum).
        /// afterN/W: en az 2 kayıp serisinin hemen ardından oynanan maçlar; lateN/W: oturumun 4. ve sonraki maçları.
        /// </summary>
        static void Tilt(List<PMatch> ms, out int afterN, out int afterW, out int lateN, out int lateW)
        {
            afterN = afterW = lateN = lateW = 0;
            var seq = ms.OrderBy(m => m.Start).ToList();
            int losses = 0, index = 0;
            for (int i = 0; i < seq.Count; i++)
            {
                if (i > 0 && seq[i].Start - seq[i - 1].Start > 3 * 3600 * 1000L) { losses = 0; index = 0; }
                index++;
                bool w = seq[i].Result == "W";
                if (losses >= 2) { afterN++; if (w) afterW++; }
                if (index >= 4) { lateN++; if (w) lateW++; }
                losses = seq[i].Result == "L" ? losses + 1 : 0;
            }
        }
    }
}
