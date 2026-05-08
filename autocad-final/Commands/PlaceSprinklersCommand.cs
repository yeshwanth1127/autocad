using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Agent;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.UI;
using autocad_final.Workflows.Placement;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    public class PlaceSprinklersCommand
    {
        [CommandMethod("PLACESPRINKLERS", CommandFlags.Modal)]
        [CommandMethod("SPRINKLERPLACESPRINKLERS", CommandFlags.Modal)]
        public void PlaceSprinklers()
        {
            AgentLog.Write("PlaceSprinklers", "command start log=" + AgentLog.Path);
            var ctx = CadContext.TryFromActiveDocument();
            if (ctx == null) return;

            var ed = ctx.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            if (!SelectPolygonBoundary.TrySelectOnNamedLayer(
                    ed,
                    SprinklerLayers.McdFloorBoundaryLayer,
                    SelectPolygonBoundary.FloorBoundaryPickPromptForLayer(SprinklerLayers.McdFloorBoundaryLayer),
                    out var zone,
                    out var boundaryEntityId))
            {
                AgentLog.Write("PlaceSprinklers", "cancelled or wrong layer pick");
                ed.WriteMessage(
                    "\nPlace sprinklers cancelled, or pick a closed polyline on layer \"" +
                    SprinklerLayers.McdFloorBoundaryLayer + "\".\n");
                return;
            }

            try
            {
                AgentLog.Write("PlaceSprinklers", "boundary picked id=" + boundaryEntityId.ToString());
                // Pre-check: estimate zone areas from floor area and shaft count.
                // (This is a cheap guard that prevents huge zones that will break downstream workflows.)
                var db = ctx.Document.Database;
                int shaftCount = 0;
                try
                {
                    AgentLog.Write("PlaceSprinklers", "GetShaftBlocksInsideBoundary start");
                    var shafts = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, zone);
                    shaftCount = shafts?.Count ?? 0;
                    AgentLog.Write("PlaceSprinklers", "GetShaftBlocksInsideBoundary done count=" + shaftCount.ToString());
                }
                catch
                {
                    shaftCount = 0;
                    AgentLog.Write("PlaceSprinklers", "GetShaftBlocksInsideBoundary threw");
                }

                if (shaftCount <= 0)
                {
                    AgentLog.Write("PlaceSprinklers", "stop: no shafts");
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        "Place sprinklers stopped: no shafts were found inside the selected floor boundary.\n" +
                        "Add shafts on layer \"" + SprinklerLayers.McdShaftsLayer +
                        "\" (or blocks/layers containing \"shaft\"), then try again.",
                        MessageBoxIcon.Warning);
                    return;
                }

                double floorAreaDu = 0.0;
                try { floorAreaDu = System.Math.Abs(PolylineNetArea.Run(zone)); } catch { floorAreaDu = 0.0; }
                if (!(floorAreaDu > 0))
                {
                    AgentLog.Write("PlaceSprinklers", "stop: invalid floor area DU");
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        "Place sprinklers stopped: could not read a valid floor boundary area.",
                        MessageBoxIcon.Warning);
                    return;
                }

                // Same capacity rule as "Create zones": each shaft can serve at most ShaftMaxServiceAreaM2 (Properties.config).
                // Sprinkler placement rule (per user requirement): Treat 1 drawing unit as 1 meter,
                // so DU² is m² for this workflow.
                double floorAreaM2 = floorAreaDu;
                double impliedZoneAreaM2 = floorAreaM2 / shaftCount;
                double maxServedM2 = shaftCount * DrawingUnitsHelper.ShaftAreaLimitM2;
                AgentLog.Write(
                    "PlaceSprinklers",
                    "pre-check floorM2=" + floorAreaM2.ToString("F2") +
                    " impliedZoneM2=" + impliedZoneAreaM2.ToString("F2") +
                    " maxServedM2=" + maxServedM2.ToString("F2") +
                    " (assume 1DU=1m)");

                if (floorAreaM2 > maxServedM2 || impliedZoneAreaM2 > DrawingUnitsHelper.ShaftAreaLimitM2)
                {
                    int required = DrawingUnitsHelper.RequiredShaftsCeil(floorAreaM2);
                    AgentLog.Write("PlaceSprinklers", "stop: floor area exceeds shaft capacity required=" + required.ToString());
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        "Sprinklers could not be placed:\n" +
                        "Floor area exceeds shaft capacity\n\n" +
                        "Floor Area: " + floorAreaM2.ToString("F2") + " m²\n" +
                        "Shafts found: " + shaftCount.ToString() + " (max served " +
                        maxServedM2.ToString("F2") + " m² at " +
                        DrawingUnitsHelper.ShaftAreaLimitM2.ToString("F0") + " m²/shaft)\n" +
                        "Required shafts: " + required.ToString() + ".",
                        MessageBoxIcon.Warning);
                    return;
                }

                AgentLog.Write("PlaceSprinklers", "PlaceSprinklersWorkflow.TryRun start");
                if (!PlaceSprinklersWorkflow.TryRun(ctx.Document, zone, boundaryEntityId, out string workflowMsg))
                {
                    AgentLog.Write("PlaceSprinklers", "PlaceSprinklersWorkflow.TryRun fail: " + workflowMsg);
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, workflowMsg ?? "Place sprinklers failed.", MessageBoxIcon.Warning);
                    return;
                }
                AgentLog.Write("PlaceSprinklers", "PlaceSprinklersWorkflow.TryRun ok: " + workflowMsg);
                ed.WriteMessage("\n" + workflowMsg + "\n");
                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                AgentLog.Write("PlaceSprinklers", "command end");
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
