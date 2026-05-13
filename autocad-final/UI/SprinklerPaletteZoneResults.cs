using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.Commands;

namespace autocad_final.UI
{
    /// <summary>
    /// Palette buttons run zone workflows by calling command methods directly (no echoed command name).
    /// That bypasses AutoCAD's command stack, so <see cref="Autodesk.AutoCAD.ApplicationServices.Document.CommandEnded"/>
    /// does not run — we must push assignment scans to the results grid ourselves.
    /// </summary>
    public static class SprinklerPaletteZoneResults
    {
        public static void PublishAssignmentsTable(Database db)
        {
            if (db == null) return;
            try
            {
                AssignShaftToZoneCommand.PublishZoneShaftAssignmentScan(db);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
