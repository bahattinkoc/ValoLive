using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Ana pencerenin görünümü: koyu lacivert zemin, kırmızı vurgu, köşeli yazı tipi (Chakra Petch), kesik köşeli
    /// düğmeler ve köşeli anahtarlar. Standart Windows denetimlerinin yerine elle çizilen eşdeğerleri.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#0f1923");
        public static readonly Color Card = ColorTranslator.FromHtml("#1f2a35");     // ayırıcı çizgiler
        public static readonly Color Field = ColorTranslator.FromHtml("#17222c");    // giriş alanları
        public static readonly Color Line = ColorTranslator.FromHtml("#2c3a47");     // alan çerçevesi
        public static readonly Color Txt = ColorTranslator.FromHtml("#ece8e1");
        public static readonly Color Mut = ColorTranslator.FromHtml("#8b978f");
        public static readonly Color Red = ColorTranslator.FromHtml("#ff4655");

        /// <summary>Başlık/düğme yazı tipi (piksel boyutu).</summary>
        public static Font Display(float px) => Assets.Display(px);

        public static string Upper(string s) => L.Upper(s);

        /// <summary>İki renkli pencere başlığı: son kelime vurgulu ("MAÇ" + "ODASI"); boşluksuz dillerde tamamı vurgulu.</summary>
        public static void SplitTitle(string title, out string head, out string tail)
        {
            int sp = title.LastIndexOf(' ');
            head = sp > 0 ? title.Substring(0, sp) : "";
            tail = sp > 0 ? title.Substring(sp + 1) : title;
        }

        /// <summary>
        /// Metin genişliğe sığmıyorsa yazı tipini orantılı küçültür (en çok minScale'e kadar; daha uzunsa çizim kırpar).
        /// Farklı bir yazı tipi dönerse onu çağıran atar: <c>if (ff != f) ff.Dispose();</c>
        /// </summary>
        public static Font Fit(Graphics g, string text, Font f, float maxW, float minScale = 0.7f)
        {
            float w = g.MeasureString(text ?? "", f).Width;
            if (w <= maxW || w <= 0) return f;
            return new Font(f.FontFamily, Math.Max(f.Size * minScale, f.Size * maxW / w * 0.98f), f.Style, f.Unit);
        }

        /// <summary>Tek satır yazı: maxW'ye sığmazsa küçülür, yine sığmazsa "…" ile biter. Çizilen genişliği döner.</summary>
        public static float DrawFit(Graphics g, string s, Font f, Color c, float x, float y, float maxW, float minScale = 0.7f)
        {
            if (string.IsNullOrEmpty(s) || maxW <= 4) return 0;
            var ff = Fit(g, s, f, maxW, minScale);
            float w = Math.Min(maxW, g.MeasureString(s, ff).Width);
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(s, ff, b, new RectangleF(x, y + (f.Height - ff.Height) / 2f, maxW + 2, ff.GetHeight(g) + 4), sf);
            if (ff != f) ff.Dispose();
            return w;
        }

        /// <summary>Kesik köşeli düğmeye (VButton) metninin sığması için gereken genişlik.</summary>
        public static int ButtonWidth(string text, int height, int min)
        {
            using (var f = Display(ButtonPx(height)))
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                return Math.Max(min, (int)Math.Ceiling(g.MeasureString(Upper(text), f).Width) + 24);
        }

        public static float ButtonPx(int height) => height >= 40 ? 17 : height >= 30 ? 13.5f : 12;

        public static Color Lighten(Color c, float k) =>
            Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        /// <summary>Pencerenin başlık çubuğunu koyu yapar (Windows 11'de zemin rengine boyar; eski sürümde koyu mod).</summary>
        public static void DarkTitle(Form f)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);                        // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = f.BackColor.R | (f.BackColor.G << 8) | (f.BackColor.B << 16); // COLORREF (0x00BBGGRR)
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);                   // DWMWA_CAPTION_COLOR (Windows 11)
            }
            catch { }
        }

        /// <summary>Sağ alt köşesi kesik dikdörtgen.</summary>
        public static GraphicsPath Cut(RectangleF r, float cut)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom - cut), new PointF(r.Right - cut, r.Bottom), new PointF(r.Left, r.Bottom) });
            return p;
        }
    }

    /// <summary>Kutucuk yerine köşeli açık/kapalı anahtarı (CheckBox davranışı aynen kalır).</summary>
    class VToggle : CheckBox
    {
        const int SwW = 30, SwH = 14, Gap = 10;
        bool hover;

        public VToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var t = TextRenderer.MeasureText(Text, Font);
            return new Size(SwW + Gap + t.Width + 2, Math.Max(22, t.Height + 4));
        }

        public static int WidthFor(string text, Font font) => SwW + Gap + TextRenderer.MeasureText(text, font).Width + 2;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.None;
            var sw = new Rectangle(0, (Height - SwH) / 2, SwW, SwH);
            float a = Enabled ? 1f : 0.4f;
            Color A(Color c) => Color.FromArgb((int)(255 * a), c);
            if (Checked)
            {
                using (var b = new SolidBrush(A(hover ? Theme.Lighten(Theme.Red, 0.15f) : Theme.Red))) g.FillRectangle(b, sw);
                using (var b = new SolidBrush(A(Color.White))) g.FillRectangle(b, sw.Right - 12, sw.Top + 2, 10, SwH - 4);
            }
            else
            {
                using (var p = new Pen(A(hover ? Theme.Txt : Theme.Mut))) g.DrawRectangle(p, sw.X, sw.Y, sw.Width - 1, sw.Height - 1);
                using (var b = new SolidBrush(A(Theme.Mut))) g.FillRectangle(b, sw.Left + 2, sw.Top + 2, 10, SwH - 4);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(SwW + Gap, 0, Width - SwW - Gap, Height), A(Enabled ? Theme.Txt : Theme.Mut),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Kesik köşeli, çerçeveli düğme. Arka plan rengi kırmızıysa ana düğme (iç dolgu + dış çerçeve).</summary>
    class VButton : Button
    {
        bool hover, down;

        public VButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnBackColorChanged(EventArgs e) { Invalidate(); base.OnBackColorChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool primary = BackColor.ToArgb() == Theme.Red.ToArgb();
            float a = Enabled ? 1f : 0.45f;
            Color A(Color c) => Color.FromArgb((int)(255 * a), c);
            var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float cut = Math.Min(12, Height / 3f);
            Color fill = primary ? Theme.Red : Theme.Field;
            if (Enabled && hover) fill = Theme.Lighten(fill, down ? 0.05f : 0.12f);
            if (primary && Height >= 36)
            {
                // Ana düğme: ince dış çerçeve, içte boşluklu kırmızı dolgu.
                using (var p = new Pen(A(Color.FromArgb(150, Theme.Red)))) using (var path = Theme.Cut(outer, cut)) g.DrawPath(p, path);
                var inner = RectangleF.Inflate(outer, -4, -4);
                using (var b = new SolidBrush(A(fill))) using (var path = Theme.Cut(inner, cut - 2)) g.FillPath(b, path);
            }
            else
            {
                using (var b = new SolidBrush(A(fill))) using (var path = Theme.Cut(outer, cut)) g.FillPath(b, path);
                using (var p = new Pen(A(hover && Enabled ? Theme.Txt : Theme.Line))) using (var path = Theme.Cut(outer, cut)) g.DrawPath(p, path);
            }
            string text = Theme.Upper(Text);
            using (var f0 = Theme.Display(Theme.ButtonPx(Height)))
            using (var b = new SolidBrush(A(Color.White)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                var f = Theme.Fit(g, text, f0, Width - 14); // uzun çevirilerde yazı küçülür, kesilmez
                g.DrawString(text, f, b, new RectangleF(0, 1, Width, Height), sf);
                if (f != f0) f.Dispose();
            }
        }
    }

    /// <summary>Sabit genişlikli durum yazısı: metin sığmazsa yazı tipi %25'e kadar küçülür, daha uzunsa "…" ile biter.</summary>
    class FitLabel : Label
    {
        Font baseFont;
        bool fitting;

        public FitLabel() { AutoSize = false; AutoEllipsis = true; UseMnemonic = false; }

        protected override void OnFontChanged(EventArgs e) { if (!fitting) baseFont = Font; base.OnFontChanged(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Refit(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Refit(); }

        void Refit()
        {
            var f0 = baseFont ?? Font;
            float size = f0.Size;
            for (float s = f0.Size; s >= f0.Size * 0.75f; s -= 0.25f)
            {
                size = s;
                using (var f = new Font(f0.FontFamily, s, f0.Style, f0.Unit))
                    if (TextRenderer.MeasureText(Text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width <= Width) break;
            }
            if (Math.Abs(size - Font.Size) < 0.01f) return;
            fitting = true;
            Font = size == f0.Size ? f0 : new Font(f0.FontFamily, size, f0.Style, f0.Unit);
            fitting = false;
        }
    }

    /// <summary>
    /// Açılır listenin kenarlığını ve ok düğmesini Windows çizdikten hemen sonra üstüne koyu temayla yeniden çizer
    /// (ComboBox'ın kendi çizimi tamamen değiştirilemiyor; liste ve davranış aynen kalır).
    /// </summary>
    sealed class ComboSkin : NativeWindow
    {
        readonly ComboBox box;

        public static void Attach(ComboBox c)
        {
            if (c.IsHandleCreated) new ComboSkin(c);
            else c.HandleCreated += (s, e) => new ComboSkin(c);
        }

        ComboSkin(ComboBox c)
        {
            box = c;
            AssignHandle(c.Handle);
            c.HandleDestroyed += (s, e) => ReleaseHandle();
            c.GotFocus += (s, e) => c.Invalidate();
            c.LostFocus += (s, e) => c.Invalidate();
            c.MouseEnter += (s, e) => c.Invalidate();
            c.MouseLeave += (s, e) => c.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F /* WM_PAINT */ || m.Msg == 0x0085 /* WM_NCPAINT */) Paint();
        }

        void Paint()
        {
            using (var g = Graphics.FromHwnd(Handle))
            {
                var r = new Rectangle(0, 0, box.Width, box.Height);
                bool hot = box.Focused || box.DroppedDown || r.Contains(box.PointToClient(Control.MousePosition));
                // Windows'un açık renkli kenarlığını ve ok kutusunu ört
                using (var p = new Pen(Theme.Field, 2)) g.DrawRectangle(p, 1, 1, r.Width - 2, r.Height - 2);
                int aw = 22;
                var arrow = new Rectangle(r.Width - aw - 1, 1, aw, r.Height - 2);
                using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, arrow);
                using (var p = new Pen(box.Focused ? Theme.Red : hot ? Theme.Mut : Theme.Line)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = arrow.Left + aw / 2f, cy = r.Height / 2f;
                using (var p = new Pen(box.Enabled ? (hot ? Theme.Txt : Theme.Mut) : Theme.Line, 1.6f))
                    g.DrawLines(p, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
            }
        }
    }

    /// <summary>İnce çizgi + kare tutamaklı kaydırıcı (TrackBar yerine; yalnızca kullanılan üyeler).</summary>
    class VSlider : Control
    {
        int min, max = 100, val;
        bool drag;

        public event EventHandler ValueChanged;

        public VSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get => min; set { min = value; Value = val; } }
        public int Maximum { get => max; set { max = value; Value = val; } }
        public int Value
        {
            get => val;
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (v == val) return;
                val = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        const int Knob = 10;
        float Track => Width - Knob;
        void SetFrom(int x) => Value = min + (int)Math.Round((max - min) * Math.Max(0, Math.Min(1, (x - Knob / 2f) / Track)));

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); drag = true; SetFrom(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFrom(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 5 : -5; base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Value -= 5;
            if (e.KeyCode == Keys.Right) Value += 5;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            float k = max > min ? (float)(val - min) / (max - min) : 0;
            float cy = Height / 2f, x = Knob / 2f + k * Track;
            using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, Knob / 2f, cy - 1, Track, 2);
            using (var b = new SolidBrush(Enabled ? Theme.Red : Theme.Mut)) g.FillRectangle(b, Knob / 2f, cy - 1, x - Knob / 2f, 2);
            using (var b = new SolidBrush(Enabled ? Theme.Txt : Theme.Mut)) g.FillRectangle(b, x - Knob / 2f, cy - Knob / 2f, Knob, Knob);
        }
    }
}
