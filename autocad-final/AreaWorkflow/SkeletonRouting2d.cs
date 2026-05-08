using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Medial-axis-like skeleton routing for the main pipe trunk.
    /// Rasterizes the zone, runs a chamfer 3-4 distance transform, extracts ridge cells
    /// (the discrete medial axis), optionally prunes short branches, and picks the longest
    /// skeleton path starting from the shaft entry point as the trunk.
    /// All tunables are exposed through <see cref="Options"/> so the agent / LLM can adjust.
    /// </summary>
    public static class SkeletonRouting2d
    {
        public sealed class Options
        {
            /// <summary>Cell size of the distance-transform grid (drawing units).
            /// Smaller = finer skeleton but more cells. Typical: spacingDu / 4.</summary>
            public double CellSizeDu;

            /// <summary>Minimum clearance from boundary for a skeleton cell (drawing units).
            /// Cells whose distance-to-boundary is below this are excluded.</summary>
            public double MinClearanceDu;

            /// <summary>Prune skeleton branches whose length (world units) is below this.
            /// Set 0 to disable pruning.</summary>
            public double PruneLengthDu;

            /// <summary>Safety cap on total grid cells. Bail instead of building huge grids.</summary>
            public long MaxCells = 2_000_000;

            /// <summary>Max thinning + pruning iterations.</summary>
            public int MaxIterations = 40;
        }

        public sealed class Result
        {
            /// <summary>Polyline from the shaft entry to the farthest reachable skeleton leaf.</summary>
            public List<Point2d> TrunkPath;

            /// <summary>True if trunk bbox is wider than tall. Used by callers for branch orientation fallback.</summary>
            public bool TrunkIsHorizontal;

            /// <summary>Approximate skeleton length (world units). Diagnostic.</summary>
            public double TrunkLengthDu;

            /// <summary>Total extracted skeleton cells before trunk selection. Diagnostic.</summary>
            public int SkeletonCellCount;
        }

        // 8-neighborhood deltas and chamfer weights.
        private static readonly int[] Dxs = { 1, -1, 0, 0, 1, -1, 1, -1 };
        private static readonly int[] Dys = { 0, 0, 1, -1, 1, 1, -1, -1 };
        private static readonly int[] Dws = { 3, 3, 3, 3, 4, 4, 4, 4 }; // Chamfer 3-4

        public static bool TryBuildTrunk(
            List<Point2d> ring,
            Point2d connectStart,
            Options opt,
            out Result result,
            out string errorMessage)
        {
            result = null;
            errorMessage = null;

            if (ring == null || ring.Count < 3)
            {
                errorMessage = "Ring must have at least 3 vertices.";
                return false;
            }
            if (opt == null || !(opt.CellSizeDu > 0))
            {
                errorMessage = "Invalid skeleton options (cell size must be > 0).";
                return false;
            }

            double cell = opt.CellSizeDu;

            // ── Bounding box + grid dimensions ──────────────────────────────────
            double minX, minY, maxX, maxY;
            GetExtents(ring, out minX, out minY, out maxX, out maxY);
            minX -= cell; minY -= cell;
            maxX += cell; maxY += cell;

            int cols = (int)Math.Ceiling((maxX - minX) / cell);
            int rows = (int)Math.Ceiling((maxY - minY) / cell);
            if (cols < 4 || rows < 4)
            {
                errorMessage = "Zone too small for skeleton grid.";
                return false;
            }
            if ((long)cols * rows > opt.MaxCells)
            {
                errorMessage = "Skeleton grid exceeds cap (" + (long)cols * rows + " cells). Increase skeleton_cell_size_m.";
                return false;
            }

            AgentLog.Write("Skeleton", "cols=" + cols + " rows=" + rows + " cell=" + cell.ToString("F3"));

            // ── Rasterize polygon ───────────────────────────────────────────────
            var inside = new bool[cols, rows];
            for (int i = 0; i < cols; i++)
            {
                double x = minX + (i + 0.5) * cell;
                for (int j = 0; j < rows; j++)
                {
                    double y = minY + (j + 0.5) * cell;
                    inside[i, j] = PointInPolygon(ring, x, y);
                }
            }

            // ── Chamfer 3-4 distance transform ─────────────────────────────────
            // d[i,j] is 3 × (cell-Euclidean distance from cell center to nearest boundary/outside cell).
            const int BIG = 1_000_000_000;
            var d = new int[cols, rows];
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                    d[i, j] = inside[i, j] ? BIG : 0;

            // Forward pass: seed from already-computed N, W, NW, NE neighbors.
            for (int j = 0; j < rows; j++)
            {
                for (int i = 0; i < cols; i++)
                {
                    if (!inside[i, j]) continue;
                    int v = d[i, j];
                    if (i > 0 && d[i - 1, j] + 3 < v) v = d[i - 1, j] + 3;
                    if (j > 0 && d[i, j - 1] + 3 < v) v = d[i, j - 1] + 3;
                    if (i > 0 && j > 0 && d[i - 1, j - 1] + 4 < v) v = d[i - 1, j - 1] + 4;
                    if (i + 1 < cols && j > 0 && d[i + 1, j - 1] + 4 < v) v = d[i + 1, j - 1] + 4;
                    d[i, j] = v;
                }
            }
            // Backward pass: seed from S, E, SE, SW neighbors.
            for (int j = rows - 1; j >= 0; j--)
            {
                for (int i = cols - 1; i >= 0; i--)
                {
                    if (!inside[i, j]) continue;
                    int v = d[i, j];
                    if (i + 1 < cols && d[i + 1, j] + 3 < v) v = d[i + 1, j] + 3;
                    if (j + 1 < rows && d[i, j + 1] + 3 < v) v = d[i, j + 1] + 3;
                    if (i + 1 < cols && j + 1 < rows && d[i + 1, j + 1] + 4 < v) v = d[i + 1, j + 1] + 4;
                    if (i > 0 && j + 1 < rows && d[i - 1, j + 1] + 4 < v) v = d[i - 1, j + 1] + 4;
                    d[i, j] = v;
                }
            }

            // ── Ridge extraction ───────────────────────────────────────────────
            // A cell is skeleton if it's a 1D local max along some axis, OR a 2D local max
            // (covers the plateau centres of squares/bays where all 4 axes tie).
            // Clearance threshold filters out cells too close to the boundary.
            int clearanceChamfer = Math.Max(3, (int)Math.Ceiling(opt.MinClearanceDu / cell * 3.0));
            var skel = new bool[cols, rows];
            int skelCount = 0;
            for (int i = 0; i < cols; i++)
            {
                for (int j = 0; j < rows; j++)
                {
                    if (!inside[i, j]) continue;
                    int dC = d[i, j];
                    if (dC < clearanceChamfer) continue;

                    int dE  = (i + 1 < cols)                  ? d[i + 1, j    ] : 0;
                    int dW  = (i - 1 >= 0)                    ? d[i - 1, j    ] : 0;
                    int dN  = (j + 1 < rows)                  ? d[i,     j + 1] : 0;
                    int dS  = (j - 1 >= 0)                    ? d[i,     j - 1] : 0;
                    int dNE = (i + 1 < cols && j + 1 < rows)  ? d[i + 1, j + 1] : 0;
                    int dSW = (i - 1 >= 0   && j - 1 >= 0)    ? d[i - 1, j - 1] : 0;
                    int dNW = (i - 1 >= 0   && j + 1 < rows)  ? d[i - 1, j + 1] : 0;
                    int dSE = (i + 1 < cols && j - 1 >= 0)    ? d[i + 1, j - 1] : 0;

                    bool ridgeH  = dC >= dE  && dC >= dW  && (dC > dE  || dC > dW );
                    bool ridgeV  = dC >= dN  && dC >= dS  && (dC > dN  || dC > dS );
                    bool ridgeD1 = dC >= dNE && dC >= dSW && (dC > dNE || dC > dSW);
                    bool ridgeD2 = dC >= dNW && dC >= dSE && (dC > dNW || dC > dSE);

                    // 2D local max (covers flat plateaus like square centres):
                    bool localMax = dC >= dE  && dC >= dW  && dC >= dN  && dC >= dS
                                 && dC >= dNE && dC >= dSW && dC >= dNW && dC >= dSE;

                    if (ridgeH || ridgeV || ridgeD1 || ridgeD2 || localMax)
                    {
                        skel[i, j] = true;
                        skelCount++;
                    }
                }
            }

            if (skelCount == 0)
            {
                errorMessage = "Skeleton extraction produced 0 cells (zone too thin relative to min_clearance_m).";
                return false;
            }

            AgentLog.Write("Skeleton", "initial skeleton cells=" + skelCount);

            // ── Prune short branches iteratively ────────────────────────────────
            if (opt.PruneLengthDu > 0)
            {
                int pruned = PruneShortBranches(skel, cols, rows, opt.PruneLengthDu, cell, opt.MaxIterations);
                skelCount -= pruned;
                AgentLog.Write("Skeleton", "pruned cells=" + pruned + " remaining=" + skelCount);
                if (skelCount <= 0)
                {
                    errorMessage = "All skeleton cells pruned. Reduce prune_branch_length_m or min_clearance_m.";
                    return false;
                }
            }

            // ── Locate skeleton entry nearest to the shaft connect point ───────
            int entryI = -1, entryJ = -1;
            double bestEntryD2 = double.MaxValue;
            double csX = connectStart.X, csY = connectStart.Y;
            for (int i = 0; i < cols; i++)
            {
                for (int j = 0; j < rows; j++)
                {
                    if (!skel[i, j]) continue;
                    double cx = minX + (i + 0.5) * cell;
                    double cy = minY + (j + 0.5) * cell;
                    double ddx = cx - csX, ddy = cy - csY;
                    double dd2 = ddx * ddx + ddy * ddy;
                    if (dd2 < bestEntryD2)
                    {
                        bestEntryD2 = dd2;
                        entryI = i;
                        entryJ = j;
                    }
                }
            }
            if (entryI < 0)
            {
                errorMessage = "Could not locate skeleton entry near shaft point.";
                return false;
            }

            // ── Dijkstra from entry, find farthest reachable skeleton cell ─────
            var dist = new int[cols, rows];
            // Parent stores linear index of predecessor cell (not 16-bit packed coords — grids can exceed 65535 on one axis).
            var parent = new int[cols, rows];
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                {
                    dist[i, j] = int.MaxValue;
                    parent[i, j] = -1;
                }

            dist[entryI, entryJ] = 0;
            var heap = new MinHeap();
            int entryLin = ToLinear(entryI, entryJ, cols);
            heap.Push(entryLin, 0);

            while (heap.Count > 0)
            {
                int curLin = heap.PopMinKey();
                FromLinear(curLin, cols, out int cx, out int cy);
                int gCur = dist[cx, cy];
                for (int kd = 0; kd < 8; kd++)
                {
                    int nx = cx + Dxs[kd];
                    int ny = cy + Dys[kd];
                    if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                    if (!skel[nx, ny]) continue;
                    int tentative = gCur + Dws[kd];
                    if (tentative < dist[nx, ny])
                    {
                        dist[nx, ny] = tentative;
                        parent[nx, ny] = curLin;
                        heap.Push(ToLinear(nx, ny, cols), tentative);
                    }
                }
            }

            int endI = entryI, endJ = entryJ, maxD = 0;
            for (int i = 0; i < cols; i++)
            {
                for (int j = 0; j < rows; j++)
                {
                    if (!skel[i, j] || dist[i, j] == int.MaxValue) continue;
                    if (dist[i, j] > maxD)
                    {
                        maxD = dist[i, j];
                        endI = i;
                        endJ = j;
                    }
                }
            }

            // ── Trace back path end → entry ────────────────────────────────────
            var cells = new List<int>();
            int kcur = ToLinear(endI, endJ, cols);
            int safety = cols * rows + 8;
            while (kcur != entryLin)
            {
                cells.Add(kcur);
                FromLinear(kcur, cols, out int px, out int py);
                int pk = parent[px, py];
                if (pk < 0) break;
                kcur = pk;
                if (--safety <= 0) break;
            }
            cells.Add(entryLin);
            cells.Reverse();

            // ── Convert cells → world points ───────────────────────────────────
            var pts = new List<Point2d>(cells.Count);
            foreach (int ck in cells)
            {
                FromLinear(ck, cols, out int ix, out int iy);
                pts.Add(new Point2d(minX + (ix + 0.5) * cell, minY + (iy + 0.5) * cell));
            }

            // ── Simplify (remove collinear interior points) ────────────────────
            pts = SimplifyCollinear(pts, cell * 1e-3);

            if (pts.Count < 2)
            {
                errorMessage = "Trunk path degenerate (fewer than 2 points after simplification).";
                return false;
            }

            double totalLen = 0;
            for (int i = 1; i < pts.Count; i++)
                totalLen += pts[i - 1].GetDistanceTo(pts[i]);

            // Bounding box of the trunk → orientation hint for callers.
            double tMinX = pts[0].X, tMaxX = pts[0].X, tMinY = pts[0].Y, tMaxY = pts[0].Y;
            foreach (var p in pts)
            {
                if (p.X < tMinX) tMinX = p.X;
                if (p.X > tMaxX) tMaxX = p.X;
                if (p.Y < tMinY) tMinY = p.Y;
                if (p.Y > tMaxY) tMaxY = p.Y;
            }
            bool trunkHoriz = (tMaxX - tMinX) >= (tMaxY - tMinY);

            result = new Result
            {
                TrunkPath         = pts,
                TrunkIsHorizontal = trunkHoriz,
                TrunkLengthDu     = totalLen,
                SkeletonCellCount = skelCount
            };
            AgentLog.Write("Skeleton", "trunk vertices=" + pts.Count + " len=" + totalLen.ToString("F1") + " horiz=" + trunkHoriz);
            return true;
        }

        private static int PruneShortBranches(bool[,] skel, int cols, int rows, double pruneLengthDu, double cell, int maxIter)
        {
            int totalPruned = 0;
            for (int iter = 0; iter < maxIter; iter++)
            {
                bool changed = false;

                // Find leaves (1 skeleton neighbor).
                var leaves = new List<int>();
                for (int i = 0; i < cols; i++)
                    for (int j = 0; j < rows; j++)
                    {
                        if (!skel[i, j]) continue;
                        if (CountSkelNeighbors(skel, cols, rows, i, j) == 1)
                            leaves.Add(ToLinear(i, j, cols));
                    }

                foreach (var leafKey in leaves)
                {
                    FromLinear(leafKey, cols, out int li, out int lj);
                    if (!skel[li, lj]) continue; // already pruned this iter
                    if (CountSkelNeighbors(skel, cols, rows, li, lj) != 1) continue;

                    // Walk the dead-end branch from this leaf until a branch-point (>2 nbrs) is hit.
                    var branch = new List<int>();
                    double branchLen = 0;
                    int ci = li, cj = lj;
                    int prevI = -1, prevJ = -1;

                    while (true)
                    {
                        branch.Add(ToLinear(ci, cj, cols));
                        int nbrs = CountSkelNeighbors(skel, cols, rows, ci, cj);
                        if (nbrs >= 3) break; // branch-point: stop, don't include it

                        int nxt = FindNextSkelNeighbor(skel, cols, rows, ci, cj, prevI, prevJ, out int w);
                        if (nxt < 0) break; // orphan segment
                        FromLinear(nxt, cols, out int ni, out int nj);
                        branchLen += (w / 3.0) * cell;
                        prevI = ci; prevJ = cj;
                        ci = ni; cj = nj;

                        // Stop if we reach a branch point (its neighbors count includes us; check ahead)
                        if (CountSkelNeighbors(skel, cols, rows, ci, cj) >= 3)
                            break;
                        if (branch.Count > cols * rows) break; // safety
                    }

                    if (branchLen < pruneLengthDu)
                    {
                        foreach (var bk in branch)
                        {
                            FromLinear(bk, cols, out int bi, out int bj);
                            if (skel[bi, bj])
                            {
                                skel[bi, bj] = false;
                                totalPruned++;
                                changed = true;
                            }
                        }
                    }
                }

                if (!changed) break;
            }
            return totalPruned;
        }

        private static int CountSkelNeighbors(bool[,] skel, int cols, int rows, int i, int j)
        {
            int n = 0;
            for (int kd = 0; kd < 8; kd++)
            {
                int nx = i + Dxs[kd];
                int ny = j + Dys[kd];
                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                if (skel[nx, ny]) n++;
            }
            return n;
        }

        private static int FindNextSkelNeighbor(bool[,] skel, int cols, int rows, int i, int j, int prevI, int prevJ, out int weight)
        {
            weight = 0;
            for (int kd = 0; kd < 8; kd++)
            {
                int nx = i + Dxs[kd];
                int ny = j + Dys[kd];
                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                if (!skel[nx, ny]) continue;
                if (nx == prevI && ny == prevJ) continue;
                weight = Dws[kd];
                return ToLinear(nx, ny, cols);
            }
            return -1;
        }

        /// <summary>Row-major linear index; unique for cols×rows grids up to int.MaxValue cells.</summary>
        private static int ToLinear(int i, int j, int cols) => j * cols + i;

        private static void FromLinear(int idx, int cols, out int i, out int j)
        {
            j = idx / cols;
            i = idx - j * cols;
        }

        private static List<Point2d> SimplifyCollinear(List<Point2d> pts, double eps)
        {
            if (pts == null || pts.Count < 3) return pts ?? new List<Point2d>();
            var outPts = new List<Point2d> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = outPts[outPts.Count - 1];
                var b = pts[i];
                var c = pts[i + 1];
                double abx = b.X - a.X, aby = b.Y - a.Y;
                double bcx = c.X - b.X, bcy = c.Y - b.Y;
                double cross = abx * bcy - aby * bcx;
                // Relative to segment lengths so eps scales with polyline scale.
                double ablen = Math.Sqrt(abx * abx + aby * aby);
                double bclen = Math.Sqrt(bcx * bcx + bcy * bcy);
                double denom = Math.Max(ablen * bclen, 1e-18);
                if (Math.Abs(cross) / denom <= eps)
                    continue;
                outPts.Add(b);
            }
            outPts.Add(pts[pts.Count - 1]);
            return outPts;
        }

        private static void GetExtents(List<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
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

        private sealed class MinHeap
        {
            private readonly List<(int Key, int Pri)> _a = new List<(int, int)>();
            public int Count => _a.Count;

            public void Push(int key, int pri)
            {
                _a.Add((key, pri));
                SiftUp(_a.Count - 1);
            }

            public int PopMinKey()
            {
                var root = _a[0].Key;
                var last = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                if (_a.Count > 0)
                {
                    _a[0] = last;
                    SiftDown(0);
                }
                return root;
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].Pri <= _a[i].Pri) break;
                    var tmp = _a[p]; _a[p] = _a[i]; _a[i] = tmp;
                    i = p;
                }
            }

            private void SiftDown(int i)
            {
                int n = _a.Count;
                while (true)
                {
                    int l = i * 2 + 1;
                    int r = l + 1;
                    int best = i;
                    if (l < n && _a[l].Pri < _a[best].Pri) best = l;
                    if (r < n && _a[r].Pri < _a[best].Pri) best = r;
                    if (best == i) break;
                    var tmp = _a[best]; _a[best] = _a[i]; _a[i] = tmp;
                    i = best;
                }
            }
        }
    }
}
