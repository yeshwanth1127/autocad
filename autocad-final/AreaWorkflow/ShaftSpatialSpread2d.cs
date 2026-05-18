using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Detects whether shaft inserts are <strong>spatially localized</strong> on the floor (small footprint vs floor
    /// bounding box). When true, zoning uses equal-area strip partitions without pairing strips to shaft order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why not max pairwise distance / floor diagonal?</strong> That metric only uses the two farthest
    /// shafts. It ignores how much territory the group occupies, aspect ratio of the floor, linear spreads, and
    /// density — so long rows or skewed plates produce false positives/negatives. Zoning needs
    /// <em>localized footprint</em>, not “small diameter”.
    /// </para>
    /// <para>
    /// <strong>Footprint analysis</strong> compares the shaft cluster’s axis-aligned bbox (and optional convex hull)
    /// to the floor’s axis-aligned bbox. Shafts may sit in a corner, center, along a wall, or in a core — position
    /// does not matter; only that they occupy a small fraction of the plate.
    /// </para>
    /// </remarks>
    public static class ShaftSpatialSpread2d
    {
        /// <summary>Cluster bbox width / floor bbox width must be ≤ this for “compact footprint” (with height).</summary>
        public const double MaxClusterWidthToFloorWidthRatio = 0.25;

        /// <summary>Cluster bbox height / floor bbox height must be ≤ this for “compact footprint” (with width).</summary>
        public const double MaxClusterHeightToFloorHeightRatio = 0.25;

        /// <summary>Cluster bbox area / floor bbox area threshold for “tiny footprint” (with safeguards).</summary>
        public const double MaxClusterBboxAreaToFloorBboxAreaRatio = 0.08;

        /// <summary>Convex hull area / floor bbox area — preferred localization test for non-axis-aligned groups.</summary>
        public const double MaxConvexHullAreaToFloorBboxAreaRatio = 0.05;

        /// <summary>
        /// With tiny bbox area, require max(width,height) ratio ≤ this so a long linear strip cannot pass as “tiny”.
        /// </summary>
        public const double MaxExtentRatioForTinyBboxAreaRule = 0.5;

        /// <summary>Legacy diagnostic only — not used for <see cref="ShaftSpreadReport.IsClustered"/>.</summary>
        public const double MaxClusterDiameterToFloorDiagonalRatio = 0.22;

        public sealed class ShaftSpreadReport
        {
            public bool IsClustered { get; set; }
            public string Reason { get; set; }
            public int ShaftCount { get; set; }

            /// <summary>Axis-aligned shaft cluster width (drawing units).</summary>
            public double ClusterBoundingWidth { get; set; }

            /// <summary>Axis-aligned shaft cluster height (drawing units).</summary>
            public double ClusterBoundingHeight { get; set; }

            /// <summary><c>ClusterBoundingWidth × ClusterBoundingHeight</c>.</summary>
            public double ClusterBoundingArea { get; set; }

            public double FloorBoundingWidth { get; set; }
            public double FloorBoundingHeight { get; set; }

            /// <summary>Axis-aligned floor plate area <c>FloorBoundingWidth × FloorBoundingHeight</c> (not geodesic floor polygon area).</summary>
            public double FloorBoundingArea { get; set; }

            public double ClusterWidthToFloorWidthRatio { get; set; }
            public double ClusterHeightToFloorHeightRatio { get; set; }
            public double ClusterAreaToFloorAreaRatio { get; set; }

            /// <summary>Absolute area of the convex hull of shaft sites; 0 if collinear/degenerate.</summary>
            public double ConvexHullArea { get; set; }

            public double ConvexHullAreaToFloorAreaRatio { get; set; }

            public bool CompactFootprintByBboxAxes { get; set; }
            public bool TinyBboxAreaFootprint { get; set; }
            public bool LocalizedByConvexHull { get; set; }

            /// <summary>Diagnostic only — largest pairwise distance among shafts (drawing units).</summary>
            public double MaxPairwiseShaftDistance { get; set; }

            /// <summary>Diagnostic only.</summary>
            public double FloorBoundingDiagonal { get; set; }

            /// <summary>Diagnostic only — do not use for clustering decisions.</summary>
            public double MaxPairwiseToDiagonalRatio { get; set; }
        }

        public static bool TryAnalyze(
            IList<Point2d> shaftSites,
            IList<Point2d> floorRing,
            double tol,
            out ShaftSpreadReport report)
        {
            report = new ShaftSpreadReport();
            int n = shaftSites?.Count ?? 0;
            report.ShaftCount = n;

            if (n < 2)
            {
                report.IsClustered = false;
                report.Reason = "Fewer than two shaft sites — multi-zone clustering check not applied.";
                return true;
            }

            if (floorRing == null || floorRing.Count < 3)
            {
                report.IsClustered = false;
                report.Reason = "Floor ring invalid — clustering check skipped.";
                return true;
            }

            GetExtents(floorRing, out double fMinX, out double fMaxX, out double fMinY, out double fMaxY);
            double floorW = fMaxX - fMinX;
            double floorH = fMaxY - fMinY;
            report.FloorBoundingWidth = floorW;
            report.FloorBoundingHeight = floorH;
            double extent = Math.Max(floorW, floorH);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            if (floorW <= eps || floorH <= eps)
            {
                report.IsClustered = false;
                report.Reason = "Floor bbox degenerate — clustering check skipped.";
                return true;
            }

            double floorArea = floorW * floorH;
            report.FloorBoundingArea = floorArea;

            GetExtents(shaftSites, out double sMinX, out double sMaxX, out double sMinY, out double sMaxY);
            double clusterW = Math.Max(sMaxX - sMinX, eps);
            double clusterH = Math.Max(sMaxY - sMinY, eps);
            report.ClusterBoundingWidth = clusterW;
            report.ClusterBoundingHeight = clusterH;
            report.ClusterBoundingArea = clusterW * clusterH;

            double widthRatio = clusterW / floorW;
            double heightRatio = clusterH / floorH;
            double areaRatio = report.ClusterBoundingArea / floorArea;

            report.ClusterWidthToFloorWidthRatio = widthRatio;
            report.ClusterHeightToFloorHeightRatio = heightRatio;
            report.ClusterAreaToFloorAreaRatio = areaRatio;

            // Legacy diagnostics (pairwise distance is misleading for localization — kept for logs only).
            double floorDiag = Math.Sqrt(floorW * floorW + floorH * floorH);
            report.FloorBoundingDiagonal = floorDiag;
            report.MaxPairwiseShaftDistance = MaxPairwiseDistance(shaftSites);
            report.MaxPairwiseToDiagonalRatio = report.MaxPairwiseShaftDistance / floorDiag;

            double hullArea = ConvexHullArea(shaftSites, eps);
            report.ConvexHullArea = hullArea;
            report.ConvexHullAreaToFloorAreaRatio = hullArea / floorArea;

            double hullAreaMin = Math.Max(eps * eps, 1e-12 * floorArea);

            // Primary: both shaft bbox dimensions small vs floor bbox (rejects long linear spreads).
            bool compactFootprint =
                widthRatio <= MaxClusterWidthToFloorWidthRatio &&
                heightRatio <= MaxClusterHeightToFloorHeightRatio;
            report.CompactFootprintByBboxAxes = compactFootprint;

            // Tiny overall bbox area — guarded so a thin row spanning the building cannot pass.
            bool tinyBboxArea =
                areaRatio <= MaxClusterBboxAreaToFloorBboxAreaRatio &&
                Math.Max(widthRatio, heightRatio) <= MaxExtentRatioForTinyBboxAreaRule;
            report.TinyBboxAreaFootprint = tinyBboxArea;

            // Preferred: hull captures “blob” extent; excludes axis-heavy stripes when combined with collinear hull ~0.
            bool hullLocalized =
                hullArea >= hullAreaMin &&
                report.ConvexHullAreaToFloorAreaRatio <= MaxConvexHullAreaToFloorBboxAreaRatio;
            report.LocalizedByConvexHull = hullLocalized;

            report.IsClustered = compactFootprint || tinyBboxArea || hullLocalized;

            if (report.IsClustered)
            {
                string via = compactFootprint
                    ? "compact bbox axes"
                    : tinyBboxArea
                        ? "tiny bbox area"
                        : "convex hull";
                report.Reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "Localized shaft footprint ({3}: bbox {0:P0}×{1:P1}, area {2:P0} of floor; hull {4:P0}) — clustered equal strips.",
                    widthRatio,
                    heightRatio,
                    areaRatio,
                    via,
                    report.ConvexHullAreaToFloorAreaRatio);
                return true;
            }

            report.Reason = string.Format(
                CultureInfo.InvariantCulture,
                "Shafts not localized: bbox {0:P0}×{1:P1}, area {2:P0} of floor; hull {3:P0} (pairwise/diag {4:P0} diagnostic only).",
                widthRatio,
                heightRatio,
                areaRatio,
                report.ConvexHullAreaToFloorAreaRatio,
                report.MaxPairwiseToDiagonalRatio);
            return true;
        }

        /// <summary>Maximum Euclidean distance over all distinct shaft pairs — diagnostic only.</summary>
        public static double MaxPairwiseDistance(IList<Point2d> shaftSites)
        {
            int n = shaftSites?.Count ?? 0;
            if (n < 2)
                return 0.0;

            double dMax = 0.0;
            for (int i = 0; i < n; i++)
            {
                var a = shaftSites[i];
                for (int j = i + 1; j < n; j++)
                {
                    double d = a.GetDistanceTo(shaftSites[j]);
                    if (d > dMax)
                        dMax = d;
                }
            }

            return dMax;
        }

        private static void GetExtents(IList<Point2d> pts, out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue;
            maxX = double.MinValue;
            minY = double.MaxValue;
            maxY = double.MinValue;
            if (pts == null) return;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        /// <summary>Monotone chain convex hull; absolute polygon area via shoelace.</summary>
        internal static double ConvexHullArea(IList<Point2d> shaftSites, double eps)
        {
            if (shaftSites == null || shaftSites.Count < 3)
                return 0.0;

            GetExtents(shaftSites, out double sx0, out double sx1, out double sy0, out double sy1);
            double sExtent = Math.Max(sx1 - sx0, sy1 - sy0);
            double orientTol = Math.Max(eps, 1e-12 * Math.Max(sExtent, 1.0));

            var sorted = shaftSites.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var lower = new List<Point2d>();
            foreach (var p in sorted)
            {
                while (lower.Count >= 2 && CrossZ(lower[lower.Count - 2], lower[lower.Count - 1], p) <= orientTol)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<Point2d>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var p = sorted[i];
                while (upper.Count >= 2 && CrossZ(upper[upper.Count - 2], upper[upper.Count - 1], p) <= orientTol)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            if (lower.Count + upper.Count < 4)
            {
                // Collinear or all duplicates → degenerate hull.
                return 0.0;
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return AbsolutePolygonArea(lower);
        }

        private static double CrossZ(Point2d o, Point2d a, Point2d b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }

        private static double AbsolutePolygonArea(IReadOnlyList<Point2d> ring)
        {
            int m = ring.Count;
            if (m < 3)
                return 0.0;
            double sum = 0.0;
            for (int i = 0; i < m; i++)
            {
                int j = (i + 1) % m;
                sum += ring[i].X * ring[j].Y - ring[j].X * ring[i].Y;
            }

            return Math.Abs(sum) * 0.5;
        }

        /// <summary>Static regression entry for ROUTINGREGRESSION / dev checks.</summary>
        public static string RunRegressionSelfTest()
        {
            var errors = new List<string>();

            var floorSquare = new List<Point2d>
            {
                new Point2d(0, 0),
                new Point2d(100, 0),
                new Point2d(100, 80),
                new Point2d(0, 80)
            };

            var cornerCluster = new List<Point2d>
            {
                new Point2d(2, 78), new Point2d(4, 78), new Point2d(2, 76),
                new Point2d(4, 76), new Point2d(2, 74), new Point2d(4, 74)
            };
            if (!TryAnalyze(cornerCluster, floorSquare, 1e-6, out var r1) || !r1.IsClustered)
                errors.Add("corner_cluster: expected IsClustered=true");

            var oppositeCorners = new List<Point2d>
            {
                new Point2d(2, 2),
                new Point2d(98, 78)
            };
            if (!TryAnalyze(oppositeCorners, floorSquare, 1e-6, out var r2) || r2.IsClustered)
                errors.Add("opposite_corners: expected IsClustered=false");

            var wideShallowFloor = new List<Point2d>
            {
                new Point2d(0, 0),
                new Point2d(800, 0),
                new Point2d(800, 12),
                new Point2d(0, 12)
            };
            var tallStackInCorner = new List<Point2d>
            {
                new Point2d(1, 9), new Point2d(3, 9), new Point2d(1, 7), new Point2d(3, 7), new Point2d(1, 5), new Point2d(3, 5)
            };
            if (!TryAnalyze(tallStackInCorner, wideShallowFloor, 1e-6, out var r3) || !r3.IsClustered)
                errors.Add("shallow_wide_corner: expected IsClustered=true");

            // Center of large square — localized, not “corner”.
            var centerCluster = new List<Point2d>
            {
                new Point2d(48, 38), new Point2d(52, 38), new Point2d(48, 42), new Point2d(52, 42)
            };
            if (!TryAnalyze(centerCluster, floorSquare, 1e-6, out var r4) || !r4.IsClustered)
                errors.Add("center_cluster: expected IsClustered=true");

            // Linear spread across the plate — must not cluster.
            var longRow = new List<Point2d>
            {
                new Point2d(10, 40), new Point2d(30, 40), new Point2d(50, 40), new Point2d(70, 40), new Point2d(90, 40)
            };
            if (!TryAnalyze(longRow, floorSquare, 1e-6, out var r5) || r5.IsClustered)
                errors.Add("long_distributed_row: expected IsClustered=false");

            // Large triangle — distributed.
            var triangleSpread = new List<Point2d>
            {
                new Point2d(10, 10), new Point2d(90, 10), new Point2d(50, 70)
            };
            if (!TryAnalyze(triangleSpread, floorSquare, 1e-6, out var r6) || r6.IsClustered)
                errors.Add("triangle_distributed: expected IsClustered=false");

            // Skinny corridor floor plate; small group in the middle.
            var skinnyFloor = new List<Point2d>
            {
                new Point2d(0, 0),
                new Point2d(2000, 0),
                new Point2d(2000, 8),
                new Point2d(0, 8)
            };
            var corridorCluster = new List<Point2d>
            {
                new Point2d(990, 3), new Point2d(1010, 3), new Point2d(990, 5), new Point2d(1010, 5)
            };
            if (!TryAnalyze(corridorCluster, skinnyFloor, 1e-6, out var r7) || !r7.IsClustered)
                errors.Add("skinny_corridor_center: expected IsClustered=true");

            var verticalStack = new List<Point2d>
            {
                new Point2d(10, 70), new Point2d(12, 50), new Point2d(11, 30)
            };
            if (ClusteredEqualAreaStripZonesInPolygon2d.ChooseStripOrientation(verticalStack, 100, 80, 1e-6))
                errors.Add("strip_orientation: vertical shaft stack should use horizontal strips");

            var horizontalRow = new List<Point2d>
            {
                new Point2d(10, 40), new Point2d(40, 41), new Point2d(70, 39)
            };
            if (!ClusteredEqualAreaStripZonesInPolygon2d.ChooseStripOrientation(horizontalRow, 100, 80, 1e-6))
                errors.Add("strip_orientation: horizontal shaft row should use vertical strips");

            var ring = new List<Point2d>
            {
                new Point2d(0, 0), new Point2d(100, 0), new Point2d(100, 60), new Point2d(0, 60)
            };
            // Strip geometry regression uses ring-only helper (no AutoCAD boundary required).
            if (!EqualAreaAxisStripZonesInPolygon2d.TryBuildZoneRingsFromRing(
                    ring, 6, 1e-6, pairToShafts: false,
                    shaftSites: null,
                    out var rings, out var owners, out _, out string stripErr))
                errors.Add("unpaired_strips_ring: build failed: " + stripErr);
            else if (rings.Count != 6)
                errors.Add("unpaired_strips_ring: expected 6 rings, got " + rings.Count);
            else
            {
                double total = PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
                double target = total / 6.0;
                for (int i = 0; i < rings.Count; i++)
                {
                    double a = PolygonVerticalHalfPlaneClip2d.AbsArea(rings[i]);
                    if (Math.Abs(a - target) > target * 0.05 + 1e-6)
                        errors.Add("unpaired_strips_ring: ring " + i + " area " + a + " vs target " + target);
                }
            }

            return errors.Count == 0
                ? "ShaftSpatialSpread2d regression: OK"
                : "ShaftSpatialSpread2d regression FAILED:\n  " + string.Join("\n  ", errors);
        }
    }
}
