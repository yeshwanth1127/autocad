using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Commands;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Zoning
{
    /// <summary>
    /// Zone creation 1: exact Voronoi-style zoning via half-plane clipping, clipped to the floor boundary.
    /// </summary>
    public static class ZoneCreation1VoronoiWorkflow
    {
        public static bool TryRun(Document doc, Polyline boundary, ObjectId boundaryEntityId, out string message)
        {
            message = null;
            if (doc == null) { message = "No document."; return false; }
            if (boundary == null) { message = "No boundary selected."; return false; }

            var db = doc.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;

            FindShaftsInsideBoundary.GetShaftHandlesAndPositionsInsideBoundary(db, boundary, out var shaftPts, out var shaftHandlesRaw);
            ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSitesWithHandles(
                shaftPts, shaftHandlesRaw, tol,
                out var shaftSites,
                out var shaftHandlesDeduped);
            if (shaftSites.Count < 2)
            {
                message = "Need at least two shaft sites inside the boundary (found " +
                          shaftSites.Count.ToString(CultureInfo.InvariantCulture) + ").";
                return false;
            }

            try
            {
                doc.Editor.WriteMessage(
                    "\n[autocad-final] Zone creation 1: building Voronoi zones (exact half-plane clipping)...\n");
            }
            catch { /* ignore */ }

            // Build exact Voronoi cells by iteratively clipping a large convex polygon to the
            // "closer to site i than site j" half-planes, then intersect that cell with the (possibly concave)
            // floor boundary via AutoCAD Region booleans to obtain one or more rings.
            var rings = new List<List<Point2d>>();
            var ownerIndexForRing = new List<int>();

            double uncoveredAreaDu = 0.0;
            double floorAreaDu = 0.0;
            try { floorAreaDu = PolylineNetArea.Run(boundary); } catch { floorAreaDu = 0.0; }

            Region boundaryRegion = null;
            string lastIntersectErr = null;
            int intersectFails = 0;
            try
            {
                if (!RegionBooleanIntersection2d.TryCreateBoundaryRegion(boundary, tol, out boundaryRegion, out string regErr))
                {
                    message = regErr ?? "Could not create Region from boundary.";
                    return false;
                }

                var bbox = GetExtentsFromBoundary(boundary);
                double extent = Math.Max(Math.Abs(bbox.maxX - bbox.minX), Math.Abs(bbox.maxY - bbox.minY));
                if (!(extent > 0)) extent = 1.0;
                // Region explode + segment chaining can fail if tolerance is too tight relative to drawing scale.
                // Use a scale-aware tolerance that is never smaller than the project’s coincident tolerance.
                double regionTol = Math.Max(tol, extent * 1e-7);
                var initialCell = MakeHugeRectangle(bbox.minX, bbox.minY, bbox.maxX, bbox.maxY);

                for (int i = 0; i < shaftSites.Count; i++)
                {
                    var site = shaftSites[i];
                    var cell = new List<Point2d>(initialCell);

                    for (int j = 0; j < shaftSites.Count; j++)
                    {
                        if (i == j) continue;
                        cell = PolygonClipToCloserSiteHalfPlane2d.ClipToCloserThan(cell, site, shaftSites[j]);
                        if (cell == null || cell.Count < 3)
                            break;
                    }

                    if (cell == null || cell.Count < 3)
                        continue;

                    var cellClean = CleanRing(cell, regionTol);
                    if (cellClean == null || cellClean.Count < 3)
                        continue;

                    using (var slab = PolylineFromRing(cellClean, boundary))
                    {
                        if (RegionBooleanIntersection2d.TryIntersectBoundaryRegionWithSlabToRings(
                                boundaryRegion,
                                slab,
                                regionTol,
                                out var clippedRings,
                                out string intErr))
                        {
                            for (int r = 0; r < clippedRings.Count; r++)
                            {
                                var rr = CleanRing(clippedRings[r], regionTol);
                                if (rr == null || rr.Count < 3) continue;
                                rings.Add(rr);
                                ownerIndexForRing.Add(i);
                            }
                        }
                        else
                        {
                            intersectFails++;
                            if (!string.IsNullOrWhiteSpace(intErr))
                                lastIntersectErr = intErr;
                        }
                    }
                }
            }
            finally
            {
                try { boundaryRegion?.Dispose(); } catch { /* ignore */ }
            }

            if (rings.Count == 0)
            {
                message =
                    "Zone creation 1 failed: Voronoi clipping produced no zone polygons. " +
                    (intersectFails > 0 ? ("Intersections failed=" + intersectFails.ToString(CultureInfo.InvariantCulture) + ". ") : string.Empty) +
                    (!string.IsNullOrWhiteSpace(lastIntersectErr) ? ("Last error: " + lastIntersectErr) : string.Empty);
                return false;
            }

            try
            {
                double sum = 0.0;
                for (int i = 0; i < rings.Count; i++)
                    sum += PolygonVerticalHalfPlaneClip2d.AbsArea(rings[i]);
                uncoveredAreaDu = Math.Max(0.0, floorAreaDu - sum);
            }
            catch { uncoveredAreaDu = 0.0; }

            try { doc.Editor.WriteMessage("[autocad-final] Zone creation 1: drawing zone outlines...\n"); }
            catch { /* ignore */ }

            // Clear prior zone outlines + labels inside this floor so zones don't stack on reruns.
            try
            {
                var floorRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
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

            // Build zone table (name + area per ring).
            var zoneTable = new List<ZoneTableEntry>(rings.Count);
            var suffix = BuildOwnerSuffixes(ownerIndexForRing);
            for (int i = 0; i < rings.Count; i++)
            {
                double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(rings[i]);
                double? aM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                int owner = (ownerIndexForRing != null && i < ownerIndexForRing.Count) ? ownerIndexForRing[i] : -1;

                zoneTable.Add(new ZoneTableEntry
                {
                    Name = owner >= 0
                        ? ("Zone " + (owner + 1).ToString(CultureInfo.InvariantCulture) + suffix[i])
                        : ("Uncovered"),
                    AreaDrawingUnits = aDu,
                    AreaM2 = aM2,
                    ZoneOwnerIndex = owner
                });
            }

            var createdZoneHandles = new List<string>();
            ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                doc,
                rings,
                boundary,
                zoneTable,
                zoneOutlinesOnFloorBoundaryLayer: false,
                createdZonePolylineHandles: createdZoneHandles);

            AssignShaftToZoneCommand.ApplyDefaultShaftAssignmentsForCreatedZones(
                db, createdZoneHandles, ownerIndexForRing, shaftHandlesDeduped, boundary);

            // Palette refreshes zone→shaft table via CommandEnded + Idle (see SprinklerPaletteExtensionApplication).

            // If a previous run left global-boundary separator lines, clear them so the
            // output is the closed zone polylines (what the user expects to see/select).
            try
            {
                ZoneGlobalBoundaryBuilder.TryClearForFloorBoundary(doc, boundaryEntityId, out _);
            }
            catch { /* ignore */ }

            double? floorM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, floorAreaDu, out _);
            double? uncoveredM2 = (uncoveredAreaDu > 1e-9)
                ? DrawingUnitsHelper.TryGetAreaSquareMeters(db, uncoveredAreaDu, out _)
                : null;

            string summary =
                "Exact Voronoi (half-plane clipped). " +
                (floorM2.HasValue ? ("Floor area: " + floorM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m². ") : string.Empty) +
                (uncoveredM2.HasValue ? ("Uncovered: " + uncoveredM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m².") :
                    (uncoveredAreaDu > 1e-9 ? ("Uncovered: " + uncoveredAreaDu.ToString("F2", CultureInfo.InvariantCulture) + " sq. units.") : "Coverage complete."));

            message =
                "Zone creation 1 complete. Zones drawn=" + rings.Count.ToString(CultureInfo.InvariantCulture) + ". " +
                summary;
            return true;
        }

        private static (double minX, double minY, double maxX, double maxY) GetExtentsFromBoundary(Polyline boundary)
        {
            try
            {
                var ext = boundary.GeometricExtents;
                return (
                    Math.Min(ext.MinPoint.X, ext.MaxPoint.X),
                    Math.Min(ext.MinPoint.Y, ext.MaxPoint.Y),
                    Math.Max(ext.MinPoint.X, ext.MaxPoint.X),
                    Math.Max(ext.MinPoint.Y, ext.MaxPoint.Y));
            }
            catch
            {
                return (0, 0, 1, 1);
            }
        }

        private static List<Point2d> MakeHugeRectangle(double minX, double minY, double maxX, double maxY)
        {
            double dx = maxX - minX;
            double dy = maxY - minY;
            double extent = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (!(extent > 0)) extent = 1.0;
            double pad = extent * 4.0 + 10.0;
            double x0 = minX - pad, y0 = minY - pad, x1 = maxX + pad, y1 = maxY + pad;
            return new List<Point2d>
            {
                new Point2d(x0, y0),
                new Point2d(x1, y0),
                new Point2d(x1, y1),
                new Point2d(x0, y1)
            };
        }

        private static Polyline PolylineFromRing(List<Point2d> ring, Polyline boundaryPlane)
        {
            var pl = new Polyline();
            int n = ring?.Count ?? 0;
            for (int i = 0; i < n; i++)
                pl.AddVertexAt(i, ring[i], 0, 0, 0);
            pl.Closed = true;
            if (boundaryPlane != null)
            {
                pl.Elevation = boundaryPlane.Elevation;
                pl.Normal = boundaryPlane.Normal;
            }
            return pl;
        }

        private static List<Point2d> CleanRing(List<Point2d> ring, double tol)
        {
            if (ring == null || ring.Count < 3) return null;
            double eps = Math.Max(1e-9, tol);
            var pts = new List<Point2d>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (pts.Count == 0 || pts[pts.Count - 1].GetDistanceTo(p) > eps)
                    pts.Add(p);
            }
            if (pts.Count >= 2 && pts[0].GetDistanceTo(pts[pts.Count - 1]) <= eps)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return null;
            // Remove one-off duplicate vertices anywhere (Region explode can return repeated points).
            for (int i = pts.Count - 1; i >= 1; i--)
            {
                if (pts[i].GetDistanceTo(pts[i - 1]) <= eps)
                    pts.RemoveAt(i);
            }
            return pts.Count >= 3 ? pts : null;
        }

        private static string[] BuildOwnerSuffixes(List<int> ownerIndexForRing)
        {
            if (ownerIndexForRing == null || ownerIndexForRing.Count == 0)
                return Array.Empty<string>();

            var counts = ownerIndexForRing
                .Where(o => o >= 0)
                .GroupBy(o => o)
                .ToDictionary(g => g.Key, g => g.Count());

            var seen = new Dictionary<int, int>();
            var suffix = new string[ownerIndexForRing.Count];
            for (int i = 0; i < ownerIndexForRing.Count; i++)
            {
                int o = ownerIndexForRing[i];
                if (o < 0 || !counts.TryGetValue(o, out int total) || total <= 1)
                {
                    suffix[i] = string.Empty;
                    continue;
                }
                if (!seen.ContainsKey(o)) seen[o] = 0;
                seen[o]++;
                suffix[i] = " (" + seen[o].ToString(CultureInfo.InvariantCulture) + ")";
            }
            return suffix;
        }
    }
}

