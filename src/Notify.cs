using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Oyunu bölmeyen küçük bildirim: odak çalmaz (WS_EX_NOACTIVATE), görev çubuğunda görünmez, yayına/kayda girmez.
    /// Windows'un kendi bildirimleri oyun açıkken susturulduğu için kendi penceremiz; yanında kısa bir sistem sesi.
    /// Valorant "tam ekran" (exclusive) modundaysa pencere oyunun üstünde görünmez, yalnızca ses duyulur.
    /// </summary>
    sealed class Toast : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        static readonly Color Bg = Theme.Bg, Txt = Theme.Txt, Red = Theme.Red;
        const int ShowMs = 8000, LineH = 26;

        readonly string title;
        readonly List<KeyValuePair<string, Color>> lines;
        readonly Timer close = new Timer { Interval = ShowMs };
        static Toast current;

        Toast(string title, List<KeyValuePair<string, Color>> lines)
        {
            this.title = title; this.lines = lines;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            // Genişlik en uzun satıra göre (çeviriler uzayabilir); ekranın yarısını geçmez, geçen satır küçülür.
            int w = 420;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            using (var tf = Theme.Display(15))
            using (var f = new Font(L.SansBold, 10.5f))
            {
                w = Math.Max(w, (int)g.MeasureString(Theme.Upper(title), tf).Width + 36);
                foreach (var l in lines) w = Math.Max(w, (int)g.MeasureString(l.Key, f).Width + 48);
            }
            ClientSize = new Size(Math.Min(w, Screen.PrimaryScreen.Bounds.Width / 2), 44 + lines.Count * LineH + 8);
            // Sağ kenar, ekranın üçte biri hizası: Valorant'ın öldürme akışının (sağ üst) altında, ajan listesinin dışında.
            var area = Screen.PrimaryScreen.Bounds;
            Location = new Point(area.Right - Width - 24, area.Top + (int)(area.Height * 0.30));
            close.Tick += (s, e) => Close();
            Click += (s, e) => Close();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11); // yayına girmez
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); close.Start(); }

        protected override void OnFormClosed(FormClosedEventArgs e) { close.Dispose(); if (current == this) current = null; base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, 0, 0, 5, Height);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var f = Theme.Display(15)) Theme.DrawFit(g, Theme.Upper(title), f, Red, 18, 10, Width - 30);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var f = new Font(L.SansBold, 10.5f))
                for (int i = 0; i < lines.Count; i++)
                {
                    float y = 40 + i * LineH;
                    using (var b = new SolidBrush(lines[i].Value)) g.FillEllipse(b, 18, y + 7, 8, 8);
                    Theme.DrawFit(g, lines[i].Key, f, Txt, 34, y, Width - 44);
                }
        }

        /// <summary>UI iş parçacığından çağrılmalı. Açık bir bildirim varsa yenisi onun yerini alır.</summary>
        public static void Show(string title, List<KeyValuePair<string, Color>> lines)
        {
            if (lines.Count == 0) return;
            current?.Close();
            current = new Toast(title, lines);
            current.Show();
            System.Media.SystemSounds.Asterisk.Play();
        }
    }
}
