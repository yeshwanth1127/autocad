using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Grid-based nearest-shaft assignment (Voronoi-style): each cell inside the boundary goes to the closest shaft insert.
    /// Optional Lloyd relaxation (see <paramref name="lloydRelaxIterations" />) moves sites toward cell centroids so zone areas
    /// stay closer to equal — only when the per-shaft cap is off (pure partition).
    /// Optional 3000 m² per-shaft cap (when INSUNITS allows): when enabled and the floor is larger than N×3000 m², overflow becomes Uncovered.
    /// When the cap is off, the full floor is partitioned between shafts (typically one connected region per shaft in a simple polygon).
    /// Outlines trace the union of assigned cells (orthogonal); not vertical strips and not convex hulls.
    /// </summary>
    public static class GridNearestShaftZoning2d
    {
        public const double DefaultCellSizeMeters = 0.5;

        /// <summary>Command-line / palette summary after grid zoning.</summary>
        /// <param name="insunitsSupportsM2Cap">INSUNITS allows converting 3000 m² to drawing area.</param>
        /// <param name="perShaftCapEnforced">User requested 3000 m²/shaft limit and INSUNITS supports it.</param>
        /// <param name="usedLloydRelaxation">True when iterative centroid balancing was applied before assignment.</param>
        public static string FormatZoningSummary(
            double? floorM2,
            int shaftCount,
            double cellSizeMeters,
            double cellStepDrawingUnits,
            bool insunitsSupportsM2Cap,
            bool perShaftCapEnforced,
            double uncoveredAreaDrawingUnits,
            Database db,
            bool gridCoarsened,
            int gridCols,
            int gridRows,
            bool usedLloydRelaxation = false,
            int lloydIterations = 0)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "Grid zoning ({0} shaft sites): requested cell ~{1:F2} m.",
                shaftCount,
                cellSizeMeters);
            if (usedLloydRelaxation && lloydIterations > 0)
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    " Lloyd relaxation ×{0} for near-equal areas. ",
                    lloydIterations);
            if (!gridCoarsened)
                sb.AppendFormat(CultureInfo.InvariantCulture, " Step {0:F3} drawing units. ", cellStepDrawingUnits);
            else
                sb.Append(" ");

            if (gridCoarsened)
            {
                double? effM = null;
                if (DrawingUnitsHelper.TryDrawingLengthToMeters(db.Insunits, cellStepDrawingUnits, out double mEff))
                    effM = mEff;
                if (effM.HasValue)
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "Grid coarsened to ~{0:F2} m (step {1:F3} du, {2}×{3} cells) — finer grid would exceed the cell budget. ",
                        effM.Value,
                        cellStepDrawingUnits,
                        gridCols,
                        gridRows);
                else
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "Grid coarsened (step {0:F3} drawing units, {1}×{2} cells) — finer grid would exceed the cell budget. ",
                        cellStepDrawingUnits,
                        gridCols,
                        gridRows);
            }

            if (perShaftCapEnforced)
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Per-shaft cap {0:F0} m² is ON: overflow to next-nearest shaft or Uncovered. ",
                    DrawingUnitsHelper.ShaftAreaLimitM2);
            else if (insunitsSupportsM2Cap)
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Per-shaft {0:F0} m² cap is OFF — full floor assigned by nearest shaft (Voronoi-style on grid). ",
                    DrawingUnitsHelper.ShaftAreaLimitM2);
            else
                sb.Append("INSUNITS does not support m² — cap cannot be applied; nearest-shaft assignment without area limit. ");

            if (uncoveredAreaDrawingUnits > 1e-9)
            {
                double? uM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, uncoveredAreaDrawingUnits, out _);
                if (uM2.HasValue)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "Uncovered: {0:F2} m². ", uM2.Value);
                else
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "Uncovered: {0:F2} sq. drawing units. ",
                        uncoveredAreaDrawingUnits);
            }

            if (floorM2.HasValue)
                sb.AppendFormat(CultureInfo.InvariantCulture, "Floor area: {0:F2} m².", floorM2.Value);

            if (perShaftCapEnforced && floorM2.HasValue && shaftCount > 0)
            {
                double maxServedM2 = shaftCount * DrawingUnitsHelper.ShaftAreaLimitM2;
                if (floorM2.Value > maxServedM2 + 1e-3)
                {
                    sb.Append(" ");
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "WARNING: Floor ({0:F0} m²) exceeds {1} shaft(s) × {2:F0} m² = {3:F0} m² — remainder is Uncovered.",
                        floorM2.Value,
                        shaftCount,
                        DrawingUnitsHelper.ShaftAreaLimitM2,
                        maxServedM2);
                }
            }

            return sb.ToString();
        }

        private const int Outside = -1;
        private const int Uncovered = -2;

        /// <summary>
        /// Builds closed rings (one per connected component per shaft, plus optional uncovered regions) in WCS XY.
        /// </summary>
        /// <param name="shaftIndexForRing">Parallel to <paramref name="rings"/>; 0-based shaft index, or -1 for uncovered.</param>
        /// <param name="enforcePerShaftCap3000M2">If true, each shaft stops at ~3000 m² (when INSUNITS allows); remainder Uncovered. If false, pure nearest-shaft (full coverage, no cap).</param>
        /// <param name="perShaftCapEnforced">True only if <paramref name="enforcePerShaftCap3000M2"/> and INSUNITS supports the cap.</param>
        /// <param name="lloydRelaxIterations">When &gt; 0 and cap is off, runs Lloyd relaxation on sites before assignment (more equal zone areas). Ignored when per-shaft cap is enforced.</param>
        public static bool TryBuildZoneRings(
            Polyline boundary,
            IList<Point2d> shaftSites,
            Database db,
            double tol,
            double cellSizeMeters,
            bool enforcePerShaftCap3000M2,
            int lloydRelaxIterations,
            out List<List<Point2d>> rings,
            out List<int> shaftIndexForRing,
            out double uncoveredAreaDrawingUnits,
            out bool insunitsSupportsM2Cap,
            out bool perShaftCapEnforced,
            out double cellStepDrawingUnits,
            out bool cellStepDerivedFromInsunits,
            out bool gridCoarsened,
            out int gridColsOut,
            out int gridRowsOut,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            shaftIndexForRing = new List<int>();
            uncoveredAreaDrawingUnits = 0;
            insunitsSupportsM2Cap = false;
            perShaftCapEnforced = false;
            cellStepDrawingUnits = 0;
            cellStepDerivedFromInsunits = false;
            gridCoarsened = false;
            gridColsOut = 0;
            gridRowsOut = 0;
            errorMessage = null;

            int n = shaftSites?.Count ?? 0;
            if (n < 2)
            {
                errorMessage = "Need at least two shafts for zones.";
                return false;
            }

            List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring.Count < 3)
            {
                errorMessage = "Boundary must be a closed polygon with enough detail (at least 3 vertices).";
                return false;
            }

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double extent = Math.Max(maxX - minX, maxY - minY);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            double step;
            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, cellSizeMeters, out step) && step > eps)
            {
                cellStepDerivedFromInsunits = true;
            }
            else
            {
                step = Math.Max(eps * 4, extent / 400.0);
                if (step <= eps)
                {
                    errorMessage = "Could not derive a positive grid cell size (check INSUNITS or boundary size).";
                    return false;
                }
            }

            double stepInitial = step;
            const int maxCells = 4_000_000;
            double ix0 = 0, iy0 = 0;
            int nCols = 1, nRows = 1;
            for (int expand = 0; expand < 64; expand++)
            {
                double stepUse = step;
                ix0 = Math.Floor(minX / stepUse) * stepUse;
                iy0 = Math.Floor(minY / stepUse) * stepUse;
                nCols = (int)Math.Ceiling((maxX - ix0) / stepUse);
                nRows = (int)Math.Ceiling((maxY - iy0) / stepUse);
                if (nCols < 1) nCols = 1;
                if (nRows < 1) nRows = 1;
                long total = (long)nCols * nRows;
                if (total <= maxCells)
                {
                    gridCoarsened = stepUse > stepInitial * 1.0000001;
                    cellStepDrawingUnits = stepUse;
                    step = stepUse;
                    gridColsOut = nCols;
                    gridRowsOut = nRows;
                    break;
                }

                double scale = Math.Sqrt(total / (double)maxCells) * 1.05;
                if (scale <= 1.001)
                    scale = 1.05;
                step *= scale;
                if (step > extent * 1000 || double.IsNaN(step) || double.IsInfinity(step))
                {
                    errorMessage =
                        "Could not build a grid within the cell budget (boundary extent is extreme relative to resolution).";
                    return false;
                }
            }

            if (gridColsOut == 0)
            {
                errorMessage = "Grid sizing failed.";
                return false;
            }

            double capAreaDu = double.PositiveInfinity;
            try
            {
                capAreaDu = DrawingUnitsHelper.SquareMetersToDrawingArea(db.Insunits, DrawingUnitsHelper.ShaftAreaLimitM2);
                insunitsSupportsM2Cap = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                insunitsSupportsM2Cap = false;
            }

            perShaftCapEnforced = enforcePerShaftCap3000M2 && insunitsSupportsM2Cap;
            if (!perShaftCapEnforced)
                capAreaDu = double.PositiveInfinity;

            double cellAreaDu = step * step;

            int[,] owner = new int[nCols, nRows];
            for (int i = 0; i < nCols; i++)
                for (int j = 0; j < nRows; j++)
                    owner[i, j] = Outside;

            var insideCells = new List<CellWork>(Math.Min(nCols * nRows / 4, 65536));
            for (int i = 0; i < nCols; i++)
            {
                for (int j = 0; j < nRows; j++)
                {
                    double cx = ix0 + (i + 0.5) * step;
                    double cy = iy0 + (j + 0.5) * step;
                    if (!PointInPolygon(ring, cx, cy))
                        continue;
                    double minDsq = double.MaxValue;
                    for (int s = 0; s < n; s++)
                    {
                        double dx = cx - shaftSites[s].X;
                        double dy = cy - shaftSites[s].Y;
                        double d2 = dx * dx + dy * dy;
                        if (d2 < minDsq)
                            minDsq = d2;
                    }

                    insideCells.Add(new CellWork(i, j, cx, cy, minDsq));
                }
            }

            if (insideCells.Count == 0)
            {
                errorMessage = "No grid cells fell inside the boundary (boundary may be too small for the cell size).";
                return false;
            }

            var sitePos = new Point2d[n];
            for (int s = 0; s < n; s++)
                sitePos[s] = shaftSites[s];

            bool useLloyd = lloydRelaxIterations > 0 && !perShaftCapEnforced;
            if (useLloyd)
                LloydRelaxSites(insideCells, sitePos, lloydRelaxIterations, n);

            // With no per-shaft cap: a zone is simply the set of cells nearest to a shaft (Voronoi-style).
            // This does NOT depend on processing order, so avoid sorting millions of cells.
            // With per-shaft cap ON: we need greedy fill order so shafts don't exceed the cap.
            bool usePureNearest = !perShaftCapEnforced;

            if (usePureNearest)
            {
                foreach (var c in insideCells)
                {
                    int best = NearestSiteIndex(c.Cx, c.Cy, sitePos, n);
                    owner[c.I, c.J] = best;
                }
            }
            else
            {
                insideCells.Sort((a, b) => a.MinDistSqToAnyShaft.CompareTo(b.MinDistSqToAnyShaft));

                var areaAcc = new double[n];
                var shaftOrder = new int[n];
                var distSqScratch = new double[n];
                foreach (var c in insideCells)
                {
                    for (int s = 0; s < n; s++)
                    {
                        double dx = c.Cx - shaftSites[s].X;
                        double dy = c.Cy - shaftSites[s].Y;
                        distSqScratch[s] = dx * dx + dy * dy;
                        shaftOrder[s] = s;
                    }

                    SortShaftIndicesByDistance(shaftOrder, distSqScratch, n);

                    int best = -1;
                    for (int rank = 0; rank < n; rank++)
                    {
                        int s = shaftOrder[rank];
                        if (areaAcc[s] + cellAreaDu <= capAreaDu + 1e-12)
                        {
                            best = s;
                            break;
                        }
                    }

                    if (best < 0)
                    {
                        owner[c.I, c.J] = Uncovered;
                        uncoveredAreaDrawingUnits += cellAreaDu;
                    }
                    else
                    {
                        owner[c.I, c.J] = best;
                        areaAcc[best] += cellAreaDu;
                    }
                }
            }

            var cellsByShaft = new List<HashSet<long>>(); // packed (i,j)
            for (int s = 0; s < n; s++)
                cellsByShaft.Add(new HashSet<long>());

            var uncoveredSet = new HashSet<long>();
            for (int i = 0; i < nCols; i++)
            {
                for (int j = 0; j < nRows; j++)
                {
                    int o = owner[i, j];
                    if (o == Outside) continue;
                    long key = PackIJ(i, j);
                    if (o == Uncovered)
                        uncoveredSet.Add(key);
                    else
                        cellsByShaft[o].Add(key);
                }
            }

            // Constrained Voronoi: each shaft must produce exactly one zone ring.
            // Irregular / concave boundaries can cut a shaft's nearest-cell region into multiple
            // disconnected fragments — emitting one ring per fragment would give zone_count > shaft_count.
            // Keep the largest fragment as the shaft's zone; reassign every smaller-fragment cell to
            // the nearest OTHER shaft's main component so coverage is preserved.
            MergeFragmentsPerShaft(owner, cellsByShaft, sitePos, n, nCols, nRows);

            for (int s = 0; s < n; s++)
            {
                if (cellsByShaft[s].Count == 0)
                    continue;
                var loop = TraceOrthogonalOutline(cellsByShaft[s], ix0, iy0, step);
                if (loop != null && loop.Count >= 3)
                {
                    rings.Add(loop);
                    shaftIndexForRing.Add(s);
                }
            }

            foreach (var comp in ConnectedComponents(uncoveredSet, nCols, nRows))
            {
                var loop = TraceOrthogonalOutline(comp, ix0, iy0, step);
                if (loop != null && loop.Count >= 3)
                {
                    rings.Add(loop);
                    shaftIndexForRing.Add(-1);
                }
            }

            if (rings.Count == 0)
            {
                errorMessage = "Zoning produced no outline polygons.";
                return false;
            }

            return true;
        }

        private struct CellWork
        {
            public readonly int I, J;
            public readonly double Cx, Cy;
            /// <summary>Squared distance to nearest shaft (same sort order as true distance).</summary>
            public readonly double MinDistSqToAnyShaft;

            public CellWork(int i, int j, double cx, double cy, double minDsq)
            {
                I = i;
                J = j;
                Cx = cx;
                Cy = cy;
                MinDistSqToAnyShaft = minDsq;
            }
        }

        private static long PackIJ(int i, int j) => ((long)(uint)i << 32) | (uint)j;

        private static void UnpackIJ(long key, out int i, out int j)
        {
            i = (int)(key >> 32);
            j = (int)(key & 0xFFFFFFFF);
        }

        /// <summary>Sorts <paramref name="shaftIndices"/>[0..<paramref name="n"/>) as a permutation so shaft distance (squared) is non-decreasing.</summary>
        private static void SortShaftIndicesByDistance(int[] shaftIndices, double[] distSq, int n)
        {
            if (n <= 1)
                return;
            // n is small (shaft count); O(n²) selection-style pass avoids comparer allocations per cell.
            for (int a = 0; a < n - 1; a++)
            {
                int best = a;
                double bestD = distSq[shaftIndices[a]];
                for (int b = a + 1; b < n; b++)
                {
                    double db = distSq[shaftIndices[b]];
                    if (db < bestD)
                    {
                        bestD = db;
                        best = b;
                    }
                }

                if (best != a)
                {
                    int tmp = shaftIndices[a];
                    shaftIndices[a] = shaftIndices[best];
                    shaftIndices[best] = tmp;
                }
            }
        }

        private static int NearestSiteIndex(double cx, double cy, Point2d[] sitePos, int n)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int s = 0; s < n; s++)
            {
                double dx = cx - sitePos[s].X;
                double dy = cy - sitePos[s].Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD)
                {
                    bestD = d2;
                    best = s;
                }
            }

            return best;
        }

        /// <summary>
        /// Lloyd relaxation: repeatedly assign each cell to nearest site, move each site to the centroid of its cells.
        /// </summary>
        private static void LloydRelaxSites(List<CellWork> insideCells, Point2d[] sitePos, int iterations, int n)
        {
            if (insideCells == null || sitePos == null || iterations <= 0 || n <= 0)
                return;

            var cnt = new int[n];
            var sumX = new double[n];
            var sumY = new double[n];

            for (int it = 0; it < iterations; it++)
            {
                for (int s = 0; s < n; s++)
                {
                    cnt[s] = 0;
                    sumX[s] = 0;
                    sumY[s] = 0;
                }

                foreach (var c in insideCells)
                {
                    int b = NearestSiteIndex(c.Cx, c.Cy, sitePos, n);
                    cnt[b]++;
                    sumX[b] += c.Cx;
                    sumY[b] += c.Cy;
                }

                for (int s = 0; s < n; s++)
                {
                    if (cnt[s] > 0)
                        sitePos[s] = new Point2d(sumX[s] / cnt[s], sumY[s] / cnt[s]);
                }
            }
        }

        private static bool PointInPolygon(IList<Point2d> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi))
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>
        /// Reassigns cells from non-main fragments to the nearest other shaft so each shaft ends up
        /// with a single connected component (the largest one). Mutates <paramref name="owner"/> and
        /// <paramref name="cellsByShaft"/> in place. Iterates until no reassignments happen or until
        /// every shaft has exactly one component (whichever comes first).
        /// </summary>
        private static void MergeFragmentsPerShaft(
            int[,] owner,
            List<HashSet<long>> cellsByShaft,
            Point2d[] sitePos,
            int n,
            int nCols,
            int nRows)
        {
            if (n <= 1) return;

            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;
                for (int s = 0; s < n; s++)
                {
                    if (cellsByShaft[s].Count == 0)
                        continue;

                    var comps = ConnectedComponents(cellsByShaft[s], nCols, nRows);
                    if (comps.Count <= 1)
                        continue;

                    // Keep the largest fragment; reassign cells in all other fragments.
                    int mainIdx = 0;
                    for (int k = 1; k < comps.Count; k++)
                        if (comps[k].Count > comps[mainIdx].Count) mainIdx = k;

                    for (int k = 0; k < comps.Count; k++)
                    {
                        if (k == mainIdx) continue;
                        foreach (long key in comps[k])
                        {
                            UnpackIJ(key, out int i, out int j);
                            int newOwner = FindNearestOtherShaft(s, i, j, sitePos, n, owner, nCols, nRows);
                            if (newOwner < 0 || newOwner == s)
                                continue;
                            owner[i, j] = newOwner;
                            cellsByShaft[s].Remove(key);
                            cellsByShaft[newOwner].Add(key);
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }
        }

        /// <summary>
        /// Returns the shaft index (other than <paramref name="currentShaft"/>) whose site is closest
        /// to cell (i,j). Returns -1 if no other shaft exists. Distance uses cell-index units, which
        /// preserve ordering relative to world distances (uniform step).
        /// </summary>
        private static int FindNearestOtherShaft(
            int currentShaft,
            int cellI,
            int cellJ,
            Point2d[] sitePos,
            int n,
            int[,] owner,
            int nCols,
            int nRows)
        {
            // Expand outward in cell-index rings until we hit another shaft's cell.
            int maxRadius = Math.Max(nCols, nRows);
            for (int r = 1; r <= maxRadius; r++)
            {
                int best = -1;
                double bestD2 = double.MaxValue;
                int iMin = Math.Max(0, cellI - r), iMax = Math.Min(nCols - 1, cellI + r);
                int jMin = Math.Max(0, cellJ - r), jMax = Math.Min(nRows - 1, cellJ + r);
                for (int i = iMin; i <= iMax; i++)
                {
                    for (int j = jMin; j <= jMax; j++)
                    {
                        // Only examine the ring boundary for efficiency.
                        if (i != iMin && i != iMax && j != jMin && j != jMax) continue;
                        int o = owner[i, j];
                        if (o < 0 || o == currentShaft) continue;
                        double di = i - cellI;
                        double dj = j - cellJ;
                        double d2 = di * di + dj * dj;
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            best = o;
                        }
                    }
                }
                if (best >= 0) return best;
            }

            // Fallback: nearest site by Euclidean distance on site positions.
            int fallback = -1;
            double fbD = double.MaxValue;
            for (int s = 0; s < n; s++)
            {
                if (s == currentShaft) continue;
                double dx = sitePos[s].X - sitePos[currentShaft].X;
                double dy = sitePos[s].Y - sitePos[currentShaft].Y;
                double d = dx * dx + dy * dy;
                if (d < fbD) { fbD = d; fallback = s; }
            }
            return fallback;
        }

        private static List<HashSet<long>> ConnectedComponents(HashSet<long> cells, int nCols, int nRows)
        {
            var result = new List<HashSet<long>>();
            if (cells == null || cells.Count == 0)
                return result;

            var visited = new HashSet<long>();
            foreach (long start in cells)
            {
                if (visited.Contains(start))
                    continue;
                var comp = new HashSet<long>();
                var q = new Queue<long>();
                q.Enqueue(start);
                visited.Add(start);
                while (q.Count > 0)
                {
                    long k = q.Dequeue();
                    comp.Add(k);
                    UnpackIJ(k, out int i, out int j);
                    // 8-connectivity merges diagonal-only touches so one shaft does not get split into
                    // multiple zone outlines (which produced "Zone N (2)" labels on concave / E-shaped floors).
                    TryEnqueue(i + 1, j);
                    TryEnqueue(i - 1, j);
                    TryEnqueue(i, j + 1);
                    TryEnqueue(i, j - 1);
                    TryEnqueue(i + 1, j + 1);
                    TryEnqueue(i + 1, j - 1);
                    TryEnqueue(i - 1, j + 1);
                    TryEnqueue(i - 1, j - 1);

                    void TryEnqueue(int ni, int nj)
                    {
                        if (ni < 0 || nj < 0 || ni >= nCols || nj >= nRows)
                            return;
                        long nk = PackIJ(ni, nj);
                        if (!cells.Contains(nk) || visited.Contains(nk))
                            return;
                        visited.Add(nk);
                        q.Enqueue(nk);
                    }
                }

                result.Add(comp);
            }

            return result;
        }

        /// <summary>
        /// CCW directed edges on cell corners; cancel opposing pairs; walk the remaining boundary cycle once.
        /// </summary>
        private static List<Point2d> TraceOrthogonalOutline(HashSet<long> cells, double ix0, double iy0, double step)
        {
            var dirCount = new Dictionary<EdgeKey, int>();
            foreach (long ck in cells)
            {
                UnpackIJ(ck, out int i, out int j);
                void AddDir(int i1, int j1, int i2, int j2)
                {
                    var ek = EdgeKey.Create(i1, j1, i2, j2);
                    if (!dirCount.ContainsKey(ek))
                        dirCount[ek] = 0;
                    dirCount[ek]++;
                }

                AddDir(i, j, i + 1, j);
                AddDir(i + 1, j, i + 1, j + 1);
                AddDir(i + 1, j + 1, i, j + 1);
                AddDir(i, j + 1, i, j);
            }

            var keys = dirCount.Keys.ToList();
            foreach (var k in keys)
            {
                if (!dirCount.ContainsKey(k) || dirCount[k] <= 0)
                    continue;
                var rev = k.Reversed();
                if (!dirCount.ContainsKey(rev) || dirCount[rev] <= 0)
                    continue;
                int m = Math.Min(dirCount[k], dirCount[rev]);
                dirCount[k] -= m;
                dirCount[rev] -= m;
            }

            var adj = new Dictionary<CornerKey, List<CornerKey>>();
            foreach (var kv in dirCount)
            {
                if (kv.Value <= 0)
                    continue;
                var e = kv.Key;
                var a = CornerKey.Create(e.I1, e.J1);
                var b = CornerKey.Create(e.I2, e.J2);
                AddAdj(a, b);
                AddAdj(b, a);
            }

            void AddAdj(CornerKey a, CornerKey b)
            {
                if (!adj.TryGetValue(a, out var list))
                {
                    list = new List<CornerKey>(2);
                    adj[a] = list;
                }

                list.Add(b);
            }

            CornerKey start = default;
            bool found = false;
            foreach (var kv in adj)
            {
                if (kv.Value.Count > 0)
                {
                    start = kv.Key;
                    found = true;
                    break;
                }
            }

            if (!found)
                return null;

            var poly = new List<Point2d>();
            var prev = new CornerKey(int.MinValue, int.MinValue);
            CornerKey cur = start;
            int guardLimit = cells.Count * 8 + 32;
            for (int guard = 0; guard < guardLimit; guard++)
            {
                poly.Add(ToWorld(cur, ix0, iy0, step));
                if (!adj.TryGetValue(cur, out var neigh) || neigh.Count == 0)
                    return null;

                CornerKey next;
                if (neigh.Count == 1)
                    next = neigh[0];
                else
                    next = neigh[0].Equals(prev) ? neigh[1] : neigh[0];

                if (next.Equals(start) && poly.Count > 1)
                {
                    RemoveHalfEdge(adj, cur, next);
                    RemoveHalfEdge(adj, next, cur);
                    SimplifyCollinearClosed(poly);
                    return poly.Count >= 3 ? poly : null;
                }

                RemoveHalfEdge(adj, cur, next);
                RemoveHalfEdge(adj, next, cur);
                prev = cur;
                cur = next;
            }

            return null;
        }

        private static void RemoveHalfEdge(Dictionary<CornerKey, List<CornerKey>> adj, CornerKey from, CornerKey to)
        {
            if (!adj.TryGetValue(from, out var list))
                return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Equals(to))
                {
                    list.RemoveAt(i);
                    return;
                }
            }
        }

        private static Point2d ToWorld(CornerKey c, double ix0, double iy0, double step)
        {
            return new Point2d(ix0 + c.I * step, iy0 + c.J * step);
        }

        private static void SimplifyCollinearClosed(List<Point2d> ring)
        {
            if (ring.Count < 3)
                return;
            int n = ring.Count;
            var keep = new bool[n];
            for (int i = 0; i < n; i++)
                keep[i] = true;
            for (int iter = 0; iter < n; iter++)
            {
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    if (!keep[i])
                        continue;
                    int ip = i;
                    do
                    {
                        ip = (ip - 1 + n) % n;
                    } while (!keep[ip] && ip != i);

                    int inext = i;
                    do
                    {
                        inext = (inext + 1) % n;
                    } while (!keep[inext] && inext != i);

                    if (ip == i || inext == i)
                        continue;
                    if (IsCollinear(ring[ip], ring[i], ring[inext]))
                    {
                        keep[i] = false;
                        any = true;
                    }
                }

                if (!any)
                    break;
            }

            var outPts = new List<Point2d>();
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                    outPts.Add(ring[i]);
            }

            ring.Clear();
            ring.AddRange(outPts);
        }

        private static bool IsCollinear(Point2d a, Point2d b, Point2d c)
        {
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            return Math.Abs(cross) <= 1e-9 * Math.Max(1.0, a.GetDistanceTo(c));
        }

        private struct CornerKey : IEquatable<CornerKey>
        {
            public readonly int I, J;

            public CornerKey(int i, int j)
            {
                I = i;
                J = j;
            }

            public static CornerKey Create(int i, int j) => new CornerKey(i, j);

            public bool Equals(CornerKey other) => I == other.I && J == other.J;
            public override bool Equals(object obj) => obj is CornerKey other && Equals(other);
            public override int GetHashCode() => I * 397 ^ J;
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly int I1, J1, I2, J2;

            private EdgeKey(int i1, int j1, int i2, int j2)
            {
                I1 = i1;
                J1 = j1;
                I2 = i2;
                J2 = j2;
            }

            public static EdgeKey Create(int i1, int j1, int i2, int j2) => new EdgeKey(i1, j1, i2, j2);

            public EdgeKey Reversed() => new EdgeKey(I2, J2, I1, J1);

            public bool Equals(EdgeKey other) => I1 == other.I1 && J1 == other.J1 && I2 == other.I2 && J2 == other.J2;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (((I1 * 397) ^ J1) * 397 ^ I2) * 397 ^ J2;
        }
    }
}
