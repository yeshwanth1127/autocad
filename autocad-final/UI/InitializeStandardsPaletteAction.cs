using System;
using System.Windows.Forms;
using autocad_final.Commands;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.UI
{
    public static class InitializeStandardsPaletteAction
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
                // Run logic directly; avoids dependency on command registration/echo.
                new InitializeStandardsCommand().Run();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }
    }
}

