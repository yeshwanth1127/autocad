using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Blocks;
using autocad_final.Geometry;
using autocad_final.Validation;

namespace autocad_final.Workflows.Placement
{
    public static class PlaceSprinklersWorkflow
    {
        public static bool TryRun(Document doc, Polyline sourceZone, ObjectId boundaryEntityId, out string message)
        {
            message = null;
            AgentLog.Write("PlaceSprinklersWorkflow", "TryRun enter boundaryId=" + boundaryEntityId.ToString());
            if (doc == null || sourceZone == null)
            {
                message = "Invalid boundary input.";
                return false;
            }

            if (!BoundaryValidator.IsMcdFloorBoundary(doc.Database, boundaryEntityId))
            {
                message = "Selected entity must be on layer \"" + SprinklerLayers.McdFloorBoundaryLayer + "\".";
                return false;
            }

            double extentHintDu = 0;
            try
            {
                var ext = sourceZone.GeometricExtents;
                extentHintDu = System.Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
            }
            catch
            {
                extentHintDu = 0;
            }

            double duPerMeter = 1.0;

            var cfg = RuntimeSettings.Load();
            double offsetDu = cfg.SprinklerToBoundaryDistanceM * duPerMeter;
            double spacing = cfg.SprinklerSpacingM * duPerMeter;
            AgentLog.Write("PlaceSprinklersWorkflow",
                "extentDu=" + extentHintDu.ToString("G6", CultureInfo.InvariantCulture) +
                " duPerM=" + duPerMeter.ToString("G6", CultureInfo.InvariantCulture) +
                " offsetDu=" + offsetDu.ToString("G6", CultureInfo.InvariantCulture) +
                " spacingDu=" + spacing.ToString("G6", CultureInfo.InvariantCulture));

            if (!SprinklerGridInPolygonWorkflow.TryComputeInteriorGrid(
                    doc,
                    sourceZone,
                    offsetDu,
                    spacing,
                    out List<Point2d> offsetRing,
                    out List<Point2d> finalPointsChosen,
                    out string gridErr))
            {
                message = gridErr;
                AgentLog.Write("PlaceSprinklersWorkflow", "grid fail: " + gridErr);
                return false;
            }

            int beforeExclusions = finalPointsChosen.Count;

            var afterShafts = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(
                doc.Database,
                sourceZone,
                finalPointsChosen);
            int removedByShafts = Math.Max(0, beforeExclusions - (afterShafts?.Count ?? 0));

            var afterRooms = SprinklerRoomFootprintExclusion.RemovePointsInsideLabeledRoomFootprints(
                doc.Database,
                sourceZone,
                afterShafts ?? new List<Point2d>(),
                out int excludedRoomCount,
                out int removedByRooms);

            AgentLog.Write(
                "PlaceSprinklersWorkflow",
                "chosen points=" + beforeExclusions.ToString(CultureInfo.InvariantCulture) +
                " after shaft exclusion=" + (afterShafts?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " removedByShafts=" + removedByShafts.ToString(CultureInfo.InvariantCulture) +
                " after room exclusion=" + (afterRooms?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " excludedRooms=" + excludedRoomCount.ToString(CultureInfo.InvariantCulture) +
                " removedByRooms=" + removedByRooms.ToString(CultureInfo.InvariantCulture) +
                " TryInsert start");

            if (!SprinklerBlockService.TryInsertSprinklersForOffsetPlacement(
                    doc,
                    sourceZone,
                    offsetRing,
                    afterRooms ?? new List<Point2d>(),
                    out string insertErr))
            {
                message = insertErr;
                AgentLog.Write("PlaceSprinklersWorkflow", "TryInsert fail: " + insertErr);
                return false;
            }

            AgentLog.Write("PlaceSprinklersWorkflow", "TryInsert ok");
            message =
                "Internal grid sprinklers placed. " +
                "Excluded " + removedByShafts.ToString(CultureInfo.InvariantCulture) + " inside shafts, " +
                removedByRooms.ToString(CultureInfo.InvariantCulture) + " inside " +
                excludedRoomCount.ToString(CultureInfo.InvariantCulture) + " labeled room footprint(s).";
            return true;
        }
    }
}
