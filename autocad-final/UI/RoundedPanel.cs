using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace autocad_final.UI
{
    internal sealed class RoundedPanel : Panel
    {
        private static readonly Color DefaultBorder = Color.FromArgb(40, 44, 58);
        private static readonly Color DefaultBg     = Color.FromArgb(28, 31, 40);

        public int   CornerRadius { get; set; } = 10;
        public Color BorderColor  { get; set; } = DefaultBorder;
        public float BorderWidth  { get; set; } = 1f;

        public RoundedPanel()
        {
            DoubleBuffered = true;
            BackColor      = DefaultBg;
            Padding        = new Padding(12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var rect   = ClientRectangle;
            rect.Width  -= 1;
            rect.Height -= 1;

            using (var path  = CreateRoundedRect(rect, CornerRadius))
            using (var brush = new SolidBrush(BackColor))
            using (var pen   = new Pen(BorderColor, BorderWidth))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Suppress default background paint to prevent flicker.
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            var r    = Math.Max(1, radius);
            var d    = r * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X,            bounds.Y,             d, d, 180, 90);
            path.AddArc(bounds.Right - d,    bounds.Y,             d, d, 270, 90);
            path.AddArc(bounds.Right - d,    bounds.Bottom - d,    d, d,   0, 90);
            path.AddArc(bounds.X,            bounds.Bottom - d,    d, d,  90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
