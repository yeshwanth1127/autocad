using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class SelectPolygonBoundary
    {
        /// <summary>
        /// Command-line prompt for picking the floor boundary (Create zones, Place sprinklers, etc.).
        /// </summary>
        public static string FloorBoundaryPickPromptForLayer(string requiredLayerName)
        {
            string layer = (requiredLayerName ?? string.Empty).Trim();
            return "\nSelect Floor boundary (Closed polygon / Circle on layer \"" + layer + "\"): ";
        }

        /// <summary>
        /// Like <see cref="TrySelect"/> but requires the picked entity to be on <see cref="SprinklerLayers.WorkLayer"/> (floor boundary).
        /// </summary>
        public static bool TrySelectOnWorkLayer(Editor ed, out Polyline zone, out ObjectId boundaryEntityId) =>
            TrySelectOnNamedLayer(ed, SprinklerLayers.WorkLayer, out zone, out boundaryEntityId);

        /// <summary>
        /// Like <see cref="TrySelect"/> but the entity must lie on <paramref name="requiredLayerName"/> (e.g. <see cref="SprinklerLayers.McdFloorBoundaryLayer"/>).
        /// </summary>
        public static bool TrySelectOnNamedLayer(Editor ed, string requiredLayerName, out Polyline zone, out ObjectId boundaryEntityId)
        {
            string defaultPrompt = "\nSelect closed polygon / circle on layer \"" + requiredLayerName.Trim() + "\": ";
            return TrySelectOnNamedLayer(ed, requiredLayerName, defaultPrompt, out zone, out boundaryEntityId);
        }

        /// <summary>
        /// Like <see cref="TrySelectOnNamedLayer(Editor,string,out Polyline,out ObjectId)"/> but uses <paramref name="commandLinePrompt"/> for the pick message.
        /// </summary>
        public static bool TrySelectOnNamedLayer(
            Editor ed,
            string requiredLayerName,
            string commandLinePrompt,
            out Polyline zone,
            out ObjectId boundaryEntityId)
        {
            zone = null;
            boundaryEntityId = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(requiredLayerName))
                return false;

            string prompt = string.IsNullOrEmpty(commandLinePrompt)
                ? "\nSelect closed polygon / circle on layer \"" + requiredLayerName.Trim() + "\": "
                : (commandLinePrompt.StartsWith("\n") ? commandLinePrompt : "\n" + commandLinePrompt);

            var peo = new PromptEntityOptions(prompt);
            peo.SetRejectMessage("\nOnly closed 2D polylines or circles on the required layer are supported.");
            peo.AddAllowedClass(typeof(Polyline), true);
            peo.AddAllowedClass(typeof(Polyline2d), true);
            peo.AddAllowedClass(typeof(Circle), true);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return false;

            boundaryEntityId = per.ObjectId;
            var db = per.ObjectId.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(per.ObjectId, OpenMode.ForRead);
                string layer = string.Empty;
                if (obj is Entity ent)
                    layer = ent.Layer ?? string.Empty;

                if (!string.Equals(layer.Trim(), requiredLayerName.Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    ed.WriteMessage("\nSelected entity must be on layer \"" + requiredLayerName.Trim() + "\".\n");
                    tr.Commit();
                    boundaryEntityId = ObjectId.Null;
                    return false;
                }

                if (obj is Polyline lw)
                {
                    var normalized = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(lw, tol);
                    if (!normalized.Closed)
                    {
                        normalized.Dispose();
                        tr.Commit();
                        boundaryEntityId = ObjectId.Null;
                        return false;
                    }
                    var copy = (Polyline)normalized.Clone();
                    normalized.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                if (obj is Polyline2d p2d)
                {
                    var converted = BoundaryEntityToClosedLwPolyline.FromPolyline2d(p2d, db);
                    converted = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(converted, tol);
                    if (!converted.Closed)
                    {
                        converted.Dispose();
                        tr.Commit();
                        boundaryEntityId = ObjectId.Null;
                        return false;
                    }
                    var copy = (Polyline)converted.Clone();
                    converted.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                if (obj is Circle circle)
                {
                    var converted = BoundaryEntityToClosedLwPolyline.FromCircle(circle);
                    var copy = (Polyline)converted.Clone();
                    converted.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                tr.Commit();
                boundaryEntityId = ObjectId.Null;
                return false;
            }
        }

        /// <summary>
        /// Selects a closed boundary polyline and returns a clone plus the source entity id (stable for tagging zone content).
        /// </summary>
        public static bool TrySelect(Editor ed, out Polyline zone, out ObjectId boundaryEntityId)
        {
            zone = null;
            boundaryEntityId = ObjectId.Null;

            var peo = new PromptEntityOptions("\nSelect closed polygon / polyline / circle: ");
            peo.SetRejectMessage("\nOnly 2D polylines or circles are supported.");
            peo.AddAllowedClass(typeof(Polyline), true);
            peo.AddAllowedClass(typeof(Polyline2d), true);
            peo.AddAllowedClass(typeof(Circle), true);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return false;

            boundaryEntityId = per.ObjectId;
            var db = per.ObjectId.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(per.ObjectId, OpenMode.ForRead);

                if (obj is Polyline lw)
                {
                    var normalized = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(lw, tol);
                    if (!normalized.Closed)
                    {
                        normalized.Dispose();
                        tr.Commit();
                        boundaryEntityId = ObjectId.Null;
                        return false;
                    }
                    var copy = (Polyline)normalized.Clone();
                    normalized.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                if (obj is Polyline2d p2d)
                {
                    var converted = BoundaryEntityToClosedLwPolyline.FromPolyline2d(p2d, db);
                    converted = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(converted, tol);
                    if (!converted.Closed)
                    {
                        converted.Dispose();
                        tr.Commit();
                        boundaryEntityId = ObjectId.Null;
                        return false;
                    }
                    var copy = (Polyline)converted.Clone();
                    converted.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                if (obj is Circle circle)
                {
                    var converted = BoundaryEntityToClosedLwPolyline.FromCircle(circle);
                    var copy = (Polyline)converted.Clone();
                    converted.Dispose();
                    tr.Commit();
                    zone = copy;
                    return true;
                }

                tr.Commit();
                boundaryEntityId = ObjectId.Null;
                return false;
            }
        }

        public static Polyline Run(Editor ed)
        {
            return TrySelect(ed, out var z, out _) ? z : null;
        }
    }
}

