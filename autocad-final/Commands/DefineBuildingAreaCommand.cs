using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Colors;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.Reporting;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class DefineBuildingAreaCommand
    {
        public const string LayerBoundary = "SPRK-BOUNDARY";

        [CommandMethod("DEFINEBUILDINGAREA", CommandFlags.Modal)]
        public void DefineBuildingArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;

            var pko = new PromptKeywordOptions("\nDefine building area [Existing/Points/FromEntities]: ")
            {
                AllowNone = false
            };
            pko.Keywords.Add("Existing");
            pko.Keywords.Add("Points");
            pko.Keywords.Add("FromEntities");
            pko.Keywords.Default = "Existing";

            var pkr = ed.GetKeywords(pko);
            if (pkr.Status != PromptStatus.OK && pkr.Status != PromptStatus.None)
                return;

            string mode = pkr.Status == PromptStatus.None ? "Existing" : pkr.StringResult;
            RunWorkflow(doc, mode);
        }

        [CommandMethod("DBA_EXISTING", CommandFlags.Modal)]
        public void DefineBuildingAreaExisting() => RunFromCommand("Existing");

        [CommandMethod("DBA_POINTS", CommandFlags.Modal)]
        public void DefineBuildingAreaPoints() => RunFromCommand("Points");

        [CommandMethod("DBA_FROMENTITIES", CommandFlags.Modal)]
        public void DefineBuildingAreaFromEntities() => RunFromCommand("FromEntities");

        private void RunFromCommand(string mode)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            RunWorkflow(doc, mode);
        }

        /// <summary>
        /// Runs the building-area workflow (used by command line, UI buttons, and aliases).
        /// </summary>
        public static void RunWorkflow(Document doc, string mode)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                Polyline boundaryPoly;
                bool createdNew;

                switch (mode)
                {
                    case "Points":
                        boundaryPoly = CollectBoundaryByPoints(ed, out createdNew);
                        break;
                    case "FromEntities":
                        boundaryPoly = CollectBoundaryFromEntities(ed, db, out createdNew);
                        break;
                    default:
                        boundaryPoly = SelectExistingClosedPolyline(ed, out createdNew);
                        break;
                }

                if (boundaryPoly == null)
                    return;

                double areaNative = boundaryPoly.Area;
                UnitsValue selectedUnit = PromptAreaUnitBasis(ed, db);
                double? areaM2 = DrawingUnitsHelper.TryGetAreaSquareMetersFromUnit(selectedUnit, areaNative);
                string areaDisplay = areaM2.HasValue
                    ? $"{areaM2.Value:F2} m²"
                    : $"{areaNative:F2} (square drawing units — set INSUNITS for m²)";

                int shafts = areaM2.HasValue
                    ? DrawingUnitsHelper.RequiredShaftsCeil(areaM2.Value)
                    : 1;

                var ppr = ed.GetPoint("\nSpecify upper-left corner for area table: ");
                if (ppr.Status != PromptStatus.OK)
                {
                    if (createdNew)
                        boundaryPoly.Dispose();
                    return;
                }

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    EnsureLayer(tr, db, LayerBoundary, 3);

                    if (createdNew)
                    {
                        boundaryPoly.Layer = LayerBoundary;
                        btr.AppendEntity(boundaryPoly);
                        tr.AddNewlyCreatedDBObject(boundaryPoly, true);
                    }

                    AreaTableService.AppendAreaTable(
                        db,
                        tr,
                        btr,
                        ppr.Value,
                        "Building footprint area",
                        areaDisplay,
                        selectedUnit.ToString(),
                        "Required shafts (≤" + DrawingUnitsHelper.ShaftAreaLimitM2.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m² each)",
                        shafts.ToString());

                    tr.Commit();
                }

                ed.WriteMessage("\nBuilding area recorded. Table inserted. Required shafts (rule): " + shafts + ".\n");

                if (!createdNew)
                    boundaryPoly.Dispose();
            }
            catch (System.Exception ex)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Error: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        private static UnitsValue PromptAreaUnitBasis(Editor ed, Database db)
        {
            var pko = new PromptKeywordOptions(
                "\nArea unit basis [Auto/Meters/Millimeters/Centimeters/Inches/Feet] <Auto>: ")
            {
                AllowNone = true
            };
            pko.Keywords.Add("Auto");
            pko.Keywords.Add("Meters");
            pko.Keywords.Add("Millimeters");
            pko.Keywords.Add("Centimeters");
            pko.Keywords.Add("Inches");
            pko.Keywords.Add("Feet");
            pko.Keywords.Default = "Auto";

            var pkr = ed.GetKeywords(pko);
            if (pkr.Status == PromptStatus.Cancel)
                return db.Insunits;

            string s = pkr.Status == PromptStatus.None ? "Auto" : pkr.StringResult;
            switch (s)
            {
                case "Meters":
                    return UnitsValue.Meters;
                case "Millimeters":
                    return UnitsValue.Millimeters;
                case "Centimeters":
                    return UnitsValue.Centimeters;
                case "Inches":
                    return UnitsValue.Inches;
                case "Feet":
                    return UnitsValue.Feet;
                default:
                    return db.Insunits;
            }
        }

        private static void EnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex) };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }

        private static Polyline SelectExistingClosedPolyline(Editor ed, out bool createdNew)
        {
            createdNew = false;
            var peo = new PromptEntityOptions("\nSelect closed polyline boundary (LWPOLYLINE, 2D POLYLINE, or CIRCLE): ");
            peo.SetRejectMessage("\nOnly 2D polylines (lightweight or classic) or circles.");
            peo.AddAllowedClass(typeof(Polyline), true);
            peo.AddAllowedClass(typeof(Polyline2d), true);
            peo.AddAllowedClass(typeof(Circle), true);
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return null;

            var db = per.ObjectId.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(per.ObjectId, OpenMode.ForRead);

                if (obj is Polyline plw)
                {
                    Polyline lw = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(plw, tol);
                    if (!lw.Closed)
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "Polyline is not closed. Use PEDIT -> Close, or draw so the first and last vertex coincide.",
                            MessageBoxIcon.Warning);
                        lw.Dispose();
                        tr.Commit();
                        return null;
                    }
                    var copy = (Polyline)lw.Clone();
                    lw.Dispose();
                    tr.Commit();
                    return copy;
                }

                if (obj is Polyline2d p2d)
                {
                    Polyline lw = BoundaryEntityToClosedLwPolyline.FromPolyline2d(p2d, db);
                    lw = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(lw, tol);
                    if (!lw.Closed)
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "2D polyline is not closed. Use PEDIT -> Close on the polyline.",
                            MessageBoxIcon.Warning);
                        lw.Dispose();
                        tr.Commit();
                        return null;
                    }
                    var copy = (Polyline)lw.Clone();
                    lw.Dispose();
                    tr.Commit();
                    return copy;
                }

                if (obj is Circle circle)
                {
                    Polyline lw = BoundaryEntityToClosedLwPolyline.FromCircle(circle);
                    var copy = (Polyline)lw.Clone();
                    lw.Dispose();
                    tr.Commit();
                    return copy;
                }

                tr.Commit();
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Unsupported entity.", MessageBoxIcon.Warning);
                return null;
            }
        }

        private static Polyline CollectBoundaryByPoints(Editor ed, out bool createdNew)
        {
            createdNew = true;
            return SelectBoundaryByPoints.Run(ed, appendWorkPolylineToModelSpace: false);
        }

        private static double ToleranceTolerance(Editor ed)
        {
            return ed.Document.Database.Insunits == UnitsValue.Millimeters ? 1.0 : 1e-4;
        }

        private static Polyline CollectBoundaryFromEntities(Editor ed, Database db, out bool createdNew)
        {
            createdNew = true;
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect lines and/or polylines forming a closed loop: ",
                AllowDuplicates = false
            };

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE,POLYLINE,LWPOLYLINE")
            });

            var psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
                return null;

            var entities = new List<Entity>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent != null)
                        entities.Add(ent);
                }
                tr.Commit();
            }

            if (entities.Count == 0)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Nothing selected.", MessageBoxIcon.Warning);
                return null;
            }

            var segments = ClosedPolylineFromPointsAndSegments.CollectSegments(entities);
            double tol = ToleranceTolerance(ed);
            try
            {
                var ring = ClosedPolylineFromPointsAndSegments.ChainSegmentsToClosedLoop(segments, tol);
                return ClosedPolylineFromPointsAndSegments.CreateClosedPolylineFromPoints(ring, 0);
            }
            catch (System.Exception ex)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not build boundary: " + ex.Message, MessageBoxIcon.Warning);
                return null;
            }
        }
    }
}
