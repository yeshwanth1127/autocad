using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Blocks;
using autocad_final.Validation;

namespace autocad_final.Workflows.Placement
{
    /// <summary>
    /// Places a dedicated sprinkler grid inside a closed room outline on <see cref="SprinklerLayers.McdFloorBoundaryLayer"/>
    /// (inner room loop on the floor plan), tagging heads with the parent MCD-zone boundary handle for downstream routing.
    /// </summary>
    public static class PlaceRoomSprinklersWorkflow
    {
        public static bool TryRun(
            Document doc,
            Polyline roomOutline,
            ObjectId roomBoundaryEntityId,
            out string message)
        {
            message = null;
            if (doc == null || roomOutline == null)
            {
                message = "Invalid room boundary.";
                return false;
            }

            if (!BoundaryValidator.IsMcdFloorBoundary(doc.Database, roomBoundaryEntityId))
            {
                message = "Selected entity must be on layer \"" + SprinklerLayers.McdFloorBoundaryLayer + "\".";
                return false;
            }

            if (!SprinklerRoomFootprintExclusion.TryHasInteriorLabel(doc.Database, roomOutline, out _))
            {
                message =
                    "Room outline must contain a non-empty DBText or MText label inside the polyline " +
                    "(same convention as floor placement room detection).";
                return false;
            }

            if (!RoomParentZoneResolver.TryResolveParentZoneForRoom(
                    doc.Database,
                    roomOutline,
                    out _,
                    out string parentZoneHandleHex,
                    out string parentErr))
            {
                message = parentErr ?? "Could not resolve parent zone for this room.";
                return false;
            }

            var cfg = RuntimeSettings.Load();
            double offsetDu = cfg.SprinklerToBoundaryDistanceM;
            double spacing = cfg.SprinklerSpacingM;

            if (!SprinklerGridInPolygonWorkflow.TryComputeInteriorGrid(
                    doc,
                    roomOutline,
                    offsetDu,
                    spacing,
                    out List<Point2d> offsetRing,
                    out List<Point2d> gridPts,
                    out string gridErr))
            {
                message = gridErr;
                return false;
            }

            var afterShafts = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(
                doc.Database,
                roomOutline,
                gridPts ?? new List<Point2d>());

            int placed = afterShafts?.Count ?? 0;
            if (placed == 0)
            {
                message = "No sprinkler grid points remained inside the room after offset and shaft exclusion.";
                return false;
            }

            if (!SprinklerBlockService.TryInsertSprinklersForOffsetPlacement(
                    doc,
                    roomOutline,
                    offsetRing,
                    afterShafts,
                    parentZoneHandleHex,
                    out string insertErr))
            {
                message = insertErr;
                AgentLog.Write("PlaceRoomSprinklersWorkflow", "insert fail: " + insertErr);
                return false;
            }

            message =
                "Room sprinklers placed: " + placed.ToString() +
                " head(s), tagged to parent zone handle " + parentZoneHandleHex + ".";
            AgentLog.Write("PlaceRoomSprinklersWorkflow", message);
            return true;
        }
    }
}
