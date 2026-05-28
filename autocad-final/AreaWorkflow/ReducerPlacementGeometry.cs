using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Plan-view reducer placement using sprinkler head circle and reducer block geometry.
    /// </summary>
    public static class ReducerPlacementGeometry
    {
        /// <summary>Built-in pendent sprinkler circle radius in drawing units.</summary>
        public const double DefaultSprinklerSymbolRadiusDu = 0.025;

        /// <summary>Built-in reducer half-length along local Y (narrow at +Y, wide at -Y).</summary>
        public const double DefaultReducerHalfLengthDu = 0.020;

        /// <summary>
        /// Resolves plan-view sprinkler symbol radius from inserted heads in the zone, then block definition / settings.
        /// </summary>
        public static double ResolveSprinklerSymbolRadiusDu(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHex)
        {
            if (tr != null && ms != null &&
                TryResolveSprinklerRadiusFromInsertedHeads(tr, ms, zoneRing, zoneBoundaryHex, out double fromInserts) &&
                fromInserts > 0)
            {
                return fromInserts;
            }

            return ResolveSprinklerSymbolRadiusDuFromDefinition(tr, db);
        }

        /// <summary>
        /// Resolves plan-view sprinkler symbol radius from the block definition, settings, or defaults.
        /// </summary>
        public static double ResolveSprinklerSymbolRadiusDu(Transaction tr, Database db) =>
            ResolveSprinklerSymbolRadiusDuFromDefinition(tr, db);

        private static double ResolveSprinklerSymbolRadiusDuFromDefinition(Transaction tr, Database db)
        {
            if (tr != null && db != null &&
                TryGetSprinklerSymbolRadiusFromBlock(tr, db, out double fromBlock) &&
                fromBlock > 0)
            {
                return fromBlock;
            }

            try
            {
                double headRadiusM = RuntimeSettings.Load().SprinklerHeadRadiusM;
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, headRadiusM, out double fromSettings) &&
                    fromSettings > 0)
                {
                    return fromSettings;
                }
            }
            catch { /* ignore */ }

            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, DefaultSprinklerSymbolRadiusDu, out double fromMeters) &&
                    fromMeters > 0)
                {
                    return fromMeters;
                }
            }
            catch { /* ignore */ }

            return DefaultSprinklerSymbolRadiusDu;
        }

        /// <summary>
        /// Median plan radius from sprinkler block inserts / circles actually drawn in the zone (WCS extents).
        /// </summary>
        public static bool TryResolveSprinklerRadiusFromInsertedHeads(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHex,
            out double radiusDu)
        {
            radiusDu = 0;
            if (tr == null || ms == null)
                return false;

            var samples = new List<double>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased)
                    continue;

                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null || !SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;
                if (!IsSprinklerEntityInZone(ent, zoneRing, zoneBoundaryHex))
                    continue;
                if (!TryGetPlanRadiusFromSprinklerEntity(tr, ent, out double sampleR) || sampleR <= 0)
                    continue;

                samples.Add(sampleR);
            }

            if (samples.Count == 0)
                return false;

            samples.Sort();
            radiusDu = samples[samples.Count / 2];
            return radiusDu > 0;
        }

        private static bool IsSprinklerEntityInZone(Entity ent, List<Point2d> zoneRing, string zoneBoundaryHex)
        {
            if (!string.IsNullOrEmpty(zoneBoundaryHex))
            {
                if (SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) &&
                    string.Equals(h, zoneBoundaryHex, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (zoneRing == null || zoneRing.Count < 3)
                return true;

            if (!TryGetPlanPointFromEntity(ent, out Point2d pt))
                return false;

            return PolygonUtils.PointInPolygon(zoneRing, pt);
        }

        private static bool TryGetPlanPointFromEntity(Entity ent, out Point2d pt)
        {
            pt = default;
            if (ent == null)
                return false;

            try
            {
                if (ent is BlockReference br)
                {
                    var p = br.Position;
                    pt = new Point2d(p.X, p.Y);
                    return true;
                }

                if (ent is Circle c)
                {
                    var p = c.Center;
                    pt = new Point2d(p.X, p.Y);
                    return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool TryGetPlanRadiusFromSprinklerEntity(Transaction tr, Entity ent, out double radiusDu)
        {
            radiusDu = 0;
            if (ent == null)
                return false;

            try
            {
                if (ent is Circle c)
                {
                    radiusDu = c.Radius;
                    return radiusDu > 1e-9;
                }

                if (ent is BlockReference br)
                {
                    if (TryGetPlanRadiusFromBlockReferenceExtents(br, out radiusDu))
                        return true;

                    var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (btr != null &&
                        TryGetCircleRadiusInBlock(tr, btr, out double defR) &&
                        defR > 0)
                    {
                        double scale = GetBlockReferencePlanScale(br);
                        radiusDu = defR * scale;
                        return radiusDu > 1e-9;
                    }
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool TryGetPlanRadiusFromBlockReferenceExtents(BlockReference br, out double radiusDu)
        {
            radiusDu = 0;
            if (br == null)
                return false;

            try
            {
                Extents3d ext = br.GeometricExtents;
                double rx = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X) * 0.5;
                double ry = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y) * 0.5;
                radiusDu = Math.Max(rx, ry);
                return radiusDu > 1e-9;
            }
            catch
            {
                return false;
            }
        }

        private static double GetBlockReferencePlanScale(BlockReference br)
        {
            if (br == null)
                return 1.0;

            try
            {
                Scale3d sf = br.ScaleFactors;
                return Math.Max(Math.Max(Math.Abs(sf.X), Math.Abs(sf.Y)), 1e-9);
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// Reads reducer block half-extents along local Y: narrow face at +Y, wide face at -Y.
        /// </summary>
        public static void ResolveReducerHalfExtentsDu(
            Transaction tr,
            ObjectId blockDefId,
            out double narrowHalfDu,
            out double wideHalfDu)
        {
            narrowHalfDu = DefaultReducerHalfLengthDu;
            wideHalfDu = DefaultReducerHalfLengthDu;

            if (tr == null || blockDefId.IsNull || !blockDefId.IsValid)
                return;

            try
            {
                var btr = tr.GetObject(blockDefId, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr == null || btr.IsErased)
                    return;

                if (TryGetBlockRecordPlanExtents(btr, tr, out Extents3d ext))
                {
                    double maxY = Math.Max(ext.MaxPoint.Y, 0);
                    double minYNeg = Math.Max(-ext.MinPoint.Y, 0);
                    if (maxY > 1e-9)
                        narrowHalfDu = maxY;
                    if (minYNeg > 1e-9)
                        wideHalfDu = minYNeg;
                }
            }
            catch { /* keep defaults */ }
        }

        /// <summary>
        /// World point of the reducer wide face when insert and branch axis are known.
        /// </summary>
        public static Point2d ComputeWideFaceFromInsert(
            Point2d insert,
            double branchAxisX,
            double branchAxisY,
            double reducerWideHalfDu)
        {
            double ux = branchAxisX;
            double uy = branchAxisY;
            double len = Math.Sqrt(ux * ux + uy * uy);
            if (len > 1e-9)
            {
                ux /= len;
                uy /= len;
            }
            else
            {
                ux = 1.0;
                uy = 0.0;
            }

            double h = Math.Max(reducerWideHalfDu, 0);
            return new Point2d(insert.X + ux * h, insert.Y + uy * h);
        }

        /// <summary>
        /// Block insert point so the wide face is tangent on the head circle on the larger-pipe (upstream) side.
        /// The reducer body sits outside the head — it does not overlap the sprinkler symbol.
        /// <paramref name="towardSmallerX"/>/<paramref name="towardSmallerY"/> must match reducer rotation axis (toward smaller pipe).
        /// </summary>
        public static Point2d ComputeBranchReducerInsertAtHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu,
            double reducerWideHalfDu)
        {
            double svx = towardSmallerX;
            double svy = towardSmallerY;
            double slen = Math.Sqrt(svx * svx + svy * svy);
            if (slen > 1e-9)
            {
                svx /= slen;
                svy /= slen;
            }
            else
            {
                svx = 1.0;
                svy = 0.0;
            }

            double r = Math.Max(sprinklerRadiusDu, 0);
            double h = Math.Max(reducerWideHalfDu, 0);
            // Wide face at insert + towardSmaller*h == joint - towardSmaller*r (upstream circumference).
            return new Point2d(
                joint.X - svx * (r + h),
                joint.Y - svy * (r + h));
        }

        /// <summary>Wide-face tangency on the upstream (larger-pipe) side of the head circle.</summary>
        public static Point2d ComputeWideFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu, towardLargerPipe: true);
        }

        /// <summary>Contact on the downstream (smaller-pipe) side of the head circle.</summary>
        public static Point2d ComputeFaceContactOnHeadTowardSmallerPipe(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu, towardLargerPipe: false);
        }

        /// <summary>World point where the reducer narrow face meets the head circle.</summary>
        public static Point2d ComputeNarrowFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu, towardLargerPipe: false);
        }

        private static Point2d ComputeFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu,
            bool towardLargerPipe)
        {
            double svx = towardSmallerX;
            double svy = towardSmallerY;
            double slen = Math.Sqrt(svx * svx + svy * svy);
            if (slen > 1e-9)
            {
                svx /= slen;
                svy /= slen;
            }
            else
            {
                svx = 1.0;
                svy = 0.0;
            }

            double r = Math.Max(sprinklerRadiusDu, 0);
            double sign = towardLargerPipe ? -1.0 : 1.0;
            return new Point2d(
                joint.X + sign * svx * r,
                joint.Y + sign * svy * r);
        }

        private static Point2d ComputeFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu, towardLargerPipe: false);
        }

        /// <summary>
        /// Block insert point along the branch from main outer fiber so the wide face sits on the fiber.
        /// </summary>
        public static Point2d ComputeMainTeeReducerInsertFromFiber(
            Point2d fiberOnMainOuter,
            double branchAxisX,
            double branchAxisY,
            double reducerWideHalfDu)
        {
            double ux = branchAxisX;
            double uy = branchAxisY;
            double len = Math.Sqrt(ux * ux + uy * uy);
            if (len > 1e-9)
            {
                ux /= len;
                uy /= len;
            }
            else
            {
                ux = 1.0;
                uy = 0.0;
            }

            double h = Math.Max(reducerWideHalfDu, 0);
            return new Point2d(
                fiberOnMainOuter.X + ux * h,
                fiberOnMainOuter.Y + uy * h);
        }

        private static bool TryGetSprinklerSymbolRadiusFromBlock(Transaction tr, Database db, out double radiusDu)
        {
            radiusDu = 0;
            if (tr == null || db == null)
                return false;

            string blockName = SprinklerLayers.GetConfiguredSprinklerBlockName();
            if (string.IsNullOrWhiteSpace(blockName))
                return false;

            if (!TryFindBlockDefinition(tr, db, blockName, out ObjectId blockDefId))
                return false;

            try
            {
                var btr = tr.GetObject(blockDefId, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr == null)
                    return false;

                if (TryGetCircleRadiusInBlock(tr, btr, out radiusDu) && radiusDu > 0)
                    return true;

                if (TryGetBlockRecordPlanExtents(btr, tr, out Extents3d ext))
                {
                    double rx = Math.Max(Math.Abs(ext.MaxPoint.X), Math.Abs(ext.MinPoint.X));
                    double ry = Math.Max(Math.Abs(ext.MaxPoint.Y), Math.Abs(ext.MinPoint.Y));
                    radiusDu = Math.Max(rx, ry);
                    return radiusDu > 1e-9;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool TryFindBlockDefinition(Transaction tr, Database db, string blockName, out ObjectId blockDefId)
        {
            blockDefId = ObjectId.Null;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            if (bt.Has(blockName))
            {
                blockDefId = bt[blockName];
                return !blockDefId.IsNull;
            }

            foreach (ObjectId oid in bt)
            {
                BlockTableRecord btr = null;
                try { btr = tr.GetObject(oid, OpenMode.ForRead, false) as BlockTableRecord; }
                catch { continue; }
                if (btr == null || btr.IsLayout || btr.IsAnonymous)
                    continue;
                if (string.Equals(btr.Name, blockName, StringComparison.OrdinalIgnoreCase))
                {
                    blockDefId = oid;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetCircleRadiusInBlock(Transaction tr, BlockTableRecord btr, out double radiusDu)
        {
            radiusDu = 0;
            if (btr == null)
                return false;

            foreach (ObjectId id in btr)
            {
                if (id.IsErased)
                    continue;
                Circle circle = null;
                try { circle = tr.GetObject(id, OpenMode.ForRead, false) as Circle; }
                catch { continue; }
                if (circle == null)
                    continue;

                radiusDu = circle.Radius;
                return radiusDu > 1e-9;
            }

            return false;
        }

        private static bool TryGetBlockRecordPlanExtents(BlockTableRecord btr, Transaction tr, out Extents3d ext)
        {
            ext = default;
            if (btr == null)
                return false;

            bool hasExtents = false;
            Extents3d combined = default;

            foreach (ObjectId id in btr)
            {
                if (id.IsErased)
                    continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null)
                    continue;

                try
                {
                    Extents3d e = ent.GeometricExtents;
                    if (!hasExtents)
                    {
                        combined = e;
                        hasExtents = true;
                    }
                    else
                    {
                        combined.AddExtents(e);
                    }
                }
                catch { /* ignore entity without extents */ }
            }

            if (!hasExtents)
                return false;

            ext = combined;
            return true;
        }
    }
}
