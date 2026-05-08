using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Agent
{
    /// <summary>
    /// Session-level store of virtual shaft points registered by the AI when the drawing
    /// has no blocks named "shaft" but shaft/riser/stair locations are known.
    /// Thread-safe; hints persist for the AutoCAD session until explicitly cleared.
    /// </summary>
    public static class ShaftHintStore
    {
        private static readonly List<Point3d> _hints = new List<Point3d>();
        private static readonly object _lock = new object();

        public static void AddHint(double x, double y, double z = 0)
        {
            lock (_lock) { _hints.Add(new Point3d(x, y, z)); }
        }

        public static void Clear()
        {
            lock (_lock) { _hints.Clear(); }
        }

        /// <summary>Returns a snapshot copy — safe to iterate without holding the lock.</summary>
        public static List<Point3d> GetAll()
        {
            lock (_lock) { return new List<Point3d>(_hints); }
        }

        public static int Count { get { lock (_lock) { return _hints.Count; } } }
    }
}
