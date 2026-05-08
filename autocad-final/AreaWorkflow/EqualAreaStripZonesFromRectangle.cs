using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Builds N equal-area strip polygons inside an orthogonal rectangular floor (formula-based; no shaft positions).
    /// Rectangles may be rotated in plan; strips run along the first edge (vertex 0 → 1).
    /// </summary>
    public static class EqualAreaStripZonesFromRectangle
    {
        /// <summary>
        /// True if the polyline is a closed orthogonal rectangle (four straight edges, zero bulge) in any XY orientation.
        /// Uses edge directions and perpendicularity; axis-aligned bbox is not used (rotated rectangles fail bbox-only checks).
        /// </summary>
        public static bool TryGetOrthogonalRectangleBounds(Polyline pl, double tol, out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = maxX = minY = maxY = 0;
            if (!TryGetOrthogonalRectangleFrame(pl, tol, out _, out _, out _, out _, out _))
                return false;

            minX = double.MaxValue;
            maxX = double.MinValue;
            minY = double.MaxValue;
            maxY = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var p = pl.GetPoint2dAt(i);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            return true;
        }

        /// <summary>
        /// First corner, unit directions along consecutive edges (vertex 0→1 and 1→2), and side lengths W and H.
        /// </summary>
        private static bool TryGetOrthogonalRectangleFrame(
            Polyline pl,
            double tol,
            out Point2d p0,
            out Vector2d uhat,
            out Vector2d vhat,
            out double w,
            out double h)
        {
            p0 = default;
            uhat = default;
            vhat = default;
            w = h = 0;

            if (pl == null || !pl.Closed)
                return false;

            int nv = pl.NumberOfVertices;
            if (nv < 4)
                return false;

            // Some DWGs use a 5th vertex coincident with the first instead of relying on Closed only.
            if (nv == 5)
            {
                Point2d a = pl.GetPoint2dAt(0);
                Point2d b = pl.GetPoint2dAt(4);
                double firstEdge = a.GetDistanceTo(pl.GetPoint2dAt(1));
                double closeTol = System.Math.Max(tol, 1e-8 * System.Math.Max(firstEdge, 1.0));
                if (a.GetDistanceTo(b) > closeTol)
                    return false;
                nv = 4;
            }
            else if (nv != 4)
                return false;

            // Straight sides only (bulge is independent of coincident tolerance; tol can be ~1 mm).
            for (int i = 0; i < 4; i++)
            {
                if (System.Math.Abs(pl.GetBulgeAt(i)) > 1e-8)
                    return false;
            }

            p0 = pl.GetPoint2dAt(0);
            Point2d p1 = pl.GetPoint2dAt(1);
            Point2d p2 = pl.GetPoint2dAt(2);
            Point2d p3 = pl.GetPoint2dAt(3);

            var e0 = p1 - p0;
            var e1 = p2 - p1;
            var e2 = p3 - p2;
            var e3 = p0 - p3;

            double len0 = e0.Length;
            double len1 = e1.Length;
            if (len0 <= tol || len1 <= tol)
                return false;

            // Relative perpendicularity: |dot|/(|e0||e1|) is small for ~90° corners (snaps are rarely sub-0.0001°).
            double dot = e0.X * e1.X + e0.Y * e1.Y;
            double lenProd = len0 * len1;
            const double maxSinFromSquare = 1e-4;
            if (lenProd > 0 && System.Math.Abs(dot) / lenProd > maxSinFromSquare)
                return false;

            // Opposite sides equal vectors (loose absolute tol — large coordinates still close e2 to -e0).
            double edgeTol = System.Math.Max(tol, 1e-6 * System.Math.Max(len0, len1));
            if ((e2 + e0).Length > edgeTol || (e3 + e1).Length > edgeTol)
                return false;

            double areaPl = System.Math.Abs(pl.Area);
            double areaRect = len0 * len1;
            double areaTol = System.Math.Max(1e-3, 1e-5 * System.Math.Max(areaRect, areaPl));
            if (System.Math.Abs(areaRect - areaPl) > areaTol)
                return false;

            uhat = e0.GetNormal();
            vhat = e1.GetNormal();
            w = len0;
            h = len1;
            return true;
        }

        /// <summary>
        /// N strips along edge 0→1 from vertex 0, each with area <paramref name="aTargetDrawingArea"/> (strip width × side length along 1→2).
        /// Remainder of the rectangle past the last strip is not included in <paramref name="rings"/>.
        /// </summary>
        public static bool TryBuildVerticalStripZoneRings(
            Polyline pl,
            int zoneCount,
            double aTargetDrawingArea,
            double tol,
            out List<List<Point2d>> rings,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            errorMessage = null;

            if (zoneCount < 2)
            {
                errorMessage = "Need at least two shafts for zones.";
                return false;
            }

            if (aTargetDrawingArea <= 0)
            {
                errorMessage = "Target zone area is not positive.";
                return false;
            }

            if (!TryGetOrthogonalRectangleFrame(pl, tol, out Point2d p0, out Vector2d uhat, out Vector2d vhat, out _, out double height))
            {
                errorMessage =
                    "Equal-area strip zones require an orthogonal rectangular floor (four straight sides on the XY plane). " +
                    "Use a rectangle on layer \"" + SprinklerLayers.WorkLayer + "\", or redraw the boundary as a rectangle.";
                return false;
            }

            double stripAlongU = aTargetDrawingArea / height;

            for (int k = 0; k < zoneCount; k++)
            {
                double s0 = k * stripAlongU;
                double s1 = (k + 1) * stripAlongU;
                var ring = new List<Point2d>(4)
                {
                    p0 + uhat * s0,
                    p0 + uhat * s1,
                    p0 + uhat * s1 + vhat * height,
                    p0 + uhat * s0 + vhat * height
                };
                rings.Add(ring);
            }

            return true;
        }

        /// <summary>
        /// Human-readable summary for formula zoning (no Voronoi).
        /// </summary>
        public static string FormatFormulaZoningSummary(
            double? floorM2,
            double? aTargetM2,
            int shaftCount,
            double floorAreaDrawingUnits)
        {
            if (shaftCount < 2)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            if (floorM2.HasValue && aTargetM2.HasValue)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Each of {0} zones: {1:F2} m² (full floor {2:F2} m² ÷ {0}).",
                    shaftCount,
                    aTargetM2.Value,
                    floorM2.Value);
            }
            else
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Floor {0:F2} sq. drawing units ÷ {1} shafts — each zone uses that fraction of the boundary in drawing units.",
                    floorAreaDrawingUnits,
                    shaftCount);
            }

            sb.Append(" Strips are vertical in drawing X (WCS), clipped to the boundary. Zone layout does not use shaft insertion points (only shaft count).");
            return sb.ToString();
        }
    }
}
