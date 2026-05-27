using System;
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
        /// Resolves plan-view sprinkler symbol radius from the block definition, settings, or defaults.
        /// </summary>
        public static double ResolveSprinklerSymbolRadiusDu(Transaction tr, Database db)
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
        /// Block insert point so the wide face sits on the sprinkler head circle toward the smaller pipe.
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
            return new Point2d(
                joint.X + svx * (r - h),
                joint.Y + svy * (r - h));
        }

        /// <summary>World point where a reducer face meets the head circle (toward smaller pipe).</summary>
        public static Point2d ComputeWideFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu);
        }

        /// <summary>World point where the reducer narrow face meets the head circle.</summary>
        public static Point2d ComputeNarrowFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
        {
            return ComputeFaceContactOnHead(joint, towardSmallerX, towardSmallerY, sprinklerRadiusDu);
        }

        private static Point2d ComputeFaceContactOnHead(
            Point2d joint,
            double towardSmallerX,
            double towardSmallerY,
            double sprinklerRadiusDu)
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
            return new Point2d(
                joint.X + svx * r,
                joint.Y + svy * r);
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
