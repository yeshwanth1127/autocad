using System;
using System.Drawing;
using System.Windows.Forms;

namespace autocad_final.UI
{
    /// <summary>
    /// Optional: set before the handle is created (e.g. in the owner form constructor after
    /// <see cref="Controls.Add"/>) to join the global single-selection highlight group.
    /// </summary>
    internal sealed class IconButton : Button
    {
        /// <summary>When set, <see cref="PluginUiButtonSelection.Register"/> runs on handle creation.</summary>
        public PluginButtonChrome? SelectionChrome { get; set; }

        // ── Theme ─────────────────────────────────────────────────────────────────
        private static readonly Color C_BgCard  = Color.FromArgb(28, 31, 40);
        private static readonly Color C_TextPrim = Color.FromArgb(215, 222, 236);
        private static readonly Color C_TextSub  = Color.FromArgb(120, 132, 155);
        private static readonly Color C_Border   = Color.FromArgb(40, 44, 58);
        private static readonly Color C_Hover    = Color.FromArgb(36, 40, 54);

        public string IconText { get; set; } = string.Empty;

        public IconButton()
        {
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
                true);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize  = 1;
            FlatAppearance.BorderColor = C_Border;
            FlatAppearance.MouseOverBackColor = C_Hover;
            BackColor = C_BgCard;
            ForeColor = C_TextPrim;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding   = new Padding(10, 0, 10, 0);
            Height    = 32;
            Cursor    = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (SelectionChrome.HasValue)
                PluginUiButtonSelection.Register(this, SelectionChrome.Value);
        }

        protected override void OnClick(EventArgs e)
        {
            PluginUiButtonSelection.NotifyClicked(this);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g    = pevent.Graphics;
            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // Background
            using (var brush = new SolidBrush(BackColor))
                g.FillRectangle(brush, rect);

            // Border
            var borderRect = rect;
            borderRect.Width  -= 1;
            borderRect.Height -= 1;
            using (var pen = new Pen(FlatAppearance.BorderColor, 1f))
                g.DrawRectangle(pen, borderRect);

            var scale    = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            int leftPad  = (int)Math.Round(10 * scale);
            int iconW    = (int)Math.Round(18 * scale);
            int iconGap  = (int)Math.Round(6  * scale);

            // Icon
            if (!string.IsNullOrWhiteSpace(IconText))
            {
                var iconRect = new Rectangle(leftPad, 0, iconW, Height);
                using (var iconFont = new Font(Font.FontFamily, Font.Size, FontStyle.Regular))
                    TextRenderer.DrawText(
                        g, IconText, iconFont, iconRect,
                        Enabled ? C_TextSub : SystemColors.GrayText,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }

            // Main label
            int textLeft = leftPad + iconW + iconGap;
            var textRect = new Rectangle(textLeft, 0, Width - textLeft - leftPad, Height);
            TextRenderer.DrawText(
                g, Text ?? string.Empty, Font, textRect,
                Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
