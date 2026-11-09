using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Platformun geliştirici uygulamasının Client ID/Secret'ını alır (bir kez; DPAPI ile şifreli saklanır).
    /// Kick: kick.com/settings/developer · YouTube: Google Cloud'da masaüstü OAuth istemcisi.
    /// </summary>
    sealed class AppDialog : Form
    {
        static readonly Color Bg = ColorTranslator.FromHtml("#0f1117"), Field = ColorTranslator.FromHtml("#1b1e27");
        static readonly Color Txt = ColorTranslator.FromHtml("#e8eaf0"), Mut = ColorTranslator.FromHtml("#8b93a4"), Red = ColorTranslator.FromHtml("#ff4655");
        readonly TextBox id, secret;

        /// <summary>Bir adım: yazı, altında isteğe bağlı açılacak sayfa ya da kopyalanacak metin.</summary>
        sealed class Step { public string Text, Link, Copy; }

        public static bool AskKick(IWin32Window owner, Settings cfg)
        {
            const string devPage = "https://kick.com/settings/developer";
            var steps = new[]
            {
                new Step { Text = L.T("Kick'te geliştirici sayfasını aç (giriş yapmış olmalısın):"), Link = devPage },
                new Step { Text = L.T("Yeni uygulama oluştur. Ad: ValoLive · Redirect URL olarak şunu yaz:"), Copy = KickApi.RedirectUri },
                new Step { Text = L.T("İzinler: user:read, channel:read, channel:write, chat:write işaretle → Kaydet.") },
                new Step { Text = L.T("Uygulamanın Client ID ve Client Secret'ını buraya yapıştır:") },
            };
            return Ask(owner, L.T("Kick hesabını bağla"), L.T("Kick, başlık ve kategoriyi değiştirmek için kendi \"geliştirici uygulamanı\" ister.\nBir kerelik, 2 dakikalık bir iş:"),
                       steps, L.T("Sonra tarayıcıda Kick onay sayfası açılır; \"İzin ver\" demen yeterli."),
                       (i, s) => { cfg.KickClientIdEnc = Settings.Protect(i); cfg.KickSecretEnc = Settings.Protect(s); cfg.Save(); });
        }

        public static bool AskYouTube(IWin32Window owner, Settings cfg)
        {
            var steps = new[]
            {
                new Step { Text = L.T("Google Cloud'da yeni bir proje oluştur (ad: ValoLive):"), Link = "https://console.cloud.google.com/projectcreate" },
                new Step { Text = L.T("Bu projede \"YouTube Data API v3\"ü etkinleştir (Enable):"), Link = "https://console.cloud.google.com/apis/library/youtube.googleapis.com" },
                new Step { Text = L.T("Google Auth Platform → Get started: uygulama adı ValoLive, kitle External. Sonra Audience sayfasında \"Publish app\" de (yoksa giriş 7 günde bir düşer)."), Link = "https://console.cloud.google.com/auth/overview" },
                new Step { Text = L.T("Clients → Create client → Application type: Desktop app → Create. Client ID ve Client secret'ı buraya yapıştır:"), Link = "https://console.cloud.google.com/auth/clients" },
            };
            return Ask(owner, L.T("YouTube hesabını bağla"), L.T("YouTube, başlığı değiştirmek ve chat'e bot cevabı yazmak için Google Cloud'da kendi uygulamanı ister.\nBir kerelik, 5 dakikalık bir iş:"),
                       steps, L.T("Sonra tarayıcıda Google onayı açılır. \"Google bu uygulamayı doğrulamadı\" derse Gelişmiş → ValoLive'a git: uygulama senin kendi projen."),
                       (i, s) => { cfg.YtClientIdEnc = Settings.Protect(i); cfg.YtSecretEnc = Settings.Protect(s); cfg.Save(); });
        }

        static bool Ask(IWin32Window owner, string title, string intro, Step[] steps, string outro, Action<string, string> save)
        {
            using (var d = new AppDialog(title, intro, steps, outro))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                save(d.id.Text.Trim(), d.secret.Text.Trim());
                return true;
            }
        }

        AppDialog(string title, string intro, Step[] steps, string outro)
        {
            Text = title;
            BackColor = Bg; ForeColor = Txt; Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 400);

            // Yazılar pencere genişliğinde satır kırar; yükseklik içeriğe göre (uzun çevirilerde pencere uzar).
            int y = 18;
            y += Wrapped(intro, 20, y, Txt) + 14;
            for (int i = 0; i < steps.Length; i++)
            {
                var s = steps[i];
                StepText((i + 1).ToString(), s.Text, ref y);
                if (s.Link != null)
                {
                    var link = new LinkLabel { Text = s.Link, AutoSize = true, Location = new Point(44, y - 4), LinkColor = Red, ActiveLinkColor = Red };
                    link.LinkClicked += (o, e) => Process.Start(s.Link);
                    Add(link); y += 26;
                }
                if (s.Copy != null)
                {
                    int copyW = Math.Max(80, TextRenderer.MeasureText(L.T("Kopyala"), Font).Width + 20);
                    var box = new TextBox { Text = s.Copy, ReadOnly = true, Location = new Point(44, y - 4), Width = 300, BackColor = Field, ForeColor = Txt, BorderStyle = BorderStyle.FixedSingle };
                    var copy = new Button { Text = L.T("Kopyala"), Bounds = new Rectangle(352, y - 5, copyW, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat };
                    copy.FlatAppearance.BorderSize = 0;
                    copy.Click += (o, e) => { Clipboard.SetText(s.Copy); copy.Text = "✓"; };
                    Add(box); Add(copy); y += 32;
                }
            }
            id = Field_("Client ID", ref y, false);
            secret = Field_("Client Secret", ref y, true);

            y += 4 + Wrapped(outro, 20, y + 4, Mut) + 12;
            int okW = Math.Max(160, TextRenderer.MeasureText(L.T("Kaydet ve bağlan"), Font).Width + 28);
            int cancelW = Math.Max(100, TextRenderer.MeasureText(L.T("Vazgeç"), Font).Width + 28);
            var ok = new Button { Text = L.T("Kaydet ve bağlan"), Bounds = new Rectangle(ClientSize.Width - 20 - okW, y, okW, 34), BackColor = Red, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = L.T("Vazgeç"), Bounds = new Rectangle(ok.Left - 10 - cancelW, y, cancelW, 34), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            ok.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
            ok.Click += (s, e) =>
            {
                if (id.Text.Trim() == "" || secret.Text.Trim() == "") { DialogResult = DialogResult.None; MessageBox.Show(this, L.T("Client ID ve Client Secret gerekli."), "ValoLive"); }
            };
            Add(ok); Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(ClientSize.Width, y + 34 + 18);
        }

        void Add(Control c) => Controls.Add(c);

        /// <summary>Pencerenin sağ kenarında satır kıran yazı; yüksekliğini döner.</summary>
        int Wrapped(string text, int x, int y, Color color)
        {
            var l = new Label { Text = text, ForeColor = color, AutoSize = true, MaximumSize = new Size(ClientSize.Width - x - 20, 0), Location = new Point(x, y) };
            Add(l);
            return l.GetPreferredSize(new Size(ClientSize.Width - x - 20, 0)).Height;
        }

        void StepText(string n, string text, ref int y)
        {
            Add(new Label { Text = n, ForeColor = Red, Font = new Font(L.SansBold, 10f), AutoSize = true, Location = new Point(20, y - 1) });
            y += Math.Max(26, Wrapped(text, 44, y, Txt) + 8);
        }

        TextBox Field_(string label, ref int y, bool hidden)
        {
            Add(new Label { Text = label, ForeColor = Mut, AutoSize = true, Location = new Point(44, y + 3) });
            var t = new TextBox { Location = new Point(150, y), Width = 380, UseSystemPasswordChar = hidden, BackColor = Field, ForeColor = Txt, BorderStyle = BorderStyle.FixedSingle };
            Add(t); y += 32;
            return t;
        }
    }
}
