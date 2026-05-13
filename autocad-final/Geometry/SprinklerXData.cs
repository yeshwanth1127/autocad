using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace autocad_final.Geometry
{
    public static class SprinklerXData
    {
        public const string RegAppName = "AUTOCAD_FINAL_SPRK";

        private const string KeyRole = "ROLE";
        private const string RoleTrunk = "TRUNK";
        private const string RoleTrunkCap = "TRUNK_CAP";
        private const string RoleConnector = "CONNECTOR";
        private const string RoleMainPipeSchedule = "MAIN_PIPE_SCHEDULE";
        private const string RoleBranchPipeSchedule = "BRANCH_PIPE_SCHEDULE";

        /// <summary>
        /// Extended data key for the selected zone boundary polyline handle (hex string). Used to delete all zone-placed
        /// content after the boundary moves, including entities no longer inside the new polygon.
        /// </summary>
        private const string KeyZoneBoundary = "BOUNDARY";

        /// <summary>
        /// Extended data key for associating sprinklers/pipes to a shaft id (e.g. "shaft_1").
        /// </summary>
        private const string KeyShaft = "SHAFT";

        /// <summary>
        /// Stored on a zone boundary polyline: the handle (hex) of the explicitly assigned shaft block.
        /// Takes priority over geometric nearest-shaft lookup during routing.
        /// </summary>
        private const string KeyShaftAssignment = "SHAFT_ASSIGNMENT";

        /// <summary>
        /// Stored on a shaft block insert: comma-separated zone boundary handles this shaft is assigned to.
        /// </summary>
        private const string KeyZoneAssignments = "ZONE_ASSIGNMENTS";

        /// <summary>
        /// Stored on a zone outline polyline: handle (hex) of the floor boundary parcel used when the zone was created.
        /// Used to scope shaft counts / numbering to the correct floor when several floors exist in one drawing.
        /// </summary>
        private const string KeyParentFloorBoundary = "PARENT_FLOOR";

        /// <summary>
        /// Floor-scoped unique shaft index (1-based), ASCII decimal. Stored on shaft block inserts; copied to a zone
        /// boundary polyline when that zone is assigned to the shaft (manual or automatic).
        /// </summary>
        private const string KeyShaftUid = "SHAFT_UID";

        public static void EnsureRegApp(Transaction tr, Database db)
        {
            if (tr == null || db == null) return;

            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(RegAppName))
                return;

            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = RegAppName };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        public static void TagAsTrunk(Entity ent)
        {
            if (ent == null) return;
            ent.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyRole),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTrunk));
        }

        public static void TagAsTrunkCap(Entity ent)
        {
            if (ent == null) return;
            ent.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyRole),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTrunkCap));
        }

        public static void TagAsConnector(Entity ent)
        {
            if (ent == null) return;
            ent.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyRole),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleConnector));
        }

        public static bool IsTaggedTrunk(Entity ent)
        {
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 2; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i].Value as string) == KeyRole &&
                        vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i + 1].Value as string) == RoleTrunk)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public static bool IsTaggedTrunkCap(Entity ent)
        {
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 2; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i].Value as string) == KeyRole &&
                        vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i + 1].Value as string) == RoleTrunkCap)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// Associates an entity with a zone by storing the boundary polyline database handle (hex).
        /// Merges with existing AUTOCAD_FINAL_SPRK xdata (e.g. ROLE).
        /// </summary>
        public static void ApplyZoneBoundaryTag(Entity ent, string boundaryHandleHex)
        {
            if (ent == null || string.IsNullOrEmpty(boundaryHandleHex))
                return;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { rb = null; }

            var list = new System.Collections.Generic.List<TypedValue>();
            if (rb != null)
            {
                var arr = rb.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i < arr.Length - 1
                        && arr[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (arr[i].Value as string) == KeyZoneBoundary)
                    {
                        i++;
                        continue;
                    }

                    list.Add(arr[i]);
                }
            }

            if (list.Count == 0)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName));

            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyZoneBoundary));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, boundaryHandleHex));
            ent.XData = new ResultBuffer(list.ToArray());
        }

        /// <summary>
        /// Associates an entity with a shaft id (string). Merges with existing AUTOCAD_FINAL_SPRK xdata.
        /// </summary>
        public static void ApplyShaftTag(Entity ent, string shaftId)
        {
            if (ent == null || string.IsNullOrEmpty(shaftId))
                return;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { rb = null; }

            var list = new System.Collections.Generic.List<TypedValue>();
            if (rb != null)
            {
                var arr = rb.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i < arr.Length - 1
                        && arr[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (arr[i].Value as string) == KeyShaft)
                    {
                        i++;
                        continue;
                    }
                    list.Add(arr[i]);
                }
            }

            if (list.Count == 0)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName));

            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyShaft));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, shaftId));
            ent.XData = new ResultBuffer(list.ToArray());
        }

        public static bool TryGetShaftId(Entity ent, out string shaftId)
        {
            shaftId = null;
            if (ent == null)
                return false;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null)
                return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 1; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (vals[i].Value as string) == KeyShaft
                        && vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        shaftId = vals[i + 1].Value as string;
                        return !string.IsNullOrEmpty(shaftId);
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public static bool TryGetZoneBoundaryHandle(Entity ent, out string boundaryHandleHex)
        {
            boundaryHandleHex = null;
            if (ent == null)
                return false;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null)
                return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 1; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (vals[i].Value as string) == KeyZoneBoundary
                        && vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        boundaryHandleHex = vals[i + 1].Value as string;
                        return !string.IsNullOrEmpty(boundaryHandleHex);
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public static bool IsTaggedConnector(Entity ent)
        {
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 2; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i].Value as string) == KeyRole &&
                        vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i + 1].Value as string) == RoleConnector)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>Merges ROLE = MAIN_PIPE_SCHEDULE with existing extended data (e.g. zone boundary tag).</summary>
        public static void TagAsMainPipeScheduleLabel(Entity ent)
        {
            if (ent == null) return;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { rb = null; }

            var list = new System.Collections.Generic.List<TypedValue>();
            if (rb != null)
            {
                var arr = rb.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i < arr.Length - 1
                        && arr[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (arr[i].Value as string) == KeyRole)
                    {
                        i++;
                        continue;
                    }

                    list.Add(arr[i]);
                }
            }

            if (list.Count == 0)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName));

            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyRole));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleMainPipeSchedule));
            ent.XData = new ResultBuffer(list.ToArray());
        }

        public static bool IsTaggedMainPipeScheduleLabel(Entity ent)
        {
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 2; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i].Value as string) == KeyRole &&
                        vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i + 1].Value as string) == RoleMainPipeSchedule)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>Merges ROLE = BRANCH_PIPE_SCHEDULE with existing extended data (e.g. zone boundary tag).</summary>
        public static void TagAsBranchPipeScheduleLabel(Entity ent)
        {
            if (ent == null) return;

            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { rb = null; }

            var list = new System.Collections.Generic.List<TypedValue>();
            if (rb != null)
            {
                var arr = rb.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i < arr.Length - 1
                        && arr[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && (arr[i].Value as string) == KeyRole)
                    {
                        i++;
                        continue;
                    }

                    list.Add(arr[i]);
                }
            }

            if (list.Count == 0)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName));

            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, KeyRole));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleBranchPipeSchedule));
            ent.XData = new ResultBuffer(list.ToArray());
        }

        public static bool IsTaggedBranchPipeScheduleLabel(Entity ent)
        {
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;

            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 2; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i].Value as string) == KeyRole &&
                        vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        (vals[i + 1].Value as string) == RoleBranchPipeSchedule)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        // ── Shaft-Zone Assignment ─────────────────────────────────────────────────

        /// <summary>
        /// Stores the shaft assignment on a zone boundary polyline.
        /// The value is the shaft block's database handle (hex string).
        /// Replaces any previous assignment.
        /// </summary>
        public static void ApplyShaftAssignmentTag(Entity ent, string shaftHandleHex)
        {
            if (ent == null || string.IsNullOrEmpty(shaftHandleHex)) return;
            MergeStringTag(ent, KeyShaftAssignment, shaftHandleHex);
        }

        /// <summary>
        /// Reads the explicitly-assigned shaft handle from a zone boundary polyline.
        /// Returns false if no assignment was stored.
        /// </summary>
        public static bool TryGetShaftAssignmentHandle(Entity ent, out string shaftHandleHex)
            => TryReadStringTag(ent, KeyShaftAssignment, out shaftHandleHex);

        /// <summary>
        /// Appends (or replaces) a zone boundary handle in the shaft block's zone-assignment list.
        /// </summary>
        public static void ApplyZoneAssignmentTag(Entity ent, string zoneHandleHex)
        {
            if (ent == null || string.IsNullOrEmpty(zoneHandleHex)) return;

            TryReadStringTag(ent, KeyZoneAssignments, out string existing);
            var handles = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(existing))
            {
                foreach (var h in existing.Split(','))
                {
                    var t = h.Trim();
                    if (!string.IsNullOrEmpty(t) && !string.Equals(t, zoneHandleHex, System.StringComparison.OrdinalIgnoreCase))
                        handles.Add(t);
                }
            }
            handles.Add(zoneHandleHex);
            MergeStringTag(ent, KeyZoneAssignments, string.Join(",", handles));
        }

        /// <summary>
        /// Reads the list of zone boundary handles assigned to a shaft block.
        /// </summary>
        public static bool TryGetZoneAssignmentHandles(Entity ent, out System.Collections.Generic.List<string> zoneHandles)
        {
            zoneHandles = new System.Collections.Generic.List<string>();
            if (!TryReadStringTag(ent, KeyZoneAssignments, out string raw) || string.IsNullOrEmpty(raw))
                return false;
            foreach (var h in raw.Split(','))
            {
                var t = h.Trim();
                if (!string.IsNullOrEmpty(t))
                    zoneHandles.Add(t);
            }
            return zoneHandles.Count > 0;
        }

        /// <summary>
        /// Stores the outer floor boundary handle (hex) used when this zone outline was created (multi-floor drawings).
        /// </summary>
        public static void ApplyParentFloorBoundaryTag(Entity ent, string floorBoundaryHandleHex)
        {
            if (ent == null || string.IsNullOrEmpty(floorBoundaryHandleHex)) return;
            MergeStringTag(ent, KeyParentFloorBoundary, floorBoundaryHandleHex.Trim());
        }

        /// <summary>Reads <see cref="ApplyParentFloorBoundaryTag"/> value when present.</summary>
        public static bool TryGetParentFloorBoundaryHandle(Entity ent, out string floorBoundaryHandleHex)
            => TryReadStringTag(ent, KeyParentFloorBoundary, out floorBoundaryHandleHex);

        /// <summary>Writes <see cref="KeyShaftUid"/> on a shaft block insert (or refreshes it).</summary>
        public static void ApplyShaftUidTag(Entity ent, int shaftUid)
        {
            if (ent == null || shaftUid < 1) return;
            MergeStringTag(ent, KeyShaftUid, shaftUid.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Writes the assigned shaft's <see cref="KeyShaftUid"/> onto a zone boundary polyline (same key/value semantics as on the shaft).
        /// </summary>
        public static void ApplyAssignedShaftUidOnZone(Entity zoneEnt, int shaftUid)
            => ApplyShaftUidTag(zoneEnt, shaftUid);

        /// <summary>Reads <see cref="KeyShaftUid"/> when present and parses as a positive integer.</summary>
        public static bool TryGetShaftUid(Entity ent, out int shaftUid)
        {
            shaftUid = 0;
            if (!TryReadStringTag(ent, KeyShaftUid, out string s) || string.IsNullOrWhiteSpace(s))
                return false;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out shaftUid)
                && shaftUid >= 1;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static bool TryReadStringTag(Entity ent, string key, out string value)
        {
            value = null;
            if (ent == null) return false;
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { return false; }
            if (rb == null) return false;
            try
            {
                var vals = rb.AsArray();
                for (int i = 0; i < vals.Length - 1; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && string.Equals(vals[i].Value as string, key, System.StringComparison.Ordinal)
                        && vals[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        value = vals[i + 1].Value as string;
                        return !string.IsNullOrEmpty(value);
                    }
                }
            }
            catch { return false; }
            return false;
        }

        private static void MergeStringTag(Entity ent, string key, string value)
        {
            ResultBuffer rb;
            try { rb = ent.GetXDataForApplication(RegAppName); }
            catch { rb = null; }

            var list = new System.Collections.Generic.List<TypedValue>();
            if (rb != null)
            {
                var arr = rb.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i < arr.Length - 1
                        && arr[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                        && string.Equals(arr[i].Value as string, key, System.StringComparison.Ordinal))
                    {
                        i++; // skip old value
                        continue;
                    }
                    list.Add(arr[i]);
                }
            }

            if (list.Count == 0)
                list.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName));

            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, key));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, value));
            ent.XData = new ResultBuffer(list.ToArray());
        }
    }
}

