using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class LoopAreaCommand
    {
        [CommandMethod("LOOPAREA", CommandFlags.Modal)]
        public void LoopArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc);
        }

        public static void Run(Document doc)
        {
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            PolygonMetrics metrics;
            if (!TryRun(doc, out metrics))
                return;

            EditorWritePolygonNetArea.Run(doc.Editor, metrics.Area);
            doc.Editor.WriteMessage("Perimeter: " + metrics.Perimeter.ToString("F3") + "\n");
        }

        public static bool TryRun(Document doc, out double area)
        {
            return TryRun(doc, out area, out _);
        }

        public static bool TryRun(Document doc, out double area, out double perimeter)
        {
            area = 0.0;
            perimeter = 0.0;
            var ed = doc.Editor;
            var db = doc.Database;

            var selection = PromptClosedLoopBoundarySelection.Run(ed);
            if (selection == null)
                return false;

            var segments = CollectSegmentEndpointsFromSelection.Run(db, selection);
            if (segments.Count < 3)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Not enough segments to make a closed polygon.", MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                var boundary = ClosedPolylineFromChainedSegments.Run(segments, tol);
                area = PolylineNetArea.Run(boundary);
                perimeter = boundary.Length;
                boundary.Dispose();
                return true;
            }
            catch (System.Exception ex)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not form a single closed loop: " + ex.Message, MessageBoxIcon.Warning);
                return false;
            }
        }

        public static bool TryRun(Document doc, out PolygonMetrics metrics)
        {
            metrics = null;
            var ed = doc.Editor;
            var db = doc.Database;

            var selection = PromptClosedLoopBoundarySelection.Run(ed);
            if (selection == null)
                return false;

            var segments = CollectSegmentEndpointsFromSelection.Run(db, selection);
            if (segments.Count < 3)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Not enough segments to make a closed polygon.", MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                var boundary = ClosedPolylineFromChainedSegments.Run(segments, tol);
                metrics = new PolygonMetrics
                {
                    Area = PolylineNetArea.Run(boundary),
                    Perimeter = boundary.Length,
                    Layer = boundary.Layer,
                    RoomName = FindRoomNameInsideBoundary.Run(db, boundary)
                };
                FindShaftsInsideBoundary.Run(db, boundary, out int shaftCount, out string shaftCoords);
                metrics.ShaftCount = shaftCount;
                metrics.ShaftCoordinates = shaftCoords;
                boundary.Dispose();
                return true;
            }
            catch (System.Exception ex)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not form a single closed loop: " + ex.Message, MessageBoxIcon.Warning);
                return false;
            }
        }
    }
}

