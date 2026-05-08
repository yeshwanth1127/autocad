using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using PolylineEntity = Autodesk.AutoCAD.DatabaseServices.Polyline;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Click any number of points, then type <see cref="ClosePolygonKeyword"/> at the prompt to close the polygon.
    /// The closed polyline is placed on <paramref name="boundaryLayerName"/> when set (e.g.
    /// <see cref="SprinklerLayers.McdFloorBoundaryLayer"/> for palette "Define floor area — points");
    /// otherwise on <see cref="SprinklerLayers.WorkLayer"/> when
    /// <paramref name="appendWorkPolylineToModelSpace"/> is true.
    /// </summary>
    public static class SelectBoundaryByPoints
    {
        /// <summary>Finish keyword at the "next point" prompt (also accepts the first letter).</summary>
        public const string ClosePolygonKeyword = "Close";

        public static PolylineEntity Run(Editor ed, bool appendWorkPolylineToModelSpace = true) =>
            Run(ed, appendWorkPolylineToModelSpace, boundaryLayerName: null);

        /// <param name="boundaryLayerName">
        /// When null, uses <see cref="SprinklerLayers.WorkLayer"/>. When
        /// <see cref="SprinklerLayers.McdFloorBoundaryLayer"/>, ensures that layer (create if missing).
        /// </param>
        public static PolylineEntity Run(Editor ed, bool appendWorkPolylineToModelSpace, string boundaryLayerName)
        {
            var doc = ed.Document;
            if (doc == null)
                return null;

            var db = doc.Database;
            ObjectId layerId;
            ObjectId continuousLtId;

            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    if (string.IsNullOrWhiteSpace(boundaryLayerName))
                        layerId = SprinklerLayers.EnsureWorkLayer(tr, db);
                    else if (string.Equals(boundaryLayerName.Trim(), SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase))
                        layerId = SprinklerLayers.EnsureMcdFloorBoundaryLayer(tr, db);
                    else
                        layerId = SprinklerLayers.EnsureWorkLayer(tr, db);

                    continuousLtId = SprinklerLayers.EnsureLinetypePresent(tr, db, "Continuous", ed);
                    tr.Commit();
                }

                var vertices = new List<Point3d>();
                var firstOpts = new PromptPointOptions("\nSpecify first point of boundary: ");
                PromptPointResult firstRes = ed.GetPoint(firstOpts);
                if (firstRes.Status != PromptStatus.OK)
                    return null;
                vertices.Add(firstRes.Value);

                double lineWidth = 0.0;

                // Create a temporary polyline in the database that will grow as points are added.
                // This ensures the user sees a persistent growing polyline, not just a vanishing rubber band.
                ObjectId tempPolyId = ObjectId.Null;
                try
                {
                    tempPolyId = CreateTemporaryPolyline(db, vertices, layerId, continuousLtId, lineWidth);

                    while (true)
                    {
                        var opts = new PromptPointOptions("\nSpecify next point or [" + ClosePolygonKeyword + "]: ");
                        opts.Keywords.Add(ClosePolygonKeyword);
                        opts.AppendKeywordsToMessage = true;
                        opts.UseBasePoint = true;
                        opts.BasePoint = vertices[vertices.Count - 1];
                        opts.UseDashedLine = true;

                        PromptPointResult res = ed.GetPoint(opts);
                        if (res.Status == PromptStatus.Cancel)
                        {
                            DeleteTemporaryPolyline(db, tempPolyId);
                            return null;
                        }

                        if (res.Status == PromptStatus.Keyword &&
                            string.Equals(res.StringResult, ClosePolygonKeyword, StringComparison.OrdinalIgnoreCase))
                        {
                            if (vertices.Count < 3)
                            {
                                ed.WriteMessage("\nA closed polygon needs at least 3 points. Pick more points or press Esc to cancel.\n");
                                continue;
                            }

                            break;
                        }

                        if (res.Status == PromptStatus.OK)
                        {
                            vertices.Add(res.Value);
                            UpdateTemporaryPolyline(db, tempPolyId, vertices);
                            try { ed.Regen(); } catch { /* ignore */ }
                            continue;
                        }

                        DeleteTemporaryPolyline(db, tempPolyId);
                        return null;
                    }

                    // Clean up the temporary polyline.
                    DeleteTemporaryPolyline(db, tempPolyId);
                }
                catch
                {
                    DeleteTemporaryPolyline(db, tempPolyId);
                    throw;
                }

                PolylineEntity pl = ClosedPolylineFromPointsAndSegments.CreateClosedPolylineFromPoints(vertices, 0);
                pl.SetDatabaseDefaults(db);
                pl.LayerId = layerId;
                pl.ConstantWidth = lineWidth;
                pl.Color = Color.FromColorIndex(ColorMethod.ByLayer, 0);
                if (!continuousLtId.IsNull)
                    pl.LinetypeId = continuousLtId;

                if (appendWorkPolylineToModelSpace)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var persisted = (PolylineEntity)pl.Clone();
                        ms.AppendEntity(persisted);
                        tr.AddNewlyCreatedDBObject(persisted, true);
                        tr.Commit();
                    }
                }

                return pl;
            }
        }

        private static ObjectId CreateTemporaryPolyline(
            Database db,
            IList<Point3d> vertices,
            ObjectId layerId,
            ObjectId continuousLtId,
            double lineWidth)
        {
            ObjectId polyId = ObjectId.Null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var pl = new PolylineEntity();
                pl.SetDatabaseDefaults(db);
                pl.LayerId = layerId;
                pl.ConstantWidth = lineWidth;
                pl.Color = Color.FromColorIndex(ColorMethod.ByLayer, 0);
                if (!continuousLtId.IsNull)
                    pl.LinetypeId = continuousLtId;

                for (int i = 0; i < vertices.Count; i++)
                {
                    var p = vertices[i];
                    pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                }

                polyId = ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                tr.Commit();
            }

            return polyId;
        }

        private static void UpdateTemporaryPolyline(
            Database db,
            ObjectId polyId,
            IList<Point3d> vertices)
        {
            if (polyId.IsNull)
                return;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var pl = (PolylineEntity)tr.GetObject(polyId, OpenMode.ForWrite);
                if (pl != null)
                {
                    // Remove current vertices beyond what we now have.
                    while (pl.NumberOfVertices > vertices.Count)
                    {
                        pl.RemoveVertexAt(pl.NumberOfVertices - 1);
                    }

                    // Update or add vertices to match the current list.
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        var p = vertices[i];
                        if (i < pl.NumberOfVertices)
                        {
                            pl.SetPointAt(i, new Point2d(p.X, p.Y));
                        }
                        else
                        {
                            pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                        }
                    }
                }

                tr.Commit();
            }
        }

        private static void DeleteTemporaryPolyline(Database db, ObjectId polyId)
        {
            if (polyId.IsNull)
                return;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ent = tr.GetObject(polyId, OpenMode.ForWrite, false);
                    if (ent != null)
                    {
                        ent.Erase();
                    }

                    tr.Commit();
                }
            }
            catch
            {
                /* Ignore errors when cleaning up temporary polyline. */
            }
        }
    }
}
