using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Samples a closed (or open) <see cref="Polyline"/> along its length into a simple 2D vertex ring for planar clipping.
    /// </summary>
    public static class PolylineClosedBoundaryRingSampler2d
    {
        /// <summary>
        /// Closed vertex ring for half-plane clipping: uses exact LW polyline corners when there are no arc bulges
        /// (avoids gaps from sampling error); otherwise samples along curve length.
        /// </summary>
        public static List<Point2d> ConvertPolylineToRingPoints(Polyline pl)
        {
            if (pl == null || pl.NumberOfVertices < 3)
                return new List<Point2d>();

            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                if (System.Math.Abs(pl.GetBulgeAt(i)) > 1e-10)
                    return SampleRing(pl);
            }

            var list = new List<Point2d>(n);
            for (int i = 0; i < n; i++)
                list.Add(pl.GetPoint2dAt(i));
            return list;
        }

        /// <summary>
        /// Samples the polyline perimeter into a closed vertex list (no duplicate closing vertex).
        /// </summary>
        public static List<Point2d> SampleRing(Polyline pl)
        {
            var list = new List<Point2d>();
            if (pl == null)
                return list;

            double len = pl.Length;
            if (len <= 1e-12)
                return list;

            double step = Math.Max(len / 256.0, len > 1000 ? 5.0 : 0.01);
            int steps = (int)Math.Ceiling(len / step);
            if (steps < 32) steps = 32;
            if (steps > 512) steps = 512;

            for (int s = 0; s < steps; s++)
            {
                double d = len * s / steps;
                if (s == steps - 1)
                    d = len - 1e-9;
                Point3d p = pl.GetPointAtDist(d);
                list.Add(new Point2d(p.X, p.Y));
            }

            if (list.Count < 3)
                return new List<Point2d>();

            return list;
        }
    }
}
