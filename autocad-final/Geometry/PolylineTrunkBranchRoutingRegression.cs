using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Pure-geometry regression checks for slanted-main branch grid attachment (no database).
    /// Call <see cref="RunAll"/> from a debugger or optional command to validate routing invariants.
    /// </summary>
    public static class PolylineTrunkBranchRoutingRegression
    {
        /// <summary>
        /// Some heads sit where the trunk-bbox default orientation cannot intersect the main, but the alternate can.
        /// </summary>
        public static bool DiagonalTrunk_AwkwardHeadNeedsAlternateOrientation()
        {
            var ring = new List<Point2d>
            {
                new Point2d(0, 0),
                new Point2d(100, 0),
                new Point2d(100, 100),
                new Point2d(0, 100),
            };
            var trunk = new List<Point2d> { new Point2d(10, 10), new Point2d(90, 90) };
            var head = new Point2d(15, 5);
            double tol = 1.0;
            var one = new List<Point2d> { head };

            bool globalHorizBranches = PolylineTrunkGridAttach2d.TrunkPolylineRunsMostlyVertical(trunk, tol);
            bool globalOk = PolylineTrunkGridAttach2d.TryFindGridAxisAttach(
                trunk,
                ring,
                globalHorizBranches,
                globalHorizBranches ? head.Y : head.X,
                one,
                tol,
                out _);
            bool altOk = PolylineTrunkGridAttach2d.TryFindGridAxisAttach(
                trunk,
                ring,
                !globalHorizBranches,
                !globalHorizBranches ? head.Y : head.X,
                one,
                tol,
                out _);

            return altOk && !globalOk;
        }

        private static bool AllWaypointSegmentsAxisAligned(List<Point2d> w, double te)
        {
            if (w == null || w.Count < 2) return false;
            for (int i = 0; i + 1 < w.Count; i++)
            {
                var p = w[i];
                var q = w[i + 1];
                double dx = Math.Abs(q.X - p.X);
                double dy = Math.Abs(q.Y - p.Y);
                bool horiz = dy <= te && dx > te;
                bool vert = dx <= te && dy > te;
                if (!horiz && !vert)
                    return false;
            }
            return true;
        }

        /// <summary>L-path: vertical leg then horizontal; every segment must be axis-aligned.</summary>
        public static bool OrthogonalWaypoints_VerticalFirstBreaksDiagonal()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 1e-3;
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                new Point2d(1, 2), new Point2d(9, 7), verticalFirstLeg: true, empty, rings, tol);
            return w.Count >= 3 && AllWaypointSegmentsAxisAligned(w, tol * 10.0);
        }

        /// <summary>L-path: horizontal leg then vertical.</summary>
        public static bool OrthogonalWaypoints_HorizontalFirstBreaksDiagonal()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 1e-3;
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                new Point2d(1, 2), new Point2d(9, 7), verticalFirstLeg: false, empty, rings, tol);
            return w.Count >= 3 && AllWaypointSegmentsAxisAligned(w, tol * 10.0);
        }

        /// <summary>Already-horizontal span stays a single segment.</summary>
        public static bool OrthogonalWaypoints_AlignedDelegatesUnchanged()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 1e-3;
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                new Point2d(0, 0), new Point2d(10, 0), verticalFirstLeg: true, empty, rings, tol);
            return w.Count == 2 && Math.Abs(w[1].X - 10) < 1e-6 && Math.Abs(w[1].Y) < 1e-6;
        }

        /// <summary>AxisAligned with no shafts must not emit a one-segment diagonal chord.</summary>
        public static bool AxisAlignedEmpty_DiagonalDelegatesToManhattan()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 1e-3;
            var w = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                new Point2d(0, 0), new Point2d(6, 9), empty, rings, tol);
            return w.Count >= 3 && AllWaypointSegmentsAxisAligned(w, tol * 10.0);
        }

        /// <summary>Regression: shallow slope (0.5% grade) must not blow the stack and must terminate.</summary>
        public static bool OrthogonalWaypoints_ShallowSlopeTerminates()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 0.01;
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                new Point2d(0, 0), new Point2d(100, 0.5), verticalFirstLeg: true, empty, rings, tol);
            return w != null && w.Count >= 2;
        }

        /// <summary>Identical start and end must return a 2-point list without crashing.</summary>
        public static bool OrthogonalWaypoints_DegeneratePointReturns2()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            var p = new Point2d(5, 5);
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                p, p, verticalFirstLeg: true, empty, rings, 1e-3);
            return w != null && w.Count == 2;
        }

        /// <summary>Near-zero-length horizontal segment must return 2 points without exploding.</summary>
        public static bool AxisAligned_MicroSegmentHandledSafely()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            var w = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                new Point2d(0, 0), new Point2d(1e-6, 0), empty, rings, 1e-3);
            return w != null && w.Count >= 2;
        }

        /// <summary>No segment in any output list may be shorter than 1e-3 (MinSegmentLength).</summary>
        public static bool OrthogonalWaypoints_NoMicroSegmentsInOutput()
        {
            var empty = new List<(Point2d min, Point2d max)>();
            var rings = new List<IList<Point2d>>();
            double tol = 1e-3;
            var w = BranchPipeShaftDetour2d.OrthogonalWaypointsAvoidingBoxes(
                new Point2d(0, 0), new Point2d(8, 6), verticalFirstLeg: true, empty, rings, tol);
            if (w == null || w.Count < 2) return false;
            for (int i = 0; i + 1 < w.Count; i++)
                if (w[i].GetDistanceTo(w[i + 1]) < tol)
                    return false;
            return true;
        }

        public static bool RunAll() =>
            DiagonalTrunk_AwkwardHeadNeedsAlternateOrientation()
            && OrthogonalWaypoints_VerticalFirstBreaksDiagonal()
            && OrthogonalWaypoints_HorizontalFirstBreaksDiagonal()
            && OrthogonalWaypoints_AlignedDelegatesUnchanged()
            && AxisAlignedEmpty_DiagonalDelegatesToManhattan()
            && OrthogonalWaypoints_ShallowSlopeTerminates()
            && OrthogonalWaypoints_DegeneratePointReturns2()
            && AxisAligned_MicroSegmentHandledSafely()
            && OrthogonalWaypoints_NoMicroSegmentsInOutput();
    }
}
