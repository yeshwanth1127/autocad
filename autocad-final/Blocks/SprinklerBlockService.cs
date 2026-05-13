using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;

namespace autocad_final.Blocks
{
    /// <summary>Transaction wrapper for placing pendent sprinkler blocks (offset-boundary workflow).</summary>
    public static class SprinklerBlockService
    {
        /// <summary>Tags sprinklers with <paramref name="sourceZone"/>'s handle (floor or zone outline).</summary>
        public static bool TryInsertSprinklersForOffsetPlacement(
            Document doc,
            Polyline sourceZone,
            List<Point2d> offsetRing,
            List<Point2d> sprinklerPoints,
            out string error)
        {
            return TryInsertSprinklersForOffsetPlacement(
                doc,
                sourceZone,
                offsetRing,
                sprinklerPoints,
                zoneBoundaryHandleHexForXdata: null,
                out error);
        }

        /// <summary>
        /// Inserts heads using <paramref name="geometryPolyline"/> for elevation/normal while optionally tagging
        /// BOUNDARY extended data with <paramref name="zoneBoundaryHandleHexForXdata"/>.
        /// When <paramref name="zoneBoundaryHandleHexForXdata"/> is null or empty, uses <paramref name="geometryPolyline"/>'s handle.
        /// </summary>
        public static bool TryInsertSprinklersForOffsetPlacement(
            Document doc,
            Polyline geometryPolyline,
            List<Point2d> offsetRing,
            List<Point2d> sprinklerPoints,
            string zoneBoundaryHandleHexForXdata,
            out string error)
        {
            error = null;
            if (doc == null || geometryPolyline == null || offsetRing == null || offsetRing.Count < 3)
            {
                error = "Invalid offset boundary.";
                return false;
            }

            AgentLog.Write("SprinklerBlockService", "TryInsert enter pts=" + (sprinklerPoints?.Count ?? 0).ToString());

            var db = doc.Database;
            AgentLog.Write("SprinklerBlockService", "LockDocument+StartTransaction");
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    if (!PendentSprinklerBlockInsert.TryGetBlockDefinitionId(tr, db, out ObjectId blockDefId, out string blockErr))
                    {
                        error = blockErr;
                        AgentLog.Write("SprinklerBlockService", "block def missing: " + blockErr);
                        return false;
                    }

                    ObjectId designLayerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);

                    string boundaryHandleHex = null;
                    if (!string.IsNullOrWhiteSpace(zoneBoundaryHandleHexForXdata))
                        boundaryHandleHex = zoneBoundaryHandleHexForXdata.Trim();
                    if (string.IsNullOrEmpty(boundaryHandleHex))
                    {
                        try { boundaryHandleHex = geometryPolyline.Handle.ToString(); }
                        catch { /* ignore */ }
                    }

                    AgentLog.Write("SprinklerBlockService", "AppendBlocksAtPoints start");
                    PendentSprinklerBlockInsert.AppendBlocksAtPoints(
                        tr,
                        db,
                        ms,
                        geometryPolyline,
                        sprinklerPoints ?? new List<Point2d>(),
                        blockDefId,
                        designLayerId,
                        boundaryHandleHex,
                        rotationRadians: 0.0);

                    tr.Commit();
                    AgentLog.Write("SprinklerBlockService", "Commit ok");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AgentLog.Write("SprinklerBlockService", "exception: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
