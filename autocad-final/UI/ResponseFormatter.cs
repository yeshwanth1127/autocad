using System;
using System.Drawing;
using System.Windows.Forms;

namespace autocad_final.UI
{
    /// <summary>
    /// Renders markdown-like AI responses into a RichTextBox with a dark theme.
    /// Handles: ## headings, **bold**, `code`, bullet lists, numbered lists,
    /// and horizontal rules.
    /// All methods must be called from the UI thread.
    /// </summary>
    internal static class ResponseFormatter
    {
        // ── Theme ─────────────────────────────────────────────────────────────
        private static readonly Color C_H1      = Color.FromArgb(255, 90,  48);   // fire orange
        private static readonly Color C_H2      = Color.FromArgb(56,  159, 255);  // blue
        private static readonly Color C_H3      = Color.FromArgb(52,  195, 110);  // green
        private static readonly Color C_Normal  = Color.FromArgb(215, 222, 236);
        private static readonly Color C_Bold    = Color.FromArgb(235, 240, 250);
        private static readonly Color C_Code    = Color.FromArgb(255, 200, 80);   // amber
        private static readonly Color C_Bullet  = Color.FromArgb(100, 170, 255);
        private static readonly Color C_Divider = Color.FromArgb(50,  56,  72);
        private static readonly Color C_SubText = Color.FromArgb(148, 160, 180);

        private static readonly Font F_H1     = new Font("Segoe UI", 11f,  FontStyle.Bold);
        private static readonly Font F_H2     = new Font("Segoe UI",  9.5f, FontStyle.Bold);
        private static readonly Font F_H3     = new Font("Segoe UI",  9f,  FontStyle.Bold);
        private static readonly Font F_Normal = new Font("Segoe UI",  9f);
        private static readonly Font F_Bold   = new Font("Segoe UI",  9f,  FontStyle.Bold);
        private static readonly Font F_Code   = new Font("Consolas",  8.5f);
        private static readonly Font F_Tiny   = new Font("Segoe UI",  7.5f);
        private static readonly Font F_Italic = new Font("Segoe UI",  9f,  FontStyle.Italic);

        // ── Public entry point ────────────────────────────────────────────────
        /// <summary>
        /// Appends a markdown-formatted block to the RichTextBox.
        /// </summary>
        public static void AppendMarkdown(RichTextBox box, string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return;

            var lines    = markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool prevBlank = false;

            foreach (var rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    if (!prevBlank) Raw(box, "\n", C_Normal, F_Normal);
                    prevBlank = true;
                    continue;
                }
                prevBlank = false;

                // ── H1:  # text ───────────────────────────────────────────────
                if (rawLine.StartsWith("# ") && !rawLine.StartsWith("## "))
                {
                    Raw(box, rawLine.Substring(2).Trim() + "\n", C_H1, F_H1);
                }
                // ── H2:  ## text ──────────────────────────────────────────────
                else if (rawLine.StartsWith("## "))
                {
                    Divider(box);
                    Raw(box, rawLine.Substring(3).Trim() + "\n", C_H2, F_H2);
                }
                // ── H3:  ### text ─────────────────────────────────────────────
                else if (rawLine.StartsWith("### "))
                {
                    Raw(box, rawLine.Substring(4).Trim() + "\n", C_H3, F_H3);
                }
                // ── Horizontal rule ───────────────────────────────────────────
                else if (rawLine.TrimStart().StartsWith("---") ||
                         rawLine.TrimStart().StartsWith("***") ||
                         rawLine.TrimStart().StartsWith("==="))
                {
                    Divider(box);
                }
                // ── Bullet:  - text  or  * text ───────────────────────────────
                else if (rawLine.StartsWith("- ") || rawLine.StartsWith("* "))
                {
                    Raw(box, "  • ", C_Bullet, F_Bold);
                    Inline(box, rawLine.Substring(2));
                    Raw(box, "\n", C_Normal, F_Normal);
                }
                // ── Nested bullet:  2+ leading spaces ─────────────────────────
                else if ((rawLine.StartsWith("  - ") || rawLine.StartsWith("  * ")))
                {
                    Raw(box, "      ◦ ", C_Bullet, F_Normal);
                    Inline(box, rawLine.TrimStart().Substring(2));
                    Raw(box, "\n", C_Normal, F_Normal);
                }
                // ── Numbered list:  1. text ───────────────────────────────────
                else if (rawLine.Length > 2 && char.IsDigit(rawLine[0]))
                {
                    int dot = rawLine.IndexOf(". ");
                    if (dot > 0 && dot <= 3)
                    {
                        Raw(box, "  " + rawLine.Substring(0, dot + 1) + " ", C_Bullet, F_Bold);
                        Inline(box, rawLine.Substring(dot + 2));
                        Raw(box, "\n", C_Normal, F_Normal);
                    }
                    else
                    {
                        // Looks like a number but not a list — render as normal
                        Inline(box, rawLine);
                        Raw(box, "\n", C_Normal, F_Normal);
                    }
                }
                // ── Whole-line bold:  **text** ────────────────────────────────
                else if (rawLine.StartsWith("**") && rawLine.EndsWith("**") && rawLine.Length > 4)
                {
                    string inner = rawLine.Substring(2, rawLine.Length - 4);
                    Raw(box, inner + "\n", C_Bold, F_Bold);
                }
                // ── Blockquote: > text ────────────────────────────────────────
                else if (rawLine.StartsWith("> "))
                {
                    Raw(box, "  │ ", C_Divider, F_Normal);
                    Inline(box, rawLine.Substring(2));
                    Raw(box, "\n", C_Normal, F_Normal);
                }
                else
                {
                    Inline(box, rawLine);
                    Raw(box, "\n", C_Normal, F_Normal);
                }
            }

            box.ScrollToCaret();
        }

        // ── Inline parser:  **bold**  and  `code` ────────────────────────────
        private static void Inline(RichTextBox box, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // **bold**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2);
                    if (end > i + 1)
                    {
                        Raw(box, text.Substring(i + 2, end - i - 2), C_Bold, F_Bold);
                        i = end + 2;
                        continue;
                    }
                }

                // `code`
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        Raw(box, text.Substring(i + 1, end - i - 1), C_Code, F_Code);
                        i = end + 1;
                        continue;
                    }
                }

                // Collect plain text up to next marker
                int nextStar = text.IndexOf("**", i);
                int nextTick = text.IndexOf('`',  i);
                int nextStop = text.Length;
                if (nextStar >= 0 && nextStar < nextStop) nextStop = nextStar;
                if (nextTick >= 0 && nextTick < nextStop) nextStop = nextTick;

                if (nextStop > i)
                {
                    Raw(box, text.Substring(i, nextStop - i), C_Normal, F_Normal);
                    i = nextStop;
                }
                else
                {
                    // Lone marker — output literally
                    Raw(box, text[i].ToString(), C_Normal, F_Normal);
                    i++;
                }
            }
        }

        // ── Low-level helpers ─────────────────────────────────────────────────
        private static void Raw(RichTextBox box, string text, Color color, Font font)
        {
            box.SelectionStart  = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor  = color;
            box.SelectionFont   = font;
            box.AppendText(text);
        }

        private static void Divider(RichTextBox box)
        {
            Raw(box, "  ──────────────────────────────────\n", C_Divider, F_Tiny);
        }
    }
}
