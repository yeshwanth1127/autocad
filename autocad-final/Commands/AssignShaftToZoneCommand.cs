using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    /// <summary>
    /// AutoCAD command ASSIGNSHAFTOZONE: user selects a zone boundary polyline then a shaft block.
    /// Stores the assignment in xdata on both entities so RouteMainPipe always routes from that shaft
    /// regardless of spatial position (shaft may be outside the zone).
    /// </summary>
    public class AssignShaftToZoneCommand
    {
        /// <summary>Fired after a successful assignment with a fresh scan of all zone→shaft mappings.</summary>
        public static event Action<PolygonMetrics> ZoneAssignmentsUpdated;

        [CommandMethod("ASSIGNSHAFTOZONE", CommandFlags.Modal)]
        public void AssignShaftToZone()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            // 1. Select zone boundary polyline.
            var peoZone = new PromptEntityOptions("\nSelect zone boundary polyline: ");
            peoZone.SetRejectMessage("\nPlease select a closed polyline.");
            peoZone.AddAllowedClass(typeof(Polyline), exactMatch: true);
            var perZone = ed.GetEntity(peoZone);
            if (perZone.Status != PromptStatus.OK) return;

            // 2. Select shaft block.
            var peoShaft = new PromptEntityOptions("\nSelect shaft block: ");
            peoShaft.SetRejectMessage("\nPlease select a block reference.");
            peoShaft.AddAllowedClass(typeof(BlockReference), exactMatch: true);
            var perShaft = ed.GetEntity(peoShaft);
            if (perShaft.Status != PromptStatus.OK) return;

            string zoneHandle, shaftHandle;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);
                var zoneEnt = tr.GetObject(perZone.ObjectId, OpenMode.ForRead, false) as Polyline;
                var shaftBr = tr.GetObject(perShaft.ObjectId, OpenMode.ForRead, false) as BlockReference;

                if (zoneEnt == null) { ed.WriteMessage("\nSelected entity is not a polyline."); return; }
                if (shaftBr == null) { ed.WriteMessage("\nSelected entity is not a block reference."); return; }

                string blockName = GetBlockName(shaftBr, tr);
                bool isShaft = (!string.IsNullOrWhiteSpace(blockName) && blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(shaftBr.Layer) && shaftBr.Layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isShaft)
                {
                    ed.WriteMessage("\nWarning: selected block name/layer does not contain \"shaft\". Assigning anyway.");
                }

                zoneHandle = zoneEnt.Handle.ToString();
                shaftHandle = shaftBr.Handle.ToString();
                tr.Commit();
            }

            string result = AssignShaft(db, zoneHandle, shaftHandle, out string err, refreshPalette: true);
            if (result == null)
                ed.WriteMessage("\nAssignment failed: " + err);
            else
                ed.WriteMessage("\nShaft [" + shaftHandle + "] assigned to zone [" + zoneHandle + "].");
        }

        /// <summary>
        /// Programmatic assignment: stores xdata on zone boundary polyline and shaft block.
        /// Returns the shaft handle string on success, null on failure.
        /// </summary>
        /// <param name="refreshPalette">When false, skips <see cref="ZoneAssignmentsUpdated"/> (batch callers refresh once).</param>
        /// <param name="mergeShaftZoneAssignments">
        /// When false (default), the shaft's zone list is replaced with this zone only and other zone outlines that
        /// referenced this shaft are cleared—manual ASSIGNSHAFTOZONE override. When true (automatic zoning batch),
        /// zone handles are appended so one shaft can remain linked to multiple zone outlines.
        /// </param>
        public static string AssignShaft(
            Database db,
            string zoneBoundaryHandle,
            string shaftHandle,
            out string errorMessage,
            bool refreshPalette = true,
            bool mergeShaftZoneAssignments = false)
        {
            errorMessage = null;
            if (db == null) { errorMessage = "Database is null."; return null; }
            if (string.IsNullOrEmpty(zoneBoundaryHandle)) { errorMessage = "zone_boundary_handle is required."; return null; }
            if (string.IsNullOrEmpty(shaftHandle)) { errorMessage = "shaft_handle is required."; return null; }

            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                using (doc?.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Resolve zone boundary.
                    Handle zh;
                    try { zh = new Handle(Convert.ToInt64(zoneBoundaryHandle, 16)); }
                    catch { errorMessage = "Invalid zone_boundary_handle '" + zoneBoundaryHandle + "'."; return null; }

                    ObjectId zoneId = ObjectId.Null;
                    try { zoneId = db.GetObjectId(false, zh, 0); } catch { }
                    if (zoneId.IsNull) { errorMessage = "Zone boundary '" + zoneBoundaryHandle + "' not found."; return null; }

                    var zoneEnt = tr.GetObject(zoneId, OpenMode.ForWrite, false) as Entity;
                    if (zoneEnt == null) { errorMessage = "Zone boundary entity is not an Entity."; return null; }

                    // Resolve shaft block.
                    Handle sh;
                    try { sh = new Handle(Convert.ToInt64(shaftHandle, 16)); }
                    catch { errorMessage = "Invalid shaft_handle '" + shaftHandle + "'."; return null; }

                    ObjectId shaftId = ObjectId.Null;
                    try { shaftId = db.GetObjectId(false, sh, 0); } catch { }
                    if (shaftId.IsNull) { errorMessage = "Shaft block '" + shaftHandle + "' not found."; return null; }

                    var shaftEnt = tr.GetObject(shaftId, OpenMode.ForWrite, false) as Entity;
                    if (shaftEnt == null) { errorMessage = "Shaft entity is not an Entity."; return null; }

                    // Write xdata on both ends.
                    SprinklerXData.ApplyShaftAssignmentTag(zoneEnt, shaftHandle);
                    if (mergeShaftZoneAssignments)
                        SprinklerXData.ApplyZoneAssignmentTag(shaftEnt, zoneBoundaryHandle);
                    else
                    {
                        SprinklerXData.ApplyZoneAssignmentsExclusive(shaftEnt, zoneBoundaryHandle);
                        ClearShaftAssignmentFromOtherZoneOutlines(tr, ms, shaftHandle.Trim(), zoneBoundaryHandle.Trim());
                    }
                    // Self-tag the zone boundary so spatial zone-detection can confirm it is a real zone
                    // (not a floor outline that shares the "sprinkler - zone" layer).
                    SprinklerXData.ApplyZoneBoundaryTag(zoneEnt, zoneBoundaryHandle);

                    if (!SprinklerXData.TryGetShaftUid(shaftEnt, out int shaftUid))
                    {
                        EnsureShaftUidsUsingDrawingInference(db);
                        SprinklerXData.TryGetShaftUid(shaftEnt, out shaftUid);
                    }
                    if (shaftUid >= 1)
                        SprinklerXData.ApplyAssignedShaftUidOnZone(zoneEnt, shaftUid);

                    tr.Commit();
                }

                if (refreshPalette)
                {
                    try
                    {
                        var refreshMetrics = ScanAllZoneAssignments(db);
                        if (refreshMetrics != null)
                            ZoneAssignmentsUpdated?.Invoke(refreshMetrics);
                    }
                    catch { /* best-effort palette refresh */ }
                }

                return shaftHandle;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// After a manual assign, removes explicit shaft/UID tags from other zone outlines that still pointed at this shaft.
        /// </summary>
        private static void ClearShaftAssignmentFromOtherZoneOutlines(
            Transaction tr,
            BlockTableRecord ms,
            string assignedShaftHandleHex,
            string keepZoneBoundaryHandleHex)
        {
            if (tr == null || ms == null || string.IsNullOrEmpty(assignedShaftHandleHex) ||
                string.IsNullOrEmpty(keepZoneBoundaryHandleHex))
                return;

            RXClass polylineClass = RXClass.GetClass(typeof(Polyline));
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue;

                Polyline plRead;
                try { plRead = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (plRead == null || plRead.IsErased || !plRead.Closed) continue;

                string thisZoneHex = plRead.Handle.ToString();
                if (string.Equals(thisZoneHex, keepZoneBoundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isMcdZoneLayer = SprinklerLayers.IsMcdZoneOutlineLayerName(plRead.Layer);
                bool selfTagged =
                    SprinklerXData.TryGetZoneBoundaryHandle(plRead, out var ztag) &&
                    !string.IsNullOrWhiteSpace(ztag) &&
                    string.Equals(ztag, thisZoneHex, StringComparison.OrdinalIgnoreCase);
                if (!isMcdZoneLayer && !selfTagged)
                    continue;

                if (!SprinklerXData.TryGetShaftAssignmentHandle(plRead, out string shHex) ||
                    string.IsNullOrEmpty(shHex))
                    continue;
                if (!string.Equals(shHex.Trim(), assignedShaftHandleHex.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var entW = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entW == null) continue;
                    SprinklerXData.RemoveShaftAssignmentTag(entW);
                    SprinklerXData.RemoveShaftUidTag(entW);
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// After automatic zoning, assigns each new zone outline to the shaft that owns that cell (same index as
        /// Voronoi/equal-area owner). Skips rings with no resolvable shaft insert (e.g. virtual hints).
        /// Manual <c>ASSIGNSHAFTOZONE</c> overwrites by replacing xdata on the zone and updating the shaft tag list.
        /// </summary>
        /// <param name="floorBoundary">When set, shaft UID xdata is written for all shaft inserts inside this floor before zone assignment (matches table / routing scope).</param>
        public static void ApplyDefaultShaftAssignmentsForCreatedZones(
            Database db,
            IList<string> createdZoneBoundaryHandles,
            IList<int> ownerIndexPerRing,
            IList<string> shaftHandleHexPerDedupedSite,
            Polyline floorBoundary = null)
        {
            if (db == null || createdZoneBoundaryHandles == null || ownerIndexPerRing == null ||
                shaftHandleHexPerDedupedSite == null)
                return;

            if (floorBoundary != null)
                EnsureShaftUidsForFloorBoundary(db, floorBoundary);
            else
                EnsureShaftUidsUsingDrawingInference(db);

            int n = Math.Min(createdZoneBoundaryHandles.Count, ownerIndexPerRing.Count);
            for (int i = 0; i < n; i++)
            {
                string zoneHandle = createdZoneBoundaryHandles[i];
                if (string.IsNullOrEmpty(zoneHandle))
                    continue; // ring was skipped during drawing (null sentinel)

                int o = ownerIndexPerRing[i];
                if (o < 0 || o >= shaftHandleHexPerDedupedSite.Count)
                    continue;
                string sh = shaftHandleHexPerDedupedSite[o];
                if (string.IsNullOrEmpty(sh))
                    continue;
                AssignShaft(db, zoneHandle, sh, out _, refreshPalette: false, mergeShaftZoneAssignments: true);
            }
            // Caller merges display names into <see cref="PolygonMetrics.ZoneTable"/> and fires palette events (e.g. ZoneAreaCompleted).
        }

        /// <summary>
        /// Assigns 1-based <c>SHAFT_UID</c> xdata on every shaft block inside the floor boundary, stable order by X then Y.
        /// </summary>
        public static void EnsureShaftUidsForFloorBoundary(Database db, Polyline floorBoundary)
        {
            if (db == null || floorBoundary == null) return;
            var blocks = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, floorBoundary);
            WriteShaftUidsForSortedBlockList(db, blocks);
        }

        private static void EnsureShaftUidsUsingDrawingInference(Database db)
        {
            if (db == null) return;
            var blocks = InferShaftBlocksForFloorScopedNumbering(db);
            WriteShaftUidsForSortedBlockList(db, blocks);
        }

        private static void WriteShaftUidsForSortedBlockList(Database db, List<FindShaftsInsideBoundary.ShaftBlockInfo> shaftBlocks)
        {
            if (db == null || shaftBlocks == null) return;
            var shaftList = new List<(string Handle, double X, double Y)>();
            foreach (var b in shaftBlocks)
            {
                if (string.IsNullOrEmpty(b.BlockHandleHex)) continue;
                shaftList.Add((b.BlockHandleHex, b.Position.X, b.Position.Y));
            }
            shaftList.Sort((a, b) =>
            {
                int cx = a.X.CompareTo(b.X);
                return cx != 0 ? cx : a.Y.CompareTo(b.Y);
            });
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                using (doc?.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    for (int i = 0; i < shaftList.Count; i++)
                    {
                        Handle h;
                        try { h = new Handle(Convert.ToInt64(shaftList[i].Handle, 16)); } catch { continue; }
                        if (h.Value == 0) continue;
                        ObjectId id = ObjectId.Null;
                        try { id = db.GetObjectId(false, h, 0); } catch { continue; }
                        if (id.IsNull) continue;
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        SprinklerXData.ApplyShaftUidTag(ent, i + 1);
                    }
                    tr.Commit();
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Infers which shaft inserts belong to the active floor (parent-floor vote on zone outlines, else smallest
        /// ring containing zone centroids, else all model-space shafts) — same rule as the zone→shaft palette scan.
        /// </summary>
        private static List<FindShaftsInsideBoundary.ShaftBlockInfo> InferShaftBlocksForFloorScopedNumbering(Database db)
        {
            var zoneCentroids = new List<Point2d>();
            var parentFloorVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    RXClass polylineClass = RXClass.GetClass(typeof(Polyline));
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue;
                        Polyline pl;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { continue; }
                        if (pl == null || pl.IsErased || !pl.Closed) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string bh)) continue;
                        if (!string.Equals(bh, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                        Point2d centroid;
                        try
                        {
                            var ext = pl.GeometricExtents;
                            centroid = new Point2d(0.5 * (ext.MinPoint.X + ext.MaxPoint.X), 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        }
                        catch { centroid = new Point2d(0, 0); }
                        zoneCentroids.Add(centroid);
                        if (SprinklerXData.TryGetParentFloorBoundaryHandle(pl, out string phex) && !string.IsNullOrWhiteSpace(phex))
                        {
                            phex = phex.Trim();
                            parentFloorVotes[phex] = parentFloorVotes.TryGetValue(phex, out int c) ? c + 1 : 1;
                        }
                    }
                    tr.Commit();
                }
            }
            catch { /* best-effort */ }

            return ResolveShaftBlocksFromFloorVotesAndCentroids(db, zoneCentroids, parentFloorVotes);
        }

        private static List<FindShaftsInsideBoundary.ShaftBlockInfo> ResolveShaftBlocksFromFloorVotesAndCentroids(
            Database db,
            IList<Point2d> zoneCentroids,
            Dictionary<string, int> parentFloorVotes)
        {
            List<Point2d> floorRing = null;
            string parentWinner = null;
            int bestVotes = 0;
            if (parentFloorVotes != null)
            {
                foreach (var kv in parentFloorVotes)
                {
                    if (kv.Value > bestVotes)
                    {
                        bestVotes = kv.Value;
                        parentWinner = kv.Key;
                    }
                }
            }

            if (!string.IsNullOrEmpty(parentWinner) &&
                FindShaftsInsideBoundary.TryGetClosedPolylineRingByHandle(db, parentWinner, out List<Point2d> ringFromTag))
                floorRing = ringFromTag;
            else if (zoneCentroids != null && zoneCentroids.Count > 0)
            {
                try
                {
                    using (var trFloor = db.TransactionManager.StartOpenCloseTransaction())
                    {
                        SprinklerXData.EnsureRegApp(trFloor, db);
                        FindShaftsInsideBoundary.TryFindSmallestFloorRingContainingAllPoints(db, trFloor, zoneCentroids, out floorRing);
                        trFloor.Commit();
                    }
                }
                catch { /* best-effort */ }
            }

            if (floorRing != null && floorRing.Count >= 3)
                return FindShaftsInsideBoundary.GetShaftBlocksInsidePolygonRing(db, floorRing);
            return FindShaftsInsideBoundary.GetAllShaftBlockInsertsInModelSpace(db);
        }

        /// <summary>
        /// Fills <see cref="ZoneTableEntry.AssignedShaftName"/> on <paramref name="metrics"/> from current xdata (after auto or manual assign).
        /// </summary>
        public static void MergeShaftAssignmentDisplayNamesIntoZoneTable(Database db, PolygonMetrics metrics)
        {
            if (metrics?.ZoneTable == null || metrics.ZoneTable.Count == 0)
                return;
            var scan = ScanAllZoneAssignments(db);
            if (scan?.ZoneTable == null || scan.ZoneTable.Count == 0)
                return;
            var byName = scan.ZoneTable.ToDictionary(z => z.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var z in metrics.ZoneTable)
            {
                if (byName.TryGetValue(z.Name, out var row) && !string.IsNullOrEmpty(row.AssignedShaftName))
                    z.AssignedShaftName = row.AssignedShaftName;
            }
        }

        /// <summary>Rebuilds zone→shaft rows from xdata on zone outlines (palette / reports).</summary>
        public static PolygonMetrics ScanAllZoneShaftAssignments(Database db)
            => ScanAllZoneAssignments(db);

        /// <summary>Notifies listeners (palette) that zone→shaft xdata changed — e.g. after automatic assignment on zone creation.</summary>
        public static void PublishZoneShaftAssignmentScan(Database db)
        {
            try
            {
                var m = ScanAllZoneAssignments(db);
                if (m != null)
                    ZoneAssignmentsUpdated?.Invoke(m);
            }
            catch { /* ignore */ }
        }

        private static PolygonMetrics ScanAllZoneAssignments(Database db)
        {
            var zoneTable = new List<ZoneTableEntry>();
            int shaftCountListed = 0;
            string shaftCoordListed = string.Empty;

            var labels = new List<(Point2d Pos, string Text)>();
            var zoneCentroids = new List<Point2d>();
            var parentFloorVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    RXClass polylineClass = RXClass.GetClass(typeof(Polyline));
                    RXClass mtextClass = RXClass.GetClass(typeof(MText));

                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        if (!id.ObjectClass.IsDerivedFrom(mtextClass)) continue;
                        MText mt;
                        try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                        catch { continue; }
                        if (mt == null || mt.IsErased) continue;
                        if (!string.Equals(mt.Layer, SprinklerLayers.ZoneLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        string raw = mt.Contents ?? string.Empty;
                        int pBreak = raw.IndexOf("\\P", StringComparison.OrdinalIgnoreCase);
                        string t = (pBreak > 0 ? raw.Substring(0, pBreak) : raw).Trim();
                        if (t.StartsWith("Zone", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Uncovered", StringComparison.OrdinalIgnoreCase))
                            labels.Add((new Point2d(mt.Location.X, mt.Location.Y), t));
                    }

                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue;
                        Polyline pl;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { continue; }
                        if (pl == null || pl.IsErased || !pl.Closed) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string bh)) continue;
                        if (!string.Equals(bh, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                        Point2d centroid;
                        try
                        {
                            var ext = pl.GeometricExtents;
                            centroid = new Point2d(0.5 * (ext.MinPoint.X + ext.MaxPoint.X), 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        }
                        catch { centroid = new Point2d(0, 0); }
                        zoneCentroids.Add(centroid);
                        if (SprinklerXData.TryGetParentFloorBoundaryHandle(pl, out string phex) && !string.IsNullOrWhiteSpace(phex))
                        {
                            phex = phex.Trim();
                            parentFloorVotes[phex] = parentFloorVotes.TryGetValue(phex, out int c) ? c + 1 : 1;
                        }
                    }

                    tr.Commit();
                }
            }
            catch { /* best-effort */ }

            List<FindShaftsInsideBoundary.ShaftBlockInfo> shaftBlocks =
                ResolveShaftBlocksFromFloorVotesAndCentroids(db, zoneCentroids, parentFloorVotes);

            var shaftList = new List<(string Handle, double X, double Y)>();
            foreach (var b in shaftBlocks)
            {
                if (string.IsNullOrEmpty(b.BlockHandleHex)) continue;
                shaftList.Add((b.BlockHandleHex, b.Position.X, b.Position.Y));
            }

            shaftList.Sort((a, b) =>
            {
                int cx = a.X.CompareTo(b.X);
                return cx != 0 ? cx : a.Y.CompareTo(b.Y);
            });
            var shaftNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < shaftList.Count; i++)
                shaftNumbers[shaftList[i].Handle] = i + 1;

            var shaftPtsOrdered = new List<Point3d>(shaftList.Count);
            foreach (var s in shaftList)
                shaftPtsOrdered.Add(new Point3d(s.X, s.Y, 0.0));
            shaftCountListed = shaftList.Count;
            shaftCoordListed = FindShaftsInsideBoundary.FormatShaftPositionsForTable(shaftPtsOrdered);

            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    RXClass polylineClass = RXClass.GetClass(typeof(Polyline));
                    var byName = new Dictionary<string, ZoneTableEntry>(StringComparer.OrdinalIgnoreCase);

                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue;
                        Polyline pl;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { continue; }
                        if (pl == null || pl.IsErased || !pl.Closed) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string bh)) continue;
                        if (!string.Equals(bh, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                        double area = 0;
                        try { area = Math.Abs(pl.Area); } catch { }
                        double? areaM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, area, out _);

                        Point2d centroid;
                        try
                        {
                            var ext = pl.GeometricExtents;
                            centroid = new Point2d(0.5 * (ext.MinPoint.X + ext.MaxPoint.X), 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        }
                        catch { centroid = new Point2d(0, 0); }

                        string zoneName = null;
                        double bestD2 = double.MaxValue;
                        foreach (var lbl in labels)
                        {
                            double dx = lbl.Pos.X - centroid.X, dy = lbl.Pos.Y - centroid.Y;
                            double d2 = dx * dx + dy * dy;
                            if (d2 < bestD2) { bestD2 = d2; zoneName = lbl.Text; }
                        }
                        if (string.IsNullOrEmpty(zoneName))
                            zoneName = "Zone " + pl.Handle;

                        string assignedShaftName = null;
                        if (SprinklerXData.TryGetShaftUid(pl, out int uidTag))
                            assignedShaftName = "Shaft " + uidTag.ToString(CultureInfo.InvariantCulture);
                        else if (SprinklerXData.TryGetShaftAssignmentHandle(pl, out string assignedH))
                        {
                            if (shaftNumbers.TryGetValue(assignedH, out int shaftNum))
                                assignedShaftName = "Shaft " + shaftNum.ToString(CultureInfo.InvariantCulture);
                            else
                                assignedShaftName = ResolveShaftBlockName(db, tr, assignedH) ?? ("Shaft ?");
                        }

                        if (!byName.TryGetValue(zoneName, out var existing) || (assignedShaftName != null && existing.AssignedShaftName == null))
                        {
                            byName[zoneName] = new ZoneTableEntry
                            {
                                Name = zoneName,
                                AreaDrawingUnits = area,
                                AreaM2 = areaM2,
                                ZoneOwnerIndex = null,
                                AssignedShaftName = assignedShaftName
                            };
                        }
                    }

                    zoneTable.AddRange(byName.Values);
                    tr.Commit();
                }
            }
            catch { /* best-effort */ }

            if (zoneTable.Count == 0) return null;
            zoneTable.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            double sumAreaDu = 0;
            foreach (var z in zoneTable)
                sumAreaDu += Math.Abs(z.AreaDrawingUnits);

            return new PolygonMetrics
            {
                Area = sumAreaDu,
                Perimeter = 0,
                ShaftCount = shaftCountListed,
                ShaftCoordinates = shaftCoordListed,
                RoomName = "Zone → shaft",
                ZoneTable = zoneTable,
                ZoningSummary = "Zone-shaft assignments (" + zoneTable.Count.ToString() + " zones)"
            };
        }

        private static string ResolveShaftBlockName(Database db, Transaction tr, string shaftHandleHex)
        {
            if (string.IsNullOrEmpty(shaftHandleHex)) return null;
            Handle sh;
            try { sh = new Handle(Convert.ToInt64(shaftHandleHex, 16)); } catch { return null; }
            if (sh.Value == 0) return null;
            ObjectId shaftId = ObjectId.Null;
            try { shaftId = db.GetObjectId(false, sh, 0); } catch { return null; }
            if (shaftId.IsNull) return null;
            try
            {
                var br = tr.GetObject(shaftId, OpenMode.ForRead, false) as BlockReference;
                return br != null ? GetBlockName(br, tr) : null;
            }
            catch { return null; }
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!br.IsDynamicBlock) return btr.Name;
            if (!br.DynamicBlockTableRecord.IsNull)
            {
                var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                return dyn.Name;
            }
            return btr.Name;
        }
    }
}
