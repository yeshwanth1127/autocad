using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.UI;
using autocad_final.Workflows.Zoning;

namespace autocad_final.Commands
{
    public class FixZonesCommand
    {
        [CommandMethod("FIXZONES", CommandFlags.Modal)]
        [CommandMethod("SPRINKLERFIXZONES", CommandFlags.Modal)]
        public void FixZones()
        {
            var ctx = CadContext.TryFromActiveDocument();
            if (ctx == null) return;
            if (!TrialGuard.EnsureActive(ctx.Editor)) return;

            try { ctx.Editor.WriteMessage("\n[autocad-final] Fix zones: pick closed polyline zone boundaries (one or more), then the floor boundary.\n"); }
            catch { /* ignore */ }

            if (!PromptClosedZonePolylinesSelection.TrySelect(ctx.Editor, ctx.Database, out var zonePolylineIds, out string selErr))
            {
                var err = selErr ?? "Fix zones cancelled.";
                if (string.Equals(err, "Selection cancelled.", StringComparison.Ordinal))
                    try { ctx.Editor.WriteMessage("\n" + err + "\n"); } catch { /* ignore */ }
                else
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ctx.Editor, err, MessageBoxIcon.Warning);
                return;
            }

            // Floor boundary defines INSUNITS context and containment for shaft assignment / cleanup.
            if (!SelectPolygonBoundary.TrySelect(ctx.Editor, out var boundary, out var boundaryEntityId))
                return;

            try
            {
                bool ok = FixZonesWorkflow.TryRun(ctx.Document, boundary, boundaryEntityId, zonePolylineIds, out string msg);
                if (!ok)
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ctx.Editor, msg ?? "Fix zones failed.", MessageBoxIcon.Warning);
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

