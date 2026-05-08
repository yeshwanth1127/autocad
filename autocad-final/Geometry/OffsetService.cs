using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;

namespace autocad_final.Geometry
{
    /// <summary>Inward offset of a closed zone polyline to an inner ring (drawing units).</summary>
    public static class OffsetService
    {
        public static bool TryBuildInwardOffsetRing(
            Polyline source,
            double offsetDrawingUnits,
            out List<Point2d> offsetRing,
            out string error)
        {
            offsetRing = null;
            error = null;

            if (source == null)
            {
                error = "Invalid boundary input.";
                return false;
            }

            var sourceRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(source);
            if (sourceRing == null || sourceRing.Count < 3)
            {
                error = "Selected boundary is invalid.";
                return false;
            }

            if (offsetDrawingUnits <= 0)
            {
                error = "Offset distance must be greater than zero.";
                return false;
            }

            PolygonUtils.GetBoundingBox(sourceRing, out double minX, out double minY, out double maxX, out double maxY);
            double w = Math.Max(0, maxX - minX);
            double h = Math.Max(0, maxY - minY);
            double minDim = Math.Min(w, h);

            // If the polygon is narrower than ~2× offset, a valid inward offset is often impossible.
            // We'll still attempt, but we keep this for diagnostics / fallback search bounds.
            double maxReasonableOffset = Math.Max(1.0, minDim * 0.49);

            double sourceArea = Math.Abs(PolygonUtils.SignedArea(sourceRing));
            List<Point2d> best = null;
            double bestArea = 0;

            string lastEx = null;

            bool TryAtOffset(double od)
            {
                bool any = false;
                foreach (double signedDistance in new[] { -od, od })
                {
                    DBObjectCollection curves = null;
                    try
                    {
                        curves = source.GetOffsetCurves(signedDistance);
                        foreach (DBObject obj in curves)
                        {
                            if (!(obj is Polyline candidate) || !candidate.Closed || candidate.NumberOfVertices < 3)
                                continue;

                            var candidateRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(candidate);
                            if (candidateRing == null || candidateRing.Count < 3)
                                continue;

                            double candidateArea = Math.Abs(PolygonUtils.SignedArea(candidateRing));
                            if (candidateArea <= 0 || candidateArea >= sourceArea)
                                continue;
                            if (!PolygonUtils.IsRingInside(sourceRing, candidateRing))
                                continue;

                            any = true;
                            if (best == null || candidateArea > bestArea)
                            {
                                best = candidateRing;
                                bestArea = candidateArea;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        lastEx = ex.Message;
                    }
                    finally
                    {
                        if (curves != null)
                        {
                            foreach (DBObject obj in curves)
                            {
                                try { obj.Dispose(); } catch { /* ignore */ }
                            }
                        }
                    }
                }
                return any;
            }

            // Primary attempt at requested offset.
            AgentLog.Write("OffsetService", "TryAtOffset primary=" + offsetDrawingUnits.ToString("G6", CultureInfo.InvariantCulture));
            TryAtOffset(offsetDrawingUnits);

            // Fallback: if 1500 is too large for a narrow boundary, try smaller offsets down to ~10% of requested.
            if (best == null || best.Count < 3)
            {
                double start = Math.Min(offsetDrawingUnits, maxReasonableOffset);
                double min = Math.Max(1.0, offsetDrawingUnits * 0.10);
                AgentLog.Write("OffsetService", "fallback loop start=" + start.ToString("G6", CultureInfo.InvariantCulture) + " min=" + min.ToString("G6", CultureInfo.InvariantCulture));
                for (double od = start; od >= min; od *= 0.75)
                {
                    // Skip if we're effectively retrying the same value.
                    if (Math.Abs(od - offsetDrawingUnits) < 1e-9)
                        continue;
                    AgentLog.Write("OffsetService", "TryAtOffset od=" + od.ToString("G6", CultureInfo.InvariantCulture));
                    TryAtOffset(od);
                    if (best != null && best.Count >= 3)
                        break;
                }
            }

            if (best == null || best.Count < 3)
            {
                error =
                    "Could not create a valid inward offset boundary.\n" +
                    "Tried offset=" + offsetDrawingUnits.ToString("F0") + " (drawing units)." +
                    (minDim > 0 && offsetDrawingUnits > maxReasonableOffset
                        ? " Boundary is narrow (min bbox dimension " + minDim.ToString("F0") + "), so this offset is likely too large."
                        : string.Empty) +
                    (!string.IsNullOrWhiteSpace(lastEx) ? " Offset error: " + lastEx : string.Empty);
                AgentLog.Write("OffsetService", "fail lastEx=" + (lastEx ?? ""));
                return false;
            }

            offsetRing = best;
            AgentLog.Write("OffsetService", "success verts=" + offsetRing.Count.ToString(CultureInfo.InvariantCulture));
            return true;
        }
    }
}
