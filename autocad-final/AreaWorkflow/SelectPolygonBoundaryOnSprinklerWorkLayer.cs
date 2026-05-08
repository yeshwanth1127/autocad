using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Prompts for an existing closed polyline that lies on <see cref="SprinklerLayers.WorkLayer"/> (e.g. floor drawn with the points workflow).
    /// </summary>
    public static class SelectPolygonBoundaryOnSprinklerWorkLayer
    {
        public static Polyline Run(Editor ed)
        {
            // Do not use AddAllowedClass: in some AutoCAD versions it suppresses hover / selection-preview
            // highlighting during the pick. Validate type and layer after the fact instead.
            var peo = new PromptEntityOptions(
                "\nSelect closed boundary polyline on layer \"" + SprinklerLayers.WorkLayer + "\": ");

            while (true)
            {
                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                    return null;

                var db = per.ObjectId.Database;
                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var obj = tr.GetObject(per.ObjectId, OpenMode.ForRead);

                    if (obj is Polyline lw)
                    {
                        if (!LayerMatchesWorkLayer(lw.Layer))
                        {
                            ed.WriteMessage(
                                "\nSelected polyline is on layer \"" + lw.Layer + "\" — select one on \"" +
                                SprinklerLayers.WorkLayer + "\".\n");
                            tr.Commit();
                            continue;
                        }

                        var normalized = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(lw, tol);
                        if (!normalized.Closed)
                        {
                            ed.WriteMessage("\nPolyline must be closed.\n");
                            normalized.Dispose();
                            tr.Commit();
                            continue;
                        }
                        var copy = (Polyline)normalized.Clone();
                        normalized.Dispose();
                        tr.Commit();
                        return copy;
                    }

                    if (obj is Polyline2d p2d)
                    {
                        if (!LayerMatchesWorkLayer(p2d.Layer))
                        {
                            ed.WriteMessage(
                                "\nSelected polyline is on layer \"" + p2d.Layer + "\" — select one on \"" +
                                SprinklerLayers.WorkLayer + "\".\n");
                            tr.Commit();
                            continue;
                        }

                        var converted = BoundaryEntityToClosedLwPolyline.FromPolyline2d(p2d, db);
                        converted = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(converted, tol);
                        if (!converted.Closed)
                        {
                            ed.WriteMessage("\nPolyline must be closed.\n");
                            converted.Dispose();
                            tr.Commit();
                            continue;
                        }
                        var copy = (Polyline)converted.Clone();
                        converted.Dispose();
                        tr.Commit();
                        return copy;
                    }

                    if (obj is Circle circle)
                    {
                        if (!LayerMatchesWorkLayer(circle.Layer))
                        {
                            ed.WriteMessage(
                                "\nSelected circle is on layer \"" + circle.Layer + "\" — select one on \"" +
                                SprinklerLayers.WorkLayer + "\".\n");
                            tr.Commit();
                            continue;
                        }

                        var converted = BoundaryEntityToClosedLwPolyline.FromCircle(circle);
                        var copy = (Polyline)converted.Clone();
                        converted.Dispose();
                        tr.Commit();
                        return copy;
                    }

                    ed.WriteMessage("\nOnly closed 2D polylines or circles are supported.\n");
                    tr.Commit();
                }
            }
        }

        private static bool LayerMatchesWorkLayer(string entityLayerName)
        {
            return string.Equals(entityLayerName, SprinklerLayers.WorkLayer, StringComparison.OrdinalIgnoreCase);
        }
    }
}
