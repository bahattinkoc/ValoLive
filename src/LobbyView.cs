using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Maç Odası'nın görünümü: sekmeler (Brifing · Harita · Rakipler · Takımın · seçili oyuncu) ve altta her sekmede
    /// duran ajan şeridi. Her an yalnızca o an gereken bilgi görünür: ajan seçiminde takımın, maçta rakipler,
    /// tıklayınca oyuncunun tamamı. Verisi olmayan bölüm hiç çizilmez; sığmayan içerik fare tekerleğiyle kayar.
    /// </summary>
    sealed partial class LobbyForm
    {
        const int ViewW = 520, ViewH = 680, TabsY = 62, BodyY = 94, FootH = 52, Pad = 16;
        static readonly Color Panel = ColorTranslator.FromHtml("#141f29"), Box = Theme.Field, Sep = Theme.Card,
            Dim = ColorTranslator.FromHtml("#5c6a66"), Teal = ColorTranslator.FromHtml("#4fd1c5"), Purple = ColorTranslator.FromHtml("#c890f5");

        static readonly Font FBody = new Font(L.Sans, 9f), FSmall = new Font(L.Sans, 8.5f), FTiny = new Font(L.Sans, 7.5f),
            FBold = new Font(L.SansBold, 9f), FName = new Font(L.SansBold, 10f), FBig = new Font(L.SansBold, 12f), FPill = new Font(L.SansBold, 7.5f);
        static readonly StringFormat Ellipsis = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        enum Tab { Brief, Map, Enemy, Ally, Player }
        Tab tab = Tab.Brief;
        string selPuuid;                                           // oyuncu sekmesindeki oyuncu
        int mapSide = -1;                                          // Harita: 0 defans, 1 atak, -1 verisi çok olan
        readonly Dictionary<Tab, int> scroll = new Dictionary<Tab, int>();

        sealed class Hot { public Rectangle R; public Action<MouseButtons> Click; public LobbyPlayer Pop; public bool Help; }
        readonly List<Hot> hots = new List<Hot>();
        Point mouse = new Point(-1, -1);
        Hot hovered;
        bool inBody; int curOff;

        static Rectangle Body => new Rectangle(0, BodyY, ViewW, ViewH - FootH - BodyY);

        /// <summary>Yeni maç ya da maç başladı (Poll iş parçacığından): brifinge dön; oyuncu sekmesi yeni maçta kapanır.</summary>
        void PhaseChanged(string phase)
        {
            bool fresh = lastPhase == "";
            if (closed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (fresh) selPuuid = null;
                    if (tab != Tab.Player || fresh) tab = Tab.Brief;
                    scroll.Clear(); mapSide = -1;
                    Invalidate();
                }));
            }
            catch { }
        }

        // ---------- Yardımcılar ----------

        PlayerHistory HistOf(LobbyPlayer p) { lock (history) return p.Puuid != null && history.TryGetValue(p.Puuid, out var h) ? h.Copy() : null; }

        Dictionary<string, List<Intel.Fact>> FactsAll() { lock (history) return facts; }

        int PendingScouts() { lock (history) return scoutBusy.Count; }

        /// <summary>Tıklanabilir/üzerine gelinebilir alan; içerik bölgesindeyse kaydırmaya göre düzeltilir.</summary>
        void Hit(Rectangle r, Action<MouseButtons> click, LobbyPlayer pop = null, bool help = false)
        {
            if (inBody) { r.Offset(0, -curOff); r.Intersect(Body); if (r.IsEmpty) return; }
            hots.Add(new Hot { R = r, Click = click, Pop = pop, Help = help });
        }

        bool Over(Rectangle r)
        {
            if (mouse.X < 0) return false;
            if (!inBody) return r.Contains(mouse);
            return Body.Contains(mouse) && r.Contains(mouse.X, mouse.Y + curOff);
        }

        Action<MouseButtons> PlayerClick(LobbyPlayer p) => b =>
        {
            if (p == null || p.Incognito || p.Puuid == null) return; // gizli oyuncu açılmaz
            if (b == MouseButtons.Right) { Scout.OpenProfile(p.Name, p.Tag); return; }
            if (p.IsSelf) { AnalysisForm.Open(); return; }
            selPuuid = p.Puuid; tab = Tab.Player; scroll[Tab.Player] = 0;
            PrioritizeScout(p);
            Invalidate();
        };

        /// <summary>Açılan oyuncu henüz incelenmediyse sıranın önüne alınır.</summary>
        void PrioritizeScout(LobbyPlayer p)
        {
            lock (history)
            {
                if (scoutPending.Contains(p.Puuid))
                {
                    var rest = scoutPending.Where(x => x != p.Puuid).ToList();
                    scoutPending.Clear(); scoutPending.Enqueue(p.Puuid); foreach (var x in rest) scoutPending.Enqueue(x);
                }
                else if (!scouted.ContainsKey(p.Puuid) && history.ContainsKey(p.Puuid) && scoutBusy.Add(p.Puuid)) { scoutPending.Enqueue(p.Puuid); Monitor.PulseAll(history); }
            }
        }

        /// <summary>Tam analiz penceresi (ısı haritası, sayılar).</summary>
        void OpenFull(LobbyPlayer p)
        {
            PrioritizeScout(p);
            if (scoutForm == null || scoutForm.IsDisposed)
                scoutForm = new ScoutForm(ScoutedOf, id => { lock (history) return scoutBusy.Contains(id); }, FactsOf);
            string mapUrl; lock (history) mapUrl = state.MapUrl;
            scoutForm.ShowPlayer(p, mapUrl);
        }

        static void Write(Graphics g, string s, Font f, Color c, float x, float y, float w)
        {
            if (w <= 4 || string.IsNullOrEmpty(s)) return;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, new RectangleF(x, y, w, f.Height + 4), Ellipsis);
        }

        /// <summary>Bulgu cümlesi → (bulgu, öneri): ilk ": " ya da ". " öncesi bulgu.</summary>
        static void Split(string text, out string head, out string tail)
        {
            int cut = new[] { text.IndexOf(": ", StringComparison.Ordinal), text.IndexOf(". ", StringComparison.Ordinal), text.IndexOf("：", StringComparison.Ordinal) }
                .Where(i => i > 0).DefaultIfEmpty(-1).Min();
            head = cut > 0 ? text.Substring(0, cut) : text.TrimEnd('.');
            tail = cut > 0 ? text.Substring(cut + 1).Trim().TrimEnd('.') : "";
        }

        /// <summary>Küçük etiket kutusu; sonraki öğenin x'ini döner.</summary>
        static float Pill(Graphics g, string s, Font f, Color bg, Color fg, float x, float y)
        {
            var sz = g.MeasureString(s, f);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, x + 2, y, sz.Width, sz.Height - 1);
            Draw(g, s, f, fg, x + 2, y - 1);
            return x + sz.Width + 5;
        }

        static void DrawFaded(Graphics g, Image img, Rectangle r, float alpha)
        {
            if (img == null) return;
            lock (img)
            {
                // Görseli alana "cover" olarak oturt (oran korunur, taşan kırpılır).
                float s = Math.Max((float)r.Width / img.Width, (float)r.Height / img.Height);
                float sw = r.Width / s, sh = r.Height / s;
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(img, r, (img.Width - sw) / 2, (img.Height - sh) / 2, sw, sh, GraphicsUnit.Pixel, ia);
                }
            }
        }

        static void DrawFit(Graphics g, Image img, Rectangle box)
        {
            if (img == null) return;
            lock (img)
            {
                float s = Math.Min((float)box.Width / img.Width, (float)box.Height / img.Height);
                g.DrawImage(img, box.X + (box.Width - img.Width * s) / 2, box.Y + (box.Height - img.Height * s) / 2, img.Width * s, img.Height * s);
            }
        }

        /// <summary>Ajan ikonu (yüklenmediyse boş kutu).</summary>
        void Agent(Graphics g, string agentId, Rectangle r, bool dim = false)
        {
            var icon = AgentIcon(agentId);
            if (icon == null) { using (var b = new SolidBrush(Box)) g.FillRectangle(b, r); return; }
            if (dim) DrawFaded(g, icon, r, 0.45f); else lock (icon) g.DrawImage(icon, r);
        }

        string AgentLabel(LobbyPlayer p) => string.IsNullOrEmpty(p.AgentId) ? (p.Name ?? L.T("Ajan")) : AgentNameCached(p.AgentId);

        int SectionTitle(Graphics g, string text, string sub, int y)
        {
            float x = Pad;
            using (var f = Theme.Display(13)) { string t = Theme.Upper(text); Draw(g, t, f, Mut, x, y); x += g.MeasureString(t, f).Width + 4; }
            if (!string.IsNullOrEmpty(sub)) Write(g, sub, FSmall, Dim, x, y + 3, ViewW - Pad - x);
            return y + 24;
        }

        // ---------- Çizim ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            LobbyState st; string stat;
            lock (history) { st = state; stat = status; }
            parties = PartyGroups(st.Players);
            hots.Clear();

            var sel = selPuuid == null ? null : st.Players.FirstOrDefault(p => p.Puuid == selPuuid);
            if (sel == null) { selPuuid = null; if (tab == Tab.Player) tab = Tab.Brief; }
            var tabs = Tabs(st);
            if (!tabs.Contains(tab) && tab != Tab.Player) tab = Tab.Brief;

            DrawHeader(g, st, stat);
            DrawTabs(g, st, tabs, sel);

            // İçerik: kırpılır ve kaydırılır.
            var body = Body;
            curOff = scroll.TryGetValue(tab, out var off) ? off : 0;
            var saved = g.Save();
            g.SetClip(body);
            g.TranslateTransform(0, -curOff);
            inBody = true;
            int y0 = BodyY + 12, end;
            switch (tab)
            {
                case Tab.Map: end = DrawMap(g, st, y0); break;
                case Tab.Enemy: end = DrawList(g, st, false, y0); break;
                case Tab.Ally: end = DrawList(g, st, true, y0); break;
                case Tab.Player: end = DrawPlayer(g, st, sel, y0); break;
                default: end = st.Phase == "pregame" ? DrawPregame(g, st, y0) : DrawBrief(g, st, y0); break;
            }
            inBody = false;
            g.Restore(saved);

            int max = Math.Max(0, end + 12 - body.Bottom);
            if (curOff > max) { scroll[tab] = max; Invalidate(); }
            else if (max > 0)
            {
                // İnce kaydırma göstergesi
                float vis = (float)body.Height / (body.Height + max);
                int th = Math.Max(24, (int)(body.Height * vis)), ty = body.Top + (int)((body.Height - th) * (curOff / (float)max));
                using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, ViewW - 5, ty, 3, th);
            }

            DrawFooter(g, st, sel);

            hovered = hots.LastOrDefault(h => h.R.Contains(mouse));
            if (hovered?.Pop != null) DrawPopup(g, hovered.Pop, hovered.R, st.MatchId);
            if (hovered != null && hovered.Help) DrawHelp(g, hovered.R);
        }

        List<Tab> Tabs(LobbyState st)
        {
            var t = new List<Tab> { Tab.Brief };
            if (HasMap(st)) t.Add(Tab.Map);
            t.Add(Tab.Enemy); t.Add(Tab.Ally);
            return t;
        }

        void DrawHeader(Graphics g, LobbyState st, string stat)
        {
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, 0, 0, ViewW, 3);
            float x;
            using (var f = Theme.Display(24))
            {
                Theme.SplitTitle(L.T("MAÇ ODASI"), out var head, out var tail);
                if (head != "") Draw(g, head, f, Txt, 14, 9);
                float tx = head == "" ? 14 : 14 + g.MeasureString(head, f).Width - 6;
                Draw(g, tail, f, Red, tx, 9);
                x = tx + g.MeasureString(tail, f).Width + 2;
            }
            var help = new Rectangle(chkTop.Left - 32, 13, 22, 22);
            Write(g, stat, FBold, st.Phase == "" ? Mut : Red, x, 17, help.Left - 8 - x);
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, help);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Over(help) ? Txt : Mut)) g.DrawString("?", FBold, b, help, sf);
            Hit(help, null, help: true);

            // Güç dengesi: ortalama ranka göre (bir alt kademe ≈ 1 puan); iki takım da biliniyorsa.
            var allies = st.Players.Where(p => p.Ally).ToList();
            int aAvg = AvgTier(allies), eAvg = AvgTier(st.Players.Where(p => !p.Ally).ToList());
            if (aAvg > 0 && eAvg > 0)
            {
                double p = 1 / (1 + Math.Pow(10, -((aAvg - eAvg) / 100.0) / 4));
                string Pct(double v) => L.Code == "tr" ? "%" + Math.Round(v * 100) : Math.Round(v * 100) + "%"; // Türkçede yüzde işareti önde
                string l = L.T("Takımın") + " " + Pct(p), r = Pct(1 - p) + " " + L.T("Rakip");
                float lw = g.MeasureString(l, FBold).Width, rw = g.MeasureString(r, FBold).Width;
                Draw(g, l, FBold, Win, Pad, 40);
                Draw(g, r, FBold, Loss, ViewW - Pad - rw, 40);
                float bx = Pad + lw + 6, bw = ViewW - Pad - rw - 6 - bx;
                using (var b = new SolidBrush(Win)) g.FillRectangle(b, bx, 46, (float)(bw * p), 5);
                using (var b = new SolidBrush(Loss)) g.FillRectangle(b, bx + (float)(bw * p), 46, (float)(bw * (1 - p)), 5);
            }
        }

        void DrawTabs(Graphics g, LobbyState st, List<Tab> tabs, LobbyPlayer sel)
        {
            using (var b = new SolidBrush(Sep)) g.FillRectangle(b, Pad, BodyY - 1, ViewW - 2 * Pad, 1);
            float x = Pad;
            int nAlly = st.Players.Count(p => p.Ally), nEnemy = st.Players.Count(p => !p.Ally);
            bool pre = st.Phase == "pregame";
            foreach (var t in tabs)
            {
                string label = t == Tab.Brief ? L.T("Brifing") : t == Tab.Map ? L.T("Harita") : t == Tab.Enemy ? L.T("Rakipler") : L.T("Takımın");
                int n = t == Tab.Enemy ? nEnemy : t == Tab.Ally ? nAlly : 0;
                bool off = t == Tab.Enemy && (pre || nEnemy == 0);   // ajan seçiminde Riot rakipleri göstermez
                float w = g.MeasureString(label, FBold).Width + 20 + (n > 0 ? g.MeasureString(n.ToString(), FTiny).Width + 8 : 0);
                var r = new Rectangle((int)x, TabsY, (int)w, BodyY - 1 - TabsY);
                bool on = t == tab;
                Draw(g, label, FBold, off ? Dim : on || Over(r) ? Txt : Mut, x + 10, TabsY + 8);
                if (n > 0)
                {
                    float nx = x + 10 + g.MeasureString(label, FBold).Width + 2;
                    var nr = new RectangleF(nx, TabsY + 10, g.MeasureString(n.ToString(), FTiny).Width + 4, 13);
                    using (var b = new SolidBrush(Box)) g.FillRectangle(b, nr);
                    Draw(g, n.ToString(), FTiny, Mut, nx + 1, TabsY + 10);
                }
                if (on) using (var b = new SolidBrush(Red)) g.FillRectangle(b, r.X, BodyY - 3, r.Width, 2);
                if (!off) { var tt = t; Hit(r, _ => { tab = tt; Invalidate(); }); }
                x += w;
            }

            if (sel != null)
            {
                // Seçili oyuncunun sekmesi sağda: ajan ikonu, ajan adı, kapatma.
                string name = AgentLabel(sel);
                float w = 10 + 18 + 6 + Math.Min(110, g.MeasureString(name, FBold).Width) + 24;
                var r = new Rectangle((int)(ViewW - Pad - w), TabsY, (int)w, BodyY - 1 - TabsY);
                var close = new Rectangle(r.Right - 22, r.Y + 4, 20, r.Height - 8);
                bool on = tab == Tab.Player;
                Agent(g, sel.AgentId, new Rectangle(r.X + 8, TabsY + 7, 18, 18));
                Write(g, name, FBold, on || Over(r) ? Txt : Mut, r.X + 30, TabsY + 8, r.Width - 30 - 24);
                Draw(g, "×", FName, Over(close) ? Txt : Mut, close.X + 4, close.Y + 1);
                if (on) using (var b = new SolidBrush(Red)) g.FillRectangle(b, r.X, BodyY - 3, r.Width, 2);
                Hit(r, _ => { tab = Tab.Player; Invalidate(); });
                Hit(close, _ => { selPuuid = null; tab = Tab.Brief; Invalidate(); });
            }
            else
            {
                int n = PendingScouts();
                if (n > 0)
                {
                    string s = L.F("{0} oyuncu inceleniyor…", n);
                    float w = g.MeasureString(s, FSmall).Width;
                    if (ViewW - Pad - w > x + 8) Draw(g, s, FSmall, Warn, ViewW - Pad - w, TabsY + 9);
                }
            }
        }

        void DrawFooter(Graphics g, LobbyState st, LobbyPlayer sel)
        {
            int y = ViewH - FootH;
            using (var b = new SolidBrush(Sep)) g.FillRectangle(b, Pad, y, ViewW - 2 * Pad, 1);
            int half = (ViewW - 2 * Pad) / 2;
            for (int side = 0; side < 2; side++)
            {
                bool ally = side == 0;
                float x = Pad + side * half;
                string label = ally ? L.T("TAKIM") : L.T("RAKİP");
                Draw(g, label, FPill, ally ? Win : Loss, x, y + 19);
                x += Math.Max(44, g.MeasureString(label, FPill).Width + 8);
                var team = st.Players.Where(p => p.Ally == ally).OrderByDescending(p => p.IsSelf).ToList();
                for (int i = 0; i < 5; i++)
                {
                    var r = new Rectangle((int)x + i * 36, y + 12, 28, 28);
                    if (i >= team.Count)
                    {
                        // Henüz bilinmeyen oyuncu (ajan seçiminde rakipler)
                        using (var pen = new Pen(Theme.Line) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                        continue;
                    }
                    var p = team[i];
                    Agent(g, p.AgentId, r, p.Incognito || (!string.IsNullOrEmpty(p.Selection) && p.Selection != "locked" && st.Phase == "pregame"));
                    var enc = EncountersOf(p, st.MatchId);
                    Color? ring = p.Puuid != null && p.Puuid == sel?.Puuid && tab == Tab.Player ? Red : enc.Count > 0 ? EncTag(enc).Value : (Color?)null;
                    if (Over(r) && !p.Incognito) ring = ring ?? Theme.Line;
                    if (ring != null) using (var pen = new Pen(ring.Value, 2f)) g.DrawRectangle(pen, r.X - 2, r.Y - 2, r.Width + 3, r.Height + 3);
                    if (p.Puuid != null && parties.TryGetValue(p.Puuid, out int pi))
                        using (var b = new SolidBrush(PartyColors[pi % PartyColors.Length])) g.FillRectangle(b, r.X, r.Bottom + 3, r.Width, 2);
                    if (!p.Incognito) Hit(r, PlayerClick(p));
                }
            }
        }

        // ---------- Brifing ----------

        /// <summary>Önem sırasına göre n bulgu; önce her oyuncudan bir tane (tek oyuncu brifingi doldurmasın).</summary>
        static List<Intel.Fact> Pick(IEnumerable<Intel.Fact> src, int n)
        {
            var all = src.OrderByDescending(f => f.Score).ToList();
            var first = all.GroupBy(f => f.Puuid).Select(gr => gr.First()).OrderByDescending(f => f.Score).Take(n).ToList();
            return first.Concat(all.Except(first)).Take(n).OrderByDescending(f => f.Score).ToList();
        }

        int Group(Graphics g, string title, string sub, Color c, List<KeyValuePair<LobbyPlayer, string[]>> items, int y)
        {
            if (items.Count == 0) return y;
            float x = Pad;
            using (var f = Theme.Display(13))
            {
                string t = Theme.Upper(title);
                var sz = g.MeasureString(t, f);
                using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, sz.Width + 2, 18);
                Draw(g, t, f, Bg, x + 1, y - 1);
                x += sz.Width + 10;
            }
            Write(g, sub, FSmall, Mut, x, y + 2, ViewW - Pad - x);
            y += 24;
            foreach (var it in items)
            {
                var p = it.Key;
                var r = new Rectangle(Pad, y, ViewW - 2 * Pad, 40);
                using (var b = new SolidBrush(Over(r) ? Box : Panel)) g.FillRectangle(b, r);
                using (var b = new SolidBrush(it.Value.Length > 2 && it.Value[2] != null ? ColorTranslator.FromHtml(it.Value[2]) : c)) g.FillRectangle(b, r.X, r.Y, 3, r.Height);
                Agent(g, p.AgentId, new Rectangle(r.X + 11, r.Y + 6, 26, 26));
                using (var b = new SolidBrush(p.Ally ? Win : Loss)) g.FillRectangle(b, r.X + 11, r.Y + 33, 26, 2);
                Write(g, it.Value[0], FBold, Txt, r.X + 44, r.Y + 4, r.Width - 52);
                Write(g, it.Value[1], FSmall, Mut, r.X + 44, r.Y + 21, r.Width - 52);
                Hit(r, PlayerClick(p));
                y += 44;
            }
            return y + 8;
        }

        List<KeyValuePair<LobbyPlayer, string[]>> Items(IEnumerable<Intel.Fact> list, Dictionary<string, LobbyPlayer> byId)
        {
            var res = new List<KeyValuePair<LobbyPlayer, string[]>>();
            foreach (var f in list)
            {
                if (!byId.TryGetValue(f.Puuid, out var p)) continue;
                Split(f.Text, out var head, out var tail);
                res.Add(new KeyValuePair<LobbyPlayer, string[]>(p, new[] { head, AgentLabel(p) + (tail != "" ? " · " + tail : ""), ColorTranslator.ToHtml(f.Color) }));
            }
            return res;
        }

        /// <summary>Daha önce aynı maçta olduğun oyuncular (karşılaşma defteri), en sık görülen önce.</summary>
        List<KeyValuePair<LobbyPlayer, string[]>> Known3(LobbyState st, bool alliesOnly)
        {
            var res = new List<KeyValuePair<LobbyPlayer, string[]>>();
            foreach (var p in st.Players.Where(p => !alliesOnly || p.Ally))
            {
                var enc = EncountersOf(p, st.MatchId);
                if (enc.Count == 0) continue;
                var tag = EncTag(enc);
                var last = enc[0];
                string r = last.Match.Outcome();
                string tail = AgentLabel(p) + " · " + Ago(last.Match.Start) + " · " + (last.Teammate ? L.T("Takım arkadaşın") : L.T("Rakibin")) + " · "
                    + (r == "W" ? L.T("kazandın") : r == "L" ? L.T("kaybettin") : L.T("berabere")) + " " + last.Match.MyRounds + "-" + last.Match.TheirRounds;
                res.Add(new KeyValuePair<LobbyPlayer, string[]>(p, new[] { tag.Key + " " + L.F("· {0} kez aynı maçtaydınız", enc.Count), tail, ColorTranslator.ToHtml(tag.Value), enc.Count.ToString() }));
            }
            return res.OrderByDescending(kv => int.Parse(kv.Value[3])).Take(3).ToList();
        }

        int Empty(Graphics g, string text, int y)
        {
            using (var b = new SolidBrush(Mut))
                g.DrawString(text, FBody, b, new RectangleF(Pad, y + 4, ViewW - 2 * Pad, 60));
            return y + 64;
        }

        int DrawBrief(Graphics g, LobbyState st, int y)
        {
            if (st.Players.Count == 0) return Empty(g, L.T("Maç bulununca oyuncuların son maçlarından en işe yarar bilgiler burada özetlenir."), y);
            var byId = st.Players.Where(p => p.Puuid != null && !p.Incognito).GroupBy(p => p.Puuid).ToDictionary(gr => gr.Key, gr => gr.First());
            var fx = FactsAll();
            List<Intel.Fact> Of(bool ally) => fx.Where(kv => byId.TryGetValue(kv.Key, out var p) && p.Ally == ally).SelectMany(kv => kv.Value).ToList();
            var enemy = Of(false); var ally = Of(true);
            int start = y;
            y = Group(g, L.T("TEHLİKE"), L.T("dikkat et"), Loss, Items(Pick(enemy.Where(f => f.Kind == Intel.Kinds.Danger), 3), byId), y);
            y = Group(g, L.T("FIRSAT"), L.T("üstüne git"), Win, Items(Pick(enemy.Where(f => f.Kind == Intel.Kinds.Chance), 3), byId), y);
            var place = enemy.Where(f => f.Kind == Intel.Kinds.Place).ToList();
            y = Group(g, L.T("KONUM"), place.Count > 2 && HasMap(st) ? L.T("tümü Harita sekmesinde") : L.T("nerede oynuyorlar"), Teal, Items(Pick(place, 2), byId), y);
            y = Group(g, L.T("TAKIMIN"), L.T("takım arkadaşların"), Known, Items(Pick(ally.Where(f => !f.Style || f.Score >= 80), 2), byId), y);
            y = Group(g, L.T("TANIDIKLAR"), L.T("daha önce aynı maçtaydınız"), Purple, Known3(st, false), y);
            if (y == start)
                return Empty(g, PendingScouts() > 0 ? L.T("Oyuncuların son maçları inceleniyor; bulgular geldikçe burada belirir.") : L.T("Bu lobide öne çıkan bir bilgi yok."), y);
            return y;
        }

        /// <summary>Ajan seçimi: rakipler henüz görünmez; takımın hakkında bilinenler ve kimin hangi ajanı oynadığı.</summary>
        int DrawPregame(Graphics g, LobbyState st, int y)
        {
            var byId = st.Players.Where(p => p.Puuid != null && !p.Incognito).GroupBy(p => p.Puuid).ToDictionary(gr => gr.Key, gr => gr.First());
            var fx = FactsAll();
            var ally = fx.Where(kv => byId.ContainsKey(kv.Key)).SelectMany(kv => kv.Value).Where(f => !f.Style || f.Score >= 80);
            y = Group(g, L.T("TAKIMIN"), L.T("takım arkadaşların"), Known, Items(Pick(ally, 3), byId), y);
            y = Group(g, L.T("TANIDIKLAR"), L.T("daha önce aynı maçtaydınız"), Purple, Known3(st, true), y);

            // Ajan havuzu: son maçlarında oynadıkları (incelenen 8 maç, yoksa son 3 maç).
            var team = st.Players.Where(p => p.Ally).OrderByDescending(p => p.IsSelf).ToList();
            if (team.Count > 0)
            {
                y = SectionTitle(g, L.T("TAKIMIN AJAN HAVUZU"), L.T("son maçlarında oynadıkları"), y);
                foreach (var p in team)
                {
                    var r = new Rectangle(Pad, y, ViewW - 2 * Pad, 32);
                    if (Over(r) && !p.Incognito) using (var b = new SolidBrush(Panel)) g.FillRectangle(b, r);
                    Agent(g, p.AgentId, new Rectangle(r.X + 4, r.Y + 5, 22, 22), p.Selection != "locked");
                    float x = r.X + 34;
                    string name = p.Incognito ? L.T("Gizli oyuncu") : p.Name + (p.IsSelf ? L.T(" (sen)") : "");
                    float nw = Math.Min(150, g.MeasureString(name ?? "", FBold).Width);
                    Write(g, name, FBold, p.Incognito ? Mut : Txt, x, r.Y + 8, nw + 2);
                    x += nw + 6;
                    if (p.Selection == "locked") x = Pill(g, L.T("kilitledi"), FPill, Win, Bg, x, r.Y + 10);
                    else if (p.Selection == "selected") x = Pill(g, L.T("seçti"), FPill, Box, Mut, x, r.Y + 10);
                    string pool = p.Incognito ? "" : Pool(p);
                    float pw = Math.Min(g.MeasureString(pool, FSmall).Width, r.Right - 6 - x - 8);
                    Write(g, pool, FSmall, Mut, r.Right - 6 - pw, r.Y + 9, pw);
                    using (var b = new SolidBrush(Box)) g.FillRectangle(b, r.X, r.Bottom - 1, r.Width, 1);
                    if (!p.Incognito) Hit(r, PlayerClick(p));
                    y += 32;
                }
                y += 10;
            }
            var note = new Rectangle(Pad, y, ViewW - 2 * Pad, 34);
            using (var pen = new Pen(Theme.Line) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, note.X, note.Y, note.Width - 1, note.Height - 1);
            Write(g, L.T("Rakipler maç başlayınca görünür (ajan seçiminde Riot göstermez)."), FSmall, Mut, note.X + 10, note.Y + 10, note.Width - 20);
            return y + 40;
        }

        /// <summary>"Jett 5 · Raze 2 · Neon 1": son maçlardaki ajanlar.</summary>
        string Pool(LobbyPlayer p)
        {
            var ms = ScoutedOf(p.Puuid);
            IEnumerable<string> agents = ms != null && ms.Count > 0 ? ms.Select(m => m.Agent) : HistOf(p)?.Matches?.Select(m => m.AgentId);
            if (agents == null) return "";
            return string.Join(" · ", agents.Where(a => !string.IsNullOrEmpty(a)).GroupBy(a => a.ToLowerInvariant()).OrderByDescending(gr => gr.Count()).Take(3)
                .Select(gr => AgentNameCached(gr.First()) + " " + gr.Count()));
        }

        // ---------- Harita ----------

        List<Intel.Fact> MapFacts(LobbyState st, List<string> zones)
        {
            var enemies = new HashSet<string>(st.Players.Where(p => !p.Ally && !p.Incognito && p.Puuid != null).Select(p => p.Puuid));
            return FactsAll().Where(kv => enemies.Contains(kv.Key)).SelectMany(kv => kv.Value).Where(f => f.Side >= 0 && zones.Contains(f.Zone)).ToList();
        }

        List<KeyValuePair<string, int>> EnemyPlants(LobbyState st)
        {
            var ms = st.Players.Where(p => !p.Ally && !p.Incognito).SelectMany(p => ScoutedOf(p.Puuid) ?? new List<PMatch>())
                .Where(m => string.Equals(m.MapUrl, st.MapUrl, StringComparison.OrdinalIgnoreCase));
            return Intel.Plants(ms);
        }

        bool HasMap(LobbyState st)
        {
            if (st.Phase != "ingame") return false;
            var zones = Intel.ZonesOf(st.MapUrl);
            return zones.Count > 0 && (MapFacts(st, zones).Count > 0 || EnemyPlants(st).Sum(kv => kv.Value) >= 3);
        }

        int DrawMap(Graphics g, LobbyState st, int y)
        {
            var zones = Intel.ZonesOf(st.MapUrl);
            var fs = MapFacts(st, zones);
            if (mapSide < 0) mapSide = fs.Count(f => f.Side == 1) > fs.Count(f => f.Side == 0) ? 1 : 0;
            var byId = st.Players.Where(p => p.Puuid != null).GroupBy(p => p.Puuid).ToDictionary(gr => gr.Key, gr => gr.First());

            if (fs.Count > 0)
            {
                SectionTitle(g, L.T("RAKİPLER NEREDE OYNUYOR"), null, y);
                // Defans / Atak anahtarı
                float rx = ViewW - Pad;
                foreach (int side in new[] { 1, 0 })
                {
                    string s = side == 0 ? L.T("Defansta") : L.T("Atakta");
                    int w = (int)g.MeasureString(s, FPill).Width + 16;
                    var r = new Rectangle((int)rx - w, y - 2, w, 22);
                    bool on = mapSide == side;
                    using (var b = new SolidBrush(on ? Red : Box)) g.FillRectangle(b, r);
                    Draw(g, s, FPill, on ? Bg : Over(r) ? Txt : Mut, r.X + 7, r.Y + 4);
                    int ss = side; Hit(r, _ => { mapSide = ss; Invalidate(); });
                    rx -= w;
                }
                y += 30;

                var here = fs.Where(f => f.Side == mapSide).ToList();
                int n = zones.Count, gap = 6, colW = (ViewW - 2 * Pad - gap * (n - 1)) / n;
                int most = zones.Max(z => here.Count(f => f.Zone == z));
                int h = Math.Max(150, 44 + most * 42);
                for (int i = 0; i < n; i++)
                {
                    var r = new Rectangle(Pad + i * (colW + gap), y, colW, h);
                    var inZone = here.Where(f => f.Zone == zones[i]).OrderByDescending(f => f.N * 100 / Math.Max(1, f.Of)).ToList();
                    using (var b = new SolidBrush(inZone.Count > 0 ? Panel : ColorTranslator.FromHtml("#111b24"))) g.FillRectangle(b, r);
                    using (var pen = new Pen(inZone.Count > 0 ? Theme.Line : Sep)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                    using (var f = Theme.Display(20)) Write(g, zones[i], f, inZone.Count > 0 ? Txt : Dim, r.X + 8, r.Y + 4, r.Width - 12);
                    int ey = r.Y + 40;
                    foreach (var fct in inZone)
                    {
                        if (!byId.TryGetValue(fct.Puuid, out var p)) continue;
                        var er = new Rectangle(r.X + 4, ey, r.Width - 8, 36);
                        if (Over(er)) using (var b = new SolidBrush(Box)) g.FillRectangle(b, er);
                        Agent(g, p.AgentId, new Rectangle(er.X + 4, er.Y + 5, 26, 26));
                        Write(g, AgentLabel(p), FBold, Txt, er.X + 36, er.Y + 2, er.Width - 38);
                        Write(g, L.F("{0} çatışmanın {1} tanesi", fct.Of, fct.N), FTiny, Mut, er.X + 36, er.Y + 19, er.Width - 38);
                        Hit(er, PlayerClick(p));
                        ey += 42;
                    }
                }
                y += h + 14;
            }

            var plants = EnemyPlants(st);
            int total = plants.Sum(kv => kv.Value);
            if (total >= 3)
            {
                var box = new Rectangle(Pad, y, ViewW - 2 * Pad, 86);
                using (var b = new SolidBrush(Panel)) g.FillRectangle(b, box);
                float x = box.X + 12;
                Draw(g, L.T("Spike kurulumları"), FBold, Txt, x, box.Y + 9);
                x += g.MeasureString(L.T("Spike kurulumları"), FBold).Width + 4;
                Write(g, L.T("rakiplerin bu haritadaki son maçları"), FSmall, Mut, x, box.Y + 10, box.Right - 12 - x);
                Color[] cols = { Warn, Known, Purple, Teal };
                float bx = box.X + 12, bw = box.Width - 24;
                for (int i = 0; i < plants.Count; i++)
                {
                    float w = bw * plants[i].Value / total;
                    using (var b = new SolidBrush(cols[i % cols.Length])) g.FillRectangle(b, bx, box.Y + 32, w, 18);
                    string s = plants[i].Key + " · " + plants[i].Value;
                    if (g.MeasureString(s, FPill).Width + 8 < w) Draw(g, s, FPill, Bg, bx + 5, box.Y + 35);
                    bx += w;
                }
                if (plants[0].Value * 100 >= total * 60)
                    Write(g, L.F("Kurulumların çoğu {0} bölgesinde: defansta oraya bir kişi fazla koy.", plants[0].Key), FSmall, Mut, box.X + 12, box.Y + 59, box.Width - 24);
                y += box.Height + 10;
            }
            return y;
        }

        // ---------- Rakipler / Takımın ----------

        int DrawList(Graphics g, LobbyState st, bool ally, int y)
        {
            var team = st.Players.Where(p => p.Ally == ally).OrderByDescending(p => p.IsSelf).ToList();
            int avg = AvgTier(team);
            y = SectionTitle(g, ally ? L.T("TAKIMIN") : L.T("RAKİPLER"), avg > 0 ? L.T("ort.") + " " + Tiers.Name((int)Math.Round(avg / 100.0)) : null, y);
            if (team.Count == 0)
                return Empty(g, ally ? L.T("Maç bulununca takımın burada listelenir.") : L.T("Rakipler maç başlayınca görünür (ajan seçiminde Riot göstermez)."), y);
            foreach (var p in team) y += Row(g, st, p, y);
            return y;
        }

        int Row(Graphics g, LobbyState st, LobbyPlayer p, int y)
        {
            var h = p.Incognito ? null : HistOf(p);
            var fs = p.Incognito ? new List<Intel.Fact>() : FactsOf(p.Puuid);
            var lines = fs.Where(f => !f.Style).Take(2).ToList();
            int rowH = 54 + lines.Count * 18;
            var row = new Rectangle(Pad, y, ViewW - 2 * Pad, rowH);
            if (!p.Incognito && Over(row)) using (var b = new SolidBrush(Panel)) g.FillRectangle(b, row);
            if (p.Puuid != null && parties.TryGetValue(p.Puuid, out int party))
                using (var b = new SolidBrush(PartyColors[party % PartyColors.Length])) g.FillRectangle(b, row.X, y + 5, 3, rowH - 10);

            var icon = new Rectangle(row.X + 10, y + 6, 32, 32);
            Agent(g, p.AgentId, icon);
            // Tanıdık oyuncu: etiket renginde halka + kaç kez karşılaşıldığı (üzerine gelince ayrıntı).
            var enc = EncountersOf(p, st.MatchId);
            if (enc.Count > 0)
            {
                var tag = EncTag(enc);
                using (var pen = new Pen(tag.Value, 2.5f)) g.DrawRectangle(pen, icon.X - 2, icon.Y - 2, icon.Width + 3, icon.Height + 3);
                string n = enc.Count + "×";
                var sz = g.MeasureString(n, FPill);
                var bub = new RectangleF(icon.Right - sz.Width + 1, icon.Bottom - sz.Height + 4, sz.Width + 2, sz.Height - 2);
                using (var b = new SolidBrush(tag.Value)) g.FillRectangle(b, bub);
                Draw(g, n, FPill, Bg, (int)bub.X + 1, (int)bub.Y - 1);
            }
            // Ajan Ustalığı: ömür boyu seviye (yoksa bu act'teki).
            int[] ms = null;
            if (h?.Mastery != null && p.AgentId != null) h.Mastery.TryGetValue(p.AgentId, out ms);
            int life = ms != null ? ms[0] : 0, act = p.AgentActLevel > 0 ? p.AgentActLevel : ms != null ? ms[1] : 0;
            if (life > 0 || act > 0)
            {
                string lv = life > 0 ? $"★{life}" : $"act {act}";
                var sz = g.MeasureString(lv, FPill);
                using (var b = new SolidBrush(Color.FromArgb(235, 20, 23, 31))) g.FillRectangle(b, icon.X, icon.Bottom + 1, sz.Width + 1, 12);
                Draw(g, lv, FPill, life >= 10 ? Warn : Txt, icon.X, icon.Bottom);
            }

            float x = row.X + 52, right = row.Right - 8;
            // Sağ üst: rank
            if (h?.RankLoaded == true && h.Rank != null)
            {
                string rank = h.RankId > 0 ? $"{h.Rank} · {h.Rr} RR" : h.Rank;
                float rw = Math.Min(160, g.MeasureString(rank, FBold).Width);
                Write(g, rank, FBold, h.RankId > 0 ? Tiers.Color(h.RankId) : Mut, right - rw, y + 6, rw + 2);
                right -= rw + 8;
            }
            // 1. satır: ad #etiket · seviye · sıralama · parti · en önemli iki etiket
            if (p.Incognito) Draw(g, L.T("Gizli oyuncu"), FName, Mut, x, y + 5);
            else
            {
                var clip = g.Clip;
                g.SetClip(new RectangleF(x, y, right - x, 24), CombineMode.Intersect);
                Draw(g, p.Name ?? "…", FName, Txt, x, y + 5);
                float cx = x + g.MeasureString(p.Name ?? "…", FName).Width - 2;
                string tg = "#" + p.Tag + (p.IsSelf ? L.T(" (sen)") : "");
                Draw(g, tg, FSmall, Mut, cx, y + 7); cx += g.MeasureString(tg, FSmall).Width + 2;
                if (p.Level > 0) cx = Pill(g, L.T("Sv") + " " + p.Level, FPill, Color.FromArgb(60, 255, 255, 255), Txt, cx, y + 9);
                if (h != null && h.Leaderboard > 0) cx = Pill(g, "#" + h.Leaderboard, FPill, Tiers.Color(27), Bg, cx, y + 9);
                if (p.Puuid != null && parties.TryGetValue(p.Puuid, out int pi)) cx = Pill(g, L.T("PARTİ"), FPill, PartyColors[pi % PartyColors.Length], Bg, cx, y + 9);
                foreach (var fct in fs.Where(fc => fc.Pill != null).Take(2))
                    if (cx + g.MeasureString(fct.Pill, FPill).Width + 8 < right) cx = Pill(g, fct.Pill, FPill, Color.FromArgb(210, fct.Color), Bg, cx, y + 9);
                g.Clip = clip; // içerik kırpması geri
            }

            // 2. satır: RR gidişatı · K/D · isabet · zirve · act
            float lx = x, lr = row.Right - 8;
            if (p.Incognito) Write(g, L.T("adını gizliyor — geçmişine bakılmaz"), FSmall, Mut, lx, y + 27, lr - lx);
            else if (h == null || (h.Matches == null && h.Error == null))
                Write(g, h?.RankLoaded == true ? L.T("geçmiş yükleniyor…") : L.T("rank yükleniyor…"), FSmall, Mut, lx, y + 27, lr - lx);
            else if (h.Error != null) Write(g, h.Error, FSmall, Loss, lx, y + 27, lr - lx);
            else
            {
                if (h.Form != null && h.Form.Count > 0)
                {
                    Draw(g, L.T("form"), FSmall, Mut, lx, y + 27); lx += g.MeasureString(L.T("form"), FSmall).Width;
                    foreach (var u in h.Form)
                    {
                        string s = (u.Earned > 0 ? "+" : u.Earned < 0 ? "−" : "±") + Math.Abs(u.Earned);
                        Draw(g, s, FPill, u.Earned > 0 ? Win : u.Earned < 0 ? Loss : Mut, lx, y + 28);
                        lx += g.MeasureString(s, FPill).Width - 1;
                    }
                    int streak = h.Form.TakeWhile(u => u.Earned > 0).Count(), lose = h.Form.TakeWhile(u => u.Earned < 0).Count();
                    string mood = streak >= 3 ? L.T(" ▲seri") : lose >= 3 ? L.T(" ▼tilt") : "";
                    if (mood != "") { Draw(g, mood, FSmall, streak >= 3 ? Win : Warn, lx, y + 27); lx += g.MeasureString(mood, FSmall).Width; }
                    lx += 4;
                }
                double kd = h.Deaths > 0 ? (double)h.Kills / h.Deaths : h.Kills;
                int l = h.Matches.Count(m => m.Result == "L");
                var parts = new List<string> { $"K/D {kd:0.00}" };
                if (h.HsAvg > 0) parts.Add($"HS %{h.HsAvg}");
                if (h.PeakId > h.RankId && h.Peak != null) parts.Add(L.T("zirve") + " " + h.Peak);
                if (h.ActGames > 0) parts.Add(L.F("· act %{0} ({1})", 100 * h.ActWins / h.ActGames, h.ActGames).TrimStart('·', ' '));
                // Sığmayan sondaki bilgi atlanır (ayrıntısı oyuncu sekmesinde).
                string sum = "· " + parts[0];
                for (int i = 1; i < parts.Count && g.MeasureString(sum + " · " + parts[i], FSmall).Width <= lr - lx; i++) sum += " · " + parts[i];
                Write(g, sum, FSmall, kd < 0.7 || l == 3 ? Warn : Mut, lx, y + 27, lr - lx);
            }

            // Bulgular: en önemli ikisi tek satır
            int fy = y + 48;
            foreach (var f in lines)
            {
                using (var b = new SolidBrush(f.Color)) g.FillRectangle(b, x, fy + 3, 3, 12);
                Split(f.Text, out var head, out var tail);
                float hw = Math.Min(g.MeasureString(head, FSmall).Width, row.Right - 8 - x - 10);
                Write(g, head, FSmall, Txt, x + 9, fy, hw + 2);
                if (tail != "" && x + 9 + hw + 40 < row.Right - 8) Write(g, "· " + tail, FSmall, Mut, x + 9 + hw + 2, fy, row.Right - 8 - (x + 9 + hw + 2));
                fy += 18;
            }

            using (var b = new SolidBrush(Box)) g.FillRectangle(b, row.X, row.Bottom - 1, row.Width, 1);
            if (!p.Incognito)
            {
                Hit(row, PlayerClick(p));
                if (enc.Count > 0) Hit(new Rectangle(icon.X - 3, icon.Y - 3, icon.Width + 6, icon.Height + 6), PlayerClick(p), p);
            }
            return rowH + 2;
        }

        // ---------- Oyuncu sekmesi ----------

        int DrawPlayer(Graphics g, LobbyState st, LobbyPlayer p, int y)
        {
            if (p == null) return y;
            var h = HistOf(p);
            int W = ViewW - 2 * Pad;

            // Başlık: oyuncu kartı soluk arka planda, ajan, ad, seviye, taraf
            var head = new Rectangle(Pad, y, W, 66);
            using (var b = new SolidBrush(Panel)) g.FillRectangle(b, head);
            DrawFaded(g, Assets.Icon(ValApi.CardWide(p.CardId)), head, 0.18f);
            var icon = new Rectangle(head.X + 10, head.Y + 9, 48, 48);
            Agent(g, p.AgentId, icon);
            var enc = EncountersOf(p, st.MatchId);
            if (enc.Count > 0) using (var pen = new Pen(EncTag(enc).Value, 2.5f)) g.DrawRectangle(pen, icon.X - 2, icon.Y - 2, icon.Width + 3, icon.Height + 3);
            var prof = new Rectangle(head.Right - 74, head.Y + 20, 64, 26);
            float nx = icon.Right + 12;
            Draw(g, p.Name ?? "…", FBig, Txt, nx, head.Y + 11);
            Write(g, "#" + p.Tag, FBody, Mut, nx + g.MeasureString(p.Name ?? "…", FBig).Width - 2, head.Y + 14, prof.X - 8 - (nx + g.MeasureString(p.Name ?? "…", FBig).Width));
            string sub = AgentLabel(p) + (p.Level > 0 ? " · " + L.T("Sv") + " " + p.Level : "") + " · " + (p.Ally ? L.T("Takım arkadaşın") : L.T("Rakibin"));
            float sx = nx + Math.Min(g.MeasureString(sub, FSmall).Width, prof.X - 8 - nx) + 2;
            Write(g, sub, FSmall, Mut, nx, head.Y + 37, prof.X - 8 - nx);
            if (p.Puuid != null && parties.TryGetValue(p.Puuid, out int pi) && sx + 50 < prof.X) Pill(g, L.T("PARTİ"), FPill, PartyColors[pi % PartyColors.Length], Bg, sx, head.Y + 39);
            using (var b = new SolidBrush(Over(prof) ? Box : Color.FromArgb(200, 15, 25, 35))) g.FillRectangle(b, prof);
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, prof.X, prof.Y, prof.Width - 1, prof.Height - 1);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Txt)) g.DrawString(L.T("Profil"), FPill, b, prof, sf);
            Hit(prof, _ => Scout.OpenProfile(p.Name, p.Tag));
            y += head.Height + 8;

            // Sayılar: yalnızca bilinenler
            var stats = new List<Tuple<string, string, Color>>();
            if (h?.RankLoaded == true && h.Rank != null)
                stats.Add(Tuple.Create("RANK", (h.RankId > 0 ? $"{h.Rank} · {h.Rr}" : h.Rank) + (h.Leaderboard > 0 ? " · #" + h.Leaderboard : ""), h.RankId > 0 ? Tiers.Color(h.RankId) : Mut));
            if (h?.Peak != null && h.PeakId > 0) stats.Add(Tuple.Create(Theme.Upper(L.T("zirve")), h.Peak, Txt));
            if (h != null && h.ActGames > 0) stats.Add(Tuple.Create("ACT", $"%{100 * h.ActWins / h.ActGames} ({h.ActGames})", h.ActWins * 2 >= h.ActGames ? Txt : Warn));
            if (h?.Matches != null && h.Matches.Count > 0)
            {
                double kd = h.Deaths > 0 ? (double)h.Kills / h.Deaths : h.Kills;
                stats.Add(Tuple.Create("K/D · HS", $"{kd:0.00}" + (h.HsAvg > 0 ? $" · %{h.HsAvg}" : ""), kd < 0.7 ? Warn : Txt));
            }
            if (stats.Count > 0)
            {
                int gap = 6, bw = (W - gap * (stats.Count - 1)) / stats.Count;
                for (int i = 0; i < stats.Count; i++)
                {
                    var r = new Rectangle(Pad + i * (bw + gap), y, bw, 40);
                    using (var b = new SolidBrush(Box)) g.FillRectangle(b, r);
                    Write(g, stats[i].Item1, FTiny, Mut, r.X + 8, r.Y + 4, r.Width - 12);
                    Write(g, stats[i].Item2, FBold, stats[i].Item3, r.X + 8, r.Y + 19, r.Width - 12);
                }
                y += 48;
            }

            // RR gidişatı ve ajan ustalığı
            int[] ms = null;
            if (h?.Mastery != null && p.AgentId != null) h.Mastery.TryGetValue(p.AgentId, out ms);
            if ((h?.Form != null && h.Form.Count > 0) || ms != null)
            {
                float x = Pad;
                if (h?.Form != null && h.Form.Count > 0)
                {
                    Draw(g, L.T("form"), FSmall, Mut, x, y); x += g.MeasureString(L.T("form"), FSmall).Width + 2;
                    foreach (var u in h.Form)
                    {
                        string s = (u.Earned > 0 ? "+" : u.Earned < 0 ? "−" : "±") + Math.Abs(u.Earned);
                        Draw(g, s, FBold, u.Earned > 0 ? Win : u.Earned < 0 ? Loss : Mut, x, y - 1);
                        x += g.MeasureString(s, FBold).Width;
                    }
                    int streak = h.Form.TakeWhile(u => u.Earned > 0).Count(), lose = h.Form.TakeWhile(u => u.Earned < 0).Count();
                    string mood = streak >= 3 ? L.T(" ▲seri") : lose >= 3 ? L.T(" ▼tilt") : "";
                    if (mood != "") { Draw(g, mood, FSmall, streak >= 3 ? Win : Warn, x, y); x += g.MeasureString(mood, FSmall).Width; }
                    x += 12;
                }
                if (ms != null && (ms[0] > 0 || ms[1] > 0))
                    Draw(g, AgentLabel(p) + (ms[0] > 0 ? $" ★{ms[0]}" : "") + (ms[1] > 0 ? $" · act {ms[1]}" : ""), FSmall, ms[0] >= 10 ? Warn : Mut, x, y);
                y += 24;
            }

            // İstihbarat: hepsi; etiketi olan önce etiketle, cümle bulgu + soluk öneri olarak
            var fs = FactsOf(p.Puuid);
            if (fs.Count > 0)
            {
                y = SectionTitle(g, L.T("İSTİHBARAT"), null, y);
                foreach (var f in fs)
                {
                    Split(f.Text, out var hd, out var tl);
                    float x = Pad + 10;
                    using (var b = new SolidBrush(f.Color)) g.FillRectangle(b, Pad, y + 3, 3, 14);
                    if (f.Pill != null) x = Pill(g, f.Pill, FPill, Color.FromArgb(210, f.Color), Bg, x - 2, y + 3);
                    Write(g, hd, FBold, Txt, x, y, ViewW - Pad - x);
                    y += 19;
                    if (tl != "")
                    {
                        var r = new RectangleF(Pad + 10, y, W - 10, 40);
                        var sz = g.MeasureString(tl, FSmall, (int)r.Width);
                        using (var b = new SolidBrush(Mut)) g.DrawString(tl, FSmall, b, r);
                        y += (int)Math.Min(40, sz.Height) + 2;
                    }
                    y += 6;
                }
                y += 4;
            }
            else if (!p.Incognito && PendingScouts() > 0 && ScoutedOf(p.Puuid) == null)
            {
                y = SectionTitle(g, L.T("İSTİHBARAT"), null, y);
                Draw(g, L.F("{0} oyuncu inceleniyor…", PendingScouts()), FSmall, Warn, Pad, y);
                y += 24;
            }

            // Karşılaşmalar (defterden)
            if (enc.Count > 0)
            {
                y = SectionTitle(g, L.T("TANIDIKLAR"), null, y);
                y = DrawEncounters(g, enc, Pad, y, W) + 8;
            }

            // Son maçlar
            if (h?.Matches != null && h.Matches.Count > 0)
            {
                y = SectionTitle(g, L.T("SON MAÇLAR"), null, y);
                foreach (var m in h.Matches.Take(3))
                {
                    var r = new Rectangle(Pad, y, W, 32);
                    using (var b = new SolidBrush(Box)) g.FillRectangle(b, r);
                    using (var b = new SolidBrush(m.Result == "W" ? Win : m.Result == "L" ? Loss : Mut)) g.FillRectangle(b, r.X, r.Y, 3, r.Height);
                    Agent(g, m.AgentId, new Rectangle(r.X + 10, r.Y + 5, 22, 22));
                    string left = $"{m.Map} {m.Score}".Trim() + (!string.IsNullOrEmpty(m.Tier) ? " · " + m.Tier : "");
                    string rt = $"{m.Kda} · {Queue(m.Queue)}";
                    float rw = g.MeasureString(rt, FSmall).Width;
                    Write(g, left, FBold, Txt, r.X + 40, r.Y + 8, r.Width - 48 - rw - 8);
                    Draw(g, rt, FSmall, Mut, r.Right - 8 - rw, r.Y + 9);
                    y += 36;
                }
                y += 6;
            }

            // Silah görünümleri (yalnızca maç içinde bilinir)
            string[] sk = null;
            lock (history) if (p.Puuid != null) skins.TryGetValue(p.Puuid, out sk);
            if (sk != null && sk.Any(s => !string.IsNullOrEmpty(s)))
            {
                y = SectionTitle(g, L.T("SİLAH GÖRÜNÜMLERİ"), null, y);
                int bw = (W - 6) / 2;
                for (int i = 0; i < 2; i++)
                {
                    var c = ValApiCached(sk[i]);
                    var r = new Rectangle(Pad + i * (bw + 6), y, bw, 56);
                    using (var b = new SolidBrush(Box)) g.FillRectangle(b, r);
                    DrawFit(g, Assets.Icon(c.Value), new Rectangle(r.X + 8, r.Y + 4, r.Width - 16, 30));
                    Write(g, c.Key ?? "", FTiny, Mut, r.X + 8, r.Y + 38, r.Width - 16);
                }
                y += 64;
            }

            // Tam analiz: ısı haritası ve tüm sayılar ayrı pencerede
            var full = new Rectangle(Pad, y + 4, W, 34);
            using (var b = new SolidBrush(Over(full) ? Theme.Lighten(Red, 0.15f) : Red)) g.FillRectangle(b, full);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Bg)) g.DrawString(L.T("TAM ANALİZ · ISI HARİTASI"), FBold, b, full, sf);
            Hit(full, _ => OpenFull(p));
            return full.Bottom + 4;
        }

        /// <summary>Karşılaşma listesi: etiket, toplam, düello özeti ve son karşılaşmalar; yeni y'yi döner.</summary>
        int DrawEncounters(Graphics g, List<Encounter> list, int x, int y, int w)
        {
            var tag = EncTag(list);
            int lines = Math.Min(5, list.Count);
            Draw(g, tag.Key, FBold, tag.Value, x, y);
            float hx = x + 4 + g.MeasureString(tag.Key, FBold).Width;
            string head = L.F("· {0} kez aynı maçtaydınız", list.Count);
            Draw(g, head, FBold, Txt, hx, y);
            // Rakipken aranızdaki düellolar (yalnızca düello sayısı bilinen maçlar).
            var duels = list.Where(e => !e.Teammate && e.Them.KilledMe >= 0).ToList();
            if (duels.Count > 0)
            {
                int mine = duels.Sum(e => e.Them.KilledByMe), theirs = duels.Sum(e => e.Them.KilledMe);
                float dx = hx + g.MeasureString(head, FBold).Width + 2;
                Write(g, L.F("· düello sen {0} – {1} o", mine, theirs), FBold, mine >= theirs ? Win : Loss, dx, y, x + w - dx);
            }
            y += 24;
            foreach (var e in list.Take(lines))
            {
                Agent(g, e.Them.Agent, new Rectangle(x, y, 26, 26));
                string r = e.Match.Outcome();
                var rc = r == "W" ? Win : r == "L" ? Loss : Mut;
                string ln = $"{(e.Teammate ? L.T("Takım arkadaşın") : L.T("Rakibin"))} · {RiotLocal.Title(e.Match.Map)} ·{(r == "W" ? L.T("kazandın") : r == "L" ? L.T("kaybettin") : L.T("berabere"))} {e.Match.MyRounds}-{e.Match.TheirRounds}";
                Write(g, ln, FBold, rc, x + 34, y - 2, w - 36);
                string duel = !e.Teammate && e.Them.KilledMe >= 0 ? L.F(" · seni {0} kez öldürdü, sen onu {1}", e.Them.KilledMe, e.Them.KilledByMe) : "";
                Write(g, L.F("{0} · o maçta {1}/{2}/{3}{4}", Ago(e.Match.Start), e.Them.K, e.Them.D, e.Them.A, duel), FSmall, Mut, x + 34, y + 13, w - 36);
                y += 34;
            }
            if (list.Count > lines) { Draw(g, L.F("+{0} karşılaşma daha", list.Count - lines), FSmall, Mut, x + 34, y - 2); y += 18; }
            return y;
        }

        /// <summary>Listede tanıdık oyuncunun ikonunun üzerine gelince geçmiş karşılaşmalar.</summary>
        void DrawPopup(Graphics g, LobbyPlayer p, Rectangle anchor, string matchId)
        {
            var list = EncountersOf(p, matchId);
            if (list.Count == 0) return;
            int lines = Math.Min(5, list.Count), h = 24 + lines * 34 + (list.Count > lines ? 18 : 0) + 20;
            int w = ViewW - 2 * Pad, x = Pad, y = anchor.Bottom + 4;
            if (y + h > ViewH - 8) y = anchor.Top - h - 4;
            var tag = EncTag(list);
            using (var b = new SolidBrush(Color.FromArgb(250, 20, 23, 31))) g.FillRectangle(b, x, y, w, h);
            using (var b = new SolidBrush(tag.Value)) g.FillRectangle(b, x, y, 4, h);
            using (var pen = new Pen(Color.FromArgb(60, tag.Value))) g.DrawRectangle(pen, x, y, w - 1, h - 1);
            DrawEncounters(g, list, x + 14, y + 10, w - 24);
        }

        /// <summary>"?" üzerine gelince: gizlilik, karşılaşma defteri ve kısayollar.</summary>
        void DrawHelp(Graphics g, Rectangle anchor)
        {
            var lines = new[]
            {
                L.T("Bu pencere yayına ve kayda girmez · adını gizleyenler aranmaz"),
                L.F("Karşılaşma defteri: {0}  ·  renkli halka = daha önce aynı maçtaydınız (üzerine gel)  ·  oyuncuya tıkla: analiz · sağ tık: profil", Encounters.Status),
                L.T("Kısayollar: 1-4 sekmeler · B brifing · Esc oyuncu sekmesini kapat · tekerlek kaydırır"),
            };
            int w = ViewW - 2 * Pad, x = Pad, y = anchor.Bottom + 6, h = 12;
            var sizes = lines.Select(l => g.MeasureString(l, FSmall, w - 24)).ToList();
            h += (int)sizes.Sum(s => s.Height + 6);
            using (var b = new SolidBrush(Color.FromArgb(250, 20, 23, 31))) g.FillRectangle(b, x, y, w, h);
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, x, y, w - 1, h - 1);
            float ly = y + 8;
            using (var b = new SolidBrush(Txt))
                for (int i = 0; i < lines.Length; i++) { g.DrawString(lines[i], FSmall, b, new RectangleF(x + 12, ly, w - 24, sizes[i].Height + 2)); ly += sizes[i].Height + 6; }
        }

        // ---------- Fare ve klavye ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            var hit = hots.LastOrDefault(h => h.R.Contains(mouse));
            Cursor = hit?.Click != null ? Cursors.Hand : Cursors.Default;
            // Yeniden çizim yalnızca üzerinde durulan alan değişince
            if (hit?.R != hovered?.R || hit?.Pop != hovered?.Pop) Invalidate();
            hovered = hit;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1);
            if (hovered != null) { hovered = null; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var hit = hots.LastOrDefault(h => h.Click != null && h.R.Contains(e.Location));
            hit?.Click(e.Button);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Body.Contains(e.Location)) return;
            int cur = scroll.TryGetValue(tab, out var s) ? s : 0;
            scroll[tab] = Math.Max(0, cur - Math.Sign(e.Delta) * 60); // üst sınır çizimde
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            LobbyState st; lock (history) st = state;
            var tabs = Tabs(st);
            int i = keyData >= Keys.D1 && keyData <= Keys.D5 ? keyData - Keys.D1 : keyData >= Keys.NumPad1 && keyData <= Keys.NumPad5 ? keyData - Keys.NumPad1 : -1;
            if (i >= 0 && i < tabs.Count)
            {
                bool off = tabs[i] == Tab.Enemy && (st.Phase == "pregame" || !st.Players.Any(p => !p.Ally));
                if (!off) { tab = tabs[i]; Invalidate(); }
                return true;
            }
            if (keyData == Keys.B) { tab = Tab.Brief; Invalidate(); return true; }
            if (keyData == Keys.Escape && selPuuid != null) { selPuuid = null; tab = Tab.Brief; Invalidate(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
