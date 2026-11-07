using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>Kick geliştirici uygulamasının Client ID/Secret'ını alır (bir kez; DPAPI ile şifreli saklanır).</summary>
    sealed class KickAppDialog : Form
    {
        static readonly Color Bg = ColorTranslator.FromHtml("#0f1117"), Field = ColorTranslator.FromHtml("#1b1e27");
        static readonly Color Txt = ColorTranslator.FromHtml("#e8eaf0"), Mut = ColorTranslator.FromHtml("#8b93a4"), Red = ColorTranslator.FromHtml("#ff4655");
        const string DevPage = "https://kick.com/settings/developer";
        readonly TextBox id, secret;

        public static bool Ask(IWin32Window owner, Settings cfg)
        {
            using (var d = new KickAppDialog())
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                cfg.KickClientIdEnc = Settings.Protect(d.id.Text.Trim());
                cfg.KickSecretEnc = Settings.Protect(d.secret.Text.Trim());
                cfg.Save();
                return true;
            }
        }

        KickAppDialog()
        {
            Text = L.T("Kick hesabını bağla");
            BackColor = Bg; ForeColor = Txt; Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 400);

            // Yazılar pencere genişliğinde satır kırar; yükseklik içeriğe göre (uzun çevirilerde pencere uzar).
            int y = 18;
            y += Wrapped(L.T("Kick, başlık ve kategoriyi değiştirmek için kendi \"geliştirici uygulamanı\" ister.\nBir kerelik, 2 dakikalık bir iş:"), 20, y, Txt) + 14;
            Step("1", L.T("Kick'te geliştirici sayfasını aç (giriş yapmış olmalısın):"), ref y);
            var open = new LinkLabel { Text = DevPage, AutoSize = true, Location = new Point(44, y - 4), LinkColor = Red, ActiveLinkColor = Red };
            open.LinkClicked += (s, e) => Process.Start(DevPage);
            Add(open); y += 26;
            Step("2", L.T("Yeni uygulama oluştur. Ad: ValoLive · Redirect URL olarak şunu yaz:"), ref y);
            int copyW = Math.Max(80, TextRenderer.MeasureText(L.T("Kopyala"), Font).Width + 20);
            var uri = new TextBox { Text = KickApi.RedirectUri, ReadOnly = true, Location = new Point(44, y - 4), Width = 300, BackColor = Field, ForeColor = Txt, BorderStyle = BorderStyle.FixedSingle };
            var copy = new Button { Text = L.T("Kopyala"), Bounds = new Rectangle(352, y - 5, copyW, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat };
            copy.FlatAppearance.BorderSize = 0;
            copy.Click += (s, e) => { Clipboard.SetText(KickApi.RedirectUri); copy.Text = "✓"; };
            Add(uri); Add(copy); y += 32;
            Step("3", L.T("İzinler: user:read, channel:read, channel:write, chat:write işaretle → Kaydet."), ref y);
            Step("4", L.T("Uygulamanın Client ID ve Client Secret'ını buraya yapıştır:"), ref y);
            id = Field_("Client ID", ref y, false);
            secret = Field_("Client Secret", ref y, true);

            y += 4 + Wrapped(L.T("Sonra tarayıcıda Kick onay sayfası açılır; \"İzin ver\" demen yeterli."), 20, y + 4, Mut) + 12;
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

        void Step(string n, string text, ref int y)
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
