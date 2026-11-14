using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ValoLive
{
    // Ana pencerenin SES DEĞİŞTİRİCİ sekmesi (Voice.cs): solda cihazlar ve kurulum, sağda efekt ızgarası.
    sealed partial class MainForm
    {
        VoiceChanger voice;
        CheckBox chkVoice, chkVoiceMon, chkVoiceStream;
        ComboBox cmbVoiceMic, cmbVoiceOut;
        LinkLabel lnkCable;
        Label lblVoiceHelp, lblVoiceState;
        VoiceGrid voiceGrid;
        Control[] voiceBelow; // ızgaranın altındakiler: özel ses eklenip silindikçe ızgarayla birlikte kayar
        VoiceMeter voiceMeter;
        List<KeyValuePair<string, string>> voiceMics, voiceOuts;
        readonly Timer voiceTimer = new Timer { Interval = 80 };

        void BuildVoicePage(Page page)
        {
            host = page;
            cx = 20;
            int y = Section(L.T("SES DEĞİŞTİRİCİ"), 4);
            chkVoice = new VToggle { Text = L.T("Ses değiştiriciyi aç (Valorant sesli sohbeti)"), ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkVoice); y += 30;
            cmbVoiceMic = AddCombo(L.T("Mikrofon"), ref y, new string[0]);
            cmbVoiceOut = AddCombo(L.T("Sanal mikrofon"), ref y, new string[0]);
            lnkCable = new LinkLabel { AutoSize = true, LinkColor = Red, ActiveLinkColor = Red, Location = new Point(FX, y - 2) };
            lnkCable.LinkClicked += (s, e) =>
            {
                FillVoiceDevices();
                if (!GoodCable) Process.Start("https://vb-audio.com/Cable/");
                else RestartVoice();
            };
            host.Controls.Add(lnkCable); y += 26;
            lblVoiceHelp = new Label { ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y) };
            host.Controls.Add(lblVoiceHelp);
            // Yardım metni cihaz adına göre uzar: en uzun hâline yer ayrılır.
            lblVoiceHelp.Text = VoiceHelp("Mikrofon (Steam Streaming Microphone)");
            y += lblVoiceHelp.GetPreferredSize(new Size(ColW, 0)).Height + 10;
            lblVoiceState = new FitLabel { Font = new Font(L.SansBold, 10f), ForeColor = Mut, Location = new Point(cx, y), Size = new Size(ColW, 22) };
            host.Controls.Add(lblVoiceState); y += 26;
            voiceMeter = new VoiceMeter { Location = new Point(cx, y), Size = new Size(ColW, 14) };
            host.Controls.Add(voiceMeter); y += 22;
            y = BuildSoundBoard(y + 6);
            int leftBottom = y;

            cx = ColW + 60;
            y = Section(L.T("EFEKTLER"), 4);
            voiceGrid = new VoiceGrid { Location = new Point(cx, y - 2), Width = ColW };
            voiceGrid.Height = voiceGrid.HeightFor(ColW);
            // Kapalıyken efekte tıklamak açar: yoksa seçim görünür ama hiçbir ses işlenmez (sanal mikrofon sessiz kalır).
            voiceGrid.Picked += id => { PickVoice(id); if (!chkVoice.Checked) chkVoice.Checked = true; };
            voiceGrid.AddNew += () => EditVoice(null);
            voiceGrid.Edit += id => EditVoice(cfg.VoiceCustoms.FirstOrDefault(v => v.Id == id));
            host.Controls.Add(voiceGrid);
            y += voiceGrid.Height + 8;
            chkVoiceMon = new VToggle { Text = L.T("Kendi sesimi kulaklıktan duy"), ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkVoiceMon); y += 26;
            chkVoiceStream = new VToggle { Text = L.T("Yayında da değişmiş sesim duyulsun"), ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkVoiceStream); y += 26;
            var keys = new Label { Text = L.T("Ctrl+Alt+V  efekti aç/kapat      Ctrl+Alt+B  sonraki efekt"), ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y) };
            host.Controls.Add(keys);
            y += keys.GetPreferredSize(new Size(ColW, 0)).Height + 4;
            var own = new Label { Text = L.T("Kendi sesini yap: \"+ Yeni ses\" · düzenlemek için sesine sağ tıkla"), ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y) };
            host.Controls.Add(own);
            y += own.GetPreferredSize(new Size(ColW, 0)).Height + 10;
            int firstPtt = host.Controls.Count;
            BuildPtt(ref y);
            voiceBelow = new Control[] { chkVoiceMon, chkVoiceStream, keys, own }.Concat(host.Controls.Cast<Control>().Skip(firstPtt)).ToArray();
            foreach (var c in voiceBelow) c.Tag = c.Top - voiceGrid.Bottom;
            AddDivider(Math.Max(leftBottom, y));
            page.Height = Math.Max(leftBottom, y);

            chkVoice.CheckedChanged += (s, e) => { if (loading) return; cfg.VoiceOn = chkVoice.Checked; SaveSettings(); RestartVoice(); };
            cmbVoiceMic.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.VoiceMic = voiceMics[Math.Max(0, cmbVoiceMic.SelectedIndex)].Key; SaveSettings(); RestartVoice(); };
            cmbVoiceOut.SelectedIndexChanged += (s, e) =>
            {
                if (loading || voiceOuts.Count == 0) return;
                cfg.VoiceOut = voiceOuts[Math.Max(0, cmbVoiceOut.SelectedIndex)].Key; SaveSettings(); RestartVoice();
            };
            chkVoiceMon.CheckedChanged += (s, e) => { if (loading) return; cfg.VoiceMonitor = chkVoiceMon.Checked; SaveSettings(); if (voice != null) voice.Monitor = cfg.VoiceMonitor; };
            chkVoiceStream.CheckedChanged += (s, e) => { if (loading) return; cfg.VoiceStream = chkVoiceStream.Checked; SaveSettings(); ApplyVoiceFlags(); };
            voiceTimer.Tick += (s, e) => TickVoice();
        }

        // ---------- Ses paneli ----------
        readonly Button[] boardButtons = new Button[SoundBoard.Max];
        Label lblBoardEmpty;
        FileSystemWatcher boardWatch;
        readonly Timer boardDebounce = new Timer { Interval = 800 };

        int BuildSoundBoard(int y)
        {
            y = Section(L.T("SES PANELİ"), y);
            var hint = new Label
            {
                Text = L.T("\"Ses bul\" ile myinstants'tan ekle ya da klasöre mp3 at. Sürükleyip sırala, sağ tıkla kaldır. Oyunda 1…9 numaralı tuşlar çalar, 0 susturur (Alt ya da Numpad: sağdaki ayar)."),
                ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y),
            };
            host.Controls.Add(hint);
            y += hint.GetPreferredSize(new Size(ColW, 0)).Height + 8;
            // Önce denetim satırı, altında düğmeler: az ses varken boşluk en altta kalır.
            int fw = Theme.ButtonWidth(L.T("Ses bul"), 24, 80), ow = Theme.ButtonWidth(L.T("Klasörü aç"), 24, 90), sw = Theme.ButtonWidth(L.T("Sustur"), 24, 70);
            var find = MakeButton(L.T("Ses bul"), new Rectangle(cx, y, fw, 30), Red);
            var open = MakeButton(L.T("Klasörü aç"), new Rectangle(cx + fw + 8, y, ow, 30), Field);
            var stop = MakeButton(L.T("Sustur"), new Rectangle(cx + fw + ow + 16, y, sw, 30), Field);
            find.Font = open.Font = stop.Font = new Font(L.SansBold, 9f);
            find.Click += (s, e) => FindSounds();
            int vx = cx + fw + ow + sw + 30;
            var volCap = new Label { Text = Theme.Upper(L.T("Ses")), ForeColor = Mut, Font = new Font(L.SansBold, 8.25f), AutoSize = true, Location = new Point(vx, y + 8) };
            host.Controls.Add(volCap);
            int sx = vx + volCap.PreferredWidth + 8;
            var vol = new VSlider { Location = new Point(sx, y + 4), Width = Math.Max(60, cx + ColW - sx), Height = 24, Minimum = 0, Maximum = 100 };
            host.Controls.Add(vol);
            y += 40;
            const int cols = 3, bh = 30, gap = 6;
            int bw = (ColW - gap * (cols - 1)) / cols;
            lblBoardEmpty = new Label { Text = L.T("Henüz ses yok: \"Klasörü aç\" ile ekle."), ForeColor = Mut, AutoSize = true, Location = new Point(cx, y + 6) };
            host.Controls.Add(lblBoardEmpty);
            for (int i = 0; i < boardButtons.Length; i++)
            {
                int k = i;
                var b = MakeButton("", new Rectangle(cx + i % cols * (bw + gap), y + i / cols * (bh + gap), bw, bh), Field);
                b.Font = new Font(L.SansBold, 9f); b.Visible = false;
                b.Click += (s, e) => { if (boardDragged) { boardDragged = false; return; } PlayBoard(k); };
                // Sürükle-bırak: basılı tutup başka bir düğmenin üstüne bırakınca o sıraya taşınır (tuş numaraları da).
                b.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { boardDragFrom = k; boardDragStart = Cursor.Position; boardDragged = false; } };
                b.MouseMove += (s, e) =>
                {
                    if (boardDragFrom != k || e.Button != MouseButtons.Left) return;
                    var p = Cursor.Position;
                    if (!boardDragged && Math.Abs(p.X - boardDragStart.X) + Math.Abs(p.Y - boardDragStart.Y) > 8) { boardDragged = true; b.Cursor = Cursors.SizeAll; }
                    if (boardDragged) HighlightBoard(BoardIndexAt(p));
                };
                b.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left || boardDragFrom != k) return;
                    b.Cursor = Cursors.Hand;
                    int to = boardDragged ? BoardIndexAt(Cursor.Position) : -1;
                    boardDragFrom = -1;
                    HighlightBoard(-1);
                    if (to >= 0 && to != k) { SoundBoard.Move(k, to); FillBoard(); }
                };
                // Sağ tık: panelden kaldır (dosya "kaldirilanlar" klasörüne taşınır, silinmez).
                b.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Right) return;
                    var snd = SoundBoard.Sounds;
                    if (k >= snd.Length) return;
                    var menu = new ContextMenuStrip();
                    menu.Items.Add(L.T("Panelden kaldır"), null, (s2, e2) => { try { SoundBoard.StopAll(); SoundBoard.Remove(snd[k]); } catch { } });
                    menu.Show(boardButtons[k], e.Location);
                };
                boardButtons[i] = b;
            }
            y += (boardButtons.Length + cols - 1) / cols * (bh + gap) + 4;
            chkBoardHud = new VToggle { Text = L.T("Oyunda kopya kâğıdı göster (Ctrl+Alt+N)"), ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkBoardHud); y += 28;
            cmbBoardHudPos = AddCombo(L.T("Yeri"), ref y, new[] { L.T("Sol orta (haritanın altı)"), L.T("Sağ orta"), L.T("Sol alt"), L.T("Sağ üst") });
            var hudHelp = new Label { Text = L.T("Tıklanmaz, oyunu bölmez, yayında görünmez. Valorant'ın ekran modu \"Pencereli tam ekran\" olmalı; \"Tam ekran\"da görünmez."), ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y) };
            host.Controls.Add(hudHelp);
            y += hudHelp.GetPreferredSize(new Size(ColW, 0)).Height + 4;
            chkBoardHud.CheckedChanged += (s, e) => { ShowBoardHud(chkBoardHud.Checked); if (loading) return; cfg.BoardHud = chkBoardHud.Checked; SaveSettings(); };
            cmbBoardHudPos.SelectedIndexChanged += (s, e) => { boardHud?.SetCorner(cmbBoardHudPos.SelectedIndex); if (loading) return; cfg.BoardHudPos = cmbBoardHudPos.SelectedIndex; SaveSettings(); };

            open.Click += (s, e) => { Directory.CreateDirectory(SoundBoard.Dir); Process.Start("explorer.exe", SoundBoard.Dir); };
            stop.Click += (s, e) => SoundBoard.StopAll();
            vol.Value = Math.Max(0, Math.Min(100, cfg.BoardVolume));
            SoundBoard.Volume = vol.Value / 100f;
            vol.ValueChanged += (s, e) => { SoundBoard.Volume = vol.Value / 100f; if (loading) return; cfg.BoardVolume = vol.Value; SaveSettings(); };

            SoundBoard.Loaded += () => { try { BeginInvoke((Action)FillBoard); } catch { } };
            boardDebounce.Tick += (s, e) => { boardDebounce.Stop(); SoundBoard.Reload(); };
            try
            {
                Directory.CreateDirectory(SoundBoard.Dir);
                // Klasöre dosya atılınca düğmeler kendiliğinden gelsin (kopyalama birkaç olay üretir: kısa bekleyip bir kez yüklenir).
                boardWatch = new FileSystemWatcher(SoundBoard.Dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (s, e) => { try { BeginInvoke((Action)(() => { boardDebounce.Stop(); boardDebounce.Start(); })); } catch { } };
                boardWatch.Created += h; boardWatch.Deleted += h; boardWatch.Changed += h;
                boardWatch.Renamed += (s, e) => h(s, e);
                boardWatch.EnableRaisingEvents = true;
            }
            catch { }
            SoundBoard.Reload();
            return y;
        }

        // ValoLive bas-konuş: Valorant'ta parti sohbeti "Otomatik"; sesin yalnızca bu tuşla, panel sesleri tuşsuz gider.
        // Tuşun durumu yalnızca okunur (GetAsyncKeyState); oyuna tuş gönderilmez.
        readonly KeyValuePair<int, string>[] PttKeys =
        {
            new KeyValuePair<int, string>(0x56, "V"), new KeyValuePair<int, string>(0x42, "B"), new KeyValuePair<int, string>(0x54, "T"),
            new KeyValuePair<int, string>(0x55, "U"), new KeyValuePair<int, string>(0x14, "Caps Lock"),
            new KeyValuePair<int, string>(0x05, L.T("Fare yan tuşu (geri)")), new KeyValuePair<int, string>(0x06, L.T("Fare yan tuşu (ileri)")),
        };
        VToggle chkPtt;
        ComboBox cmbPttKey, cmbBoardKeys;

        void BuildPtt(ref int y)
        {
            chkPtt = new VToggle { Text = L.T("ValoLive bas-konuş (panel sesleri tuşsuz gider)"), ForeColor = Txt, AutoSize = true, Location = new Point(cx, y - 4) };
            host.Controls.Add(chkPtt); y += 30;
            cmbPttKey = AddCombo(L.T("Bas-konuş tuşu"), ref y, PttKeys.Select(k => k.Value).ToArray());
            cmbBoardKeys = AddCombo(L.T("Panel tuşları"), ref y, new[] { "Alt + 1…9", "Numpad 1…9" });
            var help = new Label
            {
                Text = L.T("Bas-konuş açıkken Valorant → Ayarlar → Ses → Ses Sohbeti → Parti ses sohbeti etkinleştirme modu: Otomatik yap; arkadaşların sesleri tuşsuz duyar. Takım sohbeti yalnızca Valorant'ın bas-konuşuyla çalışır: rastgele takımda V'yi tutup paneli Numpad ile çalmak en rahatı."),
                ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y),
            };
            host.Controls.Add(help);
            y += help.GetPreferredSize(new Size(ColW, 0)).Height + 4;

            chkPtt.CheckedChanged += (s, e) => { if (loading) return; cfg.PttOn = chkPtt.Checked; SaveSettings(); ApplyPtt(); };
            cmbPttKey.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.PttKey = PttKeys[Math.Max(0, cmbPttKey.SelectedIndex)].Key; SaveSettings(); ApplyPtt(); };
            cmbBoardKeys.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.BoardNumpad = cmbBoardKeys.SelectedIndex == 1; SaveSettings(); RegisterBoardKeys(); };
        }

        VToggle chkBoardHud;
        ComboBox cmbBoardHudPos;
        BoardHud boardHud;

        void ShowBoardHud(bool on)
        {
            if (on && boardHud == null) { boardHud = new BoardHud(() => cfg.BoardNumpad, Math.Max(0, cmbBoardHudPos.SelectedIndex)); boardHud.Show(); }
            else if (!on && boardHud != null) { boardHud.Close(); boardHud = null; }
        }

        void LoadPtt()
        {
            cmbBoardHudPos.SelectedIndex = Math.Max(0, Math.Min(3, cfg.BoardHudPos));
            chkBoardHud.Checked = cfg.BoardHud;
            chkPtt.Checked = cfg.PttOn;
            cmbPttKey.SelectedIndex = Math.Max(0, Array.FindIndex(PttKeys, k => k.Key == cfg.PttKey));
            cmbBoardKeys.SelectedIndex = cfg.BoardNumpad ? 1 : 0;
            ApplyPtt();
            RegisterBoardKeys();
        }

        void ApplyPtt()
        {
            VoiceFx.PttKey = cfg.PttKey;
            VoiceFx.PttOn = cfg.PttOn;
            cmbPttKey.Enabled = cfg.PttOn;
        }

        int boardDragFrom = -1;
        Point boardDragStart;
        bool boardDragged;

        /// <summary>Ekran noktasının üstündeki görünür panel düğmesi (yoksa -1).</summary>
        int BoardIndexAt(Point screen)
        {
            for (int i = 0; i < boardButtons.Length; i++)
            {
                var b = boardButtons[i];
                if (b.Visible && b.RectangleToScreen(b.ClientRectangle).Contains(screen)) return i;
            }
            return -1;
        }

        void HighlightBoard(int target)
        {
            for (int i = 0; i < boardButtons.Length; i++)
            {
                var c = i == target && i != boardDragFrom ? Red : Field;
                if (boardButtons[i].BackColor != c) boardButtons[i].BackColor = c;
            }
        }

        void FillBoard()
        {
            var s = SoundBoard.Sounds;
            for (int i = 0; i < boardButtons.Length; i++)
            {
                var b = boardButtons[i];
                b.Visible = i < s.Length;
                if (i >= s.Length) continue;
                string name = s[i].Name.Length > 22 ? s[i].Name.Substring(0, 21) + "…" : s[i].Name;
                b.Text = i < 9 ? (i + 1) + " · " + name : name;
            }
            lblBoardEmpty.Visible = s.Length == 0;
        }

        void PlayBoard(int i)
        {
            if (!chkVoice.Checked) chkVoice.Checked = true;
            if (voice == null) { Toast.Show(L.T("SES PANELİ"), new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(L.T("Sesler sanal mikrofondan gider: önce ses değiştiriciyi aç."), Mut) }, 2500, false); return; }
            SoundBoard.Trigger(i);
        }

        /// <summary>Alt+1…9: ses, Alt+0: sustur (oyundayken).</summary>
        void BoardHotkey(int n)
        {
            if (n == 0) { SoundBoard.StopAll(); return; }
            if (!cfg.VoiceOn || voice == null) { Toast.Show(L.T("SES PANELİ"), new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(L.T("Kapalı: ValoLive → Ses Değiştirici sekmesinden aç."), Mut) }, 2500, false); return; }
            SoundBoard.Trigger(n - 1);
        }

        /// <summary>"Hoparlör (Steam Streaming Microphone)" → "Steam Streaming Microphone": kablonun hangi ucu olduğu kafa karıştırmasın.</summary>
        static string CableName(string n)
        {
            int a = n.IndexOf('('), b = n.LastIndexOf(')');
            return a >= 0 && b > a + 1 ? n.Substring(a + 1, b - a - 1) : n;
        }

        // Steam'in sanal mikrofonu kendi testlerimizde temiz ama Valorant'ta her efekti aynı boğuk sese çeviriyor
        // (Valorant o cihazı okuyor; nedeni bilinmiyor). VB-CABLE sorunsuz: Steam varsa bile indirme önerilir.
        static bool IsSteam(string n) => n != null && n.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0;
        bool GoodCable => voiceOuts != null && voiceOuts.Any(d => !IsSteam(d.Value));

        static string VoiceHelp(string captureName) => IsSteam(captureName) ? VoiceHelpText(captureName) + " " + L.T("Uyarı: Steam'in sanal mikrofonu Valorant'ta boğuk duyulur. Ücretsiz VB-CABLE'ı kur, ValoLive onu kendiliğinden seçer.") : VoiceHelpText(captureName);

        static string VoiceHelpText(string captureName) =>
            L.F("Valorant → Ayarlar → Ses → Ses Sohbeti → Giriş cihazı: \"{0}\" seç. ValoLive kapalıyken takımın seni duymaz; o zaman giriş cihazını eski mikrofonuna geri al.", captureName);

        /// <summary>Mikrofon ve sanal kablo listelerini doldurur (sekme açılınca da: yeni kurulan kablo görünsün).</summary>
        void FillVoiceDevices()
        {
            bool was = loading;
            loading = true;
            try
            {
                // Sanal kablonun kendi mikrofon ucu kaynak olamaz (ses kendine döner).
                var cableEnds = new HashSet<string>(VoiceChanger.VirtualOutputs().Select(v => VoiceChanger.CaptureSideOf(v.Value)));
                voiceMics = AudioDevices.Endpoints(false).Where(d => !cableEnds.Contains(d.Value)).ToList();
                voiceMics.Insert(0, new KeyValuePair<string, string>("", L.T("Varsayılan mikrofon")));
                cmbVoiceMic.Items.Clear();
                cmbVoiceMic.Items.AddRange(voiceMics.Select(d => (object)d.Value).ToArray());
                // Seçilmediyse yayın mikrofonu (çoğunlukla aynı mikrofon).
                int mi = voiceMics.FindIndex(d => d.Key == (cfg.VoiceMic ?? cfg.Mic ?? ""));
                cmbVoiceMic.SelectedIndex = Math.Max(0, mi);
                FitDropDown(cmbVoiceMic);

                voiceOuts = VoiceChanger.VirtualOutputs();
                cmbVoiceOut.Items.Clear();
                if (voiceOuts.Count == 0) cmbVoiceOut.Items.Add(L.T("(sanal mikrofon bulunamadı)"));
                else cmbVoiceOut.Items.AddRange(voiceOuts.Select(d => (object)CableName(d.Value)).ToArray());
                cmbVoiceOut.SelectedIndex = Math.Max(0, voiceOuts.FindIndex(d => d.Key == cfg.VoiceOut));
                FitDropDown(cmbVoiceOut);
                lnkCable.Text = !GoodCable ? L.T("Ücretsiz sanal mikrofonu indir (VB-CABLE); kurduktan sonra buraya tıkla") : L.T("Cihazları yenile");
                lblVoiceHelp.Text = voiceOuts.Count == 0
                    ? L.T("Sanal mikrofon, ValoLive'ın değiştirdiği sesi Valorant'a mikrofon gibi verir. Kurulumdan sonra bilgisayarı yeniden başlatman gerekebilir.")
                    : VoiceHelp(VoiceChanger.CaptureSideOf(SelectedVoiceOut().Value));
            }
            finally { loading = was; }
        }

        KeyValuePair<string, string> SelectedVoiceOut() =>
            voiceOuts.Count == 0 ? default : voiceOuts[Math.Max(0, Math.Min(voiceOuts.Count - 1, cmbVoiceOut.SelectedIndex))];

        void LoadVoice()
        {
            chkVoice.Checked = cfg.VoiceOn;
            chkVoiceMon.Checked = cfg.VoiceMonitor;
            chkVoiceStream.Checked = cfg.VoiceStream;
            LoadPtt();
            if (cfg.VoiceCustoms == null) cfg.VoiceCustoms = new List<VoiceCustom>();
            VoiceFx.SetCustom(cfg.VoiceCustoms);
            VoiceFx.Current = cfg.VoicePreset;
            LayoutVoiceGrid();
            FillVoiceDevices();
            RestartVoice();
        }


        /// <summary>Izgara gerçek satır sayısına iner, altındakiler hemen altına kayar (yer en çok özel sesle dolu hâline göre ayrılmıştı).</summary>
        void LayoutVoiceGrid()
        {
            int used = voiceGrid.UsedHeight;
            voiceGrid.Height = used;
            foreach (var c in voiceBelow) c.Top = voiceGrid.Top + used + (int)c.Tag;
            voiceGrid.Invalidate();
        }
        void ApplyVoiceFlags()
        {
            VoiceFx.OnStream = cfg.VoiceOn && cfg.VoiceStream;
            voiceGrid.Invalidate();
        }

        void StopVoice()
        {
            voice?.Dispose(); voice = null;
            voiceTimer.Stop();
            voiceMeter.Set(0, 0);
        }

        void RestartVoice()
        {
            StopVoice();
            ApplyVoiceFlags();
            voiceGrid.Active = cfg.VoiceOn;
            if (!cfg.VoiceOn) { VoiceState(L.T("● Kapalı"), Mut); return; }
            var outDev = SelectedVoiceOut();
            if (outDev.Key == null) { VoiceState(L.T("● Sanal mikrofon bulunamadı"), Amber); return; }
            try
            {
                voice = new VoiceChanger(voiceMics[Math.Max(0, cmbVoiceMic.SelectedIndex)].Key, outDev.Key) { Monitor = cfg.VoiceMonitor };
                voice.Start();
                voiceTimer.Start();
                VoiceRunning();
            }
            catch (Exception e)
            {
                voice?.Dispose(); voice = null;
                VoiceState(L.F("● Hata: {0}", e.Message), Red);
            }
            lblVoiceHelp.Text = VoiceHelp(VoiceChanger.CaptureSideOf(outDev.Value));
        }

        void VoiceRunning() => VoiceState(L.F("● Çalışıyor: {0}", VoiceFx.Find(VoiceFx.Current).Name), Green);

        void VoiceState(string text, Color c) { lblVoiceState.Text = text; lblVoiceState.ForeColor = c; }

        void TickVoice()
        {
            var v = voice;
            if (v == null) return;
            if (v.Problem != null) VoiceState(L.F("● Sorun: {0}", v.Problem), Amber);
            else if (lblVoiceState.ForeColor != Green) VoiceRunning();
            if (pages[3].Visible) voiceMeter.Set(v.InLevel, v.OutLevel);
        }

        void PickVoice(string id)
        {
            VoiceFx.Current = id;
            cfg.VoicePreset = VoiceFx.Current;
            if (id != "normal") cfg.VoiceLast = id;
            SaveSettings();
            voiceGrid.Invalidate();
            if (voice != null) VoiceRunning();
        }

        /// <summary>Kısayollar: Ctrl+Alt+V efekt aç/kapa (normal ↔ son efekt), Ctrl+Alt+B sonraki efekt.</summary>
        void VoiceHotkey(bool next)
        {
            if (!cfg.VoiceOn) { Toast.Show(L.T("SES DEĞİŞTİRİCİ"), new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(L.T("Kapalı: ValoLive → Ses Değiştirici sekmesinden aç."), Mut) }, 2500, false); return; }
            string id;
            if (next)
            {
                var list = VoiceFx.All.Where(p => p.Id != "normal").ToList();
                int i = list.FindIndex(p => p.Id == VoiceFx.Current);
                id = list[(i + 1) % list.Count].Id;
            }
            else id = VoiceFx.Current == "normal" ? VoiceFx.Find(cfg.VoiceLast).Id : "normal";
            PickVoice(id);
            var p0 = VoiceFx.Find(VoiceFx.Current);
            Toast.Show(L.T("SES DEĞİŞTİRİCİ"), new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(p0.Name, p0.Id == "normal" ? Mut : Green) }, 1500, false);
        }

        /// <summary>Efekt düğmeleri: dört sütun; seçili olan kırmızı. Ses değiştirici kapalıyken soluk. Sonda kullanıcının kendi
        /// sesleri (sağ tık: düzenle) ve yer varsa "+ Yeni ses".</summary>
        sealed class VoiceGrid : Control
        {
            const int Cols = 4, RowH = 36, Gap = 8;
            int hover = -1;
            public bool Active;
            public event Action<string> Picked, Edit;
            public event Action AddNew;

            public VoiceGrid() { DoubleBuffered = true; BackColor = Theme.Bg; Cursor = Cursors.Hand; }

            // Yer, en çok özel sesle dolu hâline göre ayrılır (sayfa yüksekliği sonradan değişmesin).
            public int HeightFor(int w) => (VoiceFx.BuiltinCount + VoiceFx.MaxCustom + Cols - 1) / Cols * (RowH + Gap) - Gap;
            public int UsedHeight => (Count + Cols - 1) / Cols * (RowH + Gap) - Gap;

            static bool CanAdd => VoiceFx.All.Length - VoiceFx.BuiltinCount < VoiceFx.MaxCustom;
            static int Count => VoiceFx.All.Length + (CanAdd ? 1 : 0);

            Rectangle Cell(int i)
            {
                int w = (Width - (Cols - 1) * Gap) / Cols;
                return new Rectangle(i % Cols * (w + Gap), i / Cols * (RowH + Gap), w, RowH);
            }

            int At(Point p)
            {
                for (int i = 0; i < Count; i++) if (Cell(i).Contains(p)) return i;
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
            protected override void OnMouseClick(MouseEventArgs e)
            {
                int i = At(e.Location);
                var all = VoiceFx.All;
                if (i < 0) return;
                if (i >= all.Length) { AddNew?.Invoke(); return; }
                if (e.Button == MouseButtons.Right) { if (all[i].Custom != null) Edit?.Invoke(all[i].Id); }
                else Picked?.Invoke(all[i].Id);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var all = VoiceFx.All;
                using (var f = new Font(L.SansBold, 9.5f))
                    for (int i = 0; i < Count; i++)
                    {
                        var r = Cell(i);
                        if (i >= all.Length)
                        {
                            using (var b = new SolidBrush(i == hover ? Theme.Line : Theme.Bg)) g.FillRectangle(b, r);
                            using (var pen = new Pen(Theme.Line) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                            string add = "+ " + L.T("Yeni ses");
                            float aw = Math.Min(g.MeasureString(add, f).Width, r.Width - 12);
                            Theme.DrawFit(g, add, f, Active ? Theme.Txt : Theme.Mut, r.X + (r.Width - aw) / 2, r.Y + (RowH - f.Height) / 2f, r.Width - 12);
                            continue;
                        }
                        var p = all[i];
                        bool sel = p.Id == VoiceFx.Current;
                        Color fill = sel ? (Active ? Theme.Red : ColorTranslator.FromHtml("#5b2430")) : i == hover ? Theme.Line : Theme.Field;
                        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                        if (!sel) using (var pen = new Pen(p.Custom != null ? ColorTranslator.FromHtml("#6b5a2a") : Theme.Line)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                        var c = sel ? Color.White : Active ? Theme.Txt : Theme.Mut;
                        string t = p.Custom != null ? "★ " + p.Name : p.Name;
                        float tw = Math.Min(g.MeasureString(t, f).Width, r.Width - 12);
                        Theme.DrawFit(g, t, f, c, r.X + (r.Width - tw) / 2, r.Y + (RowH - f.Height) / 2f, r.Width - 12);
                    }
            }
        }

        /// <summary>Giriş (mikrofon) ve çıkış (efektli ses) seviyesi: iki ince çubuk.</summary>
        sealed class VoiceMeter : Control
        {
            float inL, outL;
            public VoiceMeter() { DoubleBuffered = true; BackColor = Theme.Bg; }
            public void Set(float i, float o) { inL = i; outL = o; Invalidate(); }

            static float Pos(float v) => v <= 0 ? 0 : Math.Max(0, Math.Min(1, (float)(20 * Math.Log10(v) + 60) / 60)); // -60..0 dB

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int h = (Height - 2) / 2;
                using (var bg = new SolidBrush(Theme.Field))
                {
                    g.FillRectangle(bg, 0, 0, Width, h);
                    g.FillRectangle(bg, 0, h + 2, Width, h);
                }
                using (var a = new SolidBrush(Theme.Mut)) g.FillRectangle(a, 0, 0, Width * Pos(inL), h);
                using (var b = new SolidBrush(ColorTranslator.FromHtml("#3ddc97"))) g.FillRectangle(b, 0, h + 2, Width * Pos(outL), h);
            }
        }
    }
}
