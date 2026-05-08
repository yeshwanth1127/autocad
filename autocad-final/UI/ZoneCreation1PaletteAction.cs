using System;
using System.Windows.Forms;
using autocad_final.Commands;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.UI
{
    /// <summary>Palette entry for <c>ZONECREATION1</c> (Voronoi-style grid zoning).</summary>
    public static class ZoneCreation1PaletteAction
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
                // Run command logic directly so the command line shows only prompts/messages (no echoed command name).
                new ZoneCreation1Command().ZoneCreation1();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }
    }
}

