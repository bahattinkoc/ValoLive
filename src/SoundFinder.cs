using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>myinstants.com listeleri: ses adı + mp3 adresi. Sayfa HTML'inden okunur (resmî bir API yok).</summary>
    static class MyInstants
    {
        public sealed class Item { public string Name, Url; }
        const string Site = "https://www.myinstants.com";
        static readonly Regex Row = new Regex(@"play\('(?<mp3>/media/sounds/[^']+)'.*?class=""instant-link[^""]*"">(?<name>[^<]+)</a>", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>source: "tr" (Türkiye), "trend" (ABD: dünyaca bilinen memeler; /trending ziyaretçinin ülkesini gösterir) ya da arama metni.</summary>
        public static List<Item> Fetch(string source, string query, int page)
        {
            string path = source == "tr" ? "/en/index/tr/?page=" + page
                : source == "trend" ? "/en/index/us/?page=" + page
                : "/en/search/?name=" + Uri.EscapeDataString(query) + "&page=" + page;
            string html;
            using (var wc = Client()) html = wc.DownloadString(Site + path);
            return Row.Matches(html).Cast<Match>()
                .Select(m => new Item { Name = WebUtility.HtmlDecode(m.Groups["name"].Value).Trim(), Url = Site + m.Groups["mp3"].Value })
                .GroupBy(i => i.Url).Select(g => g.First()).ToList();
        }

        public static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
            wc.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ValoLive";
            return wc;
        }

        /// <summary>mp3'ü önbelleğe indirir (dinleme ve ekleme aynı dosyayı kullanır).</summary>
        public static string Cached(Item it)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ValoLive-sesler");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(new Uri(it.Url).LocalPath));
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                using (var wc = Client()) { wc.DownloadFile(it.Url, file + ".part"); File.Delete(file); File.Move(file + ".part", file); }
            return file;
        }
    }

    // Ses paneli için ses bulma penceresi: myinstants'tan ara, dinle, tek tıkla panele ekle.
    sealed partial class MainForm
    {
        System.Media.SoundPlayer previewPlayer;

        void FindSounds()
        {
            var f = new Form
            {
                Text = L.T("Ses bul"), BackColor = Bg, ForeColor = Txt, Font = Font,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            };
            f.HandleCreated += (s, e) => Theme.DarkTitle(f);
            var oldHost = host; int oldCx = cx;
            host = f; cx = 20;
            try
            {
                int y = Section(L.T("SES BUL"), 16);
                var src = AddCombo(L.T("Liste"), ref y, new[] { L.T("Türkiye'de popüler"), L.T("Dünyada popüler"), L.T("Arama") });
                var q = AddText(L.T("Ara"), ref y, false);
                int lw = ColW;
                var status = new Label { ForeColor = Mut, AutoSize = false, Size = new Size(lw, 20), Location = new Point(cx, y) };
                f.Controls.Add(status); y += 24;
                var list = new Panel { Location = new Point(cx, y), Size = new Size(lw, 380), AutoScroll = true, BackColor = Card };
                f.Controls.Add(list); y += 388;
                int mw = Theme.ButtonWidth(L.T("Daha fazla"), 24, 110), cw = Theme.ButtonWidth(L.T("Kapat"), 24, 80);
                var more = MakeButton(L.T("Daha fazla"), new Rectangle(cx, y, mw, 32), Field);
                var close = MakeButton(L.T("Kapat"), new Rectangle(cx + lw - cw, y, cw, 32), Red);
                more.Font = close.Font = new Font(L.SansBold, 9.5f);
                var note = new Label { Text = L.T("Sesler myinstants.com'dan gelir ve başkalarına aittir; yalnızca kendi oyunun için kullan."), ForeColor = Mut, AutoSize = true, MaximumSize = new Size(lw - mw - cw - 24, 0), Location = new Point(cx + mw + 12, y) };
                f.Controls.Add(note);
                f.ClientSize = new Size(lw + 40, y + 32 + 18);
                f.CancelButton = close;

                int page = 1, rowY = 0, gen = 0;
                Action<bool> load = null;
                load = fresh =>
                {
                    string source = src.SelectedIndex == 0 ? "tr" : src.SelectedIndex == 1 ? "trend" : "ara";
                    string query = q.Text.Trim();
                    if (source == "ara" && query.Length == 0) { status.Text = L.T("Aramak için bir şey yaz."); return; }
                    if (fresh) { page = 1; rowY = 0; list.Controls.Clear(); list.AutoScrollPosition = Point.Empty; }
                    int my = ++gen, pg = page;
                    status.Text = L.T("Yükleniyor…"); more.Enabled = false;
                    Task.Run(() => MyInstants.Fetch(source, query, pg)).ContinueWith(t =>
                    {
                        if (f.IsDisposed || my != gen) return;
                        more.Enabled = true;
                        if (t.IsFaulted) { status.Text = L.T("myinstants'a ulaşılamadı. İnternet bağlantını kontrol et."); return; }
                        var items = t.Result;
                        if (items.Count == 0) { status.Text = pg == 1 ? L.T("Sonuç yok.") : L.T("Hepsi bu kadar."); more.Enabled = false; return; }
                        page++;
                        list.SuspendLayout();
                        foreach (var it in items) { AddFoundRow(list, it, rowY, status); rowY += 36; }
                        list.ResumeLayout();
                        status.Text = PanelCount();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                };
                src.SelectedIndexChanged += (s, e) => { if (src.SelectedIndex != 2) load(true); else q.Focus(); };
                q.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; src.SelectedIndex = 2; load(true); } };
                more.Click += (s, e) => load(false);
                close.Click += (s, e) => f.Close();
                f.FormClosed += (s, e) => { try { previewPlayer?.Stop(); } catch { } };
                f.Shown += (s, e) => load(true); // ilk liste: Türkiye (açılır listenin değeri zaten 0, olay gelmez)
            }
            finally { host = oldHost; cx = oldCx; }
            f.ShowDialog(this);
            f.Dispose();
        }

        static string PanelCount() => L.F("Panelde {0}/{1} ses.", SoundBoard.FileCount(), SoundBoard.Max);

        void AddFoundRow(Panel list, MyInstants.Item it, int y, Label status)
        {
            int w = list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            int aw = Theme.ButtonWidth(L.T("+ Ekle"), 20, 80);
            var name = new Label { Text = it.Name, ForeColor = Txt, AutoEllipsis = true, Location = new Point(48, y + 9), Size = new Size(w - 48 - aw - 14, 20) };
            var play = new VButton { Text = "▶", Bounds = new Rectangle(8, y + 4, 32, 28), BackColor = Field, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 9f), Cursor = Cursors.Hand };
            var add = new VButton { Text = L.T("+ Ekle"), Bounds = new Rectangle(w - aw - 6, y + 4, aw, 28), BackColor = Field, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 9f), Cursor = Cursors.Hand };
            play.FlatAppearance.BorderSize = add.FlatAppearance.BorderSize = 0;
            if (SoundBoard.Has(it.Name)) { add.Text = L.T("✓ Eklendi"); add.Enabled = false; }
            play.Click += (s, e) =>
            {
                status.Text = L.T("Yükleniyor…");
                Task.Run(() => Preview(it)).ContinueWith(t =>
                {
                    if (status.IsDisposed) return;
                    if (t.IsFaulted || t.Result == null) { status.Text = L.T("Bu ses açılamadı."); return; }
                    try { previewPlayer?.Stop(); previewPlayer = new System.Media.SoundPlayer(t.Result); previewPlayer.Play(); status.Text = it.Name; }
                    catch { status.Text = L.T("Bu ses açılamadı."); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            };
            add.Click += (s, e) =>
            {
                if (SoundBoard.FileCount() >= SoundBoard.Max) { status.Text = L.F("Panel dolu ({0}): bir sesi kaldırmak için düğmesine sağ tıkla.", SoundBoard.Max); return; }
                add.Enabled = false; add.Text = "…";
                Task.Run(() => SoundBoard.Add(it.Name, MyInstants.Cached(it))).ContinueWith(t =>
                {
                    if (add.IsDisposed) return;
                    if (t.IsFaulted) { add.Enabled = true; add.Text = L.T("+ Ekle"); status.Text = L.T("İndirilemedi, tekrar dene."); return; }
                    add.Text = L.T("✓ Eklendi");
                    status.Text = PanelCount();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            };
            list.Controls.AddRange(new Control[] { play, name, add });
        }

        /// <summary>Dinleme: mp3 → wav (ffmpeg), yalnızca bu bilgisayarda çalar (takıma gitmez).</summary>
        static string Preview(MyInstants.Item it)
        {
            string mp3 = MyInstants.Cached(it), wav = Path.ChangeExtension(mp3, ".wav");
            if (!File.Exists(wav))
            {
                var psi = new ProcessStartInfo(Streamer.FindFfmpeg(), $"-y -v quiet -i \"{mp3}\" -t 30 -ac 2 -ar 44100 -c:a pcm_s16le \"{wav}\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(10000);
            }
            return File.Exists(wav) ? wav : null;
        }
    }
}
