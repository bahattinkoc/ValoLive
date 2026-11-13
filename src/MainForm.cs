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
            Updater.Cleanup();
            Application.Run(new MainForm());
        }
    }

    sealed partial class MainForm : Form
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

        readonly List<DestUi> dests = new List<DestUi>();   // yayın platformları (Platform.All sırasıyla)
        Control host;                     // yerleşim yardımcılarının denetim eklediği yer (sekme sayfası ya da form)
        Page[] pages; Button[] tabs;
        Page chips;                       // alt şeritte platform durumları
        Label lblUpload;
        static readonly Color Amber = ColorTranslator.FromHtml("#ffb547");
        TextBox txtName, txtTag, txtHdev, txtTitle, txtTags;
        ComboBox cmbCategory;
        CheckBox chkWgc, chkGpu, chkAutoLobby, chkAlerts, chkMemory, chkMemoryMap;
        string liveState = "";
        bool autoCovered;                 // perdeyi ajan seçimi açtı (F9 ile açılanı maç başı kapatmaz)
        Control[] appShift; bool appRow = true;
        NotifyIcon tray; bool exiting;
        LiveInfo live;
        readonly MinimapMemory memory = new MinimapMemory();
        readonly Timer memTimer = new Timer { Interval = 250 };
        MemoryPanel memPanel;
        MemoryMarks memMarks;
        int memScore = -1;
        bool memStarting;
        DateTime memRetry;
        PostMatchLayer postLayer;
        ChatBot bot;
        Label lblKick;
        string chatState = ""; Color chatStateColor = Theme.Mut;   // Kick chat bağlantısının canlı durumu (Yayın Paneli'nde görünür)
        FunChat funChat;
        FunLayer funLayer;
        AquariumLayer aquarium;
        Speaker speaker;
        Button btnKick;
        KickApi kick;
        KickChat kickChat;
        Label lblYt; Button btnYt;
        YouTubeApi yt;
        YouTubeChat ytChat;
        string ytChatState = ""; Color ytChatStateColor = Theme.Mut;
        volatile YouTubeApi.Broadcast ytLive;   // chat'i okunan YouTube yayını (bot cevapları onun chat'ine)
        System.Threading.CancellationTokenSource ytInfo;   // yayın başında YouTube başlığını bekleyen iş
        RadarWidget radar;
        ChatLayer chatLayer;
        MomentWatch moments;
        MomentLayer momentLayer;
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
        readonly Timer clock = new Timer { Interval = 1000 };

        public MainForm()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            screenW = GetDeviceCaps(dc, 118); // DESKTOPHORZRES: DPI ölçeğinden bağımsız gerçek çözünürlük
            screenH = GetDeviceCaps(dc, 117);
            ReleaseDC(IntPtr.Zero, dc);
            overlay = new OverlayRenderer(screenW, screenH) { Needed = () => mode != Mode.Idle };
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
            yt = new YouTubeApi(cfg);
            BuildUi();
            BuildTray();
            foreach (var l in AllControls(this).OfType<Label>()) l.UseMnemonic = false; // "&" kısayol harfi sanılmasın
            LoadSettings();

            streamer.Progress += p => BeginInvoke((Action)(() => ShowProgress(p)));
            streamer.TargetChanged += (t, s, n) => BeginInvoke((Action)(() => OnTargetChanged(t, s, n)));
            streamer.Exited += (unexpected, err) => BeginInvoke((Action)(() => OnStreamExit(unexpected, err)));
            streamer.Health += (fps, waits, noCap) => BeginInvoke((Action)(() => OnHealth(fps, waits, noCap)));
            streamer.Recovering += () => BeginInvoke((Action)(() => { if (mode != Mode.Idle) SetStatus(L.T("● EKRAN YAKALAMA YENİLENİYOR (yayın açık)"), Mut); }));
            clock.Tick += (s, e) => UpdateClock();
            clock.Start();
            updateTimer = new System.Threading.Timer(_ => CheckUpdate(), null, 5000, 6 * 3600 * 1000); // açılıştan 5 sn sonra, sonra 6 saatte bir
            StartTracker();
            Encounters.Start(); // karşılaşma defteri arka planda dolar (Riot istemcisi açıkken)
            live = new LiveInfo(); // canlı skor ve maç sonu özeti (Riot istemcisinin kendi durum bilgisi)
            live.Changed += s => { if (IsHandleCreated) try { BeginInvoke((Action)(() => ApplyLive(s))); } catch { } };
            live.PostMatchReady += pm => { moments?.PushHighlights(pm); var l = postLayer; if (l != null && (pm.Queue == "competitive" || pm.Queue == "unrated" || pm.Queue == "swiftplay" || pm.Queue == "premier")) l.Show(pm); };
            memTimer.Tick += (s, e) => TickMemory();
            memTimer.Start();
        }

        // ---------- Arayüz ----------

        void BuildUi()
        {
            // Üstte başlık şeridi, altında sekmeler (Platformlar · Görüntü & Ses · Valorant), en altta her sekmede görünen
            // yayın şeridi (düğmeler, durum, platform durumları). Her sekme iki sütun.
            // Başlık (VALO + kırmızı LIVE) OnPaint'te çizilir: köşeli yazı tipi uygulamayla gelen bir dosya, GDI+ ister.
            // Ölçüler metinlere göre (diller arasında uzunluk çok değişiyor): etiket sütunu en uzun alan etiketine,
            // sütunlar en uzun anahtar yazısına göre genişler.
            string tAutoLobby = L.T("Maç bulununca otomatik aç"), tAlerts = L.T("Bildirimler (tanıdık oyuncu, smurf, parti)"),
                   tWgc = L.T("Hafif yakalama (yalnızca oyun penceresi)"), tGpu = L.T("Ekran kartı motoru (işlemciyi boşaltır)"), tDenoise = L.T("Gürültü filtresi"),
                   tMemory = L.T("Rakip hafızası: minimap'te son görülen yerler (deneysel)"),
                   tMemoryMap = L.T("Rakip hafızası: harita üstünde ikonlar (deneysel)"), tDest = L.T("Bu platforma yayınla");
            using (var cap = new Font(L.SansBold, 8.25f))
                LabelW = Math.Max(104, new[] { "Stream URL", "Stream Key", L.T("Kick hesabı"), L.T("YouTube hesabı"), L.T("Başlık"), L.T("Kategori"), L.T("Etiketler"), "Riot ID", "HenrikDev key",
                    L.T("Kalite"), L.T("Yakalama"), L.T("Uygulama"), L.T("Mikrofon"), L.T("Oyun sesi") }
                    .Max(s => TextRenderer.MeasureText(Theme.Upper(s), cap).Width) + 12);
            int toggleW = new[] { tAutoLobby, tAlerts, tWgc, tGpu, tDenoise, tMemory, tMemoryMap, tDest }.Max(s => VToggle.WidthFor(s, Font));
            ColW = Math.Min(560, Math.Max(Math.Max(420, LabelW + 316), toggleW + 4));
            host = this;

            var sub = new Label { Text = L.T("Kick ve YouTube için hafif Valorant yayın aracı · RTX NVENC"), ForeColor = Mut, AutoSize = true, Location = new Point(22, 56) };
            string keysText = L.T("F9  Sniper koruması      F10  Yayın özeti");
            var keys = new Label { Text = keysText, ForeColor = Mut, AutoSize = true };
            keys.Location = new Point(FormW - 20 - TextRenderer.MeasureText(keysText, Font).Width, 56);
            int top = 92;
            if (sub.Left + TextRenderer.MeasureText(sub.Text, Font).Width + 24 > keys.Left) { keys.Location = new Point(22, 76); top = 112; } // yan yana sığmıyor: alt alta
            Controls.AddRange(new Control[] { sub, keys });
            // Yeni sürüm bulununca alt başlığın yerini alır.
            lblSub = sub;
            lnkUpdate = new LinkLabel { AutoSize = true, Location = sub.Location, Visible = false, LinkColor = Green, ActiveLinkColor = Txt, Font = new Font(L.SansBold, Font.Size) };
            lnkUpdate.LinkClicked += (s, e) => UpdateClicked();
            Controls.Add(lnkUpdate);
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

            // ---- Sekmeler ----
            string[] tabNames = { L.T("PLATFORMLAR"), L.T("GÖRÜNTÜ & SES"), "VALORANT", L.T("SES DEĞİŞTİRİCİ") };
            int tabW = tabNames.Max(s => Theme.ButtonWidth(s, 34, 150));
            int pageTop = top + 54;
            tabs = new Button[tabNames.Length]; pages = new Page[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                int k = i;
                tabs[i] = MakeButton(tabNames[i], new Rectangle(20 + i * (tabW + 8), top, tabW, 34), Field);
                tabs[i].Font = new Font(L.SansBold, 10f);
                tabs[i].Click += (s, e) => ShowPage(k);
                // Sayfa hemen forma eklenir: içindeki yazılar formun yazı tipiyle ölçülsün.
                pages[i] = new Page { Location = new Point(0, pageTop), Width = FormW };
                Controls.Add(pages[i]);
            }
            Controls.Add(new Panel { BackColor = Card, Location = new Point(20, top + 42), Size = new Size(FormW - 40, 1) });

            // ---- PLATFORMLAR: üstte iki platform yan yana (yayın anahtarı + hesap), altta ortak yayın bilgisi ----
            host = pages[0];
            cx = 20;
            int y = 4;
            AddDest(Platform.Kick, tDest, ref y);
            AccountRow(L.T("Kick hesabı"), ref y, out lblKick, out btnKick);
            btnKick.Click += (s, e) => KickConnectClicked();
            int leftBottom = y;

            cx = ColW + 60;
            y = 4;
            AddDest(Platform.YouTube, tDest, ref y);
            AccountRow(L.T("YouTube hesabı"), ref y, out lblYt, out btnYt);
            btnYt.Click += (s, e) => YouTubeConnectClicked();

            int infoTop = Math.Max(leftBottom, y) + 8;
            cx = 20;
            y = Section(L.T("YAYIN BİLGİSİ"), infoTop);
            txtTitle = AddText(L.T("Başlık"), ref y, false);
            txtTitle.MaxLength = 140;
            Caption(L.T("Kategori"), y);
            cmbCategory = new ComboBox { Location = new Point(FX, y), Width = FW, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat };
            StyleField(cmbCategory);
            cmbCategory.TextUpdate += (s, e) => SearchCategories();
            host.Controls.Add(cmbCategory); y += 34;
            txtTags = AddText(L.T("Etiketler"), ref y, false);
            var applyNow = new LinkLabel { Text = L.T("Bağlı hesaplara şimdi uygula"), AutoSize = true, LinkColor = Red, ActiveLinkColor = Red, Location = new Point(FX, y - 2) };
            applyNow.LinkClicked += (s, e) => ApplyStreamInfo(true);
            host.Controls.Add(applyNow);
            y += 22;
            leftBottom = y;

            cx = ColW + 60;
            y = Section(L.T("BAĞLANTI"), infoTop);
            lblUpload = new Label { ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y - 4) };
            host.Controls.Add(lblUpload);
            y += 44;
            var infoNote = new Label { Text = L.T("Başlık ve etiketler bağlı her hesaba gider, yayın başlarken kendiliğinden. Kategori yalnızca Kick'te; YouTube'da yayın Oyun kategorisinde açılır, küçük resim Studio'dan. Chat kutusu ve chat eğlencesi iki chat'i birlikte okur."),
                ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y) };
            host.Controls.Add(infoNote);
            y += infoNote.GetPreferredSize(new Size(ColW, 0)).Height + 4;
            AddDivider(Math.Max(leftBottom, y));
            pages[0].Height = Math.Max(leftBottom, y);

            // ---- GÖRÜNTÜ & SES: solda görüntü, sağda ses ve Yayın Paneli ----
            host = pages[1];
            cx = 20;
            y = Section(L.T("GÖRÜNTÜ"), 4);
            cmbQuality = AddCombo(L.T("Kalite"), ref y, Qualities.Select(q => q.Label).ToArray());
            cmbQuality.SelectedIndexChanged += (s, e) => { SaveSettings(); UpdateUpload(); };
            cmbCapture = AddCombo(L.T("Yakalama"), ref y, new[] { L.T("Tam ekran (her şey görünür)"), L.T("Sadece Valorant (önerilen)"), L.T("Seçtiğim uygulama") });
            Caption(L.T("Uygulama"), y);
            lblApp = host.Controls[host.Controls.Count - 1];
            cmbApp = new ComboBox { Location = new Point(FX, y), Width = FW - 42, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            StyleField(cmbApp);
            btnApps = new VButton { Text = "↻", Bounds = new Rectangle(FX + FW - 36, y - 1, 36, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat };
            btnApps.FlatAppearance.BorderSize = 0;
            host.Controls.AddRange(new Control[] { cmbApp, btnApps }); y += 34;
            int belowApp = host.Controls.Count; // "Seçtiğim uygulama" değilse bu satır kapanır, altındakiler yukarı kayar
            chkWgc = new VToggle { Text = tWgc, ForeColor = Txt, AutoSize = true, Location = new Point(FX, y - 4) };
            host.Controls.Add(chkWgc); y += 26;
            chkWgc.CheckedChanged += (s, e) => SaveSettings();
            // Ekran kartı motoru: valocap.exe yanında yoksa seçenek hiç görünmez.
            chkGpu = new VToggle { Text = tGpu, ForeColor = Txt, AutoSize = true, Location = new Point(FX, y - 4), Visible = Streamer.FindEngine() != null };
            host.Controls.Add(chkGpu); if (chkGpu.Visible) y += 26;
            chkGpu.CheckedChanged += (s, e) => SaveSettings();
            appShift = host.Controls.Cast<Control>().Skip(belowApp).ToArray();
            btnApps.Click += (s, e) => FillApps(apps.ElementAtOrDefault(cmbApp.SelectedIndex).Key ?? cfg.CaptureApp);
            cmbCapture.SelectedIndexChanged += (s, e) => ApplyCapture();
            cmbApp.SelectedIndexChanged += (s, e) => ApplyCapture();
            leftBottom = y;

            cx = ColW + 60;
            y = Section(L.T("SES"), 4);
            inputs = AudioDevices.ListInputs();
            cmbMic = AddCombo(L.T("Mikrofon"), ref y, inputs.Select(o => o.Value).ToArray());
            trkMic = AddSlider(ref y, out lblMic);
            trkMic.ValueChanged += (s, e) => { if (micAudio != null) micAudio.Volume = trkMic.Value / 100f; };
            chkDenoise = new VToggle { Text = tDenoise, ForeColor = Txt, AutoSize = true, Location = new Point(FX, y - 4) };
            host.Controls.Add(chkDenoise); y += 26;
            outputs = AudioDevices.ListOutputs();
            cmbOut = AddCombo(L.T("Oyun sesi"), ref y, outputs.Select(o => o.Value).ToArray());
            trkGame = AddSlider(ref y, out lblGame);
            trkGame.ValueChanged += (s, e) => { if (audio != null) audio.Volume = trkGame.Value / 100f; };

            // Yayına binen her şeyin ayarları (katmanlar, chat eğlencesi) ayrı pencerede: StreamPanel.
            y = Section(L.T("YAYIN PANELİ"), y + 8);
            var panelNote = new Label { Text = L.T("Katmanlar (radar, chat kutusu, yayın anları) ve chat eğlencesi (balon, uçak, domates, ördek yarışı…) ayrı pencerede."),
                ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y - 4) };
            host.Controls.Add(panelNote);
            y += panelNote.GetPreferredSize(new Size(ColW, 0)).Height + 4;
            var btnPanel = MakeButton(L.T("YAYIN PANELİNİ AÇ"), new Rectangle(cx, y, ColW, 36), Field);
            btnPanel.Font = new Font(L.SansBold, 10f);
            btnPanel.Click += (s, e) => StreamPanel.Open(this, cfg);
            y += 44;
            AddDivider(Math.Max(leftBottom, y));
            pages[1].Height = Math.Max(leftBottom, y);

            // ---- VALORANT: solda hesap, sağda maç odası ve rakip hafızası ----
            host = pages[2];
            cx = 20;
            y = Section("VALORANT", 4);
            Caption("Riot ID", y);
            txtName = new TextBox { Location = new Point(FX, y), Width = FW - 190 };
            host.Controls.Add(new Label { Text = "#", ForeColor = Mut, AutoSize = true, Location = new Point(FX + FW - 183, y + 3) });
            txtTag = new TextBox { Location = new Point(FX + FW - 162, y), Width = 68 };
            cmbRegion = new ComboBox { Location = new Point(FX + FW - 86, y), Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            cmbRegion.Items.AddRange(new object[] { "EU / TR", "NA", "AP", "KR", "LATAM", "BR" });
            cmbRegion.SelectedIndex = 0;
            StyleField(txtName); StyleField(txtTag); StyleField(cmbRegion);
            host.Controls.AddRange(new Control[] { txtName, txtTag, cmbRegion }); y += 34;
            txtHdev = AddText("HenrikDev key", ref y, true);
            var link = new LinkLabel { Text = L.T("Ücretsiz anahtar al"), AutoSize = true, Location = new Point(FX, y - 2), LinkColor = Red, ActiveLinkColor = Red };
            link.LinkClicked += (s, e) => Process.Start("https://api.henrikdev.xyz/dashboard/");
            int valoX = FX + TextRenderer.MeasureText(link.Text, Font).Width + 12;
            lblValo = new FitLabel { Text = "", ForeColor = Mut, Location = new Point(valoX, y - 2), Size = new Size(FX + FW - valoX, 20) };
            host.Controls.AddRange(new Control[] { link, lblValo });
            y += 22;
            foreach (var t in new[] { txtName, txtTag, txtHdev }) t.Leave += (s, e) => StartTracker();
            cmbRegion.SelectedIndexChanged += (s, e) => StartTracker();
            leftBottom = y;

            cx = ColW + 60;
            y = Section(L.T("MAÇ ODASI"), 4);
            chkAutoLobby = new VToggle { Text = tAutoLobby, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkAutoLobby); y += 26;
            chkAlerts = new VToggle { Text = tAlerts, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkAlerts); y += 26;
            chkAutoLobby.CheckedChanged += (s, e) => SaveSettings();
            chkAlerts.CheckedChanged += (s, e) => { SaveSettings(); if (lobby != null && !lobby.IsDisposed) lobby.Alerts = chkAlerts.Checked; };
            y = Section(L.T("RAKİP HAFIZASI"), y + 8);
            chkMemory = new VToggle { Text = tMemory, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkMemory); y += 26;
            chkMemory.CheckedChanged += (s, e) => SaveSettings();
            chkMemoryMap = new VToggle { Text = tMemoryMap, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkMemoryMap); y += 26;
            chkMemoryMap.CheckedChanged += (s, e) => SaveSettings();
            AddDivider(Math.Max(leftBottom, y));
            pages[2].Height = Math.Max(leftBottom, y);

            BuildVoicePage(pages[3]); // VoiceUi.cs

            // Sayfalar aynı boyda: sekme değişince alttaki yayın şeridi yerinden oynamaz.
            int pageH = pages.Max(p => p.Height) + 4;
            foreach (var p in pages) p.Height = pageH;

            // ---- Alt şerit (her sekmede): butonlar + durum + platformlar ----
            host = this;
            cx = 20;
            y = pageTop + pageH + 6;
            Controls.Add(new Panel { BackColor = Card, Location = new Point(20, y), Size = new Size(FormW - 40, 1) });
            y += 16;
            int testW = Math.Min(ColW / 2, Theme.ButtonWidth(L.T("TEST KAYDI (15 sn)"), 50, 170)); // canlı düğmesi geniş kalsın; sığmayan yazı küçülür
            btnTest = MakeButton(L.T("TEST KAYDI (15 sn)"), new Rectangle(20, y, testW, 50), Field);
            btnLive = MakeButton(L.T("YAYINI BAŞLAT"), new Rectangle(30 + testW, y, ColW + 10 - testW, 50), Red);
            btnTest.Click += (s, e) => { if (mode == Mode.Idle) Begin(Mode.Test); };
            btnLive.Click += (s, e) => { if (mode == Mode.Idle) Begin(Mode.Live); else if (mode == Mode.Live) StopLive(); };
            cx = ColW + 60;
            lblStatus = new FitLabel { Text = L.T("● HAZIR"), Font = new Font(L.SansBold, 12f), ForeColor = Mut, Location = new Point(cx, y - 2), Size = new Size(ColW, 26) };
            chips = new Page { Location = new Point(cx, y + 26), Size = new Size(ColW, 20) };
            chips.Paint += PaintChips;
            lblStats = new Label { Text = "", ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx + 2, y + 50) };
            Controls.AddRange(new Control[] { lblStatus, chips, lblStats });
            int bottom = y + 50 + 40;
            // Exclusive tam ekranda her alt-tab ekran modunu değiştirir; ekran yakalama kopar ve yayında takılma olur.
            if (Diag.ValorantFullscreenMode() == 0)
            {
                var warn = new Label
                {
                    Text = L.T("⚠  Valorant \"Tam ekran\" modunda: alt-tab'da yayın takılır.  Valorant → Ayarlar → Video → Genel → Görüntü Modu: \"Pencereli Tam Ekran\" seç."),
                    ForeColor = ColorTranslator.FromHtml("#ffb547"), AutoSize = true, MaximumSize = new Size(FormW - 40, 0), Location = new Point(20, y + 86),
                };
                Controls.Add(warn);
                bottom += warn.GetPreferredSize(new Size(FormW - 40, 0)).Height + 6; // uzun dillerde iki satıra iner
            }
            // Anahtar yazıları sütunu aşmasın (çok uzun bir çeviri en kötü "…" ile biter).
            foreach (var t in AllControls(this).OfType<VToggle>())
                t.MaximumSize = new Size((t.Left < ColW + 40 ? 20 + ColW : 2 * ColW + 60) - t.Left, 0);
            ClientSize = new Size(FormW, bottom);
            ThemeFields();
            ShowPage(0);
        }

        static IEnumerable<Control> AllControls(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(AllControls(x)));

        /// <summary>Platform sütunu: başlık, "yayınla" anahtarı, URL, anahtar ve anahtarın alındığı sayfanın bağlantısı.</summary>
        void AddDest(Platform p, string toggle, ref int y)
        {
            y = Section(p.Name.ToUpperInvariant(), y); // marka adı: Türkçe büyütme "KİCK" yapmasın
            var d = new DestUi { P = p };
            d.On = new VToggle { Text = toggle, ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(d.On); y += 30;
            d.Url = AddText("Stream URL", ref y, false);
            d.Key = AddText("Stream Key", ref y, true);
            var get = new LinkLabel { Text = p.KeyHint, AutoSize = true, LinkColor = Red, ActiveLinkColor = Red, Location = new Point(FX, y - 2) };
            get.LinkClicked += (s, e) => Process.Start(p.KeyPage);
            host.Controls.Add(get); y += 22;
            d.On.CheckedChanged += (s, e) => { SaveSettings(); UpdateUpload(); chips?.Invalidate(); StreamPanel.Sync(); };
            dests.Add(d);
        }

        /// <summary>Hesap satırı: bağlantı durumu ve Bağlan/Çıkış düğmesi.</summary>
        void AccountRow(string caption, ref int y, out Label status, out Button button)
        {
            Caption(caption, y);
            int w = Math.Max(Theme.ButtonWidth(L.T("Bağlan"), 25, 92), Theme.ButtonWidth(L.T("Çıkış"), 25, 92));
            status = new FitLabel { Text = L.T("Bağlı değil"), ForeColor = Mut, Location = new Point(FX, y + 3), Size = new Size(FW - w - 8, 20) };
            button = new VButton { Text = L.T("Bağlan"), Bounds = new Rectangle(FX + FW - w, y - 1, w, 25), BackColor = Field, ForeColor = Txt, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            host.Controls.AddRange(new Control[] { status, button }); y += 34;
        }

        /// <summary>Sayfanın iki sütunu arasındaki ince çizgi.</summary>
        void AddDivider(int bottom) => host.Controls.Add(new Panel { BackColor = Card, Location = new Point(ColW + 40, 8), Size = new Size(1, Math.Max(0, bottom - 8)) });

        void ShowPage(int i)
        {
            for (int k = 0; k < pages.Length; k++)
            {
                pages[k].Visible = k == i;
                tabs[k].BackColor = k == i ? Red : Field;
            }
            // Sanal mikrofon yoktuysa: kullanıcı az önce kurmuş olabilir, sekmeye her dönüşte yeniden bakılır.
            if (i == 3 && voiceOuts != null && voiceOuts.Count == 0) { FillVoiceDevices(); if (voiceOuts.Count > 0) RestartVoice(); }
        }

        /// <summary>Görüntü bir kez kodlanır ama her platforma ayrı gönderilir: gereken yükleme hızı platform sayısıyla katlanır.</summary>
        void UpdateUpload()
        {
            if (lblUpload == null || cmbQuality == null) return;
            int n = dests.Count(d => d.On.Checked);
            int kbps = Qualities[Math.Max(0, cmbQuality.SelectedIndex)].Kbps + 160; // + ses
            lblUpload.Text = n == 0 ? L.T("Hiçbir platform açık değil: yalnızca test kaydı alınabilir.")
                : L.F("{0} platform × ~{1} kbps → internetin yükleme hızı en az {2} Mbps olmalı (pay ile).", n, kbps, Math.Ceiling(n * kbps * 1.5 / 1000.0));
            lblUpload.ForeColor = n > 1 ? Txt : Mut;
        }

        // ---------- Platform durumları (alt şerit) ----------

        void PaintChips(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int x = 0;
            using (var bold = new Font(L.SansBold, 8.75f))
            using (var reg = new Font(L.Sans, 8.75f))
                foreach (var d in dests)
                {
                    var (text, color) = ChipText(d);
                    bool used = d.On.Checked || d.State != null;
                    using (var b = new SolidBrush(used ? d.P.Brand : Theme.Line)) g.FillRectangle(b, x, 4, 4, 12);
                    x += 9;
                    TextRenderer.DrawText(g, d.P.Name, bold, new Point(x, 2), used ? Txt : Mut, TextFormatFlags.NoPrefix);
                    x += TextRenderer.MeasureText(d.P.Name, bold).Width;
                    TextRenderer.DrawText(g, text, reg, new Point(x, 2), color, TextFormatFlags.NoPrefix);
                    x += TextRenderer.MeasureText(text, reg).Width + 16;
                }
        }

        (string, Color) ChipText(DestUi d)
        {
            // Yayın yokken: açık/kapalı. Yayın bittikten sonra da reddeden/kopan platform öyle görünür (neden bittiği okunsun).
            var s = d.State;
            if (s == null || (mode == Mode.Idle && s == TargetState.Stopped))
                return d.On.Checked ? (L.T("hazır"), Mut) : (L.T("kapalı"), Mut);
            switch (s.Value)
            {
                case TargetState.Connecting: return (L.T("bağlanıyor…"), Mut);
                case TargetState.Live: return (L.T("● canlı"), Red);
                case TargetState.Retrying: return (L.F("yeniden bağlanıyor ({0})", d.Attempts), Amber);
                case TargetState.Rejected: return (L.T("reddetti"), Red);
                case TargetState.Failed: return (L.T("koptu"), Red);
                default: return (L.T("durdu"), Mut);
            }
        }

        void OnTargetChanged(Target t, TargetState s, int attempts)
        {
            var d = dests.FirstOrDefault(x => x.P.Id == t.Id);
            if (d == null) return; // test kaydının dosya hedefi
            var old = d.State;
            d.State = s; d.Attempts = attempts;
            chips.Invalidate();
            if (mode != Mode.Live) return;
            UpdateLiveStatus();
            if (old == s) return;
            string name = d.P.Name;
            if (s == TargetState.Retrying && old == TargetState.Live) { dropped.Add(t.Id); Alert("drop:" + t.Id, L.F("{0} bağlantısı koptu, yeniden bağlanılıyor", name), Red); }
            else if (s == TargetState.Rejected) Alert("rej:" + t.Id, L.F("{0} yayını reddetti: Stream Key'i kontrol et", name), Red);
            else if (s == TargetState.Failed) Alert("fail:" + t.Id, L.F("{0} bağlantısı koptu, yeniden bağlanılamadı", name), Red);
            else if (s == TargetState.Live && dropped.Remove(t.Id)) Alert("up:" + t.Id, L.F("{0} yeniden bağlandı", name), Green);
        }

        // ---------- Yayın sağlığı uyarıları ----------
        // Oyun tam ekranken ValoLive penceresi görünmez: kopma, düşük FPS, yetişmeyen internet oyunun üstünde
        // yayına girmeyen bildirimle (ve sesle) söylenir. Kısa dalgalanmalar için iki ardışık 5 sn'lik pencere gerekir.

        readonly HashSet<string> dropped = new HashSet<string>();
        readonly Dictionary<string, int> slowNet = new Dictionary<string, int>();
        readonly Dictionary<string, DateTime> alertedAt = new Dictionary<string, DateTime>();
        int lowFps, noCapture;

        void ResetHealth()
        {
            dropped.Clear(); slowNet.Clear(); alertedAt.Clear();
            lowFps = noCapture = 0;
        }

        void OnHealth(double fps, Dictionary<Target, double> waits, bool noCap)
        {
            if (mode != Mode.Live || (DateTime.Now - liveSince).TotalSeconds < 20) return; // açılışta fps ve bağlantı oturuyor
            noCapture = noCap ? noCapture + 1 : 0;
            if (noCapture == 2) Alert("cap", L.T("Ekran yakalanamıyor: yayındaki görüntü donmuş olabilir"), Red);
            else if (noCapture == 0)
            {
                lowFps = fps < 50 ? lowFps + 1 : 0;
                if (lowFps == 2) Alert("fps", L.F("Yayın {0} FPS'e düştü: bilgisayar yetişmiyor", (int)fps), Amber);
            }
            foreach (var kv in waits)
            {
                if (kv.Key.Id == "file") continue;
                slowNet.TryGetValue(kv.Key.Id, out int n);
                slowNet[kv.Key.Id] = n = kv.Value >= 1000 ? n + 1 : 0;
                if (n == 2) Alert("net:" + kv.Key.Id, L.F("{0}: internet yetişmiyor, yayın takılabilir", kv.Key.Name), Amber);
            }
        }

        /// <summary>Aynı uyarı 2 dk içinde tekrar çıkmaz (gidip gelen bağlantı oyunu bildirime boğmasın).</summary>
        void Alert(string key, string text, Color color)
        {
            if (alertedAt.TryGetValue(key, out var at) && (DateTime.Now - at).TotalMinutes < 2) return;
            alertedAt[key] = DateTime.Now;
            Diag.Event("UYARI", text);
            Toast.Show(L.T("YAYIN"), new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(text, color) });
        }

        /// <summary>Genel durum: bir platform bile canlıysa CANLI; değilse yeniden bağlanan ya da bağlanan.</summary>
        void UpdateLiveStatus()
        {
            var used = dests.Where(d => d.State != null).ToList();
            if (used.Any(d => d.State == TargetState.Live)) SetStatus(L.T("● CANLI") + "  " + (DateTime.Now - liveSince).ToString(@"hh\:mm\:ss"), Red);
            else if (used.Any(d => d.State == TargetState.Retrying)) SetStatus(L.F("● BAĞLANTI KOPTU — yeniden bağlanılıyor ({0})", used.Max(d => d.Attempts)), Red);
            else SetStatus(L.T("● BAĞLANIYOR…"), Mut);
        }

        // ---------- Görünüm (Theme.cs) ----------

        /// <summary>Sekme sayfası: metin kutularının çerçevelerini kendisi çizer (kutunun kendi kenarlığı kaldırılır).</summary>
        sealed class Page : Panel
        {
            public readonly List<KeyValuePair<Rectangle, Control>> Frames = new List<KeyValuePair<Rectangle, Control>>();

            public Page() { DoubleBuffered = true; BackColor = Theme.Bg; }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                foreach (var fr in Frames)
                {
                    if (!fr.Value.Visible) continue;
                    using (var b = new SolidBrush(Theme.Field)) e.Graphics.FillRectangle(b, fr.Key);
                    using (var p = new Pen(fr.Value.Focused ? Theme.Red : Theme.Line)) e.Graphics.DrawRectangle(p, fr.Key.X, fr.Key.Y, fr.Key.Width - 1, fr.Key.Height - 1);
                }
            }
        }

        /// <summary>Platformun ana penceredeki alanları ve bu yayındaki son durumu (null = yayında yok).</summary>
        sealed class DestUi
        {
            public Platform P;
            public CheckBox On;
            public TextBox Url, Key;
            public TargetState? State;
            public int Attempts;
        }

        void ThemeFields()
        {
            foreach (var pg in pages)
                foreach (var t in pg.Controls.OfType<TextBox>().ToList())
                {
                    var r = t.Bounds;
                    t.BorderStyle = BorderStyle.None;
                    pg.Frames.Add(new KeyValuePair<Rectangle, Control>(new Rectangle(r.X, r.Y, r.Width, 26), t));
                    t.Location = new Point(r.X + 8, r.Y + (26 - t.Height) / 2);
                    t.Width = r.Width - 16;
                    var page = pg;
                    t.GotFocus += (s, e) => page.Invalidate();
                    t.LostFocus += (s, e) => page.Invalidate();
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
        }

        /// <summary>Açılır listeler: koyu öğeler, seçili öğe kırmızımsı.</summary>
        internal static void DarkCombo(ComboBox c)
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
            host.Controls.Add(new Panel { BackColor = Red, Location = new Point(cx, y + 4), Size = new Size(4, 13) });
            host.Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Txt, Font = Theme.Display(16), UseCompatibleTextRendering = true, AutoSize = true, Location = new Point(cx + 11, y) });
            host.Controls.Add(new Panel { BackColor = Card, Location = new Point(cx, y + 22), Size = new Size(ColW, 1) });
            return y + 32;
        }

        void Caption(string text, int y) => host.Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Mut, Font = new Font(L.SansBold, 8.25f), AutoSize = true, Location = new Point(cx, y + 5) });

        void StyleField(Control c) { c.BackColor = Field; c.ForeColor = Txt; if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle; if (c is ComboBox cb) DarkCombo(cb); }

        TextBox AddText(string label, ref int y, bool secret)
        {
            Caption(label, y);
            var t = new TextBox { Location = new Point(FX, y), Width = FW, UseSystemPasswordChar = secret };
            StyleField(t); host.Controls.Add(t); y += 34;
            return t;
        }

        ComboBox AddCombo(string label, ref int y, string[] items)
        {
            Caption(label, y);
            var c = new ComboBox { Location = new Point(FX, y), Width = FW, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            StyleField(c); c.Items.AddRange(items); if (items.Length > 0) c.SelectedIndex = 0;
            FitDropDown(c);
            host.Controls.Add(c); y += 34;
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
            host.Controls.AddRange(new Control[] { t, v });
            value = v; y += 30;
            return t;
        }

        Button MakeButton(string text, Rectangle r, Color color)
        {
            var b = new VButton { Text = text, Bounds = r, BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 11f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            host.Controls.Add(b);
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
            foreach (var d in dests)
            {
                var c = cfg.Dest(d.P.Id);
                d.On.Checked = c.On; d.Url.Text = c.Url; d.Key.Text = Settings.Unprotect(c.KeyEnc);
            }
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
            overlay.RankPanel = !cfg.MapCover;
            chkWgc.Checked = cfg.WindowCapture;
            chkGpu.Checked = cfg.GpuEngine;
            chkAutoLobby.Checked = cfg.AutoLobby;
            chkAlerts.Checked = cfg.LobbyAlerts;
            chkMemory.Checked = cfg.EnemyMemory;
            chkMemoryMap.Checked = cfg.EnemyMemoryMap;
            LoadVoice();
            UpdateKickStatus();
            UpdateYouTubeStatus();
            UpdateUpload();
        }

        void SaveSettings()
        {
            if (loading) return;
            foreach (var d in dests)
            {
                var c = cfg.Dest(d.P.Id);
                c.On = d.On.Checked; c.Url = d.Url.Text.Trim(); c.KeyEnc = Settings.Protect(d.Key.Text.Trim());
            }
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
            // Katman ayarları Yayın Paneli'nden doğrudan cfg'ye yazılır (StreamPanel).
            if (chkWgc != null) { cfg.WindowCapture = chkWgc.Checked; cfg.GpuEngine = chkGpu.Checked; cfg.AutoLobby = chkAutoLobby.Checked; cfg.LobbyAlerts = chkAlerts.Checked; cfg.EnemyMemory = chkMemory.Checked; cfg.EnemyMemoryMap = chkMemoryMap.Checked; }
            cfg.Save();
        }

        // ---------- Yayın Paneli'nin kullandıkları ----------

        internal bool Streaming => mode != Mode.Idle;
        internal bool FunRunning => funChat != null;

        /// <summary>Platforma yayın açık mı (o platformun chat'i ve yayın bilgisi yalnızca o zaman).</summary>
        bool DestOn(Platform p) => dests.First(d => d.P == p).On.Checked;
        bool KickOn => DestOn(Platform.Kick);
        bool YouTubeOn => DestOn(Platform.YouTube);

        /// <summary>Chat'i okunabilecek bir hesap var mı (Kick kanalı ya da bağlı YouTube hesabı).</summary>
        internal bool HasChatAccount => cfg.KickSlug != "" || yt.Connected;

        /// <summary>Panelde bir katman ayarı değişti: kaydet ve hemen uygulananları uygula.</summary>
        internal void LayerSettingsChanged()
        {
            cfg.Save();
            overlay.RankPanel = !cfg.MapCover;
            ApplyLive(live?.Snap);
        }

        /// <summary>Chat kutusunun yanındaki durum yazısı.</summary>
        internal KeyValuePair<string, Color> ChatHint()
        {
            if (!cfg.ChatBox && !cfg.ChatBot && !cfg.Fun) return new KeyValuePair<string, Color>("", Mut);
            if (!HasChatAccount) return new KeyValuePair<string, Color>(L.T("Kick ya da YouTube hesabını bağla"), Mut);
            if (kickChat == null && ytChat == null)
            {
                var will = new List<string>();
                if (cfg.KickSlug != "" && KickOn) will.Add("Kick");
                if (yt.Connected && YouTubeOn) will.Add("YouTube");
                return will.Count == 0 ? new KeyValuePair<string, Color>(L.T("Hesabı bağlı platforma yayın kapalı: chat okunmaz"), Mut)
                                       : new KeyValuePair<string, Color>(L.T("yayın başlayınca bağlanır") + ": " + string.Join(" + ", will), Mut);
            }
            var parts = new List<string>();
            bool all = true;
            if (kickChat != null) { parts.Add("Kick " + chatState); all &= chatStateColor == Green; }
            if (ytChat != null) { parts.Add("YouTube " + ytChatState); all &= ytChatStateColor == Green; }
            return new KeyValuePair<string, Color>("chat: " + string.Join(" · ", parts), all ? Green : Mut);
        }

        // ---------- Hesaplar: başlık, kategori, etiketler ----------

        void UpdateKickStatus()
        {
            bool on = kick.Connected;
            lblKick.Text = on ? "✓ " + (cfg.KickSlug != "" ? "kick.com/" + cfg.KickSlug : L.T("bağlı")) : L.T("Bağlı değil");
            lblKick.ForeColor = on ? Green : Mut;
            btnKick.Text = on ? L.T("Çıkış") : L.T("Bağlan");
            StreamPanel.Sync();
        }

        void KickConnectClicked()
        {
            if (kick.Connected)
            {
                kick.Logout();
                UpdateKickStatus();
                return;
            }
            if (!kick.HasApp && !AppDialog.AskKick(this, cfg)) return;
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

        void UpdateYouTubeStatus()
        {
            bool on = yt.Connected;
            lblYt.Text = on ? "✓ " + (cfg.YtChannel != "" ? cfg.YtChannel : L.T("bağlı")) : L.T("Bağlı değil");
            lblYt.ForeColor = on ? Green : Mut;
            btnYt.Text = on ? L.T("Çıkış") : L.T("Bağlan");
            StreamPanel.Sync();
        }

        void YouTubeConnectClicked()
        {
            if (yt.Connected)
            {
                yt.Logout();
                UpdateYouTubeStatus();
                return;
            }
            if (!yt.HasApp && !AppDialog.AskYouTube(this, cfg)) return;
            btnYt.Enabled = false;
            lblYt.Text = L.T("Tarayıcıda Google onayı bekleniyor…"); lblYt.ForeColor = Mut;
            Task.Run(() => yt.Login()).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                btnYt.Enabled = true;
                UpdateYouTubeStatus();
                if (!t.IsFaulted) return;
                // Yanlış yapıştırılmış Client ID/Secret bir daha sorulmadan takılı kalmasın.
                var r = MessageBox.Show(this, L.T("YouTube'a bağlanılamadı:\n") + t.Exception.GetBaseException().Message + "\n\n" +
                                        L.T("Client ID ve Client Secret'ı yeniden girmek için Evet'e bas."), "ValoLive", MessageBoxButtons.YesNo);
                if (r == DialogResult.Yes) { cfg.YtClientIdEnc = cfg.YtSecretEnc = ""; cfg.Save(); }
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

        /// <summary>
        /// Başlık ve etiketleri bağlı hesaplara (Kick'e kategoriyle) gönderir; arka planda, yayını bekletmez.
        /// interactive: "şimdi uygula" (bağlı her hesap, sonuç pencerede). Değilse yayın başı: yalnızca yayın açılan
        /// platformlar; YouTube yayını görüntü gidince açıldığından onun için ayrıca beklenir (WaitYouTubeInfo).
        /// </summary>
        void ApplyStreamInfo(bool interactive)
        {
            SaveSettings();
            bool toKick = kick.Connected && (interactive || KickOn), toYt = yt.Connected && (interactive || YouTubeOn);
            if (interactive && !toKick && !toYt)
            {
                MessageBox.Show(this, L.T("Önce Kick ya da YouTube hesabını bağla (Platformlar → Bağlan)."), "ValoLive");
                return;
            }
            string title = cfg.StreamTitle, catName = cfg.CategoryName;
            var tags = ParseTags(cfg.Tags);
            int catId = cfg.CategoryId;
            if (toYt && !interactive) WaitYouTubeInfo(title, tags);
            if (!toKick && !(toYt && interactive)) return;
            Task.Run(() =>
            {
                var lines = new List<string>();
                KickApi.Category cat = null;
                if (toKick)
                    try { cat = ApplyKickInfo(title, catId, catName, tags); lines.Add(L.T("Kick: başlık, kategori ve etiketler uygulandı ✓")); }
                    catch (Exception e) { lines.Add("Kick: " + e.Message); Diag.Event("KICK", "yayın bilgisi uygulanamadı: " + e.Message); }
                if (toYt && interactive)
                    try
                    {
                        var b = yt.Find("active") ?? yt.Find("upcoming");
                        if (b == null) lines.Add(L.T("YouTube: şu an açık ya da planlanmış yayın yok; başlık yayın başlayınca uygulanır."));
                        else { yt.SetInfo(b.Id, title, tags); lines.Add(L.T("YouTube: başlık ve etiketler uygulandı ✓")); Diag.Event("YT", "yayın bilgisi uygulandı"); }
                    }
                    catch (Exception e) { lines.Add("YouTube: " + e.Message); Diag.Event("YT", "yayın bilgisi uygulanamadı: " + e.Message); }
                return (cat, lines);
            }).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                var (cat, lines) = t.Result;
                if (cat != null) { cfg.CategoryId = cat.Id; cfg.CategoryName = cat.Name; cfg.Save(); Diag.Event("KICK", $"yayın bilgisi uygulandı · kategori {cat.Name}"); }
                if (interactive) MessageBox.Show(this, string.Join("\n", lines), "ValoLive");
                else if (cat == null) lblStats.Text = L.T("Başlık uygulanamadı: ") + lines.FirstOrDefault();
                UpdateKickStatus(); UpdateYouTubeStatus(); // oturum düşmüşse görünsün
            })));
        }

        KickApi.Category ApplyKickInfo(string title, int catId, string catName, string[] tags)
        {
            if (catId == 0 && catName != "")
            {
                // Elle yazılmış kategori: tam eşleşeni bul (yoksa ilk sonuç).
                var found = kick.SearchCategories(catName);
                var hit = found.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
                if (hit == null) throw new Exception(L.F("\"{0}\" adında kategori bulunamadı.", catName));
                catId = hit.Id; catName = hit.Name;
            }
            kick.UpdateChannel(title, catId, tags);
            return new KickApi.Category { Id = catId, Name = catName };
        }

        /// <summary>YouTube yayını görüntü gitmeye başlayınca açılır (10-60 sn): 15 sn'de bir bakar, açılınca başlığı yazar (en fazla 5 dk).</summary>
        void WaitYouTubeInfo(string title, string[] tags)
        {
            ytInfo?.Cancel();
            var cts = ytInfo = new System.Threading.CancellationTokenSource();
            Task.Run(() =>
            {
                var end = DateTime.Now.AddMinutes(5);
                while (!cts.IsCancellationRequested && DateTime.Now < end)
                {
                    try
                    {
                        var b = yt.Find("active");
                        if (b != null) { yt.SetInfo(b.Id, title, tags); Diag.Event("YT", "yayın bilgisi uygulandı"); return null; }
                    }
                    catch (Exception e) { Diag.Event("YT", "yayın bilgisi uygulanamadı: " + e.Message); return e.Message; }
                    cts.Token.WaitHandle.WaitOne(15000);
                }
                if (cts.IsCancellationRequested) return null;
                Diag.Event("YT", "5 dk içinde açık YouTube yayını görünmedi; başlık uygulanmadı");
                return L.T("YouTube yayını 5 dakikada açılmadı");
            }).ContinueWith(t =>
            {
                if (t.Result != null && !cts.IsCancellationRequested) BeginInvoke((Action)(() => lblStats.Text = L.T("YouTube başlığı uygulanamadı: ") + t.Result));
            });
        }

        // ---------- Yayın katmanları: radar rozeti, chat kutusu ----------

        /// <param name="useKick">Kick chat'i okunsun mu (Kick'e yayın açık ya da test kaydı)</param>
        /// <param name="useYt">YouTube chat'i okunsun mu (YouTube'a yayın açık ya da test kaydı)</param>
        List<Layer> StartLayers(bool useKick, bool useYt)
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
            if (cfg.Moments)
            {
                moments = new MomentWatch(() => live?.Snap);
                // Bekleme ekranında gizlenmez: maç bitince "maçın anları" menüdeyken oynar.
                momentLayer = new MomentLayer(moments, screenW, screenH, sc) { Hidden = () => overlay.Cover || overlay.Summary || overlay.Privacy };
                list.Add(momentLayer);
            }
            // İki chat aynı yerlere akar: chat kutusu, bot, chat eğlencesi. Bot cevabı mesajın geldiği chat'e gider.
            bool readKick = useKick && cfg.KickSlug != "", readYt = useYt && yt.Connected;
            bool useBot = cfg.ChatBot && ((readKick && kick.Connected) || readYt);
            if ((cfg.ChatBox || useBot || cfg.Fun) && (readKick || readYt))
            {
                var handlers = new List<Action<ChatMessage>>();
                if (cfg.ChatBox) { chatLayer = new ChatLayer(sc) { ShowSource = readKick && readYt }; list.Add(chatLayer); handlers.Add(chatLayer.Add); }
                if (useBot) { bot = new ChatBot(SendChat, () => lastState, () => live); handlers.Add(bot.Handle); }
                if (cfg.Fun)
                {
                    // Chat eğlencesi: en üstte tam ekran katman (boşken bindirilmez) ve sol altta akvaryum.
                    speaker = new Speaker();
                    aquarium = new AquariumLayer(sc) { Hidden = () => !cfg.FunOn("aquarium") || overlay.Summary };
                    funLayer = new FunLayer(screenW, screenH, sc, speaker);
                    list.Add(aquarium); list.Add(funLayer);
                    var fc = funChat = new FunChat(cfg, funLayer, aquarium, speaker)
                    {
                        Graph = (k, on) =>
                        {
                            if (k == "flip") { streamer.SendCommand("hflip@fh enable " + (on ? 1 : 0)); streamer.SendCommand("vflip@fv enable " + (on ? 1 : 0)); }
                            else { streamer.SendCommand("framestep@zs enable " + (on ? 0 : 1)); streamer.SendCommand("overlay@zoom enable " + (on ? 1 : 0)); }
                        },
                        Announce = s =>
                        {
                            if (!cfg.ChatBot) return;
                            foreach (var p in Platform.All) Task.Run(() => { try { SendChat(p, s); } catch { } });
                        },
                    };
                    handlers.Add(fc.Handle);
                }
                Action<ChatMessage> onMessage = m => { foreach (var h in handlers) h(m); };
                if (readKick)
                {
                    kickChat = new KickChat(cfg.KickSlug);
                    chatState = L.T("bağlanıyor"); chatStateColor = Mut;
                    kickChat.Message += onMessage;
                    kickChat.Status += s => BeginInvoke((Action)(() =>
                    {
                        chatState = s;
                        chatStateColor = s == L.T("bağlı") ? Green : Mut;
                        StreamPanel.Sync();
                    }));
                }
                if (readYt)
                {
                    ytChat = new YouTubeChat(() => (ytLive = yt.Find("active"))?.Id);
                    ytChatState = L.T("yayın bekleniyor"); ytChatStateColor = Mut;
                    ytChat.Message += onMessage;
                    ytChat.Status += s => BeginInvoke((Action)(() =>
                    {
                        ytChatState = s;
                        ytChatStateColor = s == L.T("bağlı") ? Green : Mut;
                        StreamPanel.Sync();
                    }));
                }
            }
            return list;
        }

        /// <summary>Bot cevabı ve duyurular: platformun chat'ine yayıncı hesabından (hesap bağlı ve o chat okunuyorsa).</summary>
        void SendChat(Platform p, string text)
        {
            if (p == Platform.YouTube)
            {
                var b = ytLive;
                if (ytChat != null && b != null && b.ChatId != "" && yt.Connected) yt.SendChat(b.ChatId, text);
            }
            else if (kickChat != null && kick.Connected) kick.SendChat(text);
        }

        /// <summary>Rozetin ekrandaki yeri (gerçek piksel); ekranın içine sığdırılır.</summary>
        internal Rectangle RadarRect()
        {
            int d = cfg.MapCoverD > 0 ? cfg.MapCoverD : (int)(360 * screenH / 1080f);
            d = Math.Max(80, Math.Min(Math.Min(screenW, screenH), d));
            int x = Math.Max(0, Math.Min(screenW - d, cfg.MapCoverX)), y = Math.Max(0, Math.Min(screenH - d, cfg.MapCoverY));
            return new Rectangle(x, y, d, d);
        }

        /// <summary>Rozeti minimap'e hizalama penceresi (Yayın Paneli'nden). Kaydedildiyse true.</summary>
        internal bool AlignRadar(IWin32Window owner)
        {
            using (var f = new RadarAlignForm(RadarRect(), screenW, screenH))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                cfg.MapCoverX = f.Result.X; cfg.MapCoverY = f.Result.Y; cfg.MapCoverD = f.Result.Width;
                cfg.Save();
                return true;
            }
        }

        void StopLayers()
        {
            kickChat?.Dispose(); kickChat = null;
            ytChat?.Dispose(); ytChat = null; ytLive = null;
            ytInfo?.Cancel(); ytInfo = null;
            funChat?.Dispose(); funChat = null;
            funLayer?.Dispose(); funLayer = null;
            aquarium?.Dispose(); aquarium = null;
            speaker?.Dispose(); speaker = null;
            radar?.Dispose(); radar = null;
            chatLayer?.Dispose(); chatLayer = null;
            postLayer?.Dispose(); postLayer = null;
            momentLayer?.Dispose(); momentLayer = null;
            moments?.Dispose(); moments = null;
            bot = null;
            StreamPanel.Sync();
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
            if (!MemoryWanted || s == null || !s.InGame)
            {
                if (memory.Active) { memory.Stop(); MinimapMemory.Log("durdu"); }
                memScore = -1;
                memPanel?.Hide(); memMarks?.Hide();
                return;
            }
            if (!memory.Active && !memStarting && DateTime.Now >= memRetry) { memRetry = DateTime.Now.AddSeconds(15); StartMemory(); }
            int score = s.Ally + s.Enemy;
            if (score != memScore) { if (memScore >= 0) { memory.NewRound(); MinimapMemory.Log($"yeni raund · skor {s.Ally}-{s.Enemy}"); } memScore = score; }

            var rows = memory.Snapshot();
            bool ready = memory.GameFront && rows.Count > 0 && !memory.MapRect.IsEmpty;
            float scale = Screen.PrimaryScreen.Bounds.Width / (float)screenW;
            if (ready && cfg.EnemyMemory)
            {
                if (memPanel == null || memPanel.IsDisposed) memPanel = new MemoryPanel(scale);
                memPanel.Set(rows, memory.MapRect);
                if (!memPanel.Visible) memPanel.Show();
            }
            else memPanel?.Hide();
            if (ready && cfg.EnemyMemoryMap && MemoryMarks.Any(rows))
            {
                if (memMarks == null || memMarks.IsDisposed) memMarks = new MemoryMarks(scale);
                memMarks.Set(rows, memory.MapRect);
                if (!memMarks.Visible) memMarks.Show();
            }
            else memMarks?.Hide();
        }

        /// <summary>Yazılı şerit ya da harita ikonları: ikisinden biri açıksa minimap okunur.</summary>
        bool MemoryWanted => cfg.EnemyMemory || cfg.EnemyMemoryMap;

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
                        if (MemoryWanted && live.Snap.InGame) { memory.Record = cfg.EnemyMemoryDiag; memory.Start(st.MapUrl, enemies); memScore = -1; }
                    }));
                }
                catch (Exception e) { MinimapMemory.Log("maç okunamadı: " + e.Message); memStarting = false; }
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
            var on = dests.Where(d => d.On.Checked).ToList();
            if (m == Mode.Live)
            {
                if (on.Count == 0)
                {
                    ShowPage(0);
                    MessageBox.Show(this, L.T("Yayının gideceği platformu seç: Platformlar sekmesinde en az birini aç."), "ValoLive");
                    return;
                }
                var missing = on.FirstOrDefault(d => d.Url.Text.Trim() == "" || d.Key.Text.Trim() == "");
                if (missing != null)
                {
                    ShowPage(0);
                    MessageBox.Show(this, L.F("{0}: Stream URL ve Stream Key gerekli.", missing.P.Name) + "\n" + missing.P.KeyHint, "ValoLive");
                    return;
                }
            }
            var q = Qualities[cfg.Quality];
            GameFrames.Enabled = cfg.EngineRegionReads;
            var o = new StreamOptions
            {
                Width = q.W, Height = q.H, BitrateK = q.Kbps,
                ScreenWidth = screenW, ScreenHeight = screenH,
                Denoise = cfg.Denoise,
                WindowCapture = cfg.WindowCapture && guard.Mode == CaptureMode.Valorant,
                Gpu = cfg.GpuEngine, GpuPriority = cfg.EngineGpuPriority,
                Overlay = overlay,
            };
            if (m == Mode.Live)
            {
                // Görüntü bir kez kodlanır; her açık platforma ayrı bağlantı (biri koparsa öbürleri sürer).
                o.Targets = on.Select(d => new Target { Id = d.P.Id, Name = d.P.Name, Url = d.P.Ingest(d.Url.Text, d.Key.Text) }).ToList();
            }
            else
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ValoLive");
                Directory.CreateDirectory(dir);
                o.ToFile = true; o.DurationSec = 15;
                o.Output = Path.Combine(dir, $"test_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                o.Targets = new List<Target> { new Target { Id = "file", Name = "Kayıt", Url = o.Output } };
                overlay.ShowBanner(L.T("ZAFER"), "Test · Jett · Ascent 13-9 · 24/12/5 · HS " + L.Pct(31), ColorTranslator.FromHtml("#3ddc97"), OverlayRenderer.AgentIcon("add6443a-41bd-e414-f6ad-e58d267f4e95"));
            }
            o.Layers = StartLayers(m == Mode.Test || KickOn, m == Mode.Test || YouTubeOn);
            if (funChat != null)
            {
                o.Flip = funChat.CanFlip = cfg.FunOn("flip");
                o.Zoom = funChat.CanZoom = cfg.FunOn("zoom");
            }
            if (m == Mode.Live) ApplyStreamInfo(false);
            lastOptions = o;
            mode = m;
            liveSince = DateTime.Now;
            foreach (var d in dests) { d.State = null; d.Attempts = 0; }
            ResetHealth();
            chips.Invalidate();
            SetInputsEnabled(false);
            int vmode = Diag.ValorantFullscreenMode();
            Diag.Begin($"{(m == Mode.Live ? "CANLI YAYIN (" + string.Join(" + ", on.Select(d => d.P.Name)) + ")" : "TEST KAYDI")} · ValoLive {File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm} · " +
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

        bool Launch()
        {
            try
            {
                overlay.Refresh(); // boştayken bekletilen kareyi tazele: Streamer başlarken Current ve Blank'i okur
                DisposeAudio();
                audio = new AudioSource(true) { Volume = trkGame.Value / 100f };
                audio.Start(cfg.OutputId);
                if (!string.IsNullOrEmpty(cfg.Mic))
                {
                    micAudio = new AudioSource(false) { Volume = trkMic.Value / 100f };
                    micAudio.Start(cfg.Mic);
                    micAudio.Effect = new VoiceRunner(); // ses değiştirici "yayında da" açıksa (VoiceFx.OnStream)
                }
                lastOptions.Masks = guard.FilterChain;
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
            // Platform kopuşlarını Streamer kendi içinde toparlar (her platform ayrı yeniden bağlanır, ekran yakalama
            // kopuşları da orada); buraya yayın tamamen bitince gelinir: durduruldu, test bitti ya da bağlı veya
            // bağlanmaya çalışan hiçbir platform kalmadı.
            var was = mode;
            var rejected = streamer.TargetStates().Where(s => s.Value == TargetState.Rejected && s.Key.Id != "file").Select(s => s.Key.Name).ToList();
            Finish();
            chips.Invalidate();
            Diag.End(!unexpected ? "normal bitiş" : was == Mode.Test ? "test başarısız" : rejected.Count > 0 ? string.Join(", ", rejected) + " bağlantıyı reddetti" : "yayın durdu (yeniden bağlanma tükendi)");
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
            else if (unexpected && rejected.Count > 0)
            {
                SetStatus(L.F("● {0} BAĞLANTIYI REDDETTİ", L.Upper(string.Join(" + ", rejected))), Red);
                lblStats.Text = L.T("Stream URL / Stream Key'i platformun panelinden yeniden kopyala");
            }
            else if (unexpected) { SetStatus(L.T("● YAYIN DURDU"), Red); lblStats.Text = Trim(err); }
            if (was == Mode.Live && unexpected) Alert("stop", L.T("Yayın durdu: hiçbir platforma bağlanılamıyor"), Red);
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
            foreach (var d in dests) d.On.Enabled = d.Url.Enabled = d.Key.Enabled = on;
            foreach (Control c in new Control[] { cmbQuality, cmbCapture, cmbApp, btnApps, cmbMic, chkDenoise, cmbOut, btnTest, chkWgc, chkGpu })
                c.Enabled = on;
            chkWgc.Enabled = on && guard.Mode == CaptureMode.Valorant;
            StreamPanel.Sync();
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
            if (mode == Mode.Live && streamer.Running) UpdateLiveStatus();
        }

        // ---------- Kısayollar ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, 1, 0x4000, (int)Keys.F9);
            RegisterHotKey(Handle, 2, 0x4000, (int)Keys.F10);
            RegisterHotKey(Handle, 3, 0x4000 | 0x0003, (int)Keys.V); // Ctrl+Alt+V: ses efekti aç/kapa
            RegisterHotKey(Handle, 4, 0x4000 | 0x0003, (int)Keys.B); // Ctrl+Alt+B: sonraki ses efekti
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                if ((int)m.WParam == 1) { overlay.Cover = !overlay.Cover; autoCovered = false; } // elle dokunulan perde artık elle yönetilir
                if ((int)m.WParam == 2) overlay.Summary = !overlay.Summary;
                if ((int)m.WParam == 3 || (int)m.WParam == 4) VoiceHotkey((int)m.WParam == 4);
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

        // ---------- Güncelleme (Updater.cs) ----------

        System.Threading.Timer updateTimer;
        Label lblSub;
        LinkLabel lnkUpdate;
        Updater.Release update;

        void CheckUpdate()
        {
            Updater.Cleanup(); // yeniden başlarken eski süreç exe'yi henüz bırakmamış olabilir: açılıştakini tamamlar
            try
            {
                var r = Updater.Check();
                if (r == null || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    if (update != null && update.Version >= r.Version) return;
                    update = r;
                    Diag.Event("GÜNCELLEME", $"yeni sürüm {r.Tag} (bu sürüm {Updater.Current})");
                    lnkUpdate.Text = L.F("Yeni sürüm {0} hazır · güncelle", r.Tag);
                    lnkUpdate.Visible = true; lblSub.Visible = false;
                }));
            }
            catch { } // çevrimdışı ya da GitHub yanıt vermedi: sonraki denemede
        }

        void UpdateClicked()
        {
            var r = update;
            if (r == null) return;
            if (mode != Mode.Idle) { MessageBox.Show(this, L.T("Güncelleme yayın bitince yapılabilir."), "ValoLive"); return; }
            // Paket yoksa ya da klasöre yazılamıyorsa (ör. Program Files) sürüm sayfası açılır, elle indirilir.
            if (string.IsNullOrEmpty(r.ZipUrl) || !Updater.CanWrite()) { Process.Start(r.Page); return; }
            if (MessageBox.Show(this, L.F("ValoLive {0} indirilip kurulsun mu? Uygulama kapanır ve yeni sürümle yeniden açılır.", r.Tag), "ValoLive",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            lnkUpdate.Enabled = false;
            btnLive.Enabled = btnTest.Enabled = false; // kurulum sürerken yayın başlamasın
            Task.Run(() =>
            {
                try
                {
                    Updater.Install(r, p => BeginInvoke((Action)(() => lnkUpdate.Text = L.F("İndiriliyor… %{0}", p))));
                    Diag.Event("GÜNCELLEME", $"{r.Tag} kuruldu, yeniden başlatılıyor");
                    BeginInvoke((Action)(() => { exiting = true; Application.Restart(); }));
                }
                catch (Exception e)
                {
                    Diag.Event("GÜNCELLEME", "kurulamadı: " + e.Message);
                    BeginInvoke((Action)(() =>
                    {
                        lnkUpdate.Enabled = true; btnLive.Enabled = btnTest.Enabled = true;
                        lnkUpdate.Text = L.F("Yeni sürüm {0} hazır · güncelle", r.Tag);
                        if (MessageBox.Show(this, L.F("Güncelleme kurulamadı: {0}\n\nSürüm sayfası açılsın mı? Oradan elle indirebilirsin.", e.Message), "ValoLive",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Process.Start(r.Page);
                    }));
                }
            });
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
                new ToolStripMenuItem(L.T("Yayın Paneli"), null, (s, e) => StreamPanel.Open(this, cfg)),
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
            UnregisterHotKey(Handle, 3);
            UnregisterHotKey(Handle, 4);
            StopVoice();
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
