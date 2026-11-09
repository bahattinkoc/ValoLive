using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Yayın Paneli: yayına binen her şeyin ayarları ana pencereden ayrı. İki sekme: katmanlar (radar, chat kutusu,
    /// maç sonu kartı, yayın anları…) ve chat eğlencesi (izleyici komutları). Ayarlar doğrudan Settings'e yazılır;
    /// ana pencere (MainForm) değişince uygular ve yayın durumunu Sync ile bildirir.
    /// </summary>
    sealed class StreamPanel : Form
    {
        static StreamPanel open;

        public static void Open(MainForm host, Settings cfg)
        {
            if (open == null || open.IsDisposed) { open = new StreamPanel(host, cfg); open.Show(); }
            else { if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal; open.Activate(); }
        }

        /// <summary>Yayın başladı/bitti, chat bağlantısı değişti: açıksa paneli tazele.</summary>
        public static void Sync()
        {
            var p = open;
            if (p != null && !p.IsDisposed && p.IsHandleCreated) try { p.BeginInvoke((Action)p.SyncState); } catch { }
        }

        static readonly int[] Cooldowns = { 0, 10, 20, 30, 60, 120 }, Goals = { 3, 5, 10, 15, 20, 30 }, Rains = { 2, 3, 5, 8, 10, 15 };

        readonly MainForm host;
        readonly Settings cfg;
        int ColW = 460;
        const int LabelW = 190;
        Panel pageLayers, pageFun;
        VButton tabLayers, tabFun;
        VToggle chkRadar, chkAutoCover, chkChat, chkPost, chkMoments, chkBot, chkBreak, chkFun, chkSubs;
        VButton btnAlign;
        FitLabel lblRadarPos, lblChat;
        Label lblFunHint;
        readonly Dictionary<string, VToggle> funToggles = new Dictionary<string, VToggle>();
        ComboBox cmbCooldown, cmbGoal, cmbRain;
        TextBox txtBlock;
        bool loading;

        StreamPanel(MainForm host, Settings cfg)
        {
            this.host = host; this.cfg = cfg;
            Text = L.T("ValoLive — Yayın Paneli");
            BackColor = Theme.Bg; ForeColor = Theme.Txt;
            Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = host.Icon;
            HandleCreated += (s, e) => Theme.DarkTitle(this);
            Build();
            SyncState();
        }

        // ---------- yerleşim ----------

        static string CmdText(FunChat.Def d)
        {
            if (d.Cmds.Length == 0) return L.T("kendiliğinden");
            if (d.Id == "balloon") return "!balloon  !pop";
            if (d.Id == "tomato") return "!tomato  !egg";
            return "!" + d.Cmds[0];
        }

        string tChat, tPost, tMoments, tBot, tBreak, tRadar, tAutoCover, tFun, tSubs;

        void Build()
        {
            tChat = L.T("Yayında chat kutusu"); tPost = L.T("Maç sonu kartı (ısı haritası + özet)");
            tMoments = L.T("Yayın anları: clutch, ilk kan, seri, ninja defuse, komik ölümler");
            tBot = L.F("Chat komutları ({0})", string.Join(" ", ChatBot.Commands)); tBreak = L.T("Menüde bekleme ekranı (sıra sayacı)");
            tRadar = L.T("Minimap'i radar rozetiyle ört"); tAutoCover = L.T("Ajan seçiminde sniper perdesi (otomatik)");
            tFun = L.T("Chat eğlencesi: izleyici komutları ekranda canlanır");
            tSubs = L.T("Ters ekran, büyüteç ve sesli okuma yalnızca abone ve modlara");
            int cmdW = FunChat.Defs.Max(d => TextRenderer.MeasureText(CmdText(d), Font).Width);
            int want = new[] { tChat, tPost, tMoments, tBot, tBreak, tRadar, tAutoCover, tSubs }.Max(t => VToggle.WidthFor(t, Font));
            want = Math.Max(want, FunChat.Defs.Max(d => VToggle.WidthFor(d.Label, Font)) + cmdW + 16);
            ColW = Math.Min(620, Math.Max(440, want + 4));
            int formW = ColW * 2 + 80;

            int tabW = Math.Max(Theme.ButtonWidth(L.T("KATMANLAR"), 34, 150), Theme.ButtonWidth(L.T("CHAT EĞLENCESİ"), 34, 150));
            tabLayers = Tab(L.T("KATMANLAR"), new Rectangle(20, 16, tabW, 34));
            tabFun = Tab(L.T("CHAT EĞLENCESİ"), new Rectangle(28 + tabW, 16, tabW, 34));
            tabLayers.Click += (s, e) => ShowPage(false);
            tabFun.Click += (s, e) => ShowPage(true);
            Controls.Add(new Panel { BackColor = Theme.Card, Location = new Point(20, 58), Size = new Size(formW - 40, 1) });

            const int pageTop = 70;
            pageLayers = new Panel { BackColor = Theme.Bg, Location = new Point(0, pageTop), Width = formW };
            pageFun = new Panel { BackColor = Theme.Bg, Location = new Point(0, pageTop), Width = formW };
            pageLayers.Height = BuildLayers(pageLayers);
            pageFun.Height = BuildFun(pageFun);
            Controls.AddRange(new Control[] { pageLayers, pageFun });
            foreach (var t in AllControls(this).OfType<VToggle>()) if (t.MaximumSize.Width == 0) t.MaximumSize = new Size(ColW, 0);
            foreach (var l in AllControls(this).OfType<Label>()) l.UseMnemonic = false;
            ClientSize = new Size(formW, pageTop + Math.Max(pageLayers.Height, pageFun.Height) + 10);
            ShowPage(false);
        }

        static IEnumerable<Control> AllControls(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(AllControls(x)));

        VButton Tab(string text, Rectangle r)
        {
            var b = new VButton { Text = text, Bounds = r, BackColor = Theme.Field, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 10f) };
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        void ShowPage(bool fun)
        {
            pageFun.Visible = fun; pageLayers.Visible = !fun;
            tabFun.BackColor = fun ? Theme.Red : Theme.Field;
            tabLayers.BackColor = fun ? Theme.Field : Theme.Red;
        }

        int Section(Control parent, string text, int x, int y)
        {
            parent.Controls.Add(new Panel { BackColor = Theme.Red, Location = new Point(x, y + 4), Size = new Size(4, 13) });
            parent.Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Theme.Txt, Font = Theme.Display(16), UseCompatibleTextRendering = true, AutoSize = true, Location = new Point(x + 11, y) });
            parent.Controls.Add(new Panel { BackColor = Theme.Card, Location = new Point(x, y + 22), Size = new Size(ColW, 1) });
            return y + 32;
        }

        VToggle Toggle(Control parent, string text, int x, ref int y)
        {
            var t = new VToggle { Text = text, ForeColor = Theme.Txt, AutoSize = true, Location = new Point(x, y - 4) };
            parent.Controls.Add(t);
            t.CheckedChanged += (s, e) => Changed();
            y += 26;
            return t;
        }

        Label Note(Control parent, string text, int x, ref int y, int w)
        {
            var l = new Label { Text = text, ForeColor = Theme.Mut, AutoSize = true, MaximumSize = new Size(w, 0), Location = new Point(x, y) };
            parent.Controls.Add(l);
            y += l.GetPreferredSize(new Size(w, 0)).Height + 8;
            return l;
        }

        void Caption(Control parent, string text, int x, int y) =>
            parent.Controls.Add(new Label { Text = Theme.Upper(text), ForeColor = Theme.Mut, Font = new Font(L.SansBold, 8.25f), AutoSize = true, Location = new Point(x, y + 5) });

        ComboBox Combo(Control parent, string label, int x, ref int y, IEnumerable<string> items)
        {
            Caption(parent, label, x, y);
            var c = new ComboBox { Location = new Point(x + LabelW, y), Width = ColW - LabelW, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Field, ForeColor = Theme.Txt };
            MainForm.DarkCombo(c);
            c.Items.AddRange(items.Cast<object>().ToArray());
            c.SelectedIndexChanged += (s, e) => Changed();
            parent.Controls.Add(c);
            y += 34;
            return c;
        }

        int BuildLayers(Panel p)
        {
            int x = 20, y = Section(p, L.T("YAYIN ÜSTÜ"), x, 0);
            chkChat = Toggle(p, tChat, x, ref y);
            int chatX = x + Math.Max(170, VToggle.WidthFor(tChat, Font) + 12);
            lblChat = new FitLabel { Text = "", ForeColor = Theme.Mut, Location = new Point(chatX, y - 27), Size = new Size(x + ColW - chatX, 20) };
            p.Controls.Add(lblChat);
            chkPost = Toggle(p, tPost, x, ref y);
            chkMoments = Toggle(p, tMoments, x, ref y);
            chkBot = Toggle(p, tBot, x, ref y);
            chkBreak = Toggle(p, tBreak, x, ref y);
            int left = y;

            x = ColW + 60;
            y = Section(p, L.T("RADAR ROZETİ"), x, 0);
            chkRadar = Toggle(p, tRadar, x, ref y);
            Caption(p, L.T("Rozet yeri"), x, y);
            int alignW = Theme.ButtonWidth(L.T("Minimap'e hizala"), 25, 120);
            int capW = TextRenderer.MeasureText(Theme.Upper(L.T("Rozet yeri")), new Font(L.SansBold, 8.25f)).Width + 12;
            lblRadarPos = new FitLabel { ForeColor = Theme.Txt, Location = new Point(x + capW, y + 3), Size = new Size(ColW - capW - alignW - 8, 20) };
            btnAlign = new VButton { Text = L.T("Minimap'e hizala"), Bounds = new Rectangle(x + ColW - alignW, y - 1, alignW, 25), BackColor = Theme.Field, ForeColor = Theme.Txt, FlatStyle = FlatStyle.Flat };
            btnAlign.FlatAppearance.BorderSize = 0;
            btnAlign.Click += (s, e) => { if (host.AlignRadar(this)) SyncState(); };
            p.Controls.AddRange(new Control[] { lblRadarPos, btnAlign });
            y += 34;
            y = Section(p, L.T("SNIPER KORUMASI"), x, y + 8);
            chkAutoCover = Toggle(p, tAutoCover, x, ref y);
            Note(p, L.T("F9: sniper perdesi · F10: yayın özeti"), x, ref y, ColW);

            y = Math.Max(left, y) + 6;
            p.Controls.Add(new Panel { BackColor = Theme.Card, Location = new Point(ColW + 40, 4), Size = new Size(1, y - 10) });
            Note(p, L.T("Yayın açıkken radar, chat kutusu, maç sonu kartı, yayın anları ve chat komutları değiştirilemez; yayın bitince uygulanır."), 20, ref y, ColW * 2 + 40);
            return y;
        }

        int BuildFun(Panel p)
        {
            int x = 20, y = 0;
            chkFun = Toggle(p, tFun, x, ref y);
            chkFun.Font = new Font(L.SansBold, 9.5f);
            lblFunHint = new Label { AutoSize = false, Size = new Size(ColW * 2 + 40, 38), Location = new Point(x, y), ForeColor = Theme.Mut };
            p.Controls.Add(lblFunHint);
            int top = y + 42;

            y = Section(p, L.T("MESAJ TAŞIYANLAR"), x, top);
            foreach (var id in new[] { "balloon", "plane", "bottle", "pigeon", "paper", "spray", "tomato", "sticker" }) FunRow(p, id, x, ref y);
            y = Section(p, L.T("AYARLAR"), x, y + 8);
            cmbCooldown = Combo(p, L.T("İzleyici başına bekleme"), x, ref y, Cooldowns.Select(c => c == 0 ? L.T("yok") : L.F("{0} sn", c)));
            Caption(p, L.T("Yasaklı kelimeler"), x, y);
            txtBlock = new TextBox { Location = new Point(x + LabelW, y), Width = ColW - LabelW, BackColor = Theme.Field, ForeColor = Theme.Txt, BorderStyle = BorderStyle.FixedSingle };
            txtBlock.TextChanged += (s, e) => Changed();
            p.Controls.Add(txtBlock);
            y += 30;
            Note(p, L.T("Virgülle ayır. Bağlantılar her zaman silinir."), x + LabelW, ref y, ColW - LabelW);
            int left = y;

            x = ColW + 60;
            y = Section(p, L.T("CHAT BİRLİKTE"), x, top);
            foreach (var id in new[] { "firework", "rain", "aquarium", "race", "say" }) FunRow(p, id, x, ref y);
            y = Section(p, L.T("EKRAN ŞAKALARI"), x, y + 8);
            foreach (var id in new[] { "flip", "zoom" }) FunRow(p, id, x, ref y);
            chkSubs = Toggle(p, tSubs, x, ref y);
            y += 6;
            cmbGoal = Combo(p, L.T("Havai fişek hedefi"), x, ref y, Goals.Select(g => L.F("{0} fişek", g)));
            cmbRain = Combo(p, L.T("Emoji yağmuru eşiği"), x, ref y, Rains.Select(r => L.F("{0} kişi", r)));

            y = Math.Max(left, y) + 6;
            p.Controls.Add(new Panel { BackColor = Theme.Card, Location = new Point(ColW + 40, top + 4), Size = new Size(1, y - top - 10) });
            int pvW = Theme.ButtonWidth(L.T("ÖNİZLEME"), 40, 170);
            var btnPreview = new VButton { Text = L.T("ÖNİZLEME"), Bounds = new Rectangle(20, y, pvW, 40), BackColor = Theme.Red, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font(L.SansBold, 11f) };
            btnPreview.FlatAppearance.BorderSize = 0;
            btnPreview.Click += (s, e) => FunPreview.Open(cfg);
            p.Controls.Add(btnPreview);
            int ny = y + 2;
            Note(p, L.T("Önizlemede her eğlenceyi kendin dene (yayın gerekmez). Ters ekran ve büyüteç yayın başlarken eklenir: yayın açıkken değiştirilemez."), 40 + pvW, ref ny, ColW * 2 + 20 - pvW);
            return Math.Max(y + 50, ny);
        }

        void FunRow(Control parent, string id, int x, ref int y)
        {
            var d = FunChat.Defs.First(z => z.Id == id);
            string cmd = CmdText(d);
            int cw = TextRenderer.MeasureText(cmd, Font).Width;
            var t = Toggle(parent, d.Label, x, ref y);
            t.MaximumSize = new Size(ColW - cw - 12, 0);
            parent.Controls.Add(new Label { Text = cmd, ForeColor = Theme.Mut, AutoSize = true, Location = new Point(x + ColW - cw, y - 27) });
            funToggles[id] = t;
        }

        // ---------- ayarlar ----------

        void Changed()
        {
            if (loading) return;
            cfg.ChatBox = chkChat.Checked; cfg.PostMatchCard = chkPost.Checked; cfg.Moments = chkMoments.Checked;
            cfg.ChatBot = chkBot.Checked; cfg.BreakScreen = chkBreak.Checked;
            cfg.MapCover = chkRadar.Checked; cfg.AutoCover = chkAutoCover.Checked;
            cfg.Fun = chkFun.Checked; cfg.FunSubsOnly = chkSubs.Checked;
            cfg.FunOff = string.Join(",", funToggles.Where(kv => !kv.Value.Checked).Select(kv => kv.Key));
            if (cmbCooldown.SelectedIndex >= 0) cfg.FunCooldown = Cooldowns[cmbCooldown.SelectedIndex];
            if (cmbGoal.SelectedIndex >= 0) cfg.FunGoal = Goals[cmbGoal.SelectedIndex];
            if (cmbRain.SelectedIndex >= 0) cfg.FunRain = Rains[cmbRain.SelectedIndex];
            cfg.FunBlock = txtBlock.Text;
            host.LayerSettingsChanged();
            SyncEnabled();
        }

        static int Nearest(int[] list, int v) => Array.IndexOf(list, list.OrderBy(x => Math.Abs(x - v)).First());

        void SyncState()
        {
            loading = true;
            try
            {
                chkChat.Checked = cfg.ChatBox; chkPost.Checked = cfg.PostMatchCard; chkMoments.Checked = cfg.Moments;
                chkBot.Checked = cfg.ChatBot; chkBreak.Checked = cfg.BreakScreen;
                chkRadar.Checked = cfg.MapCover; chkAutoCover.Checked = cfg.AutoCover;
                chkFun.Checked = cfg.Fun; chkSubs.Checked = cfg.FunSubsOnly;
                foreach (var kv in funToggles) kv.Value.Checked = !("," + cfg.FunOff + ",").Contains("," + kv.Key + ",");
                cmbCooldown.SelectedIndex = Nearest(Cooldowns, cfg.FunCooldown);
                cmbGoal.SelectedIndex = Nearest(Goals, cfg.FunGoal);
                cmbRain.SelectedIndex = Nearest(Rains, cfg.FunRain);
                if (!txtBlock.Focused) txtBlock.Text = cfg.FunBlock;
            }
            finally { loading = false; }
            var r = host.RadarRect();
            string full = L.F("çap {0} px · konum {1}, {2}", r.Width, r.X, r.Y);
            lblRadarPos.Text = TextRenderer.MeasureText(full, Font).Width * 0.78 <= lblRadarPos.Width ? full : $"Ø {r.Width} px · {r.X}, {r.Y}";
            var hint = host.ChatHint();
            lblChat.Text = hint.Key; lblChat.ForeColor = hint.Value;
            SyncEnabled();
        }

        void SyncEnabled()
        {
            bool streaming = host.Streaming;
            foreach (var c in new Control[] { chkRadar, chkChat, chkPost, chkMoments, chkBot }) c.Enabled = !streaming;
            btnAlign.Enabled = !streaming && cfg.MapCover;
            chkFun.Enabled = !streaming || host.FunRunning;
            foreach (var kv in funToggles) kv.Value.Enabled = cfg.Fun && (!streaming || !FunChat.Defs.First(d => d.Id == kv.Key).Graph);
            chkSubs.Enabled = cmbCooldown.Enabled = cmbGoal.Enabled = cmbRain.Enabled = txtBlock.Enabled = cfg.Fun;
            if (!host.HasChatAccount) { lblFunHint.Text = L.T("Komutları okumak için ana pencerede Kick ya da YouTube hesabını bağla."); lblFunHint.ForeColor = Theme.Red; }
            else if (streaming && !host.FunRunning) { lblFunHint.Text = L.T("Yayın bu ayar kapalıyken başladı: açmak için yayını yeniden başlat."); lblFunHint.ForeColor = Theme.Red; }
            else { lblFunHint.Text = L.T("İzleyiciler chat'e komut yazar, ekranda canlanır. Kanal puanı gerekmez; her izleyici için bekleme süresi var."); lblFunHint.ForeColor = Theme.Mut; }
        }
    }

    /// <summary>Chat eğlencesi önizlemesi: yayın açmadan her eğlenceyi sahte bir oyun görüntüsü üstünde dener.</summary>
    sealed class FunPreview : Form
    {
        static FunPreview open;

        public static void Open(Settings cfg)
        {
            if (open == null || open.IsDisposed) { open = new FunPreview(cfg); open.Show(); }
            else { if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal; open.Activate(); }
        }

        sealed class Canvas : Control
        {
            public Canvas() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        }

        const int SW = 1920, SH = 1080, CW = 960, CH = 540;
        static readonly string[] Names = { "kedi_sever", "valo_dede", "jett_main", "sage_abla", "ankara_reyna", "mor_ejder", "clutch_kral", "sessiz_omen", "gece_kusu", "pizza_sage" };
        static readonly string[] Emojis = { "😎", "😂", "🔥", "💀", "👑", "🤡", "🍕", "🐐" };

        readonly Settings cfg;
        readonly Speaker speaker = new Speaker();
        readonly FunLayer fx;
        readonly AquariumLayer tank;
        readonly FunChat chat;
        readonly Canvas canvas;
        readonly Timer timer = new Timer { Interval = 33 };
        readonly Random rnd = new Random();
        readonly Label lblInfo;
        Bitmap back;
        volatile bool flip, zoom;
        int userNo;

        FunPreview(Settings cfg)
        {
            this.cfg = cfg;
            Text = L.T("ValoLive — Chat eğlencesi önizleme");
            BackColor = Theme.Bg; ForeColor = Theme.Txt;
            Font = new Font(L.Sans, 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            HandleCreated += (s, e) => Theme.DarkTitle(this);

            fx = new FunLayer(SW, SH, 1f, speaker, false);
            tank = new AquariumLayer(1f, false) { Hidden = () => !cfg.FunOn("aquarium") };
            chat = new FunChat(cfg, fx, tank, speaker) { Preview = true, CanFlip = true, CanZoom = true };
            chat.Graph = (k, on) => { if (k == "flip") flip = on; else zoom = on; };
            canvas = new Canvas { Location = new Point(16, 16), Size = new Size(CW, CH) };
            canvas.Paint += PaintCanvas;
            Controls.Add(canvas);

            var flow = new FlowLayoutPanel { Location = new Point(16, CH + 28), Width = CW, BackColor = Theme.Bg, WrapContents = true };
            foreach (var (label, act) in Buttons())
            {
                var b = new VButton { Text = label, Size = new Size(Theme.ButtonWidth(label, 30, 96), 30), BackColor = Theme.Field, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 8) };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => act();
                flow.Controls.Add(b);
            }
            flow.Height = flow.GetPreferredSize(new Size(CW, 0)).Height;
            Controls.Add(flow);
            lblInfo = new Label { ForeColor = Theme.Mut, AutoSize = true, MaximumSize = new Size(CW, 0), Location = new Point(16, flow.Bottom + 4),
                Text = L.T("Önizlemede bekleme süresi yok. Kapalı eğlenceler burada da çalışmaz. Sesli okuma bilgisayarının hoparlöründen çıkar.") };
            Controls.Add(lblInfo);
            chat.Announce = s => { if (IsHandleCreated) BeginInvoke((Action)(() => lblInfo.Text = L.T("Chat'e gidecek duyuru: ") + s)); };
            ClientSize = new Size(CW + 32, lblInfo.Bottom + 36);
            timer.Tick += (s, e) => canvas.Invalidate();
            timer.Start();
            FormClosed += (s, e) => { timer.Dispose(); chat.Dispose(); fx.Dispose(); tank.Dispose(); speaker.Dispose(); back?.Dispose(); };
        }

        (string, Action)[] Buttons() => new (string, Action)[]
        {
            (L.T("Balon"), () => Msg("!balloon " + Sample())),
            (L.T("İğne"), () => Msg("!pop")),
            (L.T("Uçak"), () => Msg("!plane " + Sample())),
            (L.T("Şişe"), () => Msg("!bottle " + Sample())),
            (L.T("Güvercin"), () => Msg("!pigeon " + Sample())),
            (L.T("Kağıt uçak"), () => Msg("!paper " + Sample())),
            (L.T("Sprey"), () => Msg("!spray " + L.T("ACE GELİYOR"))),
            (L.T("Domates"), () => Msg("!tomato")),
            (L.T("Yumurta"), () => Msg("!egg")),
            (L.T("Çıkartma"), () => Msg("!sticker " + Emojis[rnd.Next(Emojis.Length)])),
            (L.T("Fişek"), () => Msg("!firework")),
            (L.T("Fişek (hepsi)"), () => { for (int i = 0; i < Math.Max(2, cfg.FunGoal); i++) Msg("!firework"); }),
            (L.T("Emoji yağmuru"), () => { for (int i = 0; i < Math.Max(2, cfg.FunRain); i++) Msg("🔥🔥"); }),
            (L.T("Yeni balık"), () => Msg(L.T("Selam chat!"))),
            (L.T("Ördek yarışı"), () => { for (int i = 0; i < 4; i++) Msg("!race"); }),
            (L.T("Sesli oku"), () => Msg("!say " + L.T("Merhaba chat, bu bir deneme!"))),
            (L.T("Ters ekran"), () => Msg("!flip")),
            (L.T("Büyüteç"), () => Msg("!zoom")),
        };

        string Sample()
        {
            var s = new[] { L.T("Selam chat! Bu yayın efsane 🔥"), L.T("Bu raund ace gelir mi? 🎯"), L.T("Bugün rank atlıyoruz 💪") };
            return s[rnd.Next(s.Length)];
        }

        void Msg(string text)
        {
            int i = userNo++;
            string user = Names[i % Names.Length] + (i >= Names.Length ? (i / Names.Length).ToString() : "");
            chat.Handle(new ChatMessage { User = user, Color = ColorTranslator.ToHtml(FunLayer.Palette[rnd.Next(FunLayer.Palette.Length)]), Text = text, Sub = rnd.Next(3) == 0 });
        }

        void PaintCanvas(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            if (back == null) back = Backdrop(CW, CH);
            var st = g.Save();
            if (flip) { g.TranslateTransform(CW, CH); g.RotateTransform(180); }
            g.DrawImage(back, 0, 0, CW, CH);
            if (zoom)
            {
                // Streamer'daki büyüteçle aynı ölçü (ekran 1080p, önizleme yarı boy)
                int z = Math.Max(60, (int)(140 * SH / 1080.0)) * 2 / 2;
                g.DrawImage(back, new Rectangle(CW / 2 - z, CH / 2 - z, 2 * z, 2 * z), new Rectangle(CW / 2 - z / 2, CH / 2 - z / 2, z, z), GraphicsUnit.Pixel);
            }
            g.Restore(st);
            fx.Render(g);
            var s2 = g.Save();
            g.ScaleTransform(CW / (float)SW, CH / (float)SH);
            g.TranslateTransform(tank.X, tank.Y);
            tank.Render(g);
            g.Restore(s2);
        }

        /// <summary>Sahte oyun görüntüsü: gökyüzü, iki bina, sandık, nişangâh, skor şeridi ve yetenek kutuları.</summary>
        static Bitmap Backdrop(int w, int h)
        {
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h / 2), ColorTranslator.FromHtml("#2b3a55"), ColorTranslator.FromHtml("#7d8ea3"), 90f)) g.FillRectangle(br, 0, 0, w, h / 2);
                using (var br = new LinearGradientBrush(new Rectangle(0, h / 2, w, h / 2), ColorTranslator.FromHtml("#5a5048"), ColorTranslator.FromHtml("#2a2420"), 90f)) g.FillRectangle(br, 0, h / 2, w, h / 2);
                using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
                    for (int i = -8; i <= 8; i++) g.DrawLine(pen, w / 2 + i * 12, h / 2, w / 2 + i * 140, h);
                using (var br = new SolidBrush(ColorTranslator.FromHtml("#8c7a66"))) g.FillPolygon(br, new[] { new Point(0, h / 7), new Point(w * 28 / 100, h / 4), new Point(w * 28 / 100, h * 66 / 100), new Point(0, h * 82 / 100) });
                using (var br = new SolidBrush(ColorTranslator.FromHtml("#6f604f"))) g.FillPolygon(br, new[] { new Point(w, h / 9), new Point(w * 70 / 100, h / 4), new Point(w * 70 / 100, h * 64 / 100), new Point(w, h * 80 / 100) });
                using (var br = new SolidBrush(ColorTranslator.FromHtml("#9a7442"))) g.FillRectangle(br, w * 44 / 100, h * 52 / 100, w * 12 / 100, h * 11 / 100);
                using (var pen = new Pen(ColorTranslator.FromHtml("#6b4f2c"), 2)) g.DrawRectangle(pen, w * 44 / 100, h * 52 / 100, w * 12 / 100, h * 11 / 100);
                using (var br = new SolidBrush(Color.FromArgb(200, 12, 16, 22)))
                {
                    g.FillEllipse(br, 5, 5, h * 33 / 100, h * 33 / 100);
                    g.FillRectangle(br, w * 36 / 100, 6, w * 28 / 100, h * 6 / 100);
                    for (int i = 0; i < 4; i++) g.FillRectangle(br, w * 40 / 100 + i * w * 5 / 100, h * 90 / 100, w * 4 / 100, h * 7 / 100);
                }
                using (var pen = new Pen(ColorTranslator.FromHtml("#00ff9c"), 2))
                {
                    g.DrawLine(pen, w / 2 - 8, h / 2, w / 2 - 3, h / 2); g.DrawLine(pen, w / 2 + 3, h / 2, w / 2 + 8, h / 2);
                    g.DrawLine(pen, w / 2, h / 2 - 8, w / 2, h / 2 - 3); g.DrawLine(pen, w / 2, h / 2 + 3, w / 2, h / 2 + 8);
                }
            }
            return bmp;
        }
    }
}
