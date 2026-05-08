using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;

namespace autocad_final.Commands
{
    public class PointsAreaCommand
    {
        [CommandMethod("POINTSAREA", CommandFlags.Modal)]
        public void PointsArea()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
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

        public static bool TryRun(Document doc, out PolygonMetrics metrics)
        {
            metrics = null;
            var ed = doc.Editor;
            var db = doc.Database;

            var boundary = SelectBoundaryByPoints.Run(ed, appendWorkPolylineToModelSpace: true, SprinklerLayers.McdFloorBoundaryLayer);
            if (boundary == null)
                return false;

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
    }
}

