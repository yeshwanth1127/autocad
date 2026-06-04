using System;
using System.Windows.Forms;
using autocad_final.Commands;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.UI
{
    public static class InsertStandardBlockPaletteAction
    {
        public static void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                // Defer via the command queue so the palette releases focus and the drawing window
                // receives the point picks (synchronous invoke breaks entity/point picking from a palette).
                doc.SendStringToExecute("._AF_INSERTBLOCK ", true, false, false);
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }
    }
}
