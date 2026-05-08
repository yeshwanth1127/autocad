using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace autocad_final.UI
{
    /// <summary>
    /// Shows palette- or command-triggered errors in a modal Windows dialog, then echoes the same text to the command line.
    /// </summary>
    public static class PaletteCommandErrorUi
    {
        public static void Show(Exception ex, Document doc)
        {
            var text = ex?.Message ?? "Unknown error.";
            if (doc?.Editor != null)
                ShowDialogThenCommandLine(doc.Editor, text, MessageBoxIcon.Error);
            else
                MessageBox.Show(text, "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void Show(string message, Document doc)
        {
            var text = message ?? string.Empty;
            if (doc?.Editor != null)
                ShowDialogThenCommandLine(doc.Editor, text, MessageBoxIcon.Error);
            else
                MessageBox.Show(text, "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Shows a standard WinForms message box first, then writes the same text to the command line
        /// (for modal commands that previously only used <see cref="Editor.WriteMessage"/>).
        /// </summary>
        public static void ShowDialogThenCommandLine(Editor ed, string message, MessageBoxIcon icon = MessageBoxIcon.Warning)
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0) return;
            MessageBox.Show(text, "autocad-final", MessageBoxButtons.OK, icon);
            if (ed == null) return;
            try
            {
                ed.WriteMessage("\n" + text + "\n");
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
