using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Oyuncu Ara: Riot ID (ya da yalnızca ad: daha önce görülenlerden öneri) → rütbe, sezonlar, yüzlerce maçlık özet,
    /// ajan/harita dağılımı, birlikte sıraya girdikleri ve ayrıntılı maçlar. Maçtaki ya da listedeki bir isme tıklayınca
    /// o oyuncunun sayfasına geçilir; ◀ ▶ ile geri/ileri.
    /// </summary>
    sealed class PlayerForm : DarkForm
    {
        const int Bar = 56, StoredPages = 4;   // üst çubuk yüksekliği; özet için 4 × 25 = 100 maç

        static PlayerForm open;
        readonly Func<string> key;
        readonly TextBox box;
        readonly Button go, back, fwd;
        readonly List<string> hist = new List<string>();   // "p:<puuid>" ya da "q:<ad#etiket>"
        int histPos = -1;

        int gen;
        string status = "", error;
        PAccount acc;
        PRank rank;
        List<PStored> stored = new List<PStored>();
        List<PDetail> details = new List<PDetail>();
        List<string[]> suggestions;
        bool storedLoading, moreLoading, noMore;
        bool compOnly = true;
        readonly HashSet<string> expanded = new HashSet<string>();
        int scroll, contentH;

        public static void Open(Func<string> key, string query = null)
        {
            if (open == null || open.IsDisposed) { open = new PlayerForm(key); open.Show(); }
            else { if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal; open.Activate(); }
            if (!string.IsNullOrWhiteSpace(query)) { open.box.Text = query; open.Search(); }
        }

        PlayerForm(Func<string> key)
        {
            this.key = key;
            Text = L.T("ValoLive — Oyuncu Ara");
            ClientSize = new Size(1040, 820);
            StartPosition = FormStartPosition.CenterScreen;
            back = Btn("◀", 20, 40); fwd = Btn("▶", 64, 40);
            box = new TextBox { Location = new Point(116, 14), Width = 420, BackColor = Theme.Field, ForeColor = Txt, BorderStyle = BorderStyle.FixedSingle, Font = new Font(L.Sans, 12f) };
            go = Btn(L.T("Ara"), 546, Theme.ButtonWidth(L.T("Ara"), 28, 70));
            go.BackColor = Red;
            Controls.Add(box);
            back.Click += (s, e) => { if (histPos > 0) { histPos--; Show(hist[histPos]); } };
            fwd.Click += (s, e) => { if (histPos < hist.Count - 1) { histPos++; Show(hist[histPos]); } };
            go.Click += (s, e) => Search();
            box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            box.MouseWheel += (s, e) => Wheel(e.Delta);
            PlayerData.Waiting += OnWaiting;
            status = L.T("Bir oyuncunun Riot ID'sini yaz: Ad#Etiket (ör. Oyuncu#TR1). Etiketi bilmiyorsan yalnızca adı yaz: daha önce gördüklerinden önerilir.");
            UpdateNav();
        }

        Button Btn(string text, int x, int w)
        {
            var b = new VButton { Text = text, Bounds = new Rectangle(x, 12, w, 30), BackColor = Theme.Field, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 10f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        void OnWaiting(string s) { try { BeginInvoke((Action)(() => { status = s; Invalidate(); })); } catch { } }

        protected override void OnFormClosed(FormClosedEventArgs e) { PlayerData.Waiting -= OnWaiting; gen++; base.OnFormClosed(e); }

        void UpdateNav() { back.Enabled = histPos > 0; fwd.Enabled = histPos < hist.Count - 1; }

        void Navigate(string target)
        {
            if (histPos >= 0 && hist[histPos] == target) { Show(target); return; }
            hist.RemoveRange(histPos + 1, hist.Count - histPos - 1);
            hist.Add(target); histPos = hist.Count - 1;
            Show(target);
        }

        void Search()
        {
            string q = box.Text.Trim();
            if (q.Length == 0) return;
            int hash = q.IndexOf('#');
            if (hash > 0 && hash < q.Length - 1) { Navigate("q:" + q); return; }
            // Yalnızca ad: HenrikDev adla arama yapmaz; daha önce görülen oyunculardan öner.
            string name = hash > 0 ? q.Substring(0, hash) : q;
            var found = Seen.Find(name);
            gen++; acc = null; error = null; scroll = 0;
            suggestions = found;
            status = found.Count == 0
                ? L.T("Bu adla daha önce kimseyi görmedik. Etiketiyle yaz: Ad#Etiket (ör. Oyuncu#TR1).")
                : L.T("Etiket yazılmadı. Daha önce gördüğün bu adlı oyuncular:");
            Invalidate();
        }

        /// <summary>Hedefi (puuid ya da ad#etiket) arka planda yükler; eski yükleme sonuçları yok sayılır.</summary>
        void Show(string target)
        {
            int my = ++gen;
            acc = null; rank = null; stored = new List<PStored>(); details = new List<PDetail>(); suggestions = null; error = null;
            expanded.Clear(); scroll = 0; noMore = false; moreLoading = false; storedLoading = true;
            status = L.T("Yükleniyor…");
            UpdateNav();
            Invalidate();
            string k = key();
            if (string.IsNullOrEmpty(k)) { Fail(my, L.T("HenrikDev anahtarı gerekli: Valorant sekmesinde gir.")); return; }
            new Thread(() =>
            {
                try
                {
                    PAccount a;
                    if (target.StartsWith("p:")) a = PlayerData.AccountByPuuid(k, target.Substring(2));
                    else
                    {
                        var q = target.Substring(2); int h = q.IndexOf('#');
                        a = PlayerData.Account(k, q.Substring(0, h).Trim(), q.Substring(h + 1).Trim());
                        // Geçmişte ad değişse de aynı kişiye dönülsün: kayıt puuid ile tutulur.
                        Post(my, () => { if (histPos >= 0 && hist[histPos] == target) hist[histPos] = "p:" + a.Puuid; });
                    }
                    Post(my, () => { acc = a; box.Text = a.Name + "#" + a.Tag; status = L.T("Yükleniyor…"); });
                    var r = PlayerData.Rank(k, a);
                    Post(my, () => rank = r);
                    var d = PlayerData.Details(k, a, 0);
                    Post(my, () => { details = d; noMore = d.Count < 10; });
                    for (int page = 1; page <= StoredPages && my == gen; page++)
                    {
                        var s = PlayerData.Stored(k, a, page, out int n);
                        Post(my, () => stored.AddRange(s.Where(x => !stored.Any(y => y.Id == x.Id))));
                        if (n < 25) break;
                    }
                    Post(my, () => { storedLoading = false; status = ""; });
                }
                catch (PlayerData.NotFound) { Fail(my, L.T("Oyuncu bulunamadı. Ad ve etiketi kontrol et.")); }
                catch (WebException e) { Fail(my, ErrorText(e)); }
                catch (Exception e) { Fail(my, e.Message); }
            }) { IsBackground = true, Name = "player-lookup" }.Start();
        }

        static string ErrorText(WebException e)
        {
            int code = (int?)(e.Response as HttpWebResponse)?.StatusCode ?? 0;
            if (code == 401 || code == 403) return L.T("HenrikDev anahtarı geçersiz.");
            if (code == 0) return L.T("HenrikDev'e ulaşılamadı. İnternet bağlantını kontrol et.");
            return L.F("HenrikDev hatası ({0}). Biraz sonra tekrar dene.", code);
        }

        void Post(int my, Action a)
        {
            if (my != gen || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (my != gen) return; a(); Invalidate(); })); } catch { }
        }

        void Fail(int my, string msg) => Post(my, () => { error = msg; status = ""; storedLoading = false; moreLoading = false; });

        void LoadMore()
        {
            if (acc == null || moreLoading || noMore) return;
            moreLoading = true;
            int my = gen; var a = acc; int start = details.Count; string k = key();
            new Thread(() =>
            {
                try
                {
                    var d = PlayerData.Details(k, a, start);
                    Post(my, () => { details.AddRange(d.Where(x => !details.Any(y => y.Id == x.Id))); noMore = d.Count < 10; moreLoading = false; });
                }
                catch (WebException e) { Post(my, () => { moreLoading = false; status = ErrorText(e); }); }
                catch (Exception e) { Post(my, () => { moreLoading = false; status = e.Message; }); }
            }) { IsBackground = true, Name = "player-more" }.Start();
        }

        // ---------- Kaydırma ----------

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Wheel(e.Delta); }

        void Wheel(int delta)
        {
            int max = Math.Max(0, contentH - (ClientSize.Height - Bar));
            int s = Math.Max(0, Math.Min(max, scroll - delta * 3 / 4));
            if (s != scroll) { scroll = s; Invalidate(); }
        }

        /// <summary>İçerik koordinatındaki alanı tıklanabilir yapar (üst çubuğun altında kalan kısmı).</summary>
        void Hit(RectangleF r, Action a)
        {
            var s = new RectangleF(r.X, r.Y + Bar - scroll, r.Width, r.Height);
            if (s.Bottom <= Bar) return;
            if (s.Top < Bar) s = RectangleF.FromLTRB(s.Left, Bar, s.Right, s.Bottom);
            clicks.Add(new KeyValuePair<RectangleF, Action>(s, a));
        }

        void OpenPlayer(string puuid) { if (acc == null || puuid != acc.Puuid) Navigate("p:" + puuid); }

        // ---------- Çizim ----------

        static string Mode(string m)
        {
            switch ((m ?? "").ToLowerInvariant())
            {
                case "competitive": return L.T("Rekabetçi");
                case "unrated": return L.T("Derecesiz");
                case "swiftplay": return L.T("Tam Gaz");
                case "spike rush": case "spikerush": return "Spike Rush";
                case "deathmatch": return L.T("Ölüm Kalım");
                case "team deathmatch": case "hurm": return L.T("Takım Ölüm Kalım");
                case "premier": return "Premier";
                case "": return "";
                default: return m;
            }
        }

        static bool IsComp(string mode) => string.Equals(mode, "competitive", StringComparison.OrdinalIgnoreCase);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            clicks.Clear();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Line)) g.FillRectangle(b, 0, Bar - 1, ClientSize.Width, 1);
            g.SetClip(new Rectangle(0, Bar, ClientSize.Width, ClientSize.Height - Bar));
            g.TranslateTransform(0, Bar - scroll);
            float x = 24, w = ClientSize.Width - 58, y = 16;
            y = PaintAll(g, x, y, w);
            contentH = (int)y + 24;
            g.ResetTransform(); g.ResetClip();
            // İnce kaydırma çubuğu
            int view = ClientSize.Height - Bar;
            if (contentH > view)
            {
                float h = Math.Max(30, view * (float)view / contentH), top = Bar + (view - h) * scroll / Math.Max(1, contentH - view);
                using (var b = new SolidBrush(Color.FromArgb(90, Mut))) g.FillRectangle(b, ClientSize.Width - 8, top, 4, h);
            }
            else if (scroll != 0) { scroll = 0; Invalidate(); }
        }

        float PaintAll(Graphics g, float x, float y, float w)
        {
            using (var f = new Font(L.Sans, 9.5f))
            {
                if (error != null) { Wrap(g, error, f, Loss, new RectangleF(x, y, w, 40)); y += 44; }
                if (!string.IsNullOrEmpty(status)) { Wrap(g, status, f, Mut, new RectangleF(x, y, w, 40)); y += 34; }
            }
            if (acc == null) return PaintSuggestions(g, x, y, w);

            y = PaintHeader(g, x, y, w);
            y = PaintSummary(g, x, y, w);
            y = PaintMates(g, x, y, w);
            y = PaintMatches(g, x, y, w);
            return y;
        }

        float PaintSuggestions(Graphics g, float x, float y, float w)
        {
            if (suggestions == null) return y;
            using (var f = new Font(L.SansBold, 11f))
            using (var t = new Font(L.Sans, 10f))
                foreach (var s in suggestions)
                {
                    var r = new RectangleF(x, y, Math.Min(w, 420), 34);
                    using (var b = new SolidBrush(Card)) g.FillRectangle(b, r);
                    Draw(g, s[1], f, Txt, x + 12, y + 7);
                    Draw(g, "#" + s[2], t, Mut, x + 14 + g.MeasureString(s[1], f).Width, y + 9);
                    string id = s[0];
                    Hit(r, () => OpenPlayer(id));
                    y += 40;
                }
            return y;
        }

        float PaintHeader(Graphics g, float x, float y, float w)
        {
            var card = Assets.Icon(ValApi.CardWide(acc.Card));
            var cr = new RectangleF(x, y, 282, 80);
            using (var b = new SolidBrush(Card)) g.FillRectangle(b, cr);
            if (card != null) g.DrawImage(card, cr);
            float tx = x + 300;
            using (var f = Theme.Display(30))
            {
                Draw(g, acc.Name, f, Txt, tx, y - 2);
                float nw = g.MeasureString(acc.Name, f).Width;
                using (var t = Theme.Display(20)) Draw(g, "#" + acc.Tag, t, Mut, tx + nw, y + 7);
            }
            using (var f = new Font(L.Sans, 9.5f))
                Draw(g, L.F("Seviye {0} · {1}", acc.Level, acc.Region.ToUpperInvariant()), f, Mut, tx + 2, y + 40);
            // Rütbe (sağda)
            if (rank != null)
            {
                float rx = x + w - 300;
                var icon = Assets.Icon(OverlayRenderer.TierIcon(rank.Tier < 3 ? 0 : rank.Tier));
                if (icon != null) g.DrawImage(icon, rx, y, 64, 64);
                using (var f = new Font(L.SansBold, 14f)) Draw(g, Tiers.Name(rank.Tier), f, Tiers.Color(rank.Tier), rx + 72, y + 6);
                using (var f = new Font(L.Sans, 9.5f))
                {
                    if (rank.Tier >= 3) Draw(g, rank.Rr + " RR", f, Txt, rx + 74, y + 32);
                    if (rank.PeakTier >= 3) Draw(g, L.F("En yüksek: {0} ({1})", Tiers.Name(rank.PeakTier), rank.PeakSeason), f, Mut, rx + 74, y + 52);
                }
            }
            return y + 100;
        }

        float PaintSummary(Graphics g, float x, float y, float w)
        {
            var list = compOnly ? stored.Where(s => IsComp(s.Mode)).ToList() : stored;
            y = Section(g, L.T("Genel"), x, y, w);
            // Rekabetçi / Hepsi
            using (var f = new Font(L.SansBold, 8.5f))
            {
                float px = x + w - 200;
                foreach (var opt in new[] { true, false })
                {
                    string t = opt ? L.T("Rekabetçi") : L.T("Hepsi");
                    var sz = g.MeasureString(t, f);
                    var r = new RectangleF(px, y - 30, sz.Width + 14, 22);
                    using (var b = new SolidBrush(opt == compOnly ? Color.FromArgb(70, Txt) : Card)) g.FillRectangle(b, r);
                    Draw(g, t, f, opt == compOnly ? Txt : Mut, px + 7, y - 27);
                    bool v = opt;
                    Hit(r, () => compOnly = v);
                    px += r.Width + 4;
                }
            }
            if (list.Count == 0)
            {
                using (var f = new Font(L.Sans, 9.5f)) Draw(g, storedLoading ? L.T("Maçlar yükleniyor…") : L.T("Bu türde maç yok."), f, Mut, x, y);
                return y + 30;
            }
            int n = list.Count, wins = list.Count(s => s.Won && s.Rounds > 0), losses = list.Count(s => !s.Won && s.Mine != s.Theirs);
            int k = list.Sum(s => s.K), d = list.Sum(s => s.D), a = list.Sum(s => s.A), rounds = list.Sum(s => s.Rounds);
            int shots = list.Sum(s => s.Head + s.Body + s.Leg), head = list.Sum(s => s.Head);
            var stats = new List<string[]>
            {
                new[] { n.ToString(), L.T("maç") },
                new[] { Pct(wins, wins + losses) + "%", L.F("kazanma · {0}G {1}M", wins, losses) },
                new[] { (d > 0 ? k / (double)d : k).ToString("0.00"), L.T("K/D") },
                new[] { ((k + a) / (double)Math.Max(1, d)).ToString("0.00"), L.T("KDA") },
                new[] { Pct(head, shots) + "%", L.T("kafadan vuruş") },
                new[] { rounds > 0 ? (list.Sum(s => s.Dmg) / rounds).ToString() : "—", L.T("round başına hasar") },
                new[] { rounds > 0 ? (list.Sum(s => s.Score) / rounds).ToString() : "—", L.T("ortalama skor (ACS)") },
            };
            float sw = w / stats.Count;
            for (int i = 0; i < stats.Count; i++)
                Stat(g, stats[i][0], stats[i][1], i == 1 ? (wins >= losses ? Win : Loss) : Txt, x + i * sw, y, sw);
            y += 64;
            if (storedLoading) using (var f = new Font(L.Sans, 8.5f)) { Draw(g, L.T("Daha eski maçlar yükleniyor…"), f, Mut, x, y - 8); y += 10; }

            // Ajanlar (sol) ve haritalar (sağ)
            float cw = (w - 30) / 2, top = y;
            float ay = Section(g, L.T("Ajanlar"), x, y, cw);
            using (var f = new Font(L.SansBold, 10f))
            using (var t = new Font(L.Sans, 9f))
                foreach (var grp in list.GroupBy(s => s.AgentId).OrderByDescending(gr => gr.Count()).Take(5))
                {
                    var icon = Assets.Icon(OverlayRenderer.AgentIcon(grp.Key));
                    if (icon != null) g.DrawImage(icon, x, ay, 28, 28);
                    Draw(g, grp.First().Agent, f, Txt, x + 36, ay + 4);
                    int gw = grp.Count(s => s.Won && s.Rounds > 0), gk = grp.Sum(s => s.K), gd = grp.Sum(s => s.D);
                    Draw(g, L.F("{0} maç · %{1} kazanma · {2} K/D", grp.Count(), Pct(gw, grp.Count()), (gd > 0 ? gk / (double)gd : gk).ToString("0.00")), t, Mut, x + 150, ay + 6);
                    ay += 34;
                }
            float mx = x + cw + 30;
            float my = Section(g, L.T("Haritalar"), mx, top, cw);
            using (var f = new Font(L.SansBold, 10f))
            using (var t = new Font(L.Sans, 9f))
                foreach (var grp in list.Where(s => !string.IsNullOrEmpty(s.Map)).GroupBy(s => s.Map).OrderByDescending(gr => gr.Count()).Take(5))
                {
                    int gw = grp.Count(s => s.Won && s.Rounds > 0), gl = grp.Count(s => !s.Won && s.Mine != s.Theirs);
                    Draw(g, grp.Key, f, Txt, mx, my + 4);
                    Draw(g, L.F("{0} maç · {1}G {2}M", grp.Count(), gw, gl), t, Mut, mx + 120, my + 6);
                    float bw = cw - 300, bx = mx + 290;
                    if (bw > 30)
                    {
                        using (var b = new SolidBrush(Card)) g.FillRectangle(b, bx, my + 10, bw, 6);
                        using (var b = new SolidBrush(gw >= gl ? Win : Loss)) g.FillRectangle(b, bx, my + 10, bw * Pct(gw, gw + gl) / 100f, 6);
                    }
                    my += 34;
                }
            y = Math.Max(ay, my) + 6;

            // Sezonlar (son 8, yeniden eskiye)
            if (rank != null && rank.Seasons.Any(s => s.Games > 0))
            {
                y = Section(g, L.T("Sezonlar"), x, y, w);
                float sx = x;
                using (var f = new Font(L.SansBold, 9.5f))
                using (var t = new Font(L.Sans, 8.5f))
                    foreach (var s in rank.Seasons.Where(s => s.Games > 0).Reverse().Take(8))
                    {
                        var r = new RectangleF(sx, y, 112, 74);
                        using (var b = new SolidBrush(Card)) g.FillRectangle(b, r);
                        var icon = Assets.Icon(OverlayRenderer.TierIcon(s.EndTier < 3 ? 0 : s.EndTier));
                        if (icon != null) g.DrawImage(icon, sx + 8, y + 8, 32, 32);
                        Draw(g, s.Short.ToUpperInvariant(), t, Mut, sx + 46, y + 8);
                        Theme.DrawFit(g, Tiers.Name(s.EndTier), f, Tiers.Color(s.EndTier), sx + 46, y + 24, 62);
                        Draw(g, L.F("{0}G / {1} maç", s.Wins, s.Games), t, Mut, sx + 8, y + 50);
                        sx += 120;
                        if (sx + 112 > x + w) break;
                    }
                y += 86;
            }
            return y;
        }

        /// <summary>Birlikte sıraya girdikleri (aynı parti) ve sık denk geldikleri, ayrıntılı maçlardan.</summary>
        float PaintMates(Graphics g, float x, float y, float w)
        {
            if (details.Count == 0) return y;
            var party = new Dictionary<string, int[]>();   // puuid → {maç, galibiyet}
            var seen = new Dictionary<string, int[]>();    // puuid → {aynı takım, rakip}
            var names = new Dictionary<string, PPlayer>();
            foreach (var m in details)
            {
                var me = m.Players.FirstOrDefault(p => p.Puuid == acc.Puuid);
                if (me == null) continue;
                m.TeamRounds.TryGetValue(me.Team, out int mine);
                int theirs = m.TeamRounds.Where(t => t.Key != me.Team).Sum(t => t.Value);
                foreach (var p in m.Players)
                {
                    if (p.Puuid == acc.Puuid || string.IsNullOrEmpty(p.Puuid)) continue;
                    names[p.Puuid] = p;
                    if (!string.IsNullOrEmpty(me.Party) && p.Party == me.Party)
                    {
                        if (!party.TryGetValue(p.Puuid, out var v)) party[p.Puuid] = v = new int[2];
                        v[0]++; if (mine > theirs) v[1]++;
                    }
                    else
                    {
                        if (!seen.TryGetValue(p.Puuid, out var v)) seen[p.Puuid] = v = new int[2];
                        if (p.Team == me.Team) v[0]++; else v[1]++;
                    }
                }
            }
            y = Section(g, L.F("Birlikte oynadıkları (son {0} maç)", details.Count), x, y, w);
            float px = x;
            using (var f = new Font(L.SansBold, 10f))
            using (var t = new Font(L.Sans, 8.5f))
            {
                if (party.Count == 0) { Draw(g, L.T("Bu maçlarda hep tek başına sıraya girmiş."), t, Mut, x, y); y += 26; }
                else
                {
                    foreach (var kv in party.OrderByDescending(kv => kv.Value[0]).Take(8))
                    {
                        var p = names[kv.Key];
                        var r = new RectangleF(px, y, 230, 50);
                        using (var b = new SolidBrush(Card)) g.FillRectangle(b, r);
                        Theme.DrawFit(g, p.Name, f, Txt, px + 10, y + 6, 150);
                        Draw(g, "#" + p.Tag, t, Mut, px + 12 + Math.Min(150, g.MeasureString(p.Name, f).Width), y + 8);
                        Draw(g, L.F("{0} maç birlikte · {1}G {2}M", kv.Value[0], kv.Value[1], kv.Value[0] - kv.Value[1]), t, kv.Value[1] * 2 >= kv.Value[0] ? Win : Loss, px + 10, y + 28);
                        string id = kv.Key;
                        Hit(r, () => OpenPlayer(id));
                        px += 238;
                        if (px + 230 > x + w) { px = x; y += 58; }
                    }
                    if (px != x) y += 58;
                }
                var often = seen.Where(kv => kv.Value[0] + kv.Value[1] >= 2).OrderByDescending(kv => kv.Value[0] + kv.Value[1]).Take(8).ToList();
                if (often.Count > 0)
                {
                    Draw(g, Theme.Upper(L.T("Sık denk geldikleri")), t, Mut, x, y + 2); y += 22;
                    px = x;
                    foreach (var kv in often)
                    {
                        var p = names[kv.Key];
                        string label = p.Name + "#" + p.Tag + "  ·  " + L.F("{0} takım, {1} rakip", kv.Value[0], kv.Value[1]);
                        var sz = g.MeasureString(label, t);
                        var r = new RectangleF(px, y, sz.Width + 14, 24);
                        if (r.Right > x + w) { px = x; y += 30; r.X = px; r.Y = y; }
                        using (var b = new SolidBrush(Card)) g.FillRectangle(b, r);
                        Draw(g, label, t, Txt, r.X + 7, r.Y + 4);
                        string id = kv.Key;
                        Hit(r, () => OpenPlayer(id));
                        px = r.Right + 6;
                    }
                    y += 34;
                }
            }
            return y + 4;
        }

        static readonly Color[] PartyColors = { ColorTranslator.FromHtml("#ffb547"), ColorTranslator.FromHtml("#7aa2ff"), ColorTranslator.FromHtml("#c890f5"), ColorTranslator.FromHtml("#3ddc97"), ColorTranslator.FromHtml("#ff7ab6") };

        float PaintMatches(Graphics g, float x, float y, float w)
        {
            y = Section(g, L.T("Maçlar"), x, y, w);
            if (details.Count == 0)
            {
                using (var f = new Font(L.Sans, 9.5f)) Draw(g, error == null ? L.T("Maçlar yükleniyor…") : "", f, Mut, x, y);
                return y + 30;
            }
            using (var f = new Font(L.SansBold, 10f))
            using (var t = new Font(L.Sans, 9f))
                foreach (var m in details)
                {
                    var me = m.Players.FirstOrDefault(p => p.Puuid == acc.Puuid);
                    if (me == null) continue;
                    m.TeamRounds.TryGetValue(me.Team, out int mine);
                    int theirs = m.TeamRounds.Where(tm => tm.Key != me.Team).Sum(tm => tm.Value);
                    bool hasRounds = m.Rounds > 0 && m.TeamRounds.Count == 2;
                    Color rc = !hasRounds ? Mut : mine > theirs ? Win : mine < theirs ? Loss : Mut;
                    var r = new RectangleF(x, y, w, 46);
                    using (var b = new SolidBrush(Card)) g.FillRectangle(b, r);
                    using (var b = new SolidBrush(rc)) g.FillRectangle(b, x, y, 4, 46);
                    var icon = Assets.Icon(OverlayRenderer.AgentIcon(me.AgentId));
                    if (icon != null) g.DrawImage(icon, x + 12, y + 7, 32, 32);
                    Draw(g, m.Map, f, Txt, x + 54, y + 5);
                    Draw(g, Mode(m.QueueId) + " · " + m.Start.ToString("dd.MM HH:mm"), t, Mut, x + 54, y + 25);
                    if (hasRounds) using (var big = new Font(L.SansBold, 13f)) Draw(g, mine + " : " + theirs, big, rc, x + 240, y + 12);
                    Draw(g, $"{me.K} / {me.D} / {me.A}", f, Txt, x + 340, y + 5);
                    Draw(g, L.T("K / D / A"), t, Mut, x + 340, y + 25);
                    int rounds = Math.Max(1, m.Rounds), shots = me.Head + me.Body + me.Leg;
                    if (m.Rounds > 0) { Draw(g, (me.Score / rounds).ToString(), f, Txt, x + 460, y + 5); Draw(g, L.T("ACS"), t, Mut, x + 460, y + 25); }
                    Draw(g, Pct(me.Head, shots) + "%", f, Txt, x + 540, y + 5); Draw(g, L.T("kafa"), t, Mut, x + 540, y + 25);
                    if (me.Tier >= 3) Draw(g, Tiers.Name(me.Tier), t, Tiers.Color(me.Tier), x + 630, y + 15);
                    bool open = expanded.Contains(m.Id);
                    Draw(g, open ? "▲" : "▼", t, Mut, x + w - 24, y + 15);
                    string id = m.Id;
                    Hit(r, () => { if (!expanded.Remove(id)) expanded.Add(id); });
                    y += 50;
                    if (open) y = PaintScoreboard(g, m, me, x, y, w);
                }
            // Daha fazla
            if (!noMore)
            {
                var r = new RectangleF(x, y + 4, 220, 32);
                using (var b = new SolidBrush(moreLoading ? Card : Theme.Field)) g.FillRectangle(b, r);
                using (var f = new Font(L.SansBold, 9.5f)) Draw(g, moreLoading ? L.T("Yükleniyor…") : L.T("10 maç daha yükle"), f, Txt, x + 14, y + 11);
                Hit(r, LoadMore);
                y += 44;
            }
            return y;
        }

        /// <summary>Açılan maç: iki takım, oyuncular (tıklanınca o oyuncuya gidilir); aynı partidekiler aynı renkte işaretli.</summary>
        float PaintScoreboard(Graphics g, PDetail m, PPlayer me, float x, float y, float w)
        {
            // Partiler: 2+ kişilik gruplara renk
            var colors = new Dictionary<string, Color>();
            foreach (var grp in m.Players.Where(p => !string.IsNullOrEmpty(p.Party)).GroupBy(p => p.Party).Where(gr => gr.Count() > 1))
                colors[grp.Key] = PartyColors[colors.Count % PartyColors.Length];
            int rounds = Math.Max(1, m.Rounds);
            using (var f = new Font(L.SansBold, 9.5f))
            using (var t = new Font(L.Sans, 8.5f))
                foreach (var team in m.Players.GroupBy(p => p.Team).OrderBy(gr => gr.Key == me.Team ? 0 : 1))
                {
                    m.TeamRounds.TryGetValue(team.Key, out int won);
                    Draw(g, Theme.Upper(team.Key == me.Team ? L.T("Takımı") : L.T("Rakip")) + (m.Rounds > 0 ? "  ·  " + won : ""), t, Mut, x + 16, y + 2);
                    y += 20;
                    foreach (var p in team.OrderByDescending(p => p.Score))
                    {
                        var r = new RectangleF(x + 12, y, w - 24, 30);
                        bool self = p.Puuid == acc.Puuid;
                        using (var b = new SolidBrush(self ? Color.FromArgb(60, Red) : Color.FromArgb(120, Theme.Field))) g.FillRectangle(b, r);
                        if (colors.TryGetValue(p.Party ?? "", out var pc)) using (var b = new SolidBrush(pc)) g.FillRectangle(b, x + 12, y, 3, 30);
                        var icon = Assets.Icon(OverlayRenderer.AgentIcon(p.AgentId));
                        if (icon != null) g.DrawImage(icon, x + 22, y + 3, 24, 24);
                        Theme.DrawFit(g, p.Name, f, self ? Red : Txt, x + 54, y + 6, 190);
                        Draw(g, "#" + p.Tag, t, Mut, x + 56 + Math.Min(190, g.MeasureString(p.Name, f).Width), y + 8);
                        if (p.Tier >= 3) Draw(g, Tiers.Name(p.Tier), t, Tiers.Color(p.Tier), x + 330, y + 8);
                        Draw(g, $"{p.K} / {p.D} / {p.A}", f, Txt, x + 460, y + 6);
                        if (m.Rounds > 0) Draw(g, L.F("{0} ACS", p.Score / rounds), t, Mut, x + 560, y + 8);
                        int shots = p.Head + p.Body + p.Leg;
                        Draw(g, L.F("%{0} kafa", Pct(p.Head, shots)), t, Mut, x + 650, y + 8);
                        if (colors.ContainsKey(p.Party ?? "")) Draw(g, L.T("parti"), t, pc, x + w - 80, y + 8);
                        if (!self) { string id = p.Puuid; Hit(r, () => OpenPlayer(id)); }
                        y += 32;
                    }
                    y += 6;
                }
            return y + 6;
        }
    }
}
