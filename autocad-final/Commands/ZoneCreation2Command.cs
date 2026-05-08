using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.UI;
using autocad_final.Workflows.Zoning;

namespace autocad_final.Commands
{
    public class ZoneCreation2Command
    {
        [CommandMethod("ZONECREATION2", CommandFlags.Modal)]
        [CommandMethod("CREATEZONES2", CommandFlags.Modal)]
        public void ZoneCreation2()
        {
            var ctx = CadContext.TryFromActiveDocument();
            if (ctx == null) return;
            if (!TrialGuard.EnsureActive(ctx.Editor)) return;

            if (!SelectPolygonBoundary.TrySelectOnNamedLayer(
                    ctx.Editor,
                    SprinklerLayers.McdFloorBoundaryLayer,
                    SelectPolygonBoundary.FloorBoundaryPickPromptForLayer(SprinklerLayers.McdFloorBoundaryLayer),
                    out var boundary,
                    out var boundaryEntityId))
                return;

            try
            {
                bool ok = ZoneCreation2EqualAreaWorkflow.TryRun(ctx.Document, boundary, boundaryEntityId, out string msg);
                if (!ok)
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ctx.Editor, msg ?? "Create zones failed.", MessageBoxIcon.Warning);
                else
                    try { ctx.Editor.WriteMessage("\n" + (msg ?? string.Empty) + "\n"); } catch { /* ignore */ }
            }
            finally
            {
                try { boundary.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
