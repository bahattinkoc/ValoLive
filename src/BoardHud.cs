using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Oyun içi ses paneli kopya kâğıdı: hangi tuş hangi ses, çalan kırmızı. Tıklanmaz (fare arkasındaki oyuna geçer),
    /// odak çalmaz, yayına/kayda girmez. Valorant "tam ekran" (exclusive) modundaysa görünmez; "pencereli tam ekran" gerekir.
    /// </summary>
    sealed class BoardHud : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        const int RowH = 22, Pad = 10, Head = 26;
        readonly Timer tick = new Timer { Interval = 120 };
        string sig = "";
        Func<bool> numpad;
        int corner;

        public BoardHud(Func<bool> numpad, int corner)
        {
            this.numpad = numpad; this.corner = corner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            Opacity = 0.85;
            DoubleBuffered = true;
            // Yalnızca değişince yeniden çizilir: ses listesi, çalanlar ya da tuş türü.
            tick.Tick += (s, e) =>
            {
                var snd = SoundBoard.Sounds;
                string now = numpad() + "|" + string.Join("|", Array.ConvertAll(snd, x => x.Name)) + "|" + string.Join(",", SoundBoard.PlayingIndexes());
                if (now == sig) return;
                sig = now;
                Relayout();
                Invalidate();
            };
            Relayout();
        }

        public void SetCorner(int c) { corner = c; Relayout(); }

        void Relayout()
        {
            int n = Math.Min(SoundBoard.Sounds.Length, 9);
            var size = new Size(240, Head + Math.Max(1, n) * RowH + Pad);
            var a = Screen.PrimaryScreen.Bounds;
            // Valorant'ın boş kalan yerleri: sol orta (harita altı), sağ orta (öldürme akışı altı), sol alt (sohbet üstü), sağ üst.
            Point p;
            switch (corner)
            {
                case 1: p = new Point(a.Right - size.Width - 16, a.Top + (int)(a.Height * 0.42)); break;
                case 2: p = new Point(a.Left + 16, a.Top + (int)(a.Height * 0.62)); break;
                case 3: p = new Point(a.Right - size.Width - 16, a.Top + (int)(a.Height * 0.08)); break;
                default: p = new Point(a.Left + 16, a.Top + (int)(a.Height * 0.42)); break;
            }
            if (Size != size) Size = size;
            if (Location != p) Location = p;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // NOACTIVATE | TOOLWINDOW | TOPMOST | LAYERED | TRANSPARENT (tıklamalar alttaki pencereye geçer)
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00080000 | 0x00000020;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11); // yayına girmez
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); tick.Start(); }
        protected override void OnFormClosed(FormClosedEventArgs e) { tick.Dispose(); base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Theme.Red)) g.FillRectangle(b, 0, 0, 4, Height);
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            var snd = SoundBoard.Sounds;
            var playing = SoundBoard.PlayingIndexes();
            string mod = numpad() ? "Num " : "Alt+";
            using (var hf = new Font(L.SansBold, 8.25f))
                Theme.DrawFit(g, Theme.Upper(L.T("Ses paneli")) + "   " + mod + "0 = " + L.T("sustur"), hf, Theme.Mut, 14, 7, Width - 22);
            using (var f = new Font(L.SansBold, 10f))
            {
                if (snd.Length == 0) Theme.DrawFit(g, L.T("Henüz ses yok"), f, Theme.Mut, 14, Head, Width - 22);
                for (int i = 0; i < snd.Length && i < 9; i++)
                {
                    bool on = Array.IndexOf(playing, i) >= 0;
                    int y = Head + i * RowH;
                    if (on) using (var b = new SolidBrush(Theme.Red)) g.FillRectangle(b, 6, y, Width - 12, RowH - 2);
                    Theme.DrawFit(g, mod + (i + 1), f, on ? Color.White : Theme.Red, 14, y + 1, 52);
                    Theme.DrawFit(g, snd[i].Name, f, on ? Color.White : Theme.Txt, 66, y + 1, Width - 74);
                }
            }
        }
    }
}
