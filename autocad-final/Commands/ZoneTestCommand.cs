using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;
// (keep file C# 7.3 compatible; no Task parallelism needed here)

namespace autocad_final.Commands
{
    /// <summary>
    /// SPRINKLERZONETEST — drops a 3 m grid of pendent sprinklers over the full floor boundary:
    /// starts 1.5 m inside the bounding box, steps 3 m, keeps only points that fall inside the
    /// selected polygon with ≥ 1.5 m clearance from every wall. A lightweight alternative to the
    /// zone / shaft pipeline for previewing sprinkler coverage on any closed boundary.
    /// </summary>
    public class ZoneTestCommand
    {
        private const double ShaftClearanceMeters = 0.5;

        [CommandMethod("SPRINKLERZONETEST", CommandFlags.Modal)]
        public void SprinklerZoneTest()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc);
        }

        public static void Run(Document doc)
        {
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelect(ed, out Polyline boundary, out ObjectId boundaryEntityId))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "No valid closed polyline selected.", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
                if (ring == null || ring.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Boundary has fewer than 3 vertices — cannot place grid.", MessageBoxIcon.Warning);
                    return;
                }

                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                for (int i = 0; i < ring.Count; i++)
                {
                    var p = ring[i];
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }

                double lengthDu = maxX - minX;
                double breadthDu = maxY - minY;
                if (lengthDu <= 0 || breadthDu <= 0)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Boundary extent is degenerate.", MessageBoxIcon.Warning);
                    return;
                }

                double extentHintDu = Math.Max(lengthDu, breadthDu);
                var cfg = RuntimeSettings.Load();
                double spacingMeters = cfg.SprinklerSpacingM;
                double maxBoundaryDistanceMeters = cfg.SprinklerToBoundaryDistanceM;
                if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, spacingMeters, extentHintDu, out double duPerMeter) || duPerMeter <= 0)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not resolve drawing scale (set INSUNITS to Meters/Millimeters/etc.).", MessageBoxIcon.Warning);
                    return;
                }

                double spacingDu = spacingMeters * duPerMeter;
                double maxBoundaryDu = maxBoundaryDistanceMeters * duPerMeter;
                double shaftClearanceDu = ShaftClearanceMeters * duPerMeter;
                // Dynamic "not on boundary" clearance (no hardcoded meters):
                // Use drawing tolerance + boundary polyline display width + overall extent to decide what "on boundary" means.
                // This makes the fix robust across drawings and avoids relying on a fixed clearance distance.
                double tolDu = 1e-6;
                try
                {
                    tolDu = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                    if (!(tolDu > 0)) tolDu = 1e-6;
                }
                catch { tolDu = 1e-6; }
                double boundaryW = 0.0;
                try { boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db); } catch { boundaryW = 0.0; }
                double extentDu = extentHintDu;
                double minBoundaryClearanceDu =
                    Math.Max(
                        tolDu * 10.0,
                        Math.Max(boundaryW * 0.6, Math.Max(1e-4, extentDu * 1e-6)));
                // Still cap by the 1.5m max-distance rule, otherwise a huge lineweight would make placement impossible.
                minBoundaryClearanceDu = Math.Min(minBoundaryClearanceDu, maxBoundaryDu * 0.9);

                // Shaft exclusion points (inside boundary). Never place sprinklers on/near shafts.
                var shaftPts = new List<Point2d>();
                try
                {
                    var shafts3 = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, boundary);
                    if (shafts3 != null)
                    {
                        for (int si = 0; si < shafts3.Count; si++)
                            shaftPts.Add(new Point2d(shafts3[si].X, shafts3[si].Y));
                    }
                }
                catch
                {
                    // ignore shaft detection errors; zoning still works
                }

                double? lengthM = DrawingUnitsHelper.TryDrawingLengthToMeters(db.Insunits, lengthDu, out double lm) ? lm : (double?)(lengthDu / duPerMeter);
                double? breadthM = DrawingUnitsHelper.TryDrawingLengthToMeters(db.Insunits, breadthDu, out double bm) ? bm : (double?)(breadthDu / duPerMeter);

                int cols = Math.Max(1, (int)Math.Floor((lengthDu - 2 * maxBoundaryDu) / spacingDu) + 1);
                int rows = Math.Max(1, (int)Math.Floor((breadthDu - 2 * maxBoundaryDu) / spacingDu) + 1);

                // If scale inference would create a nearly-empty grid, we almost certainly guessed INSUNITS wrong.
                // Re-pick duPerMeter from a small set of known architectural scales so sprinklers place as an actual grid.
                if (cols < 4 || rows < 4)
                {
                    if (TryChooseBetterScaleForGrid(extentHintDu, out double betterDuPerMeter))
                    {
                        duPerMeter = betterDuPerMeter;
                        spacingDu = spacingMeters * duPerMeter;
                        maxBoundaryDu = maxBoundaryDistanceMeters * duPerMeter;
                        cols = Math.Max(1, (int)Math.Floor((lengthDu - 2 * maxBoundaryDu) / spacingDu) + 1);
                        rows = Math.Max(1, (int)Math.Floor((breadthDu - 2 * maxBoundaryDu) / spacingDu) + 1);
                    }
                }

                double startX = minX + maxBoundaryDu;
                double startY = minY + maxBoundaryDu;

                // Build initial layout with the default grid anchor.
                var placed = BuildZoneTestLayout(
                    ring,
                    startX,
                    startY,
                    cols,
                    rows,
                    spacingDu,
                    maxBoundaryDu,
                    minBoundaryClearanceDu,
                    shaftPts,
                    shaftClearanceDu);

                // If the output is "random sparse" (too few surviving points), retry with alternate
                // grid anchors that are still aligned to the same lattice (0 or half-cell offsets).
                if (LooksLikeSparseRandomLayout(placed, lengthDu, breadthDu, spacingDu))
                {
                    var best = placed;
                    int bestCount = placed.Count;

                    double half = spacingDu * 0.5;
                    double[] ox = { startX, startX + half };
                    double[] oy = { startY, startY + half };
                    for (int xi = 0; xi < ox.Length; xi++)
                    {
                        for (int yi = 0; yi < oy.Length; yi++)
                        {
                            // Skip the default anchor we already tried.
                            if (Math.Abs(ox[xi] - startX) < 1e-9 && Math.Abs(oy[yi] - startY) < 1e-9)
                                continue;

                            // Recompute grid counts for the shifted anchor (keep coverage area the same).
                            int c2 = Math.Max(1, (int)Math.Floor((lengthDu - 2 * maxBoundaryDu) / spacingDu) + 1);
                            int r2 = Math.Max(1, (int)Math.Floor((breadthDu - 2 * maxBoundaryDu) / spacingDu) + 1);

                            var trial = BuildZoneTestLayout(
                                ring,
                                ox[xi],
                                oy[yi],
                                c2,
                                r2,
                                spacingDu,
                                maxBoundaryDu,
                                minBoundaryClearanceDu,
                                shaftPts,
                                shaftClearanceDu);

                            if (trial.Count > bestCount)
                            {
                                best = trial;
                                bestCount = trial.Count;
                            }
                        }
                    }

                    placed = best;
                }

                if (placed.Count == 0)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "No grid points fit inside the boundary — boundary may be too small or narrow.", MessageBoxIcon.Warning);
                    return;
                }

                // Create zones using the exact same logic as "Zone boundary + threshold" (SPRINKLERZONEAREA2).
                // This draws zone outlines + labels and tags them properly. If it can't create zones, Zone-Test still proceeds.
                List<string> zoneBoundaryHandles = null;
                try
                {
                    SprinklerFloorZoningCascade.TryRun(
                        doc, boundary, boundaryEntityId,
                        echoMessages: true,
                        out _, out _, out zoneBoundaryHandles, out _, out _);

                    // Clear any legacy global-boundary separator lines so zones are represented as closed polylines only.
                    ZoneGlobalBoundaryBuilder.TryClearForFloorBoundary(doc, boundaryEntityId, out _);
                }
                catch
                {
                    // ignore zoning failures
                }

                // Resolve the stable handle of the source floor polyline so we can exclude it
                // when nudging sprinklers off zone-boundary lines.
                string floorSourceHandleHex = null;
                if (!boundaryEntityId.IsNull)
                {
                    try
                    {
                        using (var trh = db.TransactionManager.StartTransaction())
                        {
                            var srcEnt = trh.GetObject(boundaryEntityId, OpenMode.ForRead, false);
                            if (srcEnt != null) floorSourceHandleHex = srcEnt.Handle.ToString();
                            trh.Commit();
                        }
                    }
                    catch { floorSourceHandleHex = null; }
                }

                // Nudge sprinklers that sit on a zone-boundary line off the line, so no head
                // visually overlaps an internal zone division. Runs before insertion.
                double zoneLineThresholdDu = Math.Min(maxBoundaryDu * 0.25, spacingDu * 0.15);
                double zoneLineMaxShiftDu = Math.Min(maxBoundaryDu, spacingDu * 0.4);
                try
                {
                    NudgeSprinklersOffZoneBoundaryLines(
                        db, zoneBoundaryHandles, ring, placed,
                        thresholdDu: zoneLineThresholdDu,
                        maxShiftDu: zoneLineMaxShiftDu,
                        floorBoundaryHandleHex: floorSourceHandleHex);
                }
                catch
                {
                    // ignore nudge failures; placement still proceeds
                }

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    if (!PendentSprinklerBlockInsert.TryGetBlockDefinitionId(tr, db, out ObjectId blockDefId, out string blockErr))
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, blockErr ?? "Could not resolve sprinkler block.", MessageBoxIcon.Warning);
                        tr.Abort();
                        return;
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    ObjectId layerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);

                    // Use the handle of the SOURCE boundary entity in the database, not the
                    // local clone (the clone is never appended, so its Handle is 0). A zero
                    // handle would collide with every other zoned boundary and cause the
                    // cleanup pass to wipe sprinklers from unrelated boundaries.
                    string zoneBoundaryHandleHex = floorSourceHandleHex;
                    if (string.IsNullOrEmpty(zoneBoundaryHandleHex) && !boundaryEntityId.IsNull)
                    {
                        try
                        {
                            var srcEnt = tr.GetObject(boundaryEntityId, OpenMode.ForRead, false);
                            if (srcEnt != null) zoneBoundaryHandleHex = srcEnt.Handle.ToString();
                        }
                        catch { zoneBoundaryHandleHex = null; }
                    }

                    // Clear prior automated content inside this boundary so reruns don't leave stale on-boundary heads behind.
                    try
                    {
                        SprinklerZoneAutomationCleanup.ClearPriorAutomatedContent(
                            tr, ms,
                            zoneRing: new List<Point2d>(ring),
                            boundaryHandleHex: zoneBoundaryHandleHex,
                            floorBoundaryEntityId: boundaryEntityId);
                    }
                    catch
                    {
                        // ignore cleanup failures
                    }

                    placed = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(db, boundary, placed);

                    var created = new List<ObjectId>(placed.Count);
                    PendentSprinklerBlockInsert.AppendBlocksAtPoints(
                        tr, db, ms, boundary, placed, blockDefId, layerId,
                        zoneBoundaryHandleHex: zoneBoundaryHandleHex,
                        rotationRadians: 0.0,
                        createdIds: created);

                    // Step 2: associate newly placed sprinklers to shafts using square-dilation labeling.
                    TryAssociateSprinklersToShafts(tr, db, boundary, created, spacingDu);

                    tr.Commit();
                }

                try { ed.Regen(); } catch { /* ignore */ }

                ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\nZone-Test: {0} sprinklers placed (grid spacing {1} m) with boundary max-distance {2} m.\n",
                    placed.Count, spacingMeters, maxBoundaryDistanceMeters));
            }
            finally
            {
                try { boundary.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void EnsureMaxBoundaryDistance(
            IList<Point2d> ring,
            List<Point2d> placed,
            double maxBoundaryDu,
            double spacingDu,
            double gridOriginX,
            double gridOriginY,
            double minBoundaryClearanceDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu)
        {
            if (ring == null || ring.Count < 3) return;
            if (placed == null) return;
            // Determine winding so we can pick the inward normal consistently.
            // For CCW rings, the interior is to the left of each directed edge.
            bool ccw = SignedArea(ring) > 0;

            // Perimeter row rules:
            // - Sprinklers must NOT sit on the boundary -> enforce the caller's dynamic minimum clearance.
            // - Extra row must keep sprinkler-to-sprinkler spacing (3m) -> enforce near-spacing between added points.
            // - Row must be orthogonal to the boundary -> place by offsetting along inward normal of each segment.
            // - Added points must align with the main grid -> snap to gridOrigin/spacing.
            double minEdgeInsetDu = Math.Max(1e-4, minBoundaryClearanceDu);
            double minSprinklerSeparationDu = Math.Max(1e-6, spacingDu * 0.92);
            double sampleStepDu = Math.Max(1e-6, Math.Min(spacingDu * 0.5, maxBoundaryDu * 0.5));

            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 1e-9) continue;

                // Sample more densely than the grid so we don't miss gaps that fall between grid-aligned points.
                int count = Math.Max(1, (int)Math.Ceiling(len / sampleStepDu));
                for (int k = 0; k <= count; k++)
                {
                    double t = (double)k / count;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;

                    var pOnBoundary = new Point2d(a.X + t * dx, a.Y + t * dy);

                    // Only add if the boundary point is farther than maxBoundaryDu from the nearest sprinkler.
                    if (MinDistance(placed, pOnBoundary) <= maxBoundaryDu + 1e-6)
                        continue;

                    // Place at exactly maxBoundaryDu inward (orthogonal). If concavity blocks it, shrink but
                    // never below minEdgeInsetDu (still must not sit on boundary).
                    var q = InsetAlongInwardNormal(ring, ccw, a, b, pOnBoundary, maxBoundaryDu, minEdgeInsetDu);
                    if (!q.HasValue) continue;

                    // Snap to the same grid lattice so the perimeter row aligns with the main grid,
                    // but never allow snapping to undo the boundary inset (i.e. land on the boundary).
                    if (!TrySnapToGridWithConstraints(
                            ring,
                            ideal: q.Value,
                            gridOriginX: gridOriginX,
                            gridOriginY: gridOriginY,
                            spacingDu: spacingDu,
                            maxBoundaryDu: maxBoundaryDu,
                            minEdgeInsetDu: minEdgeInsetDu,
                            minSprinklerSeparationDu: minSprinklerSeparationDu,
                            existing: placed,
                            shaftPts: shaftPts,
                            shaftClearanceDu: shaftClearanceDu,
                            out var snapped))
                        continue;

                    placed.Add(snapped);
                }
            }
        }

        private static double MinDistance(IList<Point2d> pts, Point2d p)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - p.X;
                double dy = pts[i].Y - p.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
            }
            return best;
        }

        private static Point2d? InsetAlongInwardNormal(
            IList<Point2d> ring,
            bool ccw,
            Point2d segA,
            Point2d segB,
            Point2d boundaryPoint,
            double insetDu,
            double minInsetDu)
        {
            double dx = segB.X - segA.X;
            double dy = segB.Y - segA.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-9) return null;

            // Left normal = (-dy, dx). Right normal = (dy, -dx).
            double nx = -dy / len;
            double ny = dx / len;
            if (!ccw)
            {
                nx = -nx;
                ny = -ny;
            }

            // Try inward; if it falls outside (concavity), flip or shrink.
            double d = insetDu;
            for (int k = 0; k < 7; k++)
            {
                var q = new Point2d(boundaryPoint.X + nx * d, boundaryPoint.Y + ny * d);
                if (PointInPolygon(ring, q))
                    return q;

                // Sometimes winding/segment can be ambiguous; try the opposite normal once.
                if (k == 0)
                {
                    var q2 = new Point2d(boundaryPoint.X - nx * d, boundaryPoint.Y - ny * d);
                    if (PointInPolygon(ring, q2))
                        return q2;
                }

                d *= 0.7;
                if (d <= Math.Max(minInsetDu, insetDu * 0.25)) break;
            }

            return null;
        }

        private static double SignedArea(IList<Point2d> ring)
        {
            if (ring == null || ring.Count < 3) return 0.0;
            double a = 0.0;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                a += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
            return 0.5 * a;
        }

        private static Point2d? ComputeCentroid(IList<Point2d> ring)
        {
            if (ring == null || ring.Count < 3) return null;
            double a = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double x0 = ring[j].X, y0 = ring[j].Y;
                double x1 = ring[i].X, y1 = ring[i].Y;
                double cross = x0 * y1 - x1 * y0;
                a += cross;
                cx += (x0 + x1) * cross;
                cy += (y0 + y1) * cross;
            }

            if (Math.Abs(a) <= 1e-12) return null;
            a *= 0.5;
            cx /= (6.0 * a);
            cy /= (6.0 * a);
            return new Point2d(cx, cy);
        }

        private static Point2d PickInteriorLabelPoint(IList<Point2d> ring)
        {
            if (ring == null || ring.Count < 3)
                return new Point2d(0, 0);

            // Try centroid first (often works for convex rings).
            var c = ComputeCentroid(ring);
            if (c.HasValue && PointInPolygon(ring, c.Value))
                return c.Value;

            // Otherwise, scan a coarse grid inside the bounding box and pick the point with
            // the largest distance to edges (stable interior point for concave shapes).
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            double dx = maxX - minX;
            double dy = maxY - minY;
            if (dx <= 1e-9 || dy <= 1e-9)
                return ring[0];

            int steps = 18; // 19x19 samples; fast and good enough for label placement
            double bestScore = double.NegativeInfinity;
            Point2d best = ring[0];

            for (int ix = 1; ix < steps; ix++)
            {
                double x = minX + (dx * ix) / steps;
                for (int iy = 1; iy < steps; iy++)
                {
                    double y = minY + (dy * iy) / steps;
                    var p = new Point2d(x, y);
                    if (!PointInPolygon(ring, p))
                        continue;
                    double d = DistanceToPolygonEdges(ring, p);
                    if (d > bestScore)
                    {
                        bestScore = d;
                        best = p;
                    }
                }
            }

            return best;
        }

        private static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                bool intersect =
                    ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static double DistanceToPolygonEdges(IList<Point2d> ring, Point2d p)
        {
            double best = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double d = PointToSegmentDistance(p, a, b);
                if (d < best) best = d;
            }
            return best;
        }

        private static double PointToSegmentDistance(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq <= 1e-24) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = a.X + t * dx, cy = a.Y + t * dy;
            return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        }

        private static void TryAssociateSprinklersToShafts(
            Transaction tr,
            Database db,
            Polyline boundary,
            List<ObjectId> sprinklerIds,
            double spacingDu)
        {
            if (tr == null || db == null || boundary == null || sprinklerIds == null || sprinklerIds.Count == 0)
                return;

            List<Point3d> shafts3;
            try
            {
                shafts3 = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, boundary);
            }
            catch
            {
                return;
            }

            if (shafts3 == null || shafts3.Count == 0)
                return;

            // Deterministic shaft ids: sort by X then Y.
            shafts3.Sort((p, q) =>
            {
                int cx = p.X.CompareTo(q.X);
                return cx != 0 ? cx : p.Y.CompareTo(q.Y);
            });

            var shafts = new List<Point2d>(shafts3.Count);
            for (int i = 0; i < shafts3.Count; i++)
                shafts.Add(new Point2d(shafts3[i].X, shafts3[i].Y));

            var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring == null || ring.Count < 3)
                return;

            // Build a coarse grid to run square (Chebyshev) dilation within the boundary.
            // Cell size roughly half the design spacing (good tradeoff of speed vs fidelity).
            double cell = Math.Max(1e-6, spacingDu * 0.5);
            if (!TryBuildInsideGrid(ring, cell, out var origin, out int w, out int h, out bool[,] inside))
                return;

            if (w * h > 2_500_000) // safety cap to avoid huge allocations on very large drawings
                return;

            var labels = new int[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    labels[x, y] = -1;

            // Multi-source BFS == "dilation per shaft in parallel".
            var qx = new int[w * h];
            var qy = new int[w * h];
            int qh = 0, qt = 0;

            for (int si = 0; si < shafts.Count; si++)
            {
                if (!TryWorldToCell(origin, cell, w, h, shafts[si], out int sx, out int sy))
                    continue;
                if (!inside[sx, sy])
                    continue;
                if (labels[sx, sy] >= 0)
                    continue;
                labels[sx, sy] = si;
                qx[qt] = sx; qy[qt] = sy; qt++;
            }

            // 8-neighborhood for square structuring element.
            int[] nx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] ny = { -1, -1, -1, 0, 0, 1, 1, 1 };

            while (qh < qt)
            {
                int x = qx[qh];
                int y = qy[qh];
                int lab = labels[x, y];
                qh++;

                for (int k = 0; k < 8; k++)
                {
                    int xx = x + nx[k], yy = y + ny[k];
                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h)
                        continue;
                    if (!inside[xx, yy])
                        continue;
                    if (labels[xx, yy] >= 0)
                        continue;
                    labels[xx, yy] = lab;
                    qx[qt] = xx; qy[qt] = yy; qt++;
                }
            }

            // Apply shaft id tags to the newly created sprinkler block references.
            SprinklerXData.EnsureRegApp(tr, db);

            for (int i = 0; i < sprinklerIds.Count; i++)
            {
                if (sprinklerIds[i].IsNull) continue;
                if (!(tr.GetObject(sprinklerIds[i], OpenMode.ForWrite, false) is BlockReference br))
                    continue;

                var p2 = new Point2d(br.Position.X, br.Position.Y);
                int si = -1;
                if (TryWorldToCell(origin, cell, w, h, p2, out int cx, out int cy))
                    si = labels[cx, cy];

                if (si < 0 || si >= shafts.Count)
                    si = FindNearestShaftIndexEuclidean(shafts, p2);
                if (si < 0) continue;

                string shaftId = "shaft_" + (si + 1).ToString(CultureInfo.InvariantCulture);
                try { SprinklerXData.ApplyShaftTag(br, shaftId); } catch { /* ignore */ }
            }
        }

        // (Manhattan-zone creation removed; Zone-Test now calls the same cascade as SPRINKLERZONEAREA2.)

        private static int FindNearestShaftIndexEuclidean(IList<Point2d> shafts, Point2d p)
        {
            if (shafts == null || shafts.Count == 0) return -1;
            int best = -1;
            double bestD = double.PositiveInfinity;
            for (int i = 0; i < shafts.Count; i++)
            {
                double dx = shafts[i].X - p.X;
                double dy = shafts[i].Y - p.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static bool TryBuildInsideGrid(
            IList<Point2d> ring,
            double cell,
            out Point2d origin,
            out int width,
            out int height,
            out bool[,] inside)
        {
            origin = new Point2d(0, 0);
            width = 0;
            height = 0;
            inside = null;

            if (ring == null || ring.Count < 3 || cell <= 0)
                return false;

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            // One-cell padding to ensure edge coverage.
            minX -= cell;
            minY -= cell;
            maxX += cell;
            maxY += cell;

            width = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cell));
            height = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cell));
            origin = new Point2d(minX, minY);
            inside = new bool[width, height];

            for (int x = 0; x < width; x++)
            {
                double wx = origin.X + (x + 0.5) * cell;
                for (int y = 0; y < height; y++)
                {
                    double wy = origin.Y + (y + 0.5) * cell;
                    inside[x, y] = PointInPolygon(ring, new Point2d(wx, wy));
                }
            }

            return true;
        }

        private static bool TryWorldToCell(Point2d origin, double cell, int w, int h, Point2d p, out int cx, out int cy)
        {
            cx = cy = 0;
            if (cell <= 0) return false;
            cx = (int)Math.Floor((p.X - origin.X) / cell);
            cy = (int)Math.Floor((p.Y - origin.Y) / cell);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) return false;
            return true;
        }

        private static bool TryChooseBetterScaleForGrid(double extentHintDu, out double duPerMeter)
        {
            duPerMeter = 0;
            if (extentHintDu <= 0) return false;

            // Same candidates as DrawingUnitsHelper (mm, cm, m, inches, feet).
            double[] candidates = { 1000.0, 100.0, 1.0, 1.0 / 0.0254, 1.0 / 0.3048 };

            const double minMeters = 0.8;
            const double maxMeters = 500.0;
            const double idealMeters = 20.0;
            const double maxCellsPerAxis = 3000.0;
            const double minCellsPerAxis = 5.0; // we want a real grid, not 1–3 points

            double bestScale = 0;
            double bestScore = double.PositiveInfinity;

            for (int i = 0; i < candidates.Length; i++)
            {
                double s = candidates[i];
                if (s <= 0) continue;

                double zoneSizeM = extentHintDu / s;
                if (zoneSizeM < minMeters || zoneSizeM > maxMeters)
                    continue;

                double cellsPerAxis = extentHintDu / (s * RuntimeSettings.Load().SprinklerSpacingM);
                if (cellsPerAxis > maxCellsPerAxis || cellsPerAxis < minCellsPerAxis)
                    continue;

                double score = Math.Abs(Math.Log(zoneSizeM) - Math.Log(idealMeters));
                if (score < bestScore)
                {
                    bestScore = score;
                    bestScale = s;
                }
            }

            if (bestScale > 0)
            {
                duPerMeter = bestScale;
                return true;
            }

            return false;
        }

        private static Point2d SnapToGrid(Point2d p, double originX, double originY, double spacingDu)
        {
            if (!(spacingDu > 1e-9))
                return p;
            double gx = originX + Math.Round((p.X - originX) / spacingDu) * spacingDu;
            double gy = originY + Math.Round((p.Y - originY) / spacingDu) * spacingDu;
            return new Point2d(gx, gy);
        }

        private static bool TrySnapToGridWithConstraints(
            IList<Point2d> ring,
            Point2d ideal,
            double gridOriginX,
            double gridOriginY,
            double spacingDu,
            double maxBoundaryDu,
            double minEdgeInsetDu,
            double minSprinklerSeparationDu,
            IList<Point2d> existing,
            IList<Point2d> shaftPts,
            double shaftClearanceDu,
            out Point2d snapped)
        {
            snapped = ideal;
            if (!(spacingDu > 1e-9))
                return false;

            // Search nearby grid points around the nearest snap so we stay on-lattice,
            // but pick a candidate that still respects all constraints.
            var basePt = SnapToGrid(ideal, gridOriginX, gridOriginY, spacingDu);
            int baseI = (int)Math.Round((basePt.X - gridOriginX) / spacingDu);
            int baseJ = (int)Math.Round((basePt.Y - gridOriginY) / spacingDu);

            const int R = 2; // ±2 cells => 25 candidates, cheap
            bool found = false;
            double bestScore = double.PositiveInfinity;
            Point2d best = basePt;

            for (int di = -R; di <= R; di++)
            {
                for (int dj = -R; dj <= R; dj++)
                {
                    double gx = gridOriginX + (baseI + di) * spacingDu;
                    double gy = gridOriginY + (baseJ + dj) * spacingDu;
                    var c = new Point2d(gx, gy);

                    if (!PointInPolygon(ring, c))
                        continue;

                    double dEdge = DistanceToPolygonEdges(ring, c);
                    // Hard rules: not on boundary, and still meets "max 1.5m to boundary".
                    if (dEdge < minEdgeInsetDu)
                        continue;
                    if (dEdge > maxBoundaryDu + 1e-6)
                        continue;

                    // Avoid shaft points.
                    if (shaftPts != null && shaftPts.Count > 0 && MinDistance(shaftPts, c) < shaftClearanceDu)
                        continue;

                    // Keep sprinkler-to-sprinkler spacing.
                    if (existing != null && existing.Count > 0 && MinDistance(existing, c) < minSprinklerSeparationDu)
                        continue;

                    // Choose candidate closest to the ideal (keeps the row visually in the right place).
                    double dx = c.X - ideal.X;
                    double dy = c.Y - ideal.Y;
                    double score = dx * dx + dy * dy;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = c;
                        found = true;
                    }
                }
            }

            if (!found)
                return false;

            snapped = best;
            return true;
        }

        private static void RepairBoundaryViolationsOnGrid(
            IList<Point2d> ring,
            List<Point2d> placed,
            IList<Point2d> originalGrid,
            double minBoundaryClearanceDu,
            double minFromOriginalGridDu,
            double maxBoundaryDu,
            double gridOriginX,
            double gridOriginY,
            double spacingDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu)
        {
            if (ring == null || placed == null || placed.Count == 0)
                return;

            // Any point closer than this to the boundary is considered "on the boundary line" for our purposes.
            double boundaryEps = Math.Max(1e-4, minBoundaryClearanceDu);

            for (int i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                double dEdge = DistanceToPolygonEdges(ring, p);
                if (dEdge >= boundaryEps)
                    continue;

                if (TryFindNearbyGridPointForRepair(
                        ring,
                        p,
                        originalGrid,
                        minBoundaryClearanceDu,
                        minFromOriginalGridDu,
                        maxBoundaryDu,
                        gridOriginX,
                        gridOriginY,
                        spacingDu,
                        placed,
                        shaftPts,
                        shaftClearanceDu,
                        out var repaired))
                {
                    placed[i] = repaired;
                }
            }
        }

        private static bool TryFindNearbyGridPointForRepair(
            IList<Point2d> ring,
            Point2d current,
            IList<Point2d> originalGrid,
            double minBoundaryClearanceDu,
            double minFromOriginalGridDu,
            double maxBoundaryDu,
            double gridOriginX,
            double gridOriginY,
            double spacingDu,
            IList<Point2d> allPlaced,
            IList<Point2d> shaftPts,
            double shaftClearanceDu,
            out Point2d repaired)
        {
            repaired = current;
            if (!(spacingDu > 1e-9))
                return false;

            // Search a slightly larger neighborhood because we may need to step in by multiple cells.
            var basePt = SnapToGrid(current, gridOriginX, gridOriginY, spacingDu);
            int baseI = (int)Math.Round((basePt.X - gridOriginX) / spacingDu);
            int baseJ = (int)Math.Round((basePt.Y - gridOriginY) / spacingDu);

            const int R = 4; // ±4 cells => 81 candidates
            bool found = false;
            double bestScore = double.PositiveInfinity;
            Point2d best = current;

            for (int di = -R; di <= R; di++)
            {
                for (int dj = -R; dj <= R; dj++)
                {
                    double gx = gridOriginX + (baseI + di) * spacingDu;
                    double gy = gridOriginY + (baseJ + dj) * spacingDu;
                    var c = new Point2d(gx, gy);

                    if (!PointInPolygon(ring, c))
                        continue;

                    double dEdge = DistanceToPolygonEdges(ring, c);
                    if (dEdge < minBoundaryClearanceDu)
                        continue;
                    if (dEdge > maxBoundaryDu + 1e-6)
                        continue;

                    if (shaftPts != null && shaftPts.Count > 0 && MinDistance(shaftPts, c) < shaftClearanceDu)
                        continue;

                    // Must be at least 1.5m away from the original grid (excluding itself by epsilon).
                    if (originalGrid != null && originalGrid.Count > 0)
                    {
                        double md = MinDistance(originalGrid, c);
                        if (md < minFromOriginalGridDu - 1e-6)
                            continue;
                    }

                    // Also keep reasonable distance to all sprinklers so we don't cluster.
                    if (allPlaced != null && allPlaced.Count > 0)
                    {
                        double mdAll = MinDistance(allPlaced, c);
                        // If we're evaluating the same location, mdAll will be 0; that's okay only if it is the current point.
                        if (mdAll < spacingDu * 0.5 - 1e-6 && (Math.Abs(c.X - current.X) > 1e-6 || Math.Abs(c.Y - current.Y) > 1e-6))
                            continue;
                    }

                    // Prefer smallest movement from current so the row still looks merged with the grid.
                    double dx = c.X - current.X;
                    double dy = c.Y - current.Y;
                    double score = dx * dx + dy * dy;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = c;
                        found = true;
                    }
                }
            }

            if (!found)
                return false;

            repaired = best;
            return true;
        }

        private static void RelaxedRepairBoundaryViolations(
            IList<Point2d> ring,
            List<Point2d> placed,
            double minBoundaryClearanceDu,
            double maxBoundaryDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu)
        {
            if (ring == null || placed == null || placed.Count == 0)
                return;

            double boundaryEps = Math.Max(1e-4, minBoundaryClearanceDu);
            bool ccw = SignedArea(ring) > 0;

            for (int i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                if (DistanceToPolygonEdges(ring, p) >= boundaryEps)
                    continue;

                if (TryMovePointInsideAlongNearestEdgeNormal(
                        ring,
                        placed,
                        i,
                        ccw,
                        minBoundaryClearanceDu,
                        maxBoundaryDu,
                        shaftPts,
                        shaftClearanceDu,
                        out var moved))
                {
                    placed[i] = moved;
                }
            }
        }

        private static bool TryMovePointInsideAlongNearestEdgeNormal(
            IList<Point2d> ring,
            List<Point2d> placed,
            int index,
            bool ccw,
            double minBoundaryClearanceDu,
            double maxBoundaryDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu,
            out Point2d moved)
        {
            moved = placed[index];
            var p = placed[index];

            // Move along inward normal of the closest boundary edge. This guarantees boundary clearance increases
            // even when the boundary is slanted (moving toward another sprinkler can be parallel to the edge).
            if (!TryGetClosestEdgeInwardNormal(ring, ccw, p, out double nx, out double ny))
                return false;

            // Push more aggressively inward until boundary clearance is satisfied.
            // Allow breaking 3m spacing, but still keep within the max-boundary distance rule (<= 1.5m from boundary).
            double d0 = DistanceToPolygonEdges(ring, p);
            double maxMove = Math.Max(1e-6, maxBoundaryDu - minBoundaryClearanceDu);

            // Start with a larger move based on how far we are from the clearance target.
            double need = Math.Max(0.0, minBoundaryClearanceDu - d0);
            double movedDist = Math.Min(maxMove, Math.Max(minBoundaryClearanceDu * 0.6, need * 2.5));
            double step = Math.Max(1e-6, minBoundaryClearanceDu * 2.0);

            for (int it = 0; it < 80; it++)
            {
                if (movedDist > maxMove)
                    break;

                var q = new Point2d(p.X + nx * movedDist, p.Y + ny * movedDist);
                if (!PointInPolygon(ring, q))
                {
                    movedDist += step;
                    continue;
                }

                double dEdge = DistanceToPolygonEdges(ring, q);
                if (dEdge < minBoundaryClearanceDu)
                {
                    movedDist += step;
                    continue;
                }
                if (dEdge > maxBoundaryDu + 1e-6)
                {
                    movedDist += step;
                    continue;
                }

                if (shaftPts != null && shaftPts.Count > 0 && MinDistance(shaftPts, q) < shaftClearanceDu)
                {
                    movedDist += step;
                    continue;
                }

                moved = q;
                return true;

                // (unreachable)
            }

            return false;
        }

        private static bool TryGetClosestEdgeInwardNormal(IList<Point2d> ring, bool ccw, Point2d p, out double nx, out double ny)
        {
            nx = ny = 0.0;
            if (ring == null || ring.Count < 3)
                return false;

            int n = ring.Count;
            double bestD2 = double.PositiveInfinity;
            double bestNx = 0.0, bestNy = 0.0;

            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                if (len2 <= 1e-18)
                    continue;

                // Distance from p to segment a-b (squared)
                double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                if (t < 0) t = 0;
                else if (t > 1) t = 1;
                double px = a.X + t * dx;
                double py = a.Y + t * dy;
                double ex = p.X - px;
                double ey = p.Y - py;
                double d2 = ex * ex + ey * ey;

                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    double len = Math.Sqrt(len2);
                    // Left normal (-dy, dx), flipped for CW.
                    double lx = -dy / len;
                    double ly = dx / len;
                    if (!ccw) { lx = -lx; ly = -ly; }
                    bestNx = lx;
                    bestNy = ly;
                }
            }

            if (double.IsInfinity(bestD2))
                return false;

            nx = bestNx;
            ny = bestNy;
            return (nx * nx + ny * ny) > 1e-12;
        }

        private static List<Point2d> BuildZoneTestLayout(
            IList<Point2d> ring,
            double gridOriginX,
            double gridOriginY,
            int cols,
            int rows,
            double spacingDu,
            double maxBoundaryDu,
            double minBoundaryClearanceDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu)
        {
            var placed = new List<Point2d>();

            // Base grid points.
            for (int r = 0; r < rows; r++)
            {
                double y = gridOriginY + r * spacingDu;
                for (int c = 0; c < cols; c++)
                {
                    double x = gridOriginX + c * spacingDu;
                    var p = new Point2d(x, y);
                    if (!PointInPolygon(ring, p)) continue;
                    if (DistanceToPolygonEdges(ring, p) < minBoundaryClearanceDu)
                        continue;
                    if (shaftPts != null && shaftPts.Count > 0 && MinDistance(shaftPts, p) < shaftClearanceDu)
                        continue;
                    placed.Add(p);
                }
            }

            // Perimeter row (snapped to this grid origin).
            EnsureMaxBoundaryDistance(
                ring, placed,
                maxBoundaryDu, spacingDu,
                gridOriginX: gridOriginX, gridOriginY: gridOriginY,
                minBoundaryClearanceDu: minBoundaryClearanceDu,
                shaftPts: shaftPts, shaftClearanceDu: shaftClearanceDu);

            // Repair boundary violations (allow breaking 3m).
            RelaxedRepairBoundaryViolations(
                ring,
                placed,
                minBoundaryClearanceDu,
                maxBoundaryDu,
                shaftPts,
                shaftClearanceDu);

            // Final pass: any sprinkler that visibly sits on the boundary line
            // gets nudged inward (or removed when no valid interior position exists)
            // so no block overlaps the boundary polyline itself.
            double onBoundaryThresholdDu = Math.Min(maxBoundaryDu * 0.25, spacingDu * 0.15);
            if (onBoundaryThresholdDu < minBoundaryClearanceDu * 2.0)
                onBoundaryThresholdDu = minBoundaryClearanceDu * 2.0;
            double targetInsetDu = Math.Min(maxBoundaryDu, Math.Max(onBoundaryThresholdDu * 1.5, minBoundaryClearanceDu * 4.0));
            NudgeOnBoundarySprinklersInward(
                ring,
                placed,
                onBoundaryThresholdDu: onBoundaryThresholdDu,
                targetInsetDu: targetInsetDu,
                maxBoundaryDu: maxBoundaryDu,
                shaftPts: shaftPts,
                shaftClearanceDu: shaftClearanceDu);

            return placed;
        }

        private static void NudgeOnBoundarySprinklersInward(
            IList<Point2d> ring,
            List<Point2d> placed,
            double onBoundaryThresholdDu,
            double targetInsetDu,
            double maxBoundaryDu,
            IList<Point2d> shaftPts,
            double shaftClearanceDu)
        {
            if (ring == null || placed == null || placed.Count == 0) return;
            if (!(onBoundaryThresholdDu > 0)) return;

            bool ccw = SignedArea(ring) > 0;
            var interiorRef = PickInteriorLabelPoint(ring);

            // Iterate in reverse because we may remove sprinklers that can't be nudged.
            for (int i = placed.Count - 1; i >= 0; i--)
            {
                var p = placed[i];
                double dEdge = DistanceToPolygonEdges(ring, p);
                if (dEdge >= onBoundaryThresholdDu)
                    continue;

                // Candidate inward directions: closest-edge inward normal, then a ray
                // toward a known interior point (handles concave corners where the
                // nearest-edge normal points outside the polygon).
                var dirs = new List<Point2d>(2);
                if (TryGetClosestEdgeInwardNormal(ring, ccw, p, out double nnx, out double nny))
                    dirs.Add(new Point2d(nnx, nny));

                double vdx = interiorRef.X - p.X;
                double vdy = interiorRef.Y - p.Y;
                double vlen = Math.Sqrt(vdx * vdx + vdy * vdy);
                if (vlen > 1e-9)
                    dirs.Add(new Point2d(vdx / vlen, vdy / vlen));

                double tooCloseDu = Math.Max(1e-6, onBoundaryThresholdDu);
                bool moved = false;

                for (int d = 0; d < dirs.Count && !moved; d++)
                {
                    double nx = dirs[d].X;
                    double ny = dirs[d].Y;

                    double startMove = Math.Min(targetInsetDu, Math.Max(0.0, maxBoundaryDu - 1e-6));
                    double tryMove = startMove;

                    for (int it = 0; it < 24; it++)
                    {
                        if (tryMove <= 1e-6) break;
                        var q = new Point2d(p.X + nx * tryMove, p.Y + ny * tryMove);
                        if (!PointInPolygon(ring, q)) { tryMove *= 0.7; continue; }

                        double newD = DistanceToPolygonEdges(ring, q);
                        if (newD < onBoundaryThresholdDu) { tryMove *= 0.7; continue; }
                        if (newD > maxBoundaryDu + 1e-6) { tryMove *= 0.7; continue; }

                        if (shaftPts != null && shaftPts.Count > 0 && MinDistance(shaftPts, q) < shaftClearanceDu)
                        { tryMove *= 0.7; continue; }

                        bool collides = false;
                        for (int j = 0; j < placed.Count; j++)
                        {
                            if (j == i) continue;
                            double ddx = placed[j].X - q.X;
                            double ddy = placed[j].Y - q.Y;
                            if (ddx * ddx + ddy * ddy < tooCloseDu * tooCloseDu)
                            { collides = true; break; }
                        }
                        if (collides) { tryMove *= 0.7; continue; }

                        placed[i] = q;
                        moved = true;
                        break;
                    }
                }

                if (!moved)
                {
                    // No interior landing spot exists that satisfies all constraints.
                    // Drop the sprinkler — it is better to lose one block than to leave
                    // a head sitting on the boundary line.
                    placed.RemoveAt(i);
                }
            }
        }

        private static void NudgeSprinklersOffZoneBoundaryLines(
            Database db,
            List<string> zoneBoundaryHandles,
            IList<Point2d> floorRing,
            List<Point2d> placed,
            double thresholdDu,
            double maxShiftDu,
            string floorBoundaryHandleHex)
        {
            if (db == null || placed == null || placed.Count == 0) return;
            if (zoneBoundaryHandles == null || zoneBoundaryHandles.Count == 0) return;
            if (!(thresholdDu > 0) || !(maxShiftDu > 0)) return;

            var segA = new List<Point2d>();
            var segB = new List<Point2d>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < zoneBoundaryHandles.Count; i++)
                {
                    var hex = zoneBoundaryHandles[i];
                    if (string.IsNullOrEmpty(hex)) continue;
                    // Skip the floor polyline itself — its handling is done separately.
                    if (!string.IsNullOrEmpty(floorBoundaryHandleHex) &&
                        string.Equals(hex, floorBoundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                        continue;
                    var handle = new Handle(hv);

                    if (!db.TryGetObjectId(handle, out ObjectId oid) || oid.IsNull)
                        continue;

                    var pl = tr.GetObject(oid, OpenMode.ForRead, false, true) as Polyline;
                    if (pl == null) continue;

                    int nv = pl.NumberOfVertices;
                    for (int k = 0; k < nv - 1; k++)
                    {
                        segA.Add(pl.GetPoint2dAt(k));
                        segB.Add(pl.GetPoint2dAt(k + 1));
                    }
                    if (pl.Closed && nv >= 2)
                    {
                        segA.Add(pl.GetPoint2dAt(nv - 1));
                        segB.Add(pl.GetPoint2dAt(0));
                    }
                }
                tr.Commit();
            }

            if (segA.Count == 0) return;

            for (int i = 0; i < placed.Count; i++)
            {
                var current = placed[i];
                // Iterate a few times: nudging off one line may bring the point near another.
                for (int pass = 0; pass < 4; pass++)
                {
                    int bestSeg = -1;
                    double bestDist = double.PositiveInfinity;
                    for (int s = 0; s < segA.Count; s++)
                    {
                        double d = PointToSegmentDistance(current, segA[s], segB[s]);
                        if (d < bestDist) { bestDist = d; bestSeg = s; }
                    }
                    if (bestSeg < 0 || bestDist >= thresholdDu)
                        break;

                    var a = segA[bestSeg];
                    var b = segB[bestSeg];
                    double ex = b.X - a.X, ey = b.Y - a.Y;
                    double elen = Math.Sqrt(ex * ex + ey * ey);
                    if (elen < 1e-9) break;
                    double nx = -ey / elen;
                    double ny = ex / elen;

                    double shift = Math.Min(maxShiftDu, thresholdDu * 1.2);
                    var q1 = new Point2d(current.X + nx * shift, current.Y + ny * shift);
                    var q2 = new Point2d(current.X - nx * shift, current.Y - ny * shift);

                    double s1 = EvaluateSegmentClearance(q1, segA, segB, floorRing);
                    double s2 = EvaluateSegmentClearance(q2, segA, segB, floorRing);

                    bool pickedQ1 = s1 >= s2;
                    Point2d pick = pickedQ1 ? q1 : q2;

                    // If chosen side is outside the floor, try the other.
                    if (!PointInPolygon(floorRing, pick))
                    {
                        pick = pickedQ1 ? q2 : q1;
                        if (!PointInPolygon(floorRing, pick)) break;
                    }

                    double floorClearance = DistanceToPolygonEdges(floorRing, pick);
                    if (floorClearance < thresholdDu) break;

                    current = pick;
                }
                placed[i] = current;
            }
        }

        private static double EvaluateSegmentClearance(
            Point2d p,
            IList<Point2d> segA,
            IList<Point2d> segB,
            IList<Point2d> floorRing)
        {
            if (!PointInPolygon(floorRing, p))
                return -1.0;
            double best = DistanceToPolygonEdges(floorRing, p);
            for (int s = 0; s < segA.Count; s++)
            {
                double d = PointToSegmentDistance(p, segA[s], segB[s]);
                if (d < best) best = d;
            }
            return best;
        }

        private static bool LooksLikeSparseRandomLayout(List<Point2d> placed, double lengthDu, double breadthDu, double spacingDu)
        {
            if (placed == null) return true;
            if (!(spacingDu > 1e-9)) return false;

            // Expected interior points for a reasonably filled rectangle-ish zone:
            // area / spacing^2. Use a very conservative threshold so we only trigger this for obviously sparse outputs.
            double areaApprox = Math.Max(0.0, lengthDu * breadthDu);
            double expected = areaApprox / (spacingDu * spacingDu);

            if (placed.Count <= 12)
                return true;
            if (expected > 0 && placed.Count < expected * 0.08)
                return true;

            return false;
        }
    }
}
