using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ValoLive
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            L.Init(Settings.Load().Language); // arayüz metinleri kurulmadan önce
            Application.Run(new MainForm());
        }
    }

    sealed class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

        static readonly Color Bg = Theme.Bg;
        static readonly Color Card = Theme.Card;
        static readonly Color Field = Theme.Field;
        static readonly Color Txt = Theme.Txt;
        static readonly Color Mut = Theme.Mut;
        static readonly Color Red = Theme.Red;
        static readonly Color Green = ColorTranslator.FromHtml("#3ddc97");

        static readonly (string Label, int W, int H, int Kbps)[] Qualities =
        {
            // Kick sınırı toplam 8000 kbps ama 7500'de gerçek akış ~7900 kbps'ye çıkıyor ve Kick
            // bağlantıyı ~15 sn'de bir kesiyordu (günlükte doğrulandı). 6000 gerçekte ~6300-6600.
            (L.T("1080p60 · 6000 kbps (önerilen)"), 1920, 1080, 6000),
            (L.T("936p60 · 6000 kbps (keskin + hafif)"), 1664, 936, 6000),
            (L.T("720p60 · 4500 kbps (zayıf internet)"), 1280, 720, 4500),
        };
        static readonly string[] Regions = { "eu", "na", "ap", "kr", "latam", "br" };

        readonly Settings cfg = Settings.Load();
        readonly int screenW, screenH;
        readonly OverlayRenderer overlay;
        readonly Streamer streamer = new Streamer();
        readonly CaptureGuard guard;
        ComboBox cmbCapture, cmbApp;
        Button btnApps;
        Control lblApp;
        List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();
        ValorantTracker tracker;
        AudioSource audio, micAudio;
        StreamOptions lastOptions;

        TextBox txtUrl, txtKey, txtName, txtTag, txtHdev, txtTitle, txtTags;
        ComboBox cmbCategory;
        Button btnAlign;
        Label lblRadarPos;
        CheckBox chkRadar, chkChat, chkPost, chkBot, chkWgc, chkBreak, chkAutoLobby, chkAlerts, chkAutoCover, chkMemory;
        string liveState = "";
        bool autoCovered;                 // perdeyi ajan seçimi açtı (F9 ile açılanı maç başı kapatmaz)
        Control[] appShift; bool appRow = true;
        NotifyIcon tray; bool exiting;
        LiveInfo live;
        readonly MinimapMemory memory = new MinimapMemory();
        readonly Timer memTimer = new Timer { Interval = 250 };
        MemoryPanel memPanel;
        int memScore = -1;
        bool memStarting;
        DateTime memRetry;
        PostMatchLayer postLayer;
        ChatBot bot;
        Label lblKick, lblChat;
        Button btnKick;
        KickApi kick;
        KickChat kickChat;
        RadarWidget radar;
        ChatLayer chatLayer;
        ValoState lastState;
        System.Threading.CancellationTokenSource categorySearch;
        ComboBox cmbQuality, cmbMic, cmbOut, cmbRegion;
        VSlider trkMic, trkGame;
        Label lblMic, lblGame, lblValo, lblStatus, lblStats;
        CheckBox chkDenoise;
        Button btnLive, btnTest;
        List<KeyValuePair<string, string>> outputs, inputs;

        enum Mode { Idle, Live, Test }
        Mode mode = Mode.Idle;
        DateTime liveSince;
        int reconnects;
        bool everLive;
        DateTime connectAt;   // son Kick bağlantısının açıldığı an
        int quickDrops;       // art arda, bağlandıktan kısa süre sonra gelen kopmalar
        readonly Timer clock = new Timer { Interval = 1000 };

        public MainForm()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            screenW = GetDeviceCaps(dc, 118); // DESKTOPHORZRES: DPI ölçeğinden bağımsız gerçek çözünürlük
            screenH = GetDeviceCaps(dc, 117);
            ReleaseDC(IntPtr.Zero, dc);
            overlay = new OverlayRenderer(screenW, screenH);
            guard = new CaptureGuard(screenW, screenH) { Send = streamer.SendCommand, WindowOnly = () => streamer.WindowOnly };
            guard.CoveredChanged += c =>
            {
                overlay.Privacy = c;
                Diag.Event("PERDE", c ? $"kapandı · öndeki uygulama: {(guard.Foreground == "" ? "(yok)" : guard.Foreground)}" : "açıldı · hedef uygulama önde");
            };

            Text = "ValoLive";
            BackColor = Bg; ForeColor = Txt;
            Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            HandleCreated += (s, e) => Theme.DarkTitle(this);
            kick = new KickApi(cfg);
            BuildUi();
            BuildTray();
            foreach (var l in Controls.OfType<Label>()) l.UseMnemonic = false; // "&" kısayol harfi sanılmasın
            LoadSettings();

            streamer.Progress += p => BeginInvoke((Action)(() => ShowProgress(p)));
            streamer.Exited += (unexpected, err) => BeginInvoke((Action)(() => OnStreamExit(unexpected, err)));
            streamer.Recovering += () => BeginInvoke((Action)(() => { if (mode != Mode.Idle) SetStatus(L.T("● EKRAN YAKALAMA YENİLENİYOR (yayın açık)"), Mut); }));
            clock.Tick += (s, e) => UpdateClock();
            clock.Start();
            StartTracker();
            Encounters.Start(); // karşılaşma defteri arka planda dolar (Riot istemcisi açıkken)
            live = new LiveInfo(); // canlı skor ve maç sonu özeti (Riot istemcisinin kendi durum bilgisi)
            live.Changed += s => { if (IsHandleCreated) try { BeginInvoke((Action)(() => ApplyLive(s))); } catch { } };
            live.PostMatchReady += pm => { var l = postLayer; if (l != null && (pm.Queue == "competitive" || pm.Queue == "unrated" || pm.Queue == "swiftplay" || pm.Queue == "premier")) l.Show(pm); };
            memTimer.Tick += (s, e) => TickMemory();
            memTimer.Start();
        }

        // ---------- Arayüz ----------

        void BuildUi()
        {
            // İki sütun: solda bağlantılar (Kick, Valorant), sağda yayın ayarları (görüntü, ses).
            // Başlık (VALO + kırmızı LIVE) OnPaint'te çizilir: köşeli yazı tipi uygulamayla gelen bir dosya, GDI+ ister.
            // Ölçüler metinlere göre (diller arasında uzunluk çok değişiyor): etiket sütunu en uzun alan etiketine,
            // sütunlar en uzun anahtar yazısına göre genişler.
            string tAutoLobby = L.T("Maç bulununca otomatik aç"), tAlerts = L.T("Bildirimler (tanıdık oyuncu, smurf, parti)"),
                   tWgc = L.T("Hafif yakalama (yalnızca oyun penceresi)"), tDenoise = L.T("Gürültü filtresi"),
                   tRadar = L.T("Minimap'i radar rozetiyle ört"), tAutoCover = L.T("Ajan seçiminde sniper perdesi (otomatik)"),
                   tChat = L.T("Yayında chat kutusu"), tPost = L.T("Maç sonu kartı (ısı haritası + özet)"),
                   tBot = L.F("Chat komutları ({0})", string.Join(" ", ChatBot.Commands)), tBreak = L.T("Menüde bekleme ekranı (sıra sayacı)"),
                   tMemory = L.T("Rakip hafızası: minimap'te son görülen yerler (deneysel)");
            using (var cap = new Font(L.SansBold, 8.25f))
                LabelW = Math.Max(104, new[] { "Stream URL", "Stream Key", L.T("Kick hesabı"), L.T("Başlık"), L.T("Kategori"), L.T("Etiketler"), "Riot ID", "HenrikDev key",
                    L.T("Kalite"), L.T("Yakalama"), L.T("Uygulama"), L.T("Mikrofon"), L.T("Oyun sesi"), L.T("Rozet yeri") }
                    .Max(s => TextRenderer.MeasureText(Theme.Upper(s), cap).Width) + 12);
            int toggleW = new[] { tAutoLobby, tAlerts, tWgc, tDenoise, tRadar, tAutoCover, tChat, tPost, tBot, tBreak, tMemory }.Max(s => VToggle.WidthFor(s, Font));
            ColW = Math.Min(560, Math.Max(Math.Max(420, LabelW + 316), toggleW + 4));

            var sub = new Label { Text = L.T("Kick için hafif Valorant yayın aracı · RTX NVENC"), ForeColor = Mut, AutoSize = true, Location = new Point(22, 56) };
            string keysText = L.T("F9  Sniper koruması      F10  Yayın özeti");
            var keys = new Label { Text = keysText, ForeColor = Mut, AutoSize = true };
            keys.Location = new Point(FormW - 20 - TextRenderer.MeasureText(keysText, Font).Width, 56);
            int top = 92;
            if (sub.Left + TextRenderer.MeasureText(sub.Text, Font).Width + 24 > keys.Left) { keys.Location = new Point(22, 76); top = 112; } // yan yana sığmıyor: alt alta
            Controls.AddRange(new Control[] { sub, keys });
            int lobbyW = Theme.ButtonWidth(L.T("MAÇ ODASI"), 32, 130), selfW = Theme.ButtonWidth(L.T("ANALİZİM"), 32, 110);
            var btnLobby = MakeButton(L.T("MAÇ ODASI"), new Rectangle(FormW - 20 - lobbyW, 16, lobbyW, 32), Field);
            btnLobby.Font = new Font(L.SansBold, 9.5f);
            btnLobby.Click += (s, e) => OpenLobby();
            var btnSelf = MakeButton(L.T("ANALİZİM"), new Rectangle(btnLobby.Left - 8 - selfW, 16, selfW, 32), Field);
            btnSelf.Font = new Font(L.SansBold, 9.5f);
            btnSelf.Click += (s, e) => AnalysisForm.Open();
            var logs = new LinkLabel { Text = L.T("Günlükler"), AutoSize = true, LinkColor = Mut, ActiveLinkColor = Txt };
            logs.Location = new Point(btnSelf.Left - 14 - TextRenderer.MeasureText(logs.Text, Font).Width, 24);
            logs.LinkClicked += (s, e) => { Directory.CreateDirectory(Diag.Dir); Process.Start("explorer.exe", Diag.Dir); };
            Controls.Add(logs);
            // Dil: değişince uygulama yeniden başlar (metinler açılışta bir kez kurulur).
            var cmbLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            cmbLang.Items.Add(L.T("Otomatik (Valorant dili)"));
            cmbLang.Items.AddRange(L.Languages.Select(l => (object)l.Name).ToArray());
            cmbLang.Width = Math.Min(220, Math.Max(140, ItemsWidth(cmbLang) + 30));
            cmbLang.SelectedIndex = Array.FindIndex(L.Languages, l => l.Code == cfg.Language) + 1; // bulunamazsa 0 = otomatik
            cmbLang.Location = new Point(logs.Left - 14 - cmbLang.Width, 21);
            StyleField(cmbLang); FitDropDown(cmbLang);
            cmbLang.SelectedIndexChanged += (s, e) => ChangeLanguage(cmbLang.SelectedIndex == 0 ? "auto" : L.Languages[cmbLang.SelectedIndex - 1].Code);
            Controls.Add(cmbLang);

            // ---- Sol sütun ----
            cx = 20;
            int y = Section("KICK", top);
            txtUrl = AddText("Stream URL", ref y, false);
            txtKey = AddText("Stream Key", ref y, true);
            var getKey = new LinkLabel { Text = L.T("Kick'ten URL ve key al"), AutoSize = true, LinkColor = Red, ActiveLinkColor = Red, Location = new Point(FX, y - 2) };
            getKey.LinkClicked += (s, e) => Process.Start("https://dashboard.kick.com/stream");
            Controls.Add(getKey); y += 22;

            y = Section(L.T("YAYIN BİLGİSİ"), y + 8);
            Caption(L.T("Kick hesabı"), y);
            int kickW = Math.Max(Theme.ButtonWidth(L.T("Bağlan"), 25, 92), Theme.ButtonWidth(L.T("Çıkış"), 25, 92));
            lblKick = new FitLabel { Text = L.T("Bağlı değil"), ForeColor = Mut, Location = new Point(FX, y + 3), Size = new Size(FW - kickW - 8, 20) };
            btnKick = new VButton { Text = L.T("Bağlan"), Bounds = new Rectangle(FX + FW - kickW, y - 1, kickW, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            btnKick.FlatAppearance.BorderSize = 0;
            btnKick.Click += (s, e) => KickConnectClicked();
            Controls.AddRange(new Control[] { lblKick, btnKick }); y += 34;
            txtTitle = AddText(L.T("Başlık"), ref y, false);
            txtTitle.MaxLength = 140;
            Caption(L.T("Kategori"), y);
            cmbCategory = new ComboBox { Location = new Point(FX, y), Width = FW, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat };
            StyleField(cmbCategory);
            cmbCategory.TextUpdate += (s, e) => SearchCategories();
            Controls.Add(cmbCategory); y += 34;
            txtTags = AddText(L.T("Etiketler"), ref y, false);
            var applyNow = new LinkLabel { Text = L.T("Kick'e şimdi uygula"), AutoSize = true, LinkColor = Red, ActiveLinkColor = Red, Location = new Point(FX, y - 2) };
            applyNow.LinkClicked += (s, e) => ApplyStreamInfo(true);
            Controls.Add(applyNow);
            y += 22;

            y = Section("VALORANT", y + 8);
            Caption("Riot ID", y);
            txtName = new TextBox { Location = new Point(FX, y), Width = FW - 190 };
            Controls.Add(new Label { Text = "#", ForeColor = Mut, AutoSize = true, Location = new Point(FX + FW - 183, y + 3) });
            txtTag = new TextBox { Location = new Point(FX + FW - 162, y), Width = 68 };
            cmbRegion = new ComboBox { Location = new Point(FX + FW - 86, y), Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            cmbRegion.Items.AddRange(new object[] { "EU / TR", "NA", "AP", "KR", "LATAM", "BR" });
            cmbRegion.SelectedIndex = 0;
            StyleField(txtName); StyleField(txtTag); StyleField(cmbRegion);
            Controls.AddRange(new Control[] { txtName, txtTag, cmbRegion }); y += 34;
            txtHdev = AddText("HenrikDev key", ref y, true);
            var link = new LinkLabel { Text = L.T("Ücretsiz anahtar al"), AutoSize = true, Location = new Point(FX, y - 2), LinkColor = Red, ActiveLinkColor = Red };
            link.LinkClicked += (s, e) => Process.Start("https://api.henrikdev.xyz/dashboard/");
            int valoX = FX + TextRenderer.MeasureText(link.Text, Font).Width + 12; // denetim forma eklenmeden yazı tipini almıyor: formunkiyle ölç
            lblValo = new FitLabel { Text = "", ForeColor = Mut, Location = new Point(valoX, y - 2), Size = new Size(FX + FW - valoX, 20) };
            Controls.AddRange(new Control[] { link, lblValo });
            y += 22;
            foreach (var t in new[] { txtName, txtTag, txtHdev }) t.Leave += (s, e) => StartTracker();
            cmbRegion.SelectedIndexChanged += (s, e) => StartTracker();

            y = Section(L.T("MAÇ ODASI"), y + 8);
            chkAutoLobby = new VToggle { Text = tAutoLobby, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkAutoLobby); y += 26;
            chkAlerts = new VToggle { Text = tAlerts, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkAlerts); y += 26;
            chkAutoLobby.CheckedChanged += (s, e) => SaveSettings();
            chkAlerts.CheckedChanged += (s, e) => { SaveSettings(); if (lobby != null && !lobby.IsDisposed) lobby.Alerts = chkAlerts.Checked; };
            chkMemory = new VToggle { Text = tMemory, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkMemory); y += 26;
            chkMemory.CheckedChanged += (s, e) => SaveSettings();
            int leftBottom = y;

            // ---- Sağ sütun ----
            cx = ColW + 60;
            var divider = new Panel { BackColor = Card, Location = new Point(ColW + 40, top + 4) };
            Controls.Add(divider);
            y = Section(L.T("GÖRÜNTÜ"), top);
            cmbQuality = AddCombo(L.T("Kalite"), ref y, Qualities.Select(q => q.Label).ToArray());
            cmbCapture = AddCombo(L.T("Yakalama"), ref y, new[] { L.T("Tam ekran (her şey görünür)"), L.T("Sadece Valorant (önerilen)"), L.T("Seçtiğim uygulama") });
            Caption(L.T("Uygulama"), y);
            lblApp = Controls[Controls.Count - 1];
            cmbApp = new ComboBox { Location = new Point(FX, y), Width = FW - 42, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            StyleField(cmbApp);
            btnApps = new VButton { Text = "↻", Bounds = new Rectangle(FX + FW - 36, y - 1, 36, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat };
            btnApps.FlatAppearance.BorderSize = 0;
            Controls.AddRange(new Control[] { cmbApp, btnApps }); y += 34;
            int belowApp = Controls.Count; // "Seçtiğim uygulama" değilse bu satır kapanır, altındakiler yukarı kayar
            chkWgc = new VToggle { Text = tWgc, ForeColor = Txt, AutoSize = true, Location = new Point(FX, y - 4) };
            Controls.Add(chkWgc); y += 26;
            chkWgc.CheckedChanged += (s, e) => SaveSettings();
            btnApps.Click += (s, e) => FillApps(apps.ElementAtOrDefault(cmbApp.SelectedIndex).Key ?? cfg.CaptureApp);
            cmbCapture.SelectedIndexChanged += (s, e) => ApplyCapture();
            cmbApp.SelectedIndexChanged += (s, e) => ApplyCapture();

            y = Section(L.T("SES"), y + 8);
            inputs = AudioDevices.ListInputs();
            cmbMic = AddCombo(L.T("Mikrofon"), ref y, inputs.Select(o => o.Value).ToArray());
            trkMic = AddSlider(ref y, out lblMic);
            trkMic.ValueChanged += (s, e) => { if (micAudio != null) micAudio.Volume = trkMic.Value / 100f; };
            chkDenoise = new VToggle { Text = tDenoise, ForeColor = Txt, AutoSize = true, Location = new Point(FX, y - 4) };
            Controls.Add(chkDenoise); y += 26;
            outputs = AudioDevices.ListOutputs();
            cmbOut = AddCombo(L.T("Oyun sesi"), ref y, outputs.Select(o => o.Value).ToArray());
            trkGame = AddSlider(ref y, out lblGame);
            trkGame.ValueChanged += (s, e) => { if (audio != null) audio.Volume = trkGame.Value / 100f; };

            y = Section(L.T("KATMANLAR"), y + 8);
            chkRadar = new VToggle { Text = tRadar, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkRadar); y += 26;
            chkAutoCover = new VToggle { Text = tAutoCover, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkAutoCover); y += 26;
            chkAutoCover.CheckedChanged += (s, e) => { SaveSettings(); ApplyLive(live?.Snap); };
            Caption(L.T("Rozet yeri"), y);
            int alignW = Theme.ButtonWidth(L.T("Minimap'e hizala"), 25, 120);
            lblRadarPos = new FitLabel { ForeColor = Txt, Location = new Point(FX, y + 3), Size = new Size(FW - alignW - 8, 20) };
            btnAlign = new VButton { Text = L.T("Minimap'e hizala"), Bounds = new Rectangle(FX + FW - alignW, y - 1, alignW, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            btnAlign.FlatAppearance.BorderSize = 0;
            btnAlign.Click += (s, e) => AlignRadar();
            Controls.AddRange(new Control[] { lblRadarPos, btnAlign }); y += 34;
            chkChat = new VToggle { Text = tChat, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            int chatX = cx + Math.Max(170, VToggle.WidthFor(tChat, Font) + 12);
            lblChat = new FitLabel { Text = "", ForeColor = Mut, Location = new Point(chatX, y - 1), Size = new Size(cx + ColW - chatX, 20) };
            Controls.AddRange(new Control[] { chkChat, lblChat }); y += 26;
            chkPost = new VToggle { Text = tPost, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkPost); y += 26;
            chkBot = new VToggle { Text = tBot, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkBot); y += 26;
            chkBreak = new VToggle { Text = tBreak, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            Controls.Add(chkBreak); y += 26;
            chkBreak.CheckedChanged += (s, e) => { SaveSettings(); ApplyLive(live?.Snap); };
            appShift = Controls.Cast<Control>().Skip(belowApp).ToArray();
            divider.Size = new Size(1, Math.Max(leftBottom, y) - top - 4);
            chkPost.CheckedChanged += (s, e) => SaveSettings();
            chkBot.CheckedChanged += (s, e) => SaveSettings();
            chkRadar.CheckedChanged += (s, e) => { btnAlign.Enabled = chkRadar.Checked; overlay.RankPanel = !chkRadar.Checked; SaveSettings(); };
            chkChat.CheckedChanged += (s, e) => { SaveSettings(); UpdateChatHint(); };

            // ---- Alt şerit: butonlar + durum ----
            y = Math.Max(leftBottom, y) + 10;
            Controls.Add(new Panel { BackColor = Card, Location = new Point(20, y), Size = new Size(FormW - 40, 1) });
            y += 16;
            int testW = Math.Min(ColW / 2, Theme.ButtonWidth(L.T("TEST KAYDI (15 sn)"), 50, 170)); // canlı düğmesi geniş kalsın; sığmayan yazı küçülür
            btnTest = MakeButton(L.T("TEST KAYDI (15 sn)"), new Rectangle(20, y, testW, 50), Field);
            btnLive = MakeButton(L.T("YAYINI BAŞLAT"), new Rectangle(30 + testW, y, ColW + 10 - testW, 50), Red);
            btnTest.Click += (s, e) => { if (mode == Mode.Idle) Begin(Mode.Test); };
            btnLive.Click += (s, e) => { if (mode == Mode.Idle) Begin(Mode.Live); else if (mode == Mode.Live) StopLive(); };
            lblStatus = new FitLabel { Text = L.T("● HAZIR"), Font = new Font(L.SansBold, 12f), ForeColor = Mut, Location = new Point(cx, y + 2), Size = new Size(ColW, 26) };
            lblStats = new Label { Text = "", ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx + 2, y + 29) };
            Controls.AddRange(new Control[] { lblStatus, lblStats });
            int bottom = y + 50 + 20;
            // Exclusive tam ekranda her alt-tab ekran modunu değiştirir; ekran yakalama kopar ve yayında takılma olur.
            if (Diag.ValorantFullscreenMode() == 0)
            {
                var warn = new Label
                {
                    Text = L.T("⚠  Valorant \"Tam ekran\" modunda: alt-tab'da yayın takılır.  Valorant → Ayarlar → Video → Genel → Görüntü Modu: \"Pencereli Tam Ekran\" seç."),
                    ForeColor = ColorTranslator.FromHtml("#ffb547"), AutoSize = true, MaximumSize = new Size(FormW - 40, 0), Location = new Point(20, y + 60),
                };
                Controls.Add(warn);
                bottom += warn.GetPreferredSize(new Size(FormW - 40, 0)).Height + 6; // uzun dillerde iki satıra iner
            }
            // Anahtar yazıları sütunu aşmasın (çok uzun bir çeviri en kötü "…" ile biter).
            foreach (var t in Controls.OfType<VToggle>())
                t.MaximumSize = new Size((t.Left < ColW + 40 ? 20 + ColW : 2 * ColW + 60) - t.Left, 0);
            ClientSize = new Size(FormW, bottom);
            ThemeFields();
        }

        // ---------- Görünüm (Theme.cs) ----------

        // Metin kutularının çerçeveleri: kutunun kendi kenarlığı kaldırılır, form koyu alan + ince çerçeve çizer.
        readonly List<KeyValuePair<Rectangle, Control>> frames = new List<KeyValuePair<Rectangle, Control>>();

        void ThemeFields()
        {
            foreach (var t in Controls.OfType<TextBox>().ToList())
            {
                var r = t.Bounds;
                t.BorderStyle = BorderStyle.None;
                frames.Add(new KeyValuePair<Rectangle, Control>(new Rectangle(r.X, r.Y, r.Width, 26), t));
                t.Location = new Point(r.X + 8, r.Y + (26 - t.Height) / 2);
                t.Width = r.Width - 16;
                t.GotFocus += (s, e) => Invalidate();
                t.LostFocus += (s, e) => Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, 0, 0, ClientSize.Width, 3);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var f = Theme.Display(36))
            {
                float w = g.MeasureString("VALO", f).Width;
                using (var b = new SolidBrush(Txt)) g.DrawString("VALO", f, b, 16, 10);
                using (var b = new SolidBrush(Red)) g.DrawString("LIVE", f, b, 16 + w - 10, 10);
            }
            foreach (var fr in frames)
            {
                if (!fr.Value.Visible) continue;
                using (var b = new SolidBrush(Field)) g.FillRectangle(b, fr.Key);
                using (var p = new Pen(fr.Value.Focused ? Red : Theme.Line)) g.DrawRectangle(p, fr.Key.X, fr.Key.Y, fr.Key.Width - 1, fr.Key.Height - 1);
            }
        }

        /// <summary>Açılır listeler: koyu öğeler, seçili öğe kırmızımsı.</summary>
        static void DarkCombo(ComboBox c)
        {
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = 20;
            ComboSkin.Attach(c);
            c.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(sel ? ColorTranslator.FromHtml("#5b2430") : Field)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                    c.Enabled ? Txt : Mut, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        // Yerleşim ölçüleri: iki eşit sütun; etiketler solda, alanlar FX'ten başlar. Genişlikler dile göre BuildUi'da büyür.
        int ColW = 420, LabelW = 104;
        int FormW => ColW * 2 + 80;
        int cx = 20;                       // geçerli sütunun sol kenarı
        int FX => cx + LabelW;             // alan başlangıcı
        int FW => ColW - LabelW;           // alan genişliği

        int Section(string text, int y)
        {
            Controls.Add(new Panel { BackColor = Red, Location = new Point(cx, y + 4), Size = new Size(4, 13) });
            Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Txt, Font = Theme.Display(16), UseCompatibleTextRendering = true, AutoSize = true, Location = new Point(cx + 11, y) });
            Controls.Add(new Panel { BackColor = Card, Location = new Point(cx, y + 22), Size = new Size(ColW, 1) });
            return y + 32;
        }

        void Caption(string text, int y) => Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Mut, Font = new Font(L.SansBold, 8.25f), AutoSize = true, Location = new Point(cx, y + 5) });

        void StyleField(Control c) { c.BackColor = Field; c.ForeColor = Txt; if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle; if (c is ComboBox cb) DarkCombo(cb); }

        TextBox AddText(string label, ref int y, bool secret)
        {
            Caption(label, y);
            var t = new TextBox { Location = new Point(FX, y), Width = FW, UseSystemPasswordChar = secret };
            StyleField(t); Controls.Add(t); y += 34;
            return t;
        }

        ComboBox AddCombo(string label, ref int y, string[] items)
        {
            Caption(label, y);
            var c = new ComboBox { Location = new Point(FX, y), Width = FW, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            StyleField(c); c.Items.AddRange(items); if (items.Length > 0) c.SelectedIndex = 0;
            FitDropDown(c);
            Controls.Add(c); y += 34;
            return c;
        }

        int ItemsWidth(ComboBox c) => c.Items.Cast<object>().Select(o => TextRenderer.MeasureText(c.GetItemText(o), Font).Width).DefaultIfEmpty(0).Max();

        /// <summary>Açılan liste kutudan dar kalmasın: uzun öğeler (cihaz adları, çeviriler) tam okunur.</summary>
        void FitDropDown(ComboBox c) => c.DropDownWidth = Math.Max(c.Width, Math.Min(640, ItemsWidth(c) + 12));

        /// <summary>Cihaz seçiminin altında, kendi satırında ses seviyesi kaydırıcısı.</summary>
        VSlider AddSlider(ref int y, out Label value)
        {
            y -= 4;
            var t = new VSlider { Location = new Point(FX, y), Width = FW - 48, Height = 26, Minimum = 0, Maximum = 200 };
            var v = new Label { ForeColor = Txt, AutoSize = true, Location = new Point(FX + FW - 40, y + 3) };
            t.ValueChanged += (s, e) => v.Text = L.Pct(t.Value);
            Controls.AddRange(new Control[] { t, v });
            value = v; y += 30;
            return t;
        }

        Button MakeButton(string text, Rectangle r, Color color)
        {
            var b = new VButton { Text = text, Bounds = r, BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 11f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        // ---------- Ayarlar ----------

        bool loading;

        void LoadSettings()
        {
            // Yükleme sırasında olaylar (bölge değişti vb.) ayarları kaydetmesin: alanların yarısı henüz boş.
            loading = true;
            try { LoadSettingsCore(); } finally { loading = false; }
        }

        void LoadSettingsCore()
        {
            txtUrl.Text = cfg.KickUrl;
            txtKey.Text = Settings.Unprotect(cfg.KickKeyEnc);
            cmbQuality.SelectedIndex = Math.Max(0, Math.Min(Qualities.Length - 1, cfg.Quality));
            FillApps(cfg.CaptureApp);
            cmbCapture.SelectedIndex = Math.Max(0, Math.Min(2, cfg.Capture));
            ApplyCapture();
            // cfg.Mic null = hiç seçilmedi (ilk açılış): HyperX varsa onu seç. "" = bilerek mikrofonsuz.
            int mic = cfg.Mic == null ? -1 : inputs.FindIndex(o => o.Key == cfg.Mic);
            if (mic < 0) mic = inputs.FindIndex(o => o.Value.IndexOf("HyperX", StringComparison.OrdinalIgnoreCase) >= 0);
            cmbMic.SelectedIndex = Math.Max(0, mic);
            trkMic.Value = Math.Max(0, Math.Min(200, cfg.MicVolume)); lblMic.Text = L.Pct(trkMic.Value);
            chkDenoise.Checked = cfg.Denoise;
            cmbOut.SelectedIndex = Math.Max(0, outputs.FindIndex(o => o.Key == cfg.OutputId));
            trkGame.Value = Math.Max(0, Math.Min(200, cfg.GameVolume)); lblGame.Text = L.Pct(trkGame.Value);
            txtName.Text = cfg.RiotName; txtTag.Text = cfg.RiotTag;
            cmbRegion.SelectedIndex = Math.Max(0, Array.IndexOf(Regions, cfg.Region));
            txtHdev.Text = Settings.Unprotect(cfg.HdevKeyEnc);
            txtTitle.Text = cfg.StreamTitle;
            cmbCategory.Text = cfg.CategoryName;
            txtTags.Text = cfg.Tags;
            chkRadar.Checked = cfg.MapCover;
            btnAlign.Enabled = cfg.MapCover;
            ShowRadarPos();
            overlay.RankPanel = !cfg.MapCover;
            chkChat.Checked = cfg.ChatBox;
            chkPost.Checked = cfg.PostMatchCard;
            chkBot.Checked = cfg.ChatBot;
            chkWgc.Checked = cfg.WindowCapture;
            chkBreak.Checked = cfg.BreakScreen;
            chkAutoLobby.Checked = cfg.AutoLobby;
            chkAlerts.Checked = cfg.LobbyAlerts;
            chkAutoCover.Checked = cfg.AutoCover;
            chkMemory.Checked = cfg.EnemyMemory;
            UpdateKickStatus();
        }

        void SaveSettings()
        {
            if (loading) return;
            cfg.KickUrl = txtUrl.Text.Trim();
            cfg.KickKeyEnc = Settings.Protect(txtKey.Text.Trim());
            cfg.Quality = cmbQuality.SelectedIndex;
            cfg.Capture = cmbCapture.SelectedIndex;
            cfg.CaptureApp = apps.ElementAtOrDefault(cmbApp.SelectedIndex).Key ?? cfg.CaptureApp;
            cfg.Mic = inputs[Math.Max(0, cmbMic.SelectedIndex)].Key;
            cfg.MicVolume = trkMic.Value;
            cfg.Denoise = chkDenoise.Checked;
            cfg.OutputId = outputs[Math.Max(0, cmbOut.SelectedIndex)].Key;
            cfg.GameVolume = trkGame.Value;
            cfg.RiotName = txtName.Text.Trim(); cfg.RiotTag = txtTag.Text.Trim().TrimStart('#');
            cfg.Region = Regions[Math.Max(0, cmbRegion.SelectedIndex)];
            cfg.HdevKeyEnc = Settings.Protect(txtHdev.Text.Trim());
            cfg.StreamTitle = txtTitle.Text.Trim();
            if (cmbCategory.SelectedItem is KickApi.Category cat) { cfg.CategoryId = cat.Id; cfg.CategoryName = cat.Name; }
            else if (!string.Equals(cmbCategory.Text.Trim(), cfg.CategoryName, StringComparison.OrdinalIgnoreCase)) { cfg.CategoryName = cmbCategory.Text.Trim(); cfg.CategoryId = 0; }
            cfg.Tags = txtTags.Text.Trim();
            cfg.MapCover = chkRadar.Checked;
            cfg.ChatBox = chkChat.Checked;
            if (chkPost != null) { cfg.PostMatchCard = chkPost.Checked; cfg.ChatBot = chkBot.Checked; }
            if (chkWgc != null) { cfg.WindowCapture = chkWgc.Checked; cfg.BreakScreen = chkBreak.Checked; cfg.AutoLobby = chkAutoLobby.Checked; cfg.LobbyAlerts = chkAlerts.Checked; cfg.AutoCover = chkAutoCover.Checked; cfg.EnemyMemory = chkMemory.Checked; }
            cfg.Save();
        }

        // ---------- Kick hesabı: başlık, kategori, etiketler ----------

        void UpdateKickStatus()
        {
            bool on = kick.Connected;
            lblKick.Text = on ? "✓ " + (cfg.KickSlug != "" ? "kick.com/" + cfg.KickSlug : L.T("bağlı")) : L.T("Bağlı değil");
            lblKick.ForeColor = on ? Green : Mut;
            btnKick.Text = on ? L.T("Çıkış") : L.T("Bağlan");
            UpdateChatHint();
        }

        void UpdateChatHint()
        {
            if (lblChat == null) return;
            if (!chkChat.Checked) lblChat.Text = "";
            else if (cfg.KickSlug == "") { lblChat.Text = L.T("Kick hesabını bağla"); lblChat.ForeColor = Mut; }
            else if (kickChat == null) { lblChat.Text = L.T("yayın başlayınca bağlanır"); lblChat.ForeColor = Mut; }
        }

        void KickConnectClicked()
        {
            if (kick.Connected)
            {
                kick.Logout();
                UpdateKickStatus();
                return;
            }
            if (!kick.HasApp && !KickAppDialog.Ask(this, cfg)) return;
            btnKick.Enabled = false;
            lblKick.Text = L.T("Tarayıcıda Kick onayı bekleniyor…"); lblKick.ForeColor = Mut;
            Task.Run(() => kick.Login()).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                btnKick.Enabled = true;
                if (t.IsFaulted)
                {
                    UpdateKickStatus();
                    MessageBox.Show(this, L.T("Kick'e bağlanılamadı:\n") + t.Exception.GetBaseException().Message, "ValoLive");
                    return;
                }
                var ch = t.Result;
                // Yeni bağlanınca kanalın şu anki bilgileri boş alanlara gelir; yazdıkların korunur.
                if (txtTitle.Text.Trim() == "") txtTitle.Text = ch.Title;
                if (ch.CategoryId > 0 && (cfg.CategoryId == 0 || cmbCategory.Text.Trim() == ""))
                {
                    cmbCategory.Items.Clear();
                    var cat = new KickApi.Category { Id = ch.CategoryId, Name = ch.CategoryName };
                    cmbCategory.Items.Add(cat); cmbCategory.SelectedItem = cat;
                }
                if (txtTags.Text.Trim() == "") txtTags.Text = string.Join(", ", ch.Tags);
                SaveSettings();
                UpdateKickStatus();
            })));
        }

        /// <summary>Kategori kutusuna yazdıkça (yarım saniye durunca) Kick'te arar, sonuçları açılır listeye koyar.</summary>
        void SearchCategories()
        {
            categorySearch?.Cancel();
            string q = cmbCategory.Text.Trim();
            if (q.Length < 3 || !kick.Connected) return;
            var cts = categorySearch = new System.Threading.CancellationTokenSource();
            Task.Delay(500, cts.Token).ContinueWith(_ => kick.SearchCategories(q), cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled || t.IsFaulted || cts.IsCancellationRequested) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (cts.IsCancellationRequested || t.Result.Count == 0) return;
                        string text = cmbCategory.Text;
                        int caret = cmbCategory.SelectionStart;
                        cmbCategory.Items.Clear();
                        cmbCategory.Items.AddRange(t.Result.Cast<object>().ToArray());
                        cmbCategory.DroppedDown = true;
                        cmbCategory.Text = text; cmbCategory.SelectionStart = caret; cmbCategory.SelectionLength = 0;
                        Cursor = Cursors.Default; // DroppedDown imleci gizliyor
                    }));
                }, TaskScheduler.Default);
        }

        static string[] ParseTags(string s) =>
            s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();

        /// <summary>Başlık/kategori/etiketleri Kick'e gönderir (arka planda; yayını bekletmez).</summary>
        void ApplyStreamInfo(bool interactive)
        {
            SaveSettings();
            if (!kick.Connected)
            {
                if (interactive) MessageBox.Show(this, L.T("Önce Kick hesabını bağla (Yayın Bilgisi → Bağlan)."), "ValoLive");
                return;
            }
            string title = cfg.StreamTitle, catName = cfg.CategoryName, tags = cfg.Tags;
            int catId = cfg.CategoryId;
            Task.Run(() =>
            {
                if (catId == 0 && catName != "")
                {
                    // Elle yazılmış kategori: tam eşleşeni bul (yoksa ilk sonuç).
                    var found = kick.SearchCategories(catName);
                    var hit = found.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
                    if (hit == null) throw new Exception(L.F("\"{0}\" adında kategori bulunamadı.", catName));
                    catId = hit.Id; catName = hit.Name;
                }
                kick.UpdateChannel(title, catId, ParseTags(tags));
                return new KickApi.Category { Id = catId, Name = catName };
            }).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                if (t.IsFaulted)
                {
                    string msg = t.Exception.GetBaseException().Message;
                    Diag.Event("KICK", "yayın bilgisi uygulanamadı: " + msg);
                    if (interactive) MessageBox.Show(this, L.T("Yayın bilgisi uygulanamadı:\n") + msg, "ValoLive");
                    else lblStats.Text = L.T("Başlık uygulanamadı: ") + msg;
                    UpdateKickStatus();
                    return;
                }
                cfg.CategoryId = t.Result.Id; cfg.CategoryName = t.Result.Name; cfg.Save();
                Diag.Event("KICK", $"yayın bilgisi uygulandı · kategori {t.Result.Name}");
                if (interactive) MessageBox.Show(this, L.T("Başlık, kategori ve etiketler Kick'e uygulandı ✓"), "ValoLive");
            })));
        }

        // ---------- Yayın katmanları: radar rozeti, chat kutusu ----------

        List<Layer> StartLayers()
        {
            var list = new List<Layer>();
            float sc = screenH / 1080f;
            if (cfg.MapCover)
            {
                var r = RadarRect();
                radar = new RadarWidget(r.X, r.Y, r.Width, sc)
                {
                    PlayerName = cfg.RiotName,
                    Hidden = () => overlay.Cover || overlay.Summary || overlay.Privacy || overlay.Break,
                    Live = () => live?.Snap,
                };
                radar.SetState(lastState);
                list.Add(radar);
            }
            if (cfg.PostMatchCard)
            {
                postLayer = new PostMatchLayer((int)(screenW - 470 * sc), (int)(230 * sc), sc) { Hidden = () => overlay.Cover || overlay.Privacy };
                list.Add(postLayer);
            }
            bool useBot = cfg.ChatBot && kick.Connected;
            if ((cfg.ChatBox || useBot) && cfg.KickSlug != "")
            {
                if (cfg.ChatBox) { chatLayer = new ChatLayer(sc); list.Add(chatLayer); }
                var layer = chatLayer;
                kickChat = new KickChat(cfg.KickSlug);
                if (layer != null) kickChat.Message += m => layer.Add(m);
                if (useBot) { bot = new ChatBot(kick, () => lastState, () => live); var b = bot; kickChat.Message += m => b.Handle(m); }
                kickChat.Status += s => BeginInvoke((Action)(() =>
                {
                    if (lblChat == null) return;
                    lblChat.Text = "chat: " + s;
                    lblChat.ForeColor = s == L.T("bağlı") ? Green : Mut;
                }));
            }
            return list;
        }

        /// <summary>Rozetin ekrandaki yeri (gerçek piksel); ekranın içine sığdırılır.</summary>
        Rectangle RadarRect()
        {
            int d = cfg.MapCoverD > 0 ? cfg.MapCoverD : (int)(360 * screenH / 1080f);
            d = Math.Max(80, Math.Min(Math.Min(screenW, screenH), d));
            int x = Math.Max(0, Math.Min(screenW - d, cfg.MapCoverX)), y = Math.Max(0, Math.Min(screenH - d, cfg.MapCoverY));
            return new Rectangle(x, y, d, d);
        }

        void ShowRadarPos()
        {
            var r = RadarRect();
            string full = L.F("çap {0} px · konum {1}, {2}", r.Width, r.X, r.Y);
            // Düğme yanında yer dar: uzun dillerde kısa biçim (FitLabel en çok %25 küçültüyor)
            lblRadarPos.Text = TextRenderer.MeasureText(full, Font).Width * 0.78 <= lblRadarPos.Width ? full : $"Ø {r.Width} px · {r.X}, {r.Y}";
        }

        void AlignRadar()
        {
            using (var f = new RadarAlignForm(RadarRect(), screenW, screenH))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                cfg.MapCoverX = f.Result.X; cfg.MapCoverY = f.Result.Y; cfg.MapCoverD = f.Result.Width;
                cfg.Save();
                ShowRadarPos();
            }
        }

        void StopLayers()
        {
            kickChat?.Dispose(); kickChat = null;
            radar?.Dispose(); radar = null;
            chatLayer?.Dispose(); chatLayer = null;
            postLayer?.Dispose(); postLayer = null;
            bot = null;
            UpdateChatHint();
        }

        // ---------- Yakalama ----------

        /// <summary>Açık uygulamaları listeler; kayıtlı uygulama şu an kapalıysa da listede tutulur.</summary>
        void FillApps(string keep)
        {
            apps = CaptureGuard.ListApps();
            if (!string.IsNullOrEmpty(keep) && !apps.Any(a => string.Equals(a.Key, keep, StringComparison.OrdinalIgnoreCase)))
                apps.Insert(0, new KeyValuePair<string, string>(keep, keep + L.T(" (şu an kapalı)")));
            cmbApp.Items.Clear();
            cmbApp.Items.AddRange(apps.Select(a => (object)a.Value).ToArray());
            int i = apps.FindIndex(a => string.Equals(a.Key, keep, StringComparison.OrdinalIgnoreCase));
            if (cmbApp.Items.Count > 0) cmbApp.SelectedIndex = Math.Max(0, i);
            FitDropDown(cmbApp);
        }

        void ApplyCapture()
        {
            var m = (CaptureMode)Math.Max(0, cmbCapture.SelectedIndex);
            bool app = m == CaptureMode.App;
            cmbApp.Visible = btnApps.Visible = lblApp.Visible = app;
            if (appShift != null && app != appRow)
            {
                SuspendLayout();
                foreach (var c in appShift) c.Top += app ? 34 : -34;
                ResumeLayout();
                appRow = app;
            }
            guard.AppName = apps.ElementAtOrDefault(cmbApp.SelectedIndex).Key ?? "";
            guard.Mode = app && guard.AppName == "" ? CaptureMode.FullScreen : m;
            overlay.PrivacyApp = guard.TargetName;
            if (chkWgc != null) chkWgc.Enabled = guard.Mode == CaptureMode.Valorant && mode == Mode.Idle; // pencere yakalama yalnızca "Sadece Valorant" modunda
        }

        /// <summary>
        /// Riot durumu değişti (UI): menüde bekleme ekranı + sıra sayacı; menüden ajan seçimine/maça geçişte Maç Odası
        /// kendiliğinden açılır (yalnızca geçiş anında: elle kapatılırsa aynı maçta yeniden açılmaz).
        /// </summary>
        void ApplyLive(LiveSnap s)
        {
            if (s == null) return;
            overlay.Break = cfg.BreakScreen && s.State == "MENUS";
            overlay.QueueSince = s.Queueing && s.QueueSince != default(DateTime) ? s.QueueSince : (DateTime?)null;
            bool entering = (liveState == "" || liveState == "MENUS") && (s.State == "PREGAME" || s.State == "INGAME");
            // Otomatik sniper perdesi: ajan seçiminde iner, maç başlayınca (ya da seçim bozulunca) kalkar.
            // Yalnızca kendi indirdiği perdeyi kaldırır; F9 ile elle açılan perdeye dokunmaz.
            if (cfg.AutoCover && s.State == "PREGAME" && !overlay.Cover) { overlay.Cover = autoCovered = true; UpdateTitle(); }
            else if (autoCovered && (s.State != "PREGAME" || !cfg.AutoCover)) { overlay.Cover = autoCovered = false; UpdateTitle(); }
            liveState = s.State;
            if (entering && cfg.AutoLobby) OpenLobby(true);
        }

        // ---------- Rakip hafızası (deneysel) ----------

        /// <summary>
        /// Maçtayken minimap okuyucusunu çalıştırır, raund değişince (skor toplamı) hafızayı sıfırlar ve şeridi günceller.
        /// Rakip ajanlar maç başında Riot istemcisinden bir kez okunur; tanıma yalnızca onlar arasında yapılır.
        /// </summary>
        void TickMemory()
        {
            var s = live?.Snap;
            if (!cfg.EnemyMemory || s == null || !s.InGame)
            {
                if (memory.Active) { memory.Stop(); Diag.Event("HAFIZA", "durdu"); }
                memScore = -1;
                memPanel?.Hide();
                return;
            }
            if (!memory.Active && !memStarting && DateTime.Now >= memRetry) { memRetry = DateTime.Now.AddSeconds(15); StartMemory(); }
            int score = s.Ally + s.Enemy;
            if (score != memScore) { if (memScore >= 0) memory.NewRound(); memScore = score; }

            var rows = memory.Snapshot();
            if (memory.GameFront && rows.Count > 0 && !memory.MapRect.IsEmpty)
            {
                if (memPanel == null || memPanel.IsDisposed) memPanel = new MemoryPanel(Screen.PrimaryScreen.Bounds.Width / (float)screenW);
                memPanel.Set(rows, memory.MapRect);
                if (!memPanel.Visible) memPanel.Show();
            }
            else memPanel?.Hide();
        }

        void StartMemory()
        {
            memStarting = true;
            Task.Run(() =>
            {
                try
                {
                    var st = new RiotLocal().Read();
                    var enemies = st.Players.Where(p => !p.Ally).Select(p => p.AgentId).ToList();
                    if (st.Phase != "ingame" || enemies.Count == 0) { memStarting = false; return; }
                    BeginInvoke((Action)(() =>
                    {
                        memStarting = false;
                        if (cfg.EnemyMemory && live.Snap.InGame) { memory.Start(st.MapUrl, enemies); memScore = -1; }
                    }));
                }
                catch (Exception e) { Diag.Event("HAFIZA", "maç okunamadı: " + e.Message); memStarting = false; }
            });
        }

        // ---------- Maç odası ----------

        LobbyForm lobby;

        /// <summary>auto: maç bulununca kendiliğinden — odak çalmaz; tek ekranda simge durumunda, ikinci ekran varsa orada açılır.</summary>
        void OpenLobby(bool auto = false)
        {
            if (lobby != null && !lobby.IsDisposed) { if (!auto) { if (lobby.WindowState == FormWindowState.Minimized) lobby.WindowState = FormWindowState.Normal; lobby.Activate(); } return; }
            SaveSettings();
            // HenrikDev yalnızca yedek: Riot istemcisi her şeyi veriyor, anahtar yoksa da açılır.
            lobby = new LobbyForm(txtHdev.Text.Trim(), cfg.Region) { Alerts = cfg.LobbyAlerts, Quiet = auto };
            if (auto)
            {
                var second = Screen.AllScreens.FirstOrDefault(sc => !sc.Primary);
                if (second != null)
                {
                    lobby.StartPosition = FormStartPosition.Manual;
                    var wa = second.WorkingArea;
                    lobby.Location = new Point(wa.Left + Math.Max(0, (wa.Width - lobby.Width) / 2), wa.Top + Math.Max(0, (wa.Height - lobby.Height) / 2));
                }
                else lobby.WindowState = FormWindowState.Minimized;
            }
            lobby.Show();
        }

        // ---------- Valorant ----------

        string trackerKey;

        void StartTracker()
        {
            if (loading) return;
            SaveSettings();
            string hdev = txtHdev.Text.Trim();
            string id = $"{cfg.RiotName}#{cfg.RiotTag}@{cfg.Region}:{hdev}";
            if (id == trackerKey) return;
            trackerKey = id;
            tracker?.Dispose();
            tracker = null;
            overlay.SetState(null);
            lastState = null; radar?.SetState(null);
            overlay.PlayerName = cfg.RiotName;
            if (cfg.RiotName == "" || cfg.RiotTag == "" || hdev == "")
            {
                lblValo.Text = L.T("Riot ID + anahtar gerekli");
                return;
            }
            var t = new ValorantTracker(cfg.RiotName, cfg.RiotTag, cfg.Region, hdev);
            t.StatusChanged += s => BeginInvoke((Action)(() => { lblValo.Text = s; lblValo.ForeColor = s.StartsWith("✓") ? Green : Mut; }));
            t.Updated += st => { overlay.SetState(st); lastState = st; radar?.SetState(st); };
            t.RrChanged += d => { overlay.PopRr(d); radar?.Pop(d); };
            t.NewMatch += m =>
            {
                var (title, color) = m.Result == "W" ? (L.T("ZAFER"), ColorTranslator.FromHtml("#3ddc97"))
                                   : m.Result == "L" ? (L.T("YENİLGİ"), ColorTranslator.FromHtml("#ff5a6e"))
                                   : (L.T("BERABERE"), ColorTranslator.FromHtml("#9aa3b2"));
                overlay.ShowBanner(title, $"{m.Agent} · {m.Map} {m.Score} · {m.Kda} · HS {L.Pct(m.Hs)}", color, OverlayRenderer.AgentIcon(m.AgentId));
            };
            t.RankUp += st => overlay.ShowBanner(L.T("RANK ATLADIN!"), st.TierName.ToUpper(L.Culture), Tiers.Color(st.TierId), OverlayRenderer.TierIcon(st.TierId));
            tracker = t;
            t.Start();
        }

        // ---------- Yayın ----------

        void Begin(Mode m)
        {
            SaveSettings();
            if (m == Mode.Live && (cfg.KickUrl == "" || txtKey.Text.Trim() == ""))
            {
                MessageBox.Show(this, L.T("Kick Stream URL ve Stream Key gerekli.\nKick → Creator Dashboard → Settings bölümünden kopyalayabilirsin."), "ValoLive");
                return;
            }
            var q = Qualities[cfg.Quality];
            var o = new StreamOptions
            {
                Width = q.W, Height = q.H, BitrateK = q.Kbps,
                ScreenWidth = screenW, ScreenHeight = screenH,
                Denoise = cfg.Denoise,
                WindowCapture = cfg.WindowCapture && guard.Mode == CaptureMode.Valorant,
                Overlay = overlay,
            };
            if (m == Mode.Live)
            {
                o.Output = IngestUrl(cfg.KickUrl) + "/" + txtKey.Text.Trim();
            }
            else
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ValoLive");
                Directory.CreateDirectory(dir);
                o.ToFile = true; o.DurationSec = 15;
                o.Output = Path.Combine(dir, $"test_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                overlay.ShowBanner(L.T("ZAFER"), "Test · Jett · Ascent 13-9 · 24/12/5 · HS " + L.Pct(31), ColorTranslator.FromHtml("#3ddc97"), OverlayRenderer.AgentIcon("add6443a-41bd-e414-f6ad-e58d267f4e95"));
            }
            o.Layers = StartLayers();
            if (m == Mode.Live && kick.Connected) ApplyStreamInfo(false);
            lastOptions = o;
            mode = m;
            reconnects = 0;
            quickDrops = 0;
            everLive = false;
            liveSince = DateTime.Now;
            SetInputsEnabled(false);
            int vmode = Diag.ValorantFullscreenMode();
            Diag.Begin($"{(m == Mode.Live ? "CANLI YAYIN" : "TEST KAYDI")} · ValoLive {File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm} · " +
                       $"kalite {q.W}x{q.H} {q.Kbps} kbps · ekran {screenW}x{screenH} · yakalama: {cmbCapture.Text}" +
                       $"{(guard.Mode == CaptureMode.App ? " (" + guard.AppName + ")" : "")} · mikrofon: {(string.IsNullOrEmpty(cfg.Mic) ? "yok" : cmbMic.Text)} · " +
                       $"Valorant görüntü modu: {Diag.ModeName(vmode)}{(vmode == 0 ? " (alt-tab'da ekran modu değişir → yakalama kopar)" : "")}");
            if (!Launch()) { mode = Mode.Idle; StopLayers(); SetInputsEnabled(true); Diag.End("başlatılamadı"); return; }
            btnLive.Text = m == Mode.Live ? L.T("YAYINI BİTİR") : L.T("TEST KAYDEDİLİYOR…");
            btnLive.BackColor = m == Mode.Live ? Field : Red;
            SetStatus(m == Mode.Live ? L.T("● BAĞLANIYOR…") : L.T("● TEST KAYDI"), m == Mode.Live ? Mut : Red);
            if (m == Mode.Test)
            {
                var t = new Timer { Interval = o.DurationSec * 1000 };
                t.Tick += (s, e) => { t.Dispose(); if (mode == Mode.Test) Task.Run(() => streamer.Stop()); };
                t.Start();
            }
        }

        /// <summary>Kick panelindeki URL'de uygulama yolu yok; o olmadan anahtar "app" sanılıp yayın reddediliyor.</summary>
        static string IngestUrl(string url)
        {
            url = url.Trim().TrimEnd('/');
            int host = url.IndexOf("://", StringComparison.Ordinal);
            return host >= 0 && url.IndexOf('/', host + 3) < 0 ? url + "/app" : url;
        }

        bool Launch()
        {
            try
            {
                DisposeAudio();
                audio = new AudioSource(true) { Volume = trkGame.Value / 100f };
                audio.Start(cfg.OutputId);
                if (!string.IsNullOrEmpty(cfg.Mic))
                {
                    micAudio = new AudioSource(false) { Volume = trkMic.Value / 100f };
                    micAudio.Start(cfg.Mic);
                }
                lastOptions.Masks = guard.FilterChain;
                connectAt = DateTime.Now;
                streamer.Start(lastOptions, audio, micAudio);
                return true;
            }
            catch (Exception e)
            {
                DisposeAudio();
                SetStatus(L.T("● BAŞLATILAMADI"), Red);
                MessageBox.Show(this, e.Message, "ValoLive");
                return false;
            }
        }

        void StopLive()
        {
            mode = Mode.Idle;
            SetStatus(L.T("● DURDURULUYOR…"), Mut);
            btnLive.Enabled = false;
            Task.Run(() => streamer.Stop()).ContinueWith(_ => BeginInvoke((Action)(() => btnLive.Enabled = true)));
        }

        void OnStreamExit(bool unexpected, string err)
        {
            // Buraya yalnızca yayıncı süreç (Kick bağlantısı) kapanınca gelinir; ekran yakalama kopuşlarını
            // Streamer kendi içinde toparlar. Beklenmedik kapanış = bağlantı koptu → yeniden bağlan.
            bool rejected = mode == Mode.Live && !everLive && reconnects >= 2;
            if (unexpected && !rejected && reconnects < 20 && mode == Mode.Live)
            {
                reconnects++;
                Diag.Event("KICK", $"yeniden bağlanılıyor (deneme {reconnects}) · {Diag.Explain(err)}");
                // Bağlandıktan sonra 60 sn dolmadan tekrar tekrar kopuyorsa hat (ya da Kick) bu bitrate'i
                // kaldırmıyor: her iki hızlı kopmada 1000 kbps düş (en az 3500).
                quickDrops = (DateTime.Now - connectAt).TotalSeconds < 60 ? quickDrops + 1 : 0;
                if (quickDrops >= 2 && lastOptions.BitrateK > 3500)
                {
                    lastOptions.BitrateK = Math.Max(3500, lastOptions.BitrateK - 1000);
                    quickDrops = 0;
                    Diag.Event("KICK", $"bağlantı sürekli kopuyor → bitrate {lastOptions.BitrateK} kbps'ye düşürüldü");
                }
                SetStatus(L.F("● BAĞLANTI KOPTU — yeniden bağlanılıyor ({0})", reconnects), Red);
                lblStats.Text = Trim(err);
                var t = new Timer { Interval = 2000 };
                t.Tick += (s, e) => { t.Dispose(); if (mode == Mode.Live && !Launch()) Finish(); };
                t.Start();
                return;
            }
            var was = mode;
            Finish();
            Diag.End(rejected ? "Kick bağlantıyı reddetti" : !unexpected ? "normal bitiş" : was == Mode.Live ? "yayın durdu (yeniden bağlanma tükendi)" : "test başarısız");
            if (rejected)
            {
                SetStatus(L.T("● KICK BAĞLANTIYI REDDETTİ"), Red);
                lblStats.Text = L.T("Stream URL / Stream Key'i Kick panelinden yeniden kopyala");
                return;
            }
            if (was == Mode.Test)
            {
                if (unexpected) { SetStatus(L.T("● TEST BAŞARISIZ"), Red); lblStats.Text = Trim(err); }
                else
                {
                    SetStatus(L.T("● TEST KAYDI HAZIR"), Green);
                    lblStats.Text = Path.GetFileName(lastOptions.Output);
                    Process.Start("explorer.exe", $"/select,\"{lastOptions.Output}\"");
                }
            }
            else if (unexpected) { SetStatus(L.T("● YAYIN DURDU"), Red); lblStats.Text = Trim(err); }
            else { SetStatus(L.T("● YAYIN BİTTİ"), Mut); lblStats.Text = L.F("Süre: {0:hh\\:mm\\:ss}", DateTime.Now - liveSince); }
        }

        static string Trim(string s) => string.IsNullOrEmpty(s) ? L.T("Ayrıntı: %LOCALAPPDATA%\\ValoLive\\ffmpeg.log") : (s.Length > 70 ? s.Substring(0, 70) + "…" : s);

        void Finish()
        {
            mode = Mode.Idle;
            DisposeAudio();
            StopLayers();
            btnLive.Text = L.T("YAYINI BAŞLAT"); btnLive.BackColor = Red;
            SetInputsEnabled(true);
        }

        void SetInputsEnabled(bool on)
        {
            foreach (Control c in new Control[] { txtUrl, txtKey, cmbQuality, cmbCapture, cmbApp, btnApps, cmbMic, chkDenoise, cmbOut, btnTest, chkRadar, btnAlign, chkChat, chkPost, chkBot, chkWgc })
                c.Enabled = on;
            btnAlign.Enabled = on && chkRadar.Checked;
            chkWgc.Enabled = on && guard.Mode == CaptureMode.Valorant;
        }

        void DisposeAudio()
        {
            audio?.Dispose(); audio = null;
            micAudio?.Dispose(); micAudio = null;
        }

        void SetStatus(string text, Color c) { lblStatus.Text = text; lblStatus.ForeColor = c; }

        void ShowProgress(Dictionary<string, string> p)
        {
            if (mode == Mode.Idle) return;
            if (mode == Mode.Live) { SetStatus(L.T("● CANLI") + "  " + (DateTime.Now - liveSince).ToString(@"hh\:mm\:ss"), Red); reconnects = 0; everLive = true; }
            p.TryGetValue("fps", out var fps);
            p.TryGetValue("bitrate", out var br);
            p.TryGetValue("drop_frames", out var drop);
            p.TryGetValue("speed", out var speed);
            if (double.TryParse((fps ?? "0").Split('.')[0], out var f)) fps = f.ToString();
            br = (br ?? "").Replace("kbits/s", " kbps").Trim();
            lblStats.Text = L.F("{0} fps  ·  {1}  ·  düşen kare: {2}  ·  hız {3}", fps, br, drop ?? "0", speed?.Trim());
        }

        void UpdateClock()
        {
            if (mode == Mode.Live && streamer.Running && lblStatus.Text.StartsWith(L.T("● CANLI")))
                SetStatus(L.T("● CANLI") + "  " + (DateTime.Now - liveSince).ToString(@"hh\:mm\:ss"), Red);
        }

        // ---------- Kısayollar ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, 1, 0x4000, (int)Keys.F9);
            RegisterHotKey(Handle, 2, 0x4000, (int)Keys.F10);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                if ((int)m.WParam == 1) { overlay.Cover = !overlay.Cover; autoCovered = false; } // elle dokunulan perde artık elle yönetilir
                if ((int)m.WParam == 2) overlay.Summary = !overlay.Summary;
                UpdateTitle();
            }
            base.WndProc(ref m);
        }

        void ChangeLanguage(string code)
        {
            if (code == cfg.Language) return;
            cfg.Language = code; cfg.Save();
            if (mode != Mode.Idle) { MessageBox.Show(this, L.T("Dil, yayın bitince ValoLive yeniden açıldığında değişir."), "ValoLive"); return; }
            exiting = true;
            Application.Restart();
        }

        void UpdateTitle() => Text = overlay.Cover ? L.T("ValoLive — SNIPER KORUMASI AÇIK") : overlay.Summary ? L.T("ValoLive — YAYIN ÖZETİ") : "ValoLive";

        // ---------- Bildirim alanı ----------

        /// <summary>Çarpı pencereyi saatin yanındaki simgeye küçültür; uygulama (yayın, kısayollar, maç odası) çalışmaya devam eder.</summary>
        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new TrayRenderer(), ShowImageMargin = false, Font = new Font(L.Sans, 9.5f) };
            var open = new ToolStripMenuItem(L.T("ValoLive'ı aç"), null, (s, e) => ShowFromTray()) { Font = new Font(L.SansBold, 9.5f) };
            var stream = new ToolStripMenuItem(L.T("Yayını başlat"), null, (s, e) => { if (mode == Mode.Idle) Begin(Mode.Live); else if (mode == Mode.Live) StopLive(); });
            var cover = new ToolStripMenuItem(L.T("Sniper koruması (F9)"), null, (s, e) => { overlay.Cover = !overlay.Cover; autoCovered = false; UpdateTitle(); });
            menu.Items.AddRange(new ToolStripItem[]
            {
                open, new ToolStripSeparator(), stream, cover,
                new ToolStripMenuItem(L.T("Maç Odası"), null, (s, e) => OpenLobby()),
                new ToolStripMenuItem(L.T("Analizim"), null, (s, e) => AnalysisForm.Open()),
                new ToolStripMenuItem(L.T("Günlükler"), null, (s, e) => { Directory.CreateDirectory(Diag.Dir); Process.Start("explorer.exe", Diag.Dir); }),
                new ToolStripSeparator(),
                new ToolStripMenuItem(L.T("Çıkış"), null, (s, e) => { exiting = true; Close(); }),
            });
            menu.Opening += (s, e) =>
            {
                stream.Text = mode == Mode.Live ? L.T("Yayını bitir") : L.T("Yayını başlat");
                stream.Enabled = mode != Mode.Test && btnLive.Enabled;
                cover.Text = (overlay.Cover ? "✓ " : "") + L.T("Sniper koruması (F9)");
            };
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "ValoLive", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        sealed class TrayRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1); }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                using (var b = new SolidBrush(ColorTranslator.FromHtml("#5b2430"))) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2); }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Txt : Theme.Mut; base.OnRenderItemText(e); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Çarpı: kapanmaz, bildirim alanına iner. Gerçek çıkış: simge menüsündeki "Çıkış" (ya da Windows kapanışı).
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!cfg.TrayHintShown)
                {
                    cfg.TrayHintShown = true; cfg.Save();
                    tray.ShowBalloonTip(4000, L.T("ValoLive arka planda çalışıyor"), L.T("Açmak için simgeye tıkla; çıkış simgenin sağ tık menüsünde."), ToolTipIcon.Info);
                }
                return;
            }
            exiting = false;
            if (mode == Mode.Live && MessageBox.Show(this, L.T("Yayın açık. Kapatılsın mı?"), "ValoLive", MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            mode = Mode.Idle;
            streamer.Stop();
            guard.Dispose();
            SaveSettings();
            UnregisterHotKey(Handle, 1);
            UnregisterHotKey(Handle, 2);
            tracker?.Dispose();
            live?.Dispose();
            memory.Dispose();
            DisposeAudio();
            overlay.Dispose();
            tray.Visible = false; tray.Dispose(); // yoksa simge fare üstünden geçene kadar bildirim alanında kalır
            base.OnFormClosing(e);
        }
    }
}
