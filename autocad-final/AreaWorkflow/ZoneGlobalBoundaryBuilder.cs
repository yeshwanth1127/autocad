using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Builds a selectable "global zone boundary" as real entities in model space that contain
    /// unique segments from all zone-outline polylines (outer edges + internal separators).
    /// Entities are placed on <see cref="SprinklerLayers.McdZoneBoundaryLayer"/> and are
    /// tagged with the floor boundary handle via <see cref="SprinklerXData.ApplyZoneBoundaryTag"/>.
    /// </summary>
    public static class ZoneGlobalBoundaryBuilder
    {
        /// <summary>
        /// Removes previously built global-boundary entities for the given floor boundary entity.
        /// Useful when you want only closed zone polylines visible (no separator-line overlay).
        /// </summary>
        public static bool TryClearForFloorBoundary(
            Document doc,
            ObjectId floorBoundaryEntityId,
            out string message)
        {
            message = null;
            if (doc == null || floorBoundaryEntityId.IsNull)
            {
                message = "No floor boundary.";
                return false;
            }

            var db = doc.Database;
            string floorHandleHex = null;
            try
            {
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    var floorEnt = tr0.GetObject(floorBoundaryEntityId, OpenMode.ForRead, false);
                    floorHandleHex = floorEnt?.Handle.ToString();
                    tr0.Commit();
                }
            }
            catch
            {
                floorHandleHex = null;
            }

            if (string.IsNullOrEmpty(floorHandleHex))
            {
                message = "Could not resolve floor boundary handle.";
                return false;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerLayers.EnsureMcdZoneBoundaryLayer(tr, db);
                    ErasePriorGlobalBoundaries(tr, db, floorHandleHex);
                    tr.Commit();
                }

                message = "Global boundary cleared.";
                return true;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static bool TryRebuildFromZoneHandles(
            Document doc,
            ObjectId floorBoundaryEntityId,
            IList<string> zoneBoundaryHandles,
            out ObjectId globalBoundaryEntityId,
            out string message)
        {
            globalBoundaryEntityId = ObjectId.Null;
            message = null;

            if (doc == null || floorBoundaryEntityId.IsNull || zoneBoundaryHandles == null || zoneBoundaryHandles.Count == 0)
            {
                message = "No zones provided.";
                return false;
            }

            var db = doc.Database;
            string floorHandleHex = null;
            try
            {
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    var floorEnt = tr0.GetObject(floorBoundaryEntityId, OpenMode.ForRead, false);
                    floorHandleHex = floorEnt?.Handle.ToString();
                    tr0.Commit();
                }
            }
            catch
            {
                floorHandleHex = null;
            }

            if (string.IsNullOrEmpty(floorHandleHex))
            {
                message = "Could not resolve floor boundary handle.";
                return false;
            }

            return TryRebuildInternal(doc, floorHandleHex, zoneBoundaryHandles, out globalBoundaryEntityId, out message);
        }

        public static bool TryRebuildFromExistingZones(
            Document doc,
            ObjectId floorBoundaryEntityId,
            out ObjectId globalBoundaryEntityId,
            out string message)
        {
            globalBoundaryEntityId = ObjectId.Null;
            message = null;
            if (doc == null || floorBoundaryEntityId.IsNull)
            {
                message = "No floor boundary.";
                return false;
            }

            var db = doc.Database;
            string floorHandleHex = null;
            var zoneHandles = new List<string>();

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var floorEnt = tr.GetObject(floorBoundaryEntityId, OpenMode.ForRead, false);
                    floorHandleHex = floorEnt?.Handle.ToString();

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        if (ent == null || ent.IsErased) continue;
                        if (!(ent is Polyline pl)) continue;
                        if (!pl.Closed) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)) continue;
                        if (!string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                        zoneHandles.Add(pl.Handle.ToString());
                    }

                    tr.Commit();
                }
            }
            catch
            {
                // ignore and fall through
            }

            if (string.IsNullOrEmpty(floorHandleHex))
            {
                message = "Could not resolve floor boundary handle.";
                return false;
            }

            if (zoneHandles.Count == 0)
            {
                message = "No zone outlines found.";
                return false;
            }

            return TryRebuildInternal(doc, floorHandleHex, zoneHandles, out globalBoundaryEntityId, out message);
        }

        private static bool TryRebuildInternal(
            Document doc,
            string floorBoundaryHandleHex,
            IList<string> zoneBoundaryHandles,
            out ObjectId globalBoundaryEntityId,
            out string message)
        {
            globalBoundaryEntityId = ObjectId.Null;
            message = null;
            if (doc == null || string.IsNullOrEmpty(floorBoundaryHandleHex) || zoneBoundaryHandles == null || zoneBoundaryHandles.Count == 0)
            {
                message = "Invalid input.";
                return false;
            }

            var db = doc.Database;
            double tolDu = 1e-6;
            try { tolDu = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db); if (!(tolDu > 0)) tolDu = 1e-6; }
            catch { tolDu = 1e-6; }
            double keyTol = Math.Max(1e-6, tolDu * 10.0);

            var segCounts = new Dictionary<SegKey, SegGeom>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerLayers.EnsureMcdZoneBoundaryLayer(tr, db);
                SprinklerXData.EnsureRegApp(tr, db);

                // Remove older global boundaries for this same floor boundary (block refs on the layer with matching xdata).
                ErasePriorGlobalBoundaries(tr, db, floorBoundaryHandleHex);

                // Collect unique segments from all zone polylines.
                for (int i = 0; i < zoneBoundaryHandles.Count; i++)
                {
                    var hex = zoneBoundaryHandles[i];
                    if (string.IsNullOrEmpty(hex)) continue;
                    if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv)) continue;
                    var handle = new Handle(hv);
                    if (!db.TryGetObjectId(handle, out ObjectId oid) || oid.IsNull) continue;

                    var pl = tr.GetObject(oid, OpenMode.ForRead, false) as Polyline;
                    if (pl == null || !pl.Closed) continue;

                    int nv = pl.NumberOfVertices;
                    for (int k = 0; k < nv; k++)
                    {
                        var a = pl.GetPoint2dAt(k);
                        var b = pl.GetPoint2dAt((k + 1) % nv);
                        if (a.GetDistanceTo(b) <= keyTol * 0.25) continue;
                        var key = SegKey.From(a, b, keyTol);
                        if (segCounts.TryGetValue(key, out var geom))
                            segCounts[key] = geom.Increment();
                        else
                            segCounts[key] = new SegGeom(a, b, 1);
                    }
                }

                if (segCounts.Count == 0)
                {
                    message = "No segments found to build global boundary.";
                    tr.Commit();
                    return false;
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId globalLayerId = SprinklerLayers.EnsureMcdZoneBoundaryLayer(tr, db);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var ed = doc.Editor;
                ObjectId ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "DASHED", ed);
                if (ltId.IsNull) ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "HIDDEN", ed);
                if (ltId.IsNull) ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "ACAD_ISO02W100", ed);

                ObjectId first = ObjectId.Null;
                foreach (var kvp in segCounts)
                {
                    // Keep one copy of every unique segment (outer + internal). Even if two zones share the segment,
                    // it is drawn once globally which makes selection/repair deterministic.
                    var geom = kvp.Value;
                    var ln = new Line(
                        new Point3d(geom.A.X, geom.A.Y, 0),
                        new Point3d(geom.B.X, geom.B.Y, 0));
                    ln.SetDatabaseDefaults(db);
                    ln.LayerId = globalLayerId;
                    ln.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    if (!ltId.IsNull) ln.LinetypeId = ltId;
                    ms.AppendEntity(ln);
                    tr.AddNewlyCreatedDBObject(ln, true);

                    // Tag each segment so selecting ANY one can resolve the floor boundary.
                    SprinklerXData.ApplyZoneBoundaryTag(ln, floorBoundaryHandleHex);
                    if (first.IsNull) first = ln.ObjectId;
                }

                globalBoundaryEntityId = first;
                message = "Global boundary built (" + segCounts.Count.ToString(CultureInfo.InvariantCulture) + " unique segments).";
                tr.Commit();
                return true;
            }
        }

        private static void ErasePriorGlobalBoundaries(Transaction tr, Database db, string floorBoundaryHandleHex)
        {
            if (tr == null || db == null || string.IsNullOrEmpty(floorBoundaryHandleHex)) return;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null || ent.IsErased) continue;
                if (!SprinklerLayers.IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(ent.Layer)) continue;

                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h)) continue;
                if (!string.Equals(h, floorBoundaryHandleHex, StringComparison.OrdinalIgnoreCase)) continue;

                try { ent.UpgradeOpen(); ent.Erase(); } catch { /* ignore */ }
            }
        }

        private readonly struct SegKey : IEquatable<SegKey>
        {
            private readonly long _ax;
            private readonly long _ay;
            private readonly long _bx;
            private readonly long _by;

            private SegKey(long ax, long ay, long bx, long by)
            {
                _ax = ax; _ay = ay; _bx = bx; _by = by;
            }

            public static SegKey From(Point2d a, Point2d b, double tol)
            {
                long ax = Quant(a.X, tol), ay = Quant(a.Y, tol);
                long bx = Quant(b.X, tol), by = Quant(b.Y, tol);

                // Undirected key: sort endpoints.
                if (ax > bx || (ax == bx && ay > by))
                {
                    var tx = ax; ax = bx; bx = tx;
                    var ty = ay; ay = by; by = ty;
                }

                return new SegKey(ax, ay, bx, by);
            }

            private static long Quant(double v, double tol) => (long)Math.Round(v / tol);

            public bool Equals(SegKey other) =>
                _ax == other._ax && _ay == other._ay && _bx == other._bx && _by == other._by;

            public override bool Equals(object obj) => obj is SegKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + _ax.GetHashCode();
                    h = h * 31 + _ay.GetHashCode();
                    h = h * 31 + _bx.GetHashCode();
                    h = h * 31 + _by.GetHashCode();
                    return h;
                }
            }
        }

        private readonly struct SegGeom
        {
            public readonly Point2d A;
            public readonly Point2d B;
            public readonly int Count;

            public SegGeom(Point2d a, Point2d b, int count)
            {
                A = a; B = b; Count = count;
            }

            public SegGeom Increment() => new SegGeom(A, B, Count + 1);
        }
    }
}

