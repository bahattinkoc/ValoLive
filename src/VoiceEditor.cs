using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ValoLive
{
    // Ses değiştirici: kullanıcının kendi sesini oluşturduğu/düzenlediği pencere. Her değişiklik anında duyulur;
    // "İptal" ya da pencereyi kapatmak her şeyi açılıştaki hâline döndürür.
    sealed partial class MainForm
    {
        static readonly Random voiceRnd = new Random();

        /// <summary>existing null ise yeni (rastgele başlayan) bir ses oluşturur.</summary>
        void EditVoice(VoiceCustom existing)
        {
            var before = cfg.VoiceCustoms.Select(v => v.Clone()).ToList();
            string beforePreset = VoiceFx.Current;
            var c = existing;
            if (c == null)
            {
                if (cfg.VoiceCustoms.Count >= VoiceFx.MaxCustom) return;
                int n = 1;
                while (cfg.VoiceCustoms.Any(v => v.Name == L.F("Sesim {0}", n))) n++;
                c = new VoiceCustom { Id = "ozel-" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = L.F("Sesim {0}", n) };
                VoiceFx.Randomize(c, voiceRnd);
                cfg.VoiceCustoms.Add(c);
            }
            VoiceFx.SetCustom(cfg.VoiceCustoms);
            VoiceFx.Current = c.Id;
            if (!chkVoice.Checked) chkVoice.Checked = true;
            LayoutVoiceGrid();
            if (voice != null) VoiceRunning();

            var f = new Form
            {
                Text = L.T("Kendi sesin"), BackColor = Bg, ForeColor = Txt, Font = Font,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            };
            f.HandleCreated += (s, e) => Theme.DarkTitle(f);
            var oldHost = host; int oldCx = cx;
            host = f; cx = 20;
            bool done = false;
            try
            {
                int y = Section(L.T("KENDİ SESİN"), 16);
                var name = AddText(L.T("Ad"), ref y, false);
                name.MaxLength = 24;
                var pitch = EditSlider(L.T("Perde"), ref y, -12, 12, v => L.F("{0} yarım ton", v > 0 ? "+" + v : v.ToString()));
                var formant = EditSlider(L.T("Ses rengi"), ref y, -30, 50, v => v > 0 ? "+" + v + "%" : v + "%");
                var robot = EditSlider(L.T("Robot"), ref y, 0, 100, v => L.Pct(v));
                var breath = EditSlider(L.T("Fısıltı"), ref y, 0, 100, v => L.Pct(v));
                var shake = EditSlider(L.T("Titreme"), ref y, 0, 100, v => L.Pct(v));
                var place = AddCombo(L.T("Ortam"), ref y, VoiceFx.Places.Select(p => p.Value).ToArray());
                var amount = EditSlider(L.T("Ortam miktarı"), ref y, 0, 100, v => L.Pct(v));
                var hint = new Label
                {
                    Text = L.T("Değişiklikleri anında duyarsın: \"Kendi sesimi kulaklıktan duy\" açıkken konuşarak dene. Kalın ses için perde ve rengi birlikte indir, ince ses için birlikte artır."),
                    ForeColor = Mut, AutoSize = true, MaximumSize = new Size(ColW, 0), Location = new Point(cx, y + 2),
                };
                f.Controls.Add(hint);
                y += hint.GetPreferredSize(new Size(ColW, 0)).Height + 16;

                int bw = Theme.ButtonWidth(L.T("Rastgele"), 28, 96);
                var rnd = MakeButton(L.T("Rastgele"), new Rectangle(cx, y, bw, 34), Field);
                rnd.Font = new Font(L.SansBold, 10f);
                Button del = null;
                if (existing != null)
                {
                    del = MakeButton(L.T("Sil"), new Rectangle(cx + bw + 8, y, Theme.ButtonWidth(L.T("Sil"), 28, 70), 34), Field);
                    del.Font = new Font(L.SansBold, 10f); del.ForeColor = Red;
                }
                int sw = Theme.ButtonWidth(L.T("Kaydet"), 28, 96), cw = Theme.ButtonWidth(L.T("İptal"), 28, 80);
                var save = MakeButton(L.T("Kaydet"), new Rectangle(cx + ColW - sw, y, sw, 34), Red);
                var cancel = MakeButton(L.T("İptal"), new Rectangle(cx + ColW - sw - 8 - cw, y, cw, 34), Field);
                save.Font = cancel.Font = new Font(L.SansBold, 10f);
                f.ClientSize = new Size(ColW + 40, y + 34 + 18);
                f.AcceptButton = save; f.CancelButton = cancel;

                bool filling = false;
                Action fill = () =>
                {
                    filling = true;
                    name.Text = c.Name; pitch.Value = c.Pitch; formant.Value = c.Formant; robot.Value = c.Robot; breath.Value = c.Breath;
                    shake.Value = c.Shake; amount.Value = c.Amount;
                    place.SelectedIndex = Math.Max(0, Array.FindIndex(VoiceFx.Places, p => p.Key == c.Place));
                    filling = false;
                    amount.Enabled = c.Place == "oda" || c.Place == "banyo" || c.Place == "salon" || c.Place == "magara" || c.Place == "stadyum" || c.Place == "kafe";
                };
                Action apply = () =>
                {
                    if (filling) return;
                    c.Name = string.IsNullOrWhiteSpace(name.Text) ? L.T("Sesim") : name.Text.Trim();
                    c.Pitch = pitch.Value; c.Formant = formant.Value; c.Robot = robot.Value; c.Breath = breath.Value; c.Shake = shake.Value;
                    c.Place = VoiceFx.Places[Math.Max(0, place.SelectedIndex)].Key; c.Amount = amount.Value;
                    amount.Enabled = c.Place == "oda" || c.Place == "banyo" || c.Place == "salon" || c.Place == "magara" || c.Place == "stadyum" || c.Place == "kafe";
                    VoiceFx.SetCustom(cfg.VoiceCustoms);
                    voiceGrid.Invalidate();
                    if (voice != null) VoiceRunning();
                };
                fill();
                name.TextChanged += (s, e) => apply();
                foreach (var sl in new[] { pitch, formant, robot, breath, shake, amount }) sl.ValueChanged += (s, e) => apply();
                place.SelectedIndexChanged += (s, e) => apply();
                rnd.Click += (s, e) => { string keep = c.Name; VoiceFx.Randomize(c, voiceRnd); c.Name = keep; fill(); apply(); };
                save.Click += (s, e) =>
                {
                    done = true;
                    cfg.VoicePreset = cfg.VoiceLast = c.Id;
                    SaveSettings();
                    f.Close();
                };
                if (del != null)
                    del.Click += (s, e) =>
                    {
                        done = true;
                        cfg.VoiceCustoms.RemoveAll(v => v.Id == c.Id);
                        VoiceFx.SetCustom(cfg.VoiceCustoms);
                        if (cfg.VoiceLast == c.Id) cfg.VoiceLast = "robot";
                        PickVoice(beforePreset == c.Id ? "normal" : beforePreset);
                        f.Close();
                    };
                cancel.Click += (s, e) => f.Close();
                f.FormClosed += (s, e) =>
                {
                    if (!done)
                    {
                        cfg.VoiceCustoms = before;
                        VoiceFx.SetCustom(before);
                        VoiceFx.Current = beforePreset;
                    }
                    LayoutVoiceGrid();
                    if (voice != null) VoiceRunning();
                };
            }
            finally { host = oldHost; cx = oldCx; }
            f.ShowDialog(this);
            f.Dispose();
        }

        /// <summary>Başlıklı kaydırıcı; değer sağında yazıyla.</summary>
        VSlider EditSlider(string label, ref int y, int min, int max, Func<int, string> fmt)
        {
            Caption(label, y);
            var t = new VSlider { Location = new Point(FX, y + 1), Width = FW - 92, Height = 24, Minimum = min, Maximum = max };
            var v = new Label { ForeColor = Txt, AutoSize = true, Location = new Point(FX + FW - 86, y + 5) };
            t.ValueChanged += (s, e) => v.Text = fmt(t.Value);
            v.Text = fmt(t.Value);
            host.Controls.AddRange(new Control[] { t, v });
            y += 32;
            return t;
        }
    }
}
