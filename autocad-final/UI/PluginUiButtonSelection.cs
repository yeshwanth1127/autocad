using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace autocad_final.UI
{
    /// <summary>Visual style bucket for normal vs selected chrome.</summary>
    internal enum PluginButtonChrome
    {
        /// <summary>Primary tools (full-width palette actions).</summary>
        Primary,
        /// <summary>Secondary / muted caption buttons.</summary>
        Secondary,
        /// <summary>Destructive actions (e.g. delete).</summary>
        Danger
    }

    /// <summary>
    /// Single-selection highlight across all plugin UI buttons (palette, dialogs, etc.).
    /// </summary>
    internal static class PluginUiButtonSelection
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Button, PluginButtonChrome> Registry =
            new Dictionary<Button, PluginButtonChrome>();

        private static readonly Color C_BgCard = Color.FromArgb(28, 31, 40);
        private static readonly Color C_TextPrim = Color.FromArgb(215, 222, 236);
        private static readonly Color C_TextSub = Color.FromArgb(148, 160, 180);
        private static readonly Color C_Border = Color.FromArgb(40, 44, 58);
        private static readonly Color C_Blue = Color.FromArgb(56, 159, 255);
        private static readonly Color C_Red = Color.FromArgb(255, 70, 70);
        private static readonly Color C_DangerBg = Color.FromArgb(48, 20, 20);
        private static readonly Color C_DangerBorder = Color.FromArgb(80, 30, 30);

        private static readonly Color C_SelBg = Color.FromArgb(32, 44, 72);
        private static readonly Color C_SelHover = Color.FromArgb(40, 54, 86);
        private static readonly Color C_SelPress = Color.FromArgb(26, 36, 60);
        private static readonly Color C_DangerSelBg = Color.FromArgb(56, 28, 32);
        private static readonly Color C_DangerSelHover = Color.FromArgb(64, 34, 40);
        private static readonly Color C_DangerSelPress = Color.FromArgb(44, 22, 26);

        /// <summary>Registers a button for global single-selection styling. Idempotent per instance.</summary>
        public static void Register(Button btn, PluginButtonChrome chrome)
        {
            if (btn == null) return;
            lock (Sync)
            {
                Registry[btn] = chrome;
            }

            btn.Disposed -= OnButtonDisposed;
            btn.Disposed += OnButtonDisposed;
            ApplyVisual(btn, chrome, selected: false);
        }

        public static void NotifyClicked(Button btn)
        {
            if (btn == null) return;
            lock (Sync)
            {
                if (!Registry.ContainsKey(btn))
                    return;

                foreach (var kv in Snapshot())
                {
                    bool on = ReferenceEquals(kv.Key, btn);
                    ApplyVisual(kv.Key, kv.Value, on);
                }
            }
        }

        /// <summary>
        /// Resets every registered button to its normal (non-selected) look. Safe from any thread.
        /// </summary>
        public static void ClearHighlight()
        {
            List<KeyValuePair<Button, PluginButtonChrome>> snap;
            lock (Sync)
            {
                if (Registry.Count == 0)
                    return;
                snap = Snapshot();
            }

            foreach (var kv in snap)
            {
                var btn = kv.Key;
                if (btn == null || btn.IsDisposed)
                    continue;
                var chrome = kv.Value;
                void applyNormal()
                {
                    try
                    {
                        if (btn.IsDisposed) return;
                        ApplyVisual(btn, chrome, selected: false);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                try
                {
                    if (btn.InvokeRequired)
                        btn.BeginInvoke(new Action(applyNormal));
                    else
                        applyNormal();
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        private static List<KeyValuePair<Button, PluginButtonChrome>> Snapshot()
        {
            return new List<KeyValuePair<Button, PluginButtonChrome>>(Registry);
        }

        private static void OnButtonDisposed(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            btn.Disposed -= OnButtonDisposed;
            lock (Sync)
            {
                Registry.Remove(btn);
            }
        }

        private static void ApplyVisual(Button btn, PluginButtonChrome chrome, bool selected)
        {
            switch (chrome)
            {
                case PluginButtonChrome.Primary:
                    if (selected)
                    {
                        btn.BackColor = C_SelBg;
                        btn.ForeColor = Color.White;
                        btn.FlatAppearance.BorderColor = C_Blue;
                        btn.FlatAppearance.MouseOverBackColor = C_SelHover;
                        btn.FlatAppearance.MouseDownBackColor = C_SelPress;
                    }
                    else
                    {
                        btn.BackColor = C_BgCard;
                        btn.ForeColor = C_TextPrim;
                        btn.FlatAppearance.BorderColor = C_Border;
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 40, 54);
                        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(28, 32, 44);
                    }
                    break;

                case PluginButtonChrome.Secondary:
                    if (selected)
                    {
                        btn.BackColor = C_SelBg;
                        btn.ForeColor = Color.White;
                        btn.FlatAppearance.BorderColor = C_Blue;
                        btn.FlatAppearance.MouseOverBackColor = C_SelHover;
                        btn.FlatAppearance.MouseDownBackColor = C_SelPress;
                    }
                    else
                    {
                        btn.BackColor = C_BgCard;
                        btn.ForeColor = C_TextSub;
                        btn.FlatAppearance.BorderColor = C_Border;
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 40, 54);
                        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(26, 30, 42);
                    }
                    break;

                case PluginButtonChrome.Danger:
                    if (selected)
                    {
                        btn.BackColor = C_DangerSelBg;
                        btn.ForeColor = Color.White;
                        btn.FlatAppearance.BorderColor = C_Blue;
                        btn.FlatAppearance.MouseOverBackColor = C_DangerSelHover;
                        btn.FlatAppearance.MouseDownBackColor = C_DangerSelPress;
                    }
                    else
                    {
                        btn.BackColor = C_DangerBg;
                        btn.ForeColor = C_Red;
                        btn.FlatAppearance.BorderColor = C_DangerBorder;
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 26, 26);
                        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 18, 18);
                    }
                    break;
            }

            btn.FlatAppearance.BorderSize = 1;
            btn.Invalidate();
        }
    }
}
