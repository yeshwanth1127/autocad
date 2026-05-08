using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class PolygonAreaCommand
    {
        [CommandMethod("POLYGONAREA", CommandFlags.Modal)]
        public void PolygonArea()
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
            if (!SelectPolygonBoundary.TrySelect(ed, out var boundary, out _))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Selected object is not a closed polygon/polyline.", MessageBoxIcon.Warning);
                return false;
            }

            TryAppendBoundaryCopyOnFloorBoundaryLayer(doc, boundary);

            area = PolylineNetArea.Run(boundary);
            perimeter = boundary.Length;
            boundary.Dispose();
            return true;
        }

        public static bool TryRun(Document doc, out PolygonMetrics metrics)
        {
            metrics = null;
            var ed = doc.Editor;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelect(ed, out var boundary, out _))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Selected object is not a closed polygon/polyline.", MessageBoxIcon.Warning);
                return false;
            }

            TryAppendBoundaryCopyOnFloorBoundaryLayer(doc, boundary);

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

        private static void TryAppendBoundaryCopyOnFloorBoundaryLayer(Document doc, Polyline boundary)
        {
            if (doc == null || boundary == null)
                return;

            var db = doc.Database;
            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId workLayerId = SprinklerLayers.EnsureWorkLayer(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var copy = (Polyline)boundary.Clone();
                    copy.SetDatabaseDefaults(db);
                    copy.LayerId = workLayerId;
                    ms.AppendEntity(copy);
                    tr.AddNewlyCreatedDBObject(copy, true);
                    tr.Commit();
                }
            }
            catch
            {
                // Keep area calculation usable even if boundary copy cannot be persisted.
            }
        }
    }
}

