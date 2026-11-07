using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ValoLive
{
    /// <summary>
    /// Rozeti minimap'in üstüne hizalama: ekranın üstünde yarı saydam bir daire. Sürükle = taşı,
    /// tekerlek = boyut, ok tuşları = 1 px, Enter = kaydet, Esc = vazgeç. Yayına/kayda girmez.
    /// </summary>
    sealed class RadarAlignForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        static readonly Color Key = Color.FromArgb(255, 0, 255);
        static readonly Color Red = ColorTranslator.FromHtml("#ff4655");
        readonly float k;           // gerçek piksel → form pikseli (Windows ölçeklemesi)
        readonly int screenW, screenH;
        RectangleF circle;          // form koordinatı
        Point dragFrom; PointF circleFrom; bool dragging;

        /// <summary>Gerçek ekran pikselinde sonuç.</summary>
        public Rectangle Result { get; private set; }

        public RadarAlignForm(Rectangle initial, int screenW, int screenH)
        {
            this.screenW = screenW; this.screenH = screenH;
            var b = Screen.PrimaryScreen.Bounds;
            k = b.Width / (float)screenW;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            StartPosition = FormStartPosition.Manual; Bounds = b;
            BackColor = Key; TransparencyKey = Key; Opacity = 0.6;
            DoubleBuffered = true; Cursor = Cursors.SizeAll;
            circle = new RectangleF(initial.X * k, initial.Y * k, initial.Width * k, initial.Height * k);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE: yayına girmesin
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None; // kenar yumuşatma saydamlık rengiyle karışıp pembe hale bırakır
            using (var br = new SolidBrush(Color.FromArgb(16, 20, 32))) g.FillEllipse(br, circle);
            using (var pen = new Pen(Red, 3)) g.DrawEllipse(pen, circle);
            float cx = circle.X + circle.Width / 2, cy = circle.Y + circle.Height / 2;
            using (var pen = new Pen(Color.White, 1)) { g.DrawLine(pen, cx - 12, cy, cx + 12, cy); g.DrawLine(pen, cx, cy - 12, cx, cy + 12); }

            string help = L.T("ROZETİ MİNİMAP'E HİZALA\n\n") +
                          L.T("Sürükle: taşı\nFare tekerleği: büyüt / küçült (Shift: ince)\nOk tuşları: 1 px (Shift: 10 px)\n\n") +
                          L.T("Enter: kaydet     Esc: vazgeç\n\n") +
                          L.F("Çap {0} px · konum {1}, {2}", Real(circle.Width), Real(circle.X), Real(circle.Y));
            using (var f = new Font(L.SansBold, 11f))
            {
                var size = g.MeasureString(help, f);
                float x = circle.Right + 24, y = circle.Top;
                if (x + size.Width + 24 > Width) x = circle.Left - size.Width - 48;
                if (x < 0) x = 24;
                if (y + size.Height + 24 > Height) y = Height - size.Height - 24;
                var box = new RectangleF(x, y, size.Width + 24, size.Height + 20);
                using (var br = new SolidBrush(Color.FromArgb(16, 18, 26))) g.FillRectangle(br, box);
                using (var br = new SolidBrush(Red)) g.FillRectangle(br, box.Left, box.Top, 4, box.Height);
                g.DrawString(help, f, Brushes.White, box.Left + 14, box.Top + 10);
            }
        }

        int Real(float v) => (int)Math.Round(v / k);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            dragging = true; dragFrom = e.Location; circleFrom = circle.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            MoveBy(circleFrom.X + e.X - dragFrom.X - circle.X, circleFrom.Y + e.Y - dragFrom.Y - circle.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e) => dragging = false;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float step = (ModifierKeys & Keys.Shift) != 0 ? 2 : 10;
            Resize_(e.Delta > 0 ? step : -step);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            float step = (keyData & Keys.Shift) != 0 ? 10 : 1;
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: MoveBy(-step, 0); return true;
                case Keys.Right: MoveBy(step, 0); return true;
                case Keys.Up: MoveBy(0, -step); return true;
                case Keys.Down: MoveBy(0, step); return true;
                case Keys.Add: case Keys.Oemplus: Resize_(step * 2); return true;
                case Keys.Subtract: case Keys.OemMinus: Resize_(-step * 2); return true;
                case Keys.Enter:
                    Result = new Rectangle(Real(circle.X), Real(circle.Y), Real(circle.Width), Real(circle.Width));
                    DialogResult = DialogResult.OK; return true;
                case Keys.Escape: DialogResult = DialogResult.Cancel; return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MoveBy(float dx, float dy)
        {
            circle.X = Math.Max(0, Math.Min(Width - circle.Width, circle.X + dx));
            circle.Y = Math.Max(0, Math.Min(Height - circle.Height, circle.Y + dy));
            Invalidate();
        }

        void Resize_(float delta)
        {
            float d = Math.Max(80 * k, Math.Min(Math.Min(Width, Height), circle.Width + delta));
            float cx = circle.X + circle.Width / 2, cy = circle.Y + circle.Height / 2;
            circle = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            MoveBy(0, 0);
        }
    }
}
