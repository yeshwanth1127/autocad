using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Zoning
{
    /// <summary>
    /// Zone creation 2: partition the floor into <c>N</c> approximately equal-area zones (one per shaft),
    /// using recursive equal-area bisection when possible, otherwise equal-area axis-aligned strips.
    /// </summary>
    public static class ZoneCreation2EqualAreaWorkflow
    {
        public static bool TryRun(Document doc, Polyline boundary, ObjectId boundaryEntityId, out string message)
        {
            message = null;
            if (doc == null) { message = "No document."; return false; }
            if (boundary == null) { message = "No boundary selected."; return false; }

            var db = doc.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;

            var shaftPts3 = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, boundary);
            var shaftSites = ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSites(shaftPts3, tol);
            if (shaftSites.Count < 2)
            {
                message =
                    "Zones can be created for 2 or more shafts inside floor boundary\n" +
                    "Found : " + shaftSites.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var floorRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (floorRing == null || floorRing.Count < 3)
            {
                message = "Could not sample the floor boundary as a closed polygon.";
                return false;
            }

            // Enforce shaft capacity rule: 3000 m² maximum served per shaft.
            // If INSUNITS is wrong this conversion can be wrong too, but the rule is still a useful guardrail:
            // it prevents generating huge zones when only a few shafts exist.
            double floorAreaDuPre = 0.0;
            try { floorAreaDuPre = Math.Abs(PolylineNetArea.Run(boundary)); } catch { floorAreaDuPre = 0.0; }
            double? floorM2Pre = (floorAreaDuPre > 0)
                ? DrawingUnitsHelper.TryGetAreaSquareMeters(db, floorAreaDuPre, out _)
                : null;

            if (floorM2Pre.HasValue && floorM2Pre.Value > 0)
            {
                int shaftCount = shaftSites.Count;
                double maxServedM2 = shaftCount * DrawingUnitsHelper.ShaftAreaLimitM2;
                if (floorM2Pre.Value > maxServedM2)
                {
                    int required = DrawingUnitsHelper.RequiredShaftsCeil(floorM2Pre.Value);
                    message =
                        "Zones could not be created:\n" +
                        "Floor area exceeds shaft capacity\n\n" +
                        "Floor Area: " + floorM2Pre.Value.ToString("F2", CultureInfo.InvariantCulture) + " m²\n" +
                        "Shafts found: " + shaftCount.ToString(CultureInfo.InvariantCulture) + " (max served " +
                        maxServedM2.ToString("F2", CultureInfo.InvariantCulture) + " m² at " +
                        DrawingUnitsHelper.ShaftAreaLimitM2.ToString("F0", CultureInfo.InvariantCulture) + " m²/shaft)\n" +
                        "Required shafts: " + required.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
            }

            try
            {
                doc.Editor.WriteMessage(
                    "\n[autocad-final] Create zones 2: equal-area partition (~" +
                    shaftSites.Count.ToString(CultureInfo.InvariantCulture) + " zones)...\n");
            }
            catch { /* ignore */ }

            // Clear prior zone outlines + labels inside this floor.
            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    SprinklerZoneAutomationCleanup.ClearPriorZoneOutlinesInsideFloor(tr, ms, floorRing, boundaryEntityId);
                    tr.Commit();
                }
            }
            catch { /* ignore */ }

            List<List<Point2d>> rings;
            List<int> ownerPerRing;
            string methodNote;
            string stripErr = null;
            if (EqualAreaRecursiveBisection2d.TryBuildZoneRings(
                    floorRing,
                    shaftSites,
                    new EqualAreaRecursiveBisection2d.Options { AxisAlignedOnly = true },
                    out rings,
                    out ownerPerRing,
                    out string bisectSummary))
            {
                methodNote = "Method: equal-area recursive bisection (axis-aligned cuts only). " + bisectSummary;
            }
            else if (EqualAreaAxisStripZonesInPolygon2d.TryBuildZoneRings(
                         boundary,
                         shaftSites,
                         shaftSites.Count,
                         tol,
                         out rings,
                         out ownerPerRing,
                         out bool _,
                         out stripErr))
            {
                methodNote = "Method: equal-area axis-aligned strips (fallback; bisection unavailable). " +
                             (stripErr ?? string.Empty);
            }
            else
            {
                message = "Create zones 2 failed: no axis-aligned equal-area partition found for this boundary + shaft layout. " +
                          "Axis-aligned bisection did not succeed, and equal-area strips failed: " +
                          (stripErr ?? "unknown error.");
                return false;
            }

            if (rings == null || rings.Count == 0)
            {
                message = "Create zones 2 produced no zone polygons.";
                return false;
            }

            // NOTE: No orthogonal post-process is applied here.
            // The equal-area engine is constrained to axis-aligned cuts, so only shared separators become X/Y,
            // while floor-boundary-following edges remain exactly from polygon clipping (can be diagonal).

            double floorAreaDu = 0;
            try { floorAreaDu = PolylineNetArea.Run(boundary); } catch { /* ignore */ }
            double targetDu = shaftSites.Count > 0 ? floorAreaDu / shaftSites.Count : 0;

            var zoneTable = new List<ZoneTableEntry>(rings.Count);
            for (int i = 0; i < rings.Count; i++)
            {
                double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(rings[i]);
                double? aM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                int owner = (ownerPerRing != null && i < ownerPerRing.Count) ? ownerPerRing[i] : i;
                zoneTable.Add(new ZoneTableEntry
                {
                    Name = "Zone " + (owner + 1).ToString(CultureInfo.InvariantCulture),
                    AreaDrawingUnits = aDu,
                    AreaM2 = aM2,
                    ZoneOwnerIndex = owner
                });
            }

            var createdHandles = new List<string>();
            ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                doc,
                rings,
                boundary,
                zoneTable,
                zoneOutlinesOnFloorBoundaryLayer: false,
                createdZonePolylineHandles: createdHandles,
                useMcdZoneBoundaryLayer: true);

            // Clear any legacy global-boundary separator lines so the output is the closed zone
            // polylines only (what the user expects to see/select — consistent with ZoneCreation1).
            try
            {
                ZoneGlobalBoundaryBuilder.TryClearForFloorBoundary(doc, boundaryEntityId, out _);
            }
            catch { /* ignore */ }

            double? floorM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, floorAreaDu, out _);
            message =
                "Create zones 2 complete. Zones=" + rings.Count.ToString(CultureInfo.InvariantCulture) +
                ". Target ≈ " + targetDu.ToString("F2", CultureInfo.InvariantCulture) + " sq. units per zone" +
                (floorM2.HasValue ? (" (~" + (floorM2.Value / shaftSites.Count).ToString("F2", CultureInfo.InvariantCulture) + " m² each). ") : ". ") +
                "Interior separators axis-aligned (X/Y). " +
                methodNote;
            return true;
        }
    }
}
