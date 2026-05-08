using System;
using System.Collections.Generic;
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

            string result = AssignShaft(db, zoneHandle, shaftHandle, out string err);
            if (result == null)
                ed.WriteMessage("\nAssignment failed: " + err);
            else
                ed.WriteMessage("\nShaft [" + shaftHandle + "] assigned to zone [" + zoneHandle + "].");
        }

        /// <summary>
        /// Programmatic assignment: stores xdata on zone boundary polyline and shaft block.
        /// Returns the shaft handle string on success, null on failure.
        /// </summary>
        public static string AssignShaft(Database db, string zoneBoundaryHandle, string shaftHandle, out string errorMessage)
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
                    SprinklerXData.ApplyZoneAssignmentTag(shaftEnt, zoneBoundaryHandle);

                    tr.Commit();
                }

                try
                {
                    var refreshMetrics = ScanAllZoneAssignments(db);
                    if (refreshMetrics != null)
                        ZoneAssignmentsUpdated?.Invoke(refreshMetrics);
                }
                catch { /* best-effort palette refresh */ }

                return shaftHandle;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        private static PolygonMetrics ScanAllZoneAssignments(Database db)
        {
            var zoneTable = new List<ZoneTableEntry>();
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    RXClass polylineClass  = RXClass.GetClass(typeof(Polyline));
                    RXClass mtextClass     = RXClass.GetClass(typeof(MText));
                    RXClass blockRefClass  = RXClass.GetClass(typeof(BlockReference));

                    // Pass 1: collect all shaft blocks, sort by position (X then Y), assign numbers.
                    var shaftList = new List<(string Handle, double X, double Y)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!id.ObjectClass.IsDerivedFrom(blockRefClass)) continue;
                        BlockReference br;
                        try { br = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference; }
                        catch { continue; }
                        if (br == null || br.IsErased) continue;

                        string bName = GetBlockName(br, tr);
                        string bLayer = br.Layer ?? string.Empty;
                        bool isShaft = bName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0
                                    || bLayer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isShaft) continue;
                        shaftList.Add((br.Handle.ToString(), br.Position.X, br.Position.Y));
                    }
                    // Sort left-to-right, bottom-to-top so numbering is spatially consistent.
                    shaftList.Sort((a, b) =>
                    {
                        int cx = a.X.CompareTo(b.X);
                        return cx != 0 ? cx : a.Y.CompareTo(b.Y);
                    });
                    var shaftNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < shaftList.Count; i++)
                        shaftNumbers[shaftList[i].Handle] = i + 1;

                    var labels = new List<(Point2d Pos, string Text)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!id.ObjectClass.IsDerivedFrom(mtextClass)) continue;
                        MText mt;
                        try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                        catch { continue; }
                        if (mt == null || mt.IsErased) continue;
                        if (!string.Equals(mt.Layer, SprinklerLayers.ZoneLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        // Strip MText paragraph codes (\P) — take only the first line (the zone name)
                        string raw = mt.Contents ?? string.Empty;
                        int pBreak = raw.IndexOf("\\P", StringComparison.OrdinalIgnoreCase);
                        string t = (pBreak > 0 ? raw.Substring(0, pBreak) : raw).Trim();
                        if (t.StartsWith("Zone", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Uncovered", StringComparison.OrdinalIgnoreCase))
                            labels.Add((new Point2d(mt.Location.X, mt.Location.Y), t));
                    }

                    // Key: zone name → entry. Prefer entries with an explicit shaft assignment.
                    var byName = new Dictionary<string, ZoneTableEntry>(StringComparer.OrdinalIgnoreCase);

                    foreach (ObjectId id in ms)
                    {
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
                        if (SprinklerXData.TryGetShaftAssignmentHandle(pl, out string assignedH))
                        {
                            if (shaftNumbers.TryGetValue(assignedH, out int shaftNum))
                                assignedShaftName = "Shaft " + shaftNum.ToString();
                            else
                                assignedShaftName = ResolveShaftBlockName(db, tr, assignedH) ?? ("Shaft ?");
                        }

                        // Deduplicate: only keep one entry per zone name.
                        // An entry with an explicit assignment wins over one without.
                        if (!byName.TryGetValue(zoneName, out var existing) || (assignedShaftName != null && existing.AssignedShaftName == null))
                        {
                            byName[zoneName] = new ZoneTableEntry
                            {
                                Name              = zoneName,
                                AreaDrawingUnits  = area,
                                AreaM2            = areaM2,
                                ZoneOwnerIndex    = null,
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
            return new PolygonMetrics
            {
                ZoneTable      = zoneTable,
                ZoningSummary  = "Zone-shaft assignments (" + zoneTable.Count.ToString() + " zones)"
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
