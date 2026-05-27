using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Agent;
using autocad_final.Geometry;
using autocad_final.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.Commands
{
    /// <summary>
    /// Places reducer blocks at branch joints where labelled segment diameters change
    /// (e.g. Ø50 → Ø40). Run after <see cref="LabelBranchesCommand"/>.
    /// </summary>
    public class PlaceReducersCommand
    {
        private const string DbgTag = "PlaceReducers";

        private static void DbgStep(Editor ed, ref string lastStep, string step)
        {
            lastStep = step ?? "(null)";
            AgentLog.Write(DbgTag, step);
            try { ed?.WriteMessage("\n[PlaceReducers] " + step); } catch { /* ignore */ }
        }

        private static void DbgInfo(Editor ed, string message)
        {
            AgentLog.Write(DbgTag, message ?? string.Empty);
            try { ed?.WriteMessage("\n[PlaceReducers]   " + message); } catch { /* ignore */ }
        }

        private sealed class BranchLabelSnapshot
        {
            public ObjectId Id { get; set; }
            public Point2d Location { get; set; }
            public double Rotation { get; set; }
            public int NominalMm { get; set; }
        }

        [CommandMethod("PLACEREDUCERS", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var ed = doc.Editor;

            try
            {
                AgentLog.Write(DbgTag, "command start log=" + AgentLog.Path);

                var peo = new PromptEntityOptions("\nSelect a shaft entity to identify zone: ");
                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                {
                    AgentLog.Write(DbgTag, "cancelled shaft pick status=" + per.Status);
                    return;
                }

                AgentLog.Write(DbgTag, "shaft selected id=" + per.ObjectId);
                ed.WriteMessage("\n[PlaceReducers] Shaft selected: " + per.ObjectId);
                ed.WriteMessage("\n[PlaceReducers] Debug log: " + AgentLog.Path);

                if (!TryRunForShaft(doc, db, ed, per.ObjectId, out string resultMessage, out bool success))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        resultMessage ?? "Place reducers failed.",
                        MessageBoxIcon.Warning);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(resultMessage))
                    ed.WriteMessage("\n" + resultMessage);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                AgentLog.Write(DbgTag, "outer ACAD exception: " + ex.ErrorStatus + " / " + ex.Message);
                PaletteCommandErrorUi.ShowDialogThenCommandLine(
                    ed,
                    "Place reducers failed (outer): " + ex.ErrorStatus + " / " + ex.Message,
                    MessageBoxIcon.Error);
            }
            catch (System.Exception ex)
            {
                AgentLog.Write(DbgTag, "outer exception: " + ex.Message);
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        internal static bool TryRunForShaft(
            Document doc,
            Database db,
            Editor ed,
            ObjectId shaftEntityId,
            out string resultMessage,
            out bool success)
        {
            resultMessage = null;
            success = false;
            string lastStep = "(start)";

            try
            {
                DbgStep(ed, ref lastStep, "01 LockDocument");
                DocumentLock docLock = doc.LockDocument();
                try
                {
                    DbgStep(ed, ref lastStep, "02 StartTransaction");
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        DbgStep(ed, ref lastStep, "03 EnsureRegApp");
                        SprinklerXData.EnsureRegApp(tr, db);

                        DbgStep(ed, ref lastStep, "04 ReadShaft");
                        if (!TryGetShaftPoint2d(tr, shaftEntityId, out Point2d shaftPt2d, out string shaftErr))
                        {
                            resultMessage = shaftErr ?? "Could not read shaft entity.";
                            DbgInfo(ed, "FAILED: " + resultMessage);
                            tr.Commit();
                            return false;
                        }
                        DbgInfo(ed, "Shaft at " + shaftPt2d.X.ToString("F2") + "," + shaftPt2d.Y.ToString("F2"));

                        DbgStep(ed, ref lastStep, "05 OpenModelSpace");
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                        DbgStep(ed, ref lastStep, "06 FindZone");
                        if (!TryFindZoneContainingPoint(tr, ms, shaftPt2d, out Polyline zonePolyline, out List<Point2d> zoneRing, out string zoneBoundaryHex))
                        {
                            resultMessage = "Shaft is not inside any zone.";
                            DbgInfo(ed, "FAILED: " + resultMessage);
                            tr.Commit();
                            return false;
                        }
                        DbgInfo(ed, "Zone handle=" + (zoneBoundaryHex ?? "(none)") + " ringVerts=" + (zoneRing?.Count ?? 0));

                        DbgStep(ed, ref lastStep, "07 ResolveMainWidth");
                        double mainW = ResolveMainPipeWidthDu(tr, ms, zoneRing, db);
                        double tickLen = 1.0;
                        try { if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0) tickLen = t; }
                        catch { /* ignore */ }

                        double labelMatchTol = tickLen * 2.5;
                        try
                        {
                            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.50, out double lm) && lm > 0)
                                labelMatchTol = lm;
                        }
                        catch { /* ignore */ }
                        DbgInfo(ed, "mainW=" + mainW.ToString("F3") + " labelMatchTol=" + labelMatchTol.ToString("F3"));

                        DbgStep(ed, ref lastStep, "08 CollectBranches");
                        var branches = CollectBranchPolylinesInZone(tr, ms, zoneRing);
                        DbgInfo(ed, "branches=" + branches.Count);
                        if (branches.Count == 0)
                        {
                            resultMessage = "No branch pipes found in zone.";
                            tr.Commit();
                            return false;
                        }

                        DbgStep(ed, ref lastStep, "09 CollectLabels");
                        var labels = CollectBranchLabelsInZone(tr, ms, zoneRing, zoneBoundaryHex);
                        DbgInfo(ed, "labels=" + labels.Count);
                        if (labels.Count == 0)
                        {
                            resultMessage = "No branch diameter labels found in zone. Run Label branches first.";
                            tr.Commit();
                            return false;
                        }

                        DbgStep(ed, ref lastStep, "10 ResolveReducerBlock");
                        if (!ReducerBlockInsert.TryGetBlockDefinitionId(tr, db, out ObjectId reducerBlockDefId, out string reducerBlockErr))
                        {
                            resultMessage = reducerBlockErr ?? "Reducer block is missing.";
                            DbgInfo(ed, "FAILED: " + resultMessage);
                            tr.Commit();
                            return false;
                        }
                        DbgInfo(ed, "reducerBlockDefId=" + reducerBlockDefId);

                        DbgStep(ed, ref lastStep, "11 ValidateReducerBlock");
                        if (!TryValidateReducerBlockDefinition(tr, reducerBlockDefId, out string blockDefErr))
                        {
                            resultMessage = blockDefErr ?? "Reducer block definition is invalid.";
                            DbgInfo(ed, "FAILED: " + resultMessage);
                            tr.Commit();
                            return false;
                        }

                        DbgStep(ed, ref lastStep, "12 EraseOldReducers");
                        EraseTaggedReducersInZone(tr, ms, zoneBoundaryHex);

                        DbgStep(ed, ref lastStep, "13 EnsureReducerLayer");
                        ObjectId reducerLayerId = SprinklerLayers.EnsureMcdReducerLayer(tr, db);
                        DbgInfo(ed, "reducerLayerId=" + reducerLayerId);

                        DbgStep(ed, ref lastStep, "14 ComputePlacementSizes");
                        double reducerHalf = ComputeReducerHalfArm(db, tickLen, mainW);
                        double radiusDu = ResolveSprinklerHeadRadiusDu(db, tickLen);

                        double elevation = zonePolyline?.Elevation ?? 0;
                        bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHex);
                        int reducersDrawn = 0;
                        int reducersSkipped = 0;
                        int branchIndex = 0;

                        DbgStep(ed, ref lastStep, "15 PlaceReducersLoop");
                        foreach (var branch in branches)
                        {
                            branchIndex++;
                            var pts = branch.pts;
                            int segCount = pts.Count - 1;
                            if (segCount < 1) continue;

                            var segmentNominals = MatchLabelsToSegments(labels, pts, labelMatchTol);

                            for (int j = 1; j < pts.Count - 1; j++)
                            {
                                int segBefore = j - 1;
                                int segAfter = j;
                                if (!segmentNominals[segBefore].HasValue || !segmentNominals[segAfter].HasValue)
                                    continue;
                                if (segmentNominals[segBefore].Value == segmentNominals[segAfter].Value)
                                    continue;

                                int nomBefore = segmentNominals[segBefore].Value;
                                int nomAfter = segmentNominals[segAfter].Value;

                                DbgInfo(ed, "branch " + branchIndex + " joint " + j + ": Ø" + nomBefore + " -> Ø" + nomAfter);

                                Point2d joint = pts[j];
                                Point2d upstreamPt = pts[j - 1];
                                Point2d downstreamPt = pts[j + 1];

                                double beforeW = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nomBefore, mainW);
                                double afterW = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nomAfter, mainW);

                                double dxAlong = joint.X - upstreamPt.X;
                                double dyAlong = joint.Y - upstreamPt.Y;
                                double lenAlong = Math.Sqrt(dxAlong * dxAlong + dyAlong * dyAlong);
                                double ux = lenAlong > 1e-9 ? dxAlong / lenAlong : 1.0;
                                double uy = lenAlong > 1e-9 ? dyAlong / lenAlong : 0.0;

                                bool smallerDownstream = nomAfter < nomBefore;
                                double svx;
                                double svy;
                                if (smallerDownstream)
                                {
                                    svx = downstreamPt.X - joint.X;
                                    svy = downstreamPt.Y - joint.Y;
                                }
                                else
                                {
                                    svx = upstreamPt.X - joint.X;
                                    svy = upstreamPt.Y - joint.Y;
                                }

                                double slen = Math.Sqrt(svx * svx + svy * svy);
                                if (slen > 1e-9)
                                {
                                    svx /= slen;
                                    svy /= slen;
                                }
                                else
                                {
                                    svx = ux;
                                    svy = uy;
                                }

                                double wedgeLen = Math.Max(
                                    reducerHalf * 2.0,
                                    Math.Max(beforeW, afterW) * 1.35 + Math.Abs(beforeW - afterW) * 0.25);
                                double halfLenPre = ComputeReducerWedgeHalfLengthDu(wedgeLen, beforeW, afterW);

                                Point2d radiusPt = new Point2d(
                                    joint.X + svx * radiusDu,
                                    joint.Y + svy * radiusDu);
                                Point2d center = new Point2d(
                                    radiusPt.X - svx * halfLenPre,
                                    radiusPt.Y - svy * halfLenPre);

                                center = PolygonUtils.ClampPointToClosedRing(zoneRing, center, labelMatchTol * 0.5);

                                double suxL = smallerDownstream ? ux : -ux;
                                double suyL = smallerDownstream ? uy : -uy;
                                double rotation = Math.Atan2(suyL, suxL) + Math.PI * 0.5;

                                if (!TryInsertReducerBlock(
                                        tr,
                                        db,
                                        ms,
                                        reducerBlockDefId,
                                        reducerLayerId,
                                        center,
                                        elevation,
                                        rotation,
                                        zonePolyline,
                                        tagZone ? zoneBoundaryHex : null,
                                        ed,
                                        out string insertErr))
                                {
                                    reducersSkipped++;
                                    DbgInfo(ed, "insert skipped: " + (insertErr ?? "unknown"));
                                    continue;
                                }

                                reducersDrawn++;
                            }
                        }

                        DbgStep(ed, ref lastStep, "16 Commit");
                        tr.Commit();
                        success = true;
                        resultMessage = reducersSkipped > 0
                            ? $"Placed {reducersDrawn} reducer(s) from branch labels. Skipped {reducersSkipped} invalid placement(s)."
                            : $"Placed {reducersDrawn} reducer(s) from branch labels.";
                        DbgInfo(ed, "DONE: " + resultMessage);
                        return true;
                    }
                }
                finally
                {
                    docLock?.Dispose();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                resultMessage = "Place reducers failed at step [" + lastStep + "]: " + ex.ErrorStatus + " / " + ex.Message;
                AgentLog.Write(DbgTag, "EXCEPTION " + ex.ErrorStatus + " at " + lastStep + " — " + ex.Message);
                AgentLog.Write(DbgTag, ex.StackTrace ?? string.Empty);
                DbgStep(ed, ref lastStep, "EXCEPTION " + ex.ErrorStatus);
                return false;
            }
            catch (System.Exception ex)
            {
                resultMessage = "Place reducers failed at step [" + lastStep + "]: " + ex.Message;
                AgentLog.Write(DbgTag, "EXCEPTION (managed) at " + lastStep + " — " + ex.Message);
                AgentLog.Write(DbgTag, ex.StackTrace ?? string.Empty);
                DbgStep(ed, ref lastStep, "EXCEPTION (managed)");
                return false;
            }
        }

        private static bool TryInsertReducerBlock(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            ObjectId reducerBlockDefId,
            ObjectId reducerLayerId,
            Point2d center,
            double elevation,
            double rotation,
            Polyline zonePolyline,
            string zoneBoundaryHex,
            Editor ed,
            out string errorMessage)
        {
            errorMessage = null;
            if (ms == null || reducerBlockDefId.IsNull || reducerLayerId.IsNull)
            {
                errorMessage = "null ms or invalid block/layer id";
                return false;
            }
            if (!IsFinite(center.X) || !IsFinite(center.Y) || !IsFinite(elevation) || !IsFinite(rotation))
            {
                errorMessage = "non-finite insert point or rotation";
                return false;
            }

            try
            {
                DbgInfo(ed, "insert at " + center.X.ToString("F2") + "," + center.Y.ToString("F2") + " rot=" + rotation.ToString("F3"));

                var insPt = new Point3d(center.X, center.Y, elevation);
                var blockRef = new BlockReference(insPt, reducerBlockDefId);
                blockRef.SetDatabaseDefaults(db);
                blockRef.LayerId = reducerLayerId;
                blockRef.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                blockRef.Rotation = rotation;

                try
                {
                    if (zonePolyline != null && zonePolyline.Normal.Length > 1e-9)
                        blockRef.Normal = zonePolyline.Normal;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    DbgInfo(ed, "normal skipped: " + ex.ErrorStatus);
                }

                if (!string.IsNullOrEmpty(zoneBoundaryHex))
                    SprinklerXData.ApplyZoneBoundaryTag(blockRef, zoneBoundaryHex);

                ms.AppendEntity(blockRef);
                tr.AddNewlyCreatedDBObject(blockRef, true);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = ex.ErrorStatus + " / " + ex.Message;
                AgentLog.Write(DbgTag, "insert ACAD error: " + errorMessage);
                DbgInfo(ed, "insert ACAD error: " + errorMessage);
                return false;
            }
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool TryValidateReducerBlockDefinition(Transaction tr, ObjectId blockDefId, out string errorMessage)
        {
            errorMessage = null;
            if (blockDefId.IsNull || !blockDefId.IsValid)
            {
                errorMessage = "Reducer block definition id is invalid.";
                return false;
            }

            try
            {
                var btr = tr.GetObject(blockDefId, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr == null || btr.IsErased)
                {
                    errorMessage = "Reducer block definition was erased.";
                    return false;
                }

                if (btr.IsLayout || btr.IsAnonymous)
                {
                    errorMessage = "Reducer block must be a named block definition (\"reducer\" or \"mcd-reducer\").";
                    return false;
                }

                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = "Could not open reducer block definition: " + ex.Message;
                return false;
            }
        }

        private static double ResolveSprinklerHeadRadiusDu(Database db, double tickLen)
        {
            double headRadiusM = 0.15;
            try { headRadiusM = RuntimeSettings.Load().SprinklerHeadRadiusM; }
            catch { /* ignore */ }

            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, headRadiusM, out double radiusDu) && radiusDu > 0)
                    return radiusDu;
            }
            catch { /* ignore */ }

            return Math.Max(tickLen * 0.5, 1e-6);
        }

        private static bool TryGetShaftPoint2d(Transaction tr, ObjectId shaftEntityId, out Point2d shaftPt2d, out string errorMessage)
        {
            shaftPt2d = default;
            errorMessage = null;

            Entity shaftEnt = null;
            try { shaftEnt = tr.GetObject(shaftEntityId, OpenMode.ForRead, false) as Entity; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = "Could not open shaft entity: " + ex.Message;
                return false;
            }

            if (shaftEnt == null)
            {
                errorMessage = "Could not open shaft entity.";
                return false;
            }

            try
            {
                Point3d shaftCenter;
                if (shaftEnt is Circle c)
                    shaftCenter = c.Center;
                else if (shaftEnt is BlockReference br)
                    shaftCenter = br.Position;
                else if (shaftEnt is DBPoint pt)
                    shaftCenter = pt.Position;
                else if (shaftEnt.Bounds.HasValue)
                {
                    var b = shaftEnt.Bounds.Value;
                    shaftCenter = new Point3d(
                        (b.MinPoint.X + b.MaxPoint.X) * 0.5,
                        (b.MinPoint.Y + b.MaxPoint.Y) * 0.5,
                        (b.MinPoint.Z + b.MaxPoint.Z) * 0.5);
                }
                else
                {
                    errorMessage = "Unsupported shaft entity type.";
                    return false;
                }

                shaftPt2d = new Point2d(shaftCenter.X, shaftCenter.Y);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = "Could not read shaft location: " + ex.Message;
                return false;
            }
        }

        private static bool TryFindZoneContainingPoint(
            Transaction tr,
            BlockTableRecord ms,
            Point2d pt,
            out Polyline zonePolyline,
            out List<Point2d> zoneRing,
            out string zoneBoundaryHex)
        {
            zonePolyline = null;
            zoneRing = null;
            zoneBoundaryHex = null;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null) continue;
                if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer) && !SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer))
                    continue;

                if (!TryPolylineToRing(pl, out List<Point2d> ring) || ring.Count < 3)
                    continue;
                if (!PolygonUtils.PointInPolygon(ring, pt))
                    continue;

                zonePolyline = pl;
                zoneRing = ring;
                SprinklerXData.TryGetZoneBoundaryHandle(pl, out zoneBoundaryHex);
                return true;
            }

            return false;
        }

        private static int?[] MatchLabelsToSegments(
            List<BranchLabelSnapshot> labels,
            List<Point2d> pts,
            double matchTol)
        {
            int segCount = Math.Max(0, pts.Count - 1);
            var result = new int?[segCount];
            var usedLabels = new HashSet<ObjectId>();

            for (int si = 0; si < segCount; si++)
            {
                var segA = pts[si];
                var segB = pts[si + 1];
                var mid = new Point2d((segA.X + segB.X) * 0.5, (segA.Y + segB.Y) * 0.5);

                double bestDist = double.MaxValue;
                int bestNominal = 0;
                ObjectId bestId = ObjectId.Null;

                for (int li = 0; li < labels.Count; li++)
                {
                    var label = labels[li];
                    if (usedLabels.Contains(label.Id)) continue;

                    var labelPt = label.Location;
                    var closest = ClosestOnSegment(segA, segB, labelPt);
                    double dMid = closest.GetDistanceTo(labelPt);
                    double dSeg = DistancePointToSegment(labelPt, segA, segB);

                    double d = Math.Min(dMid, dSeg);
                    if (d > matchTol)
                        continue;

                    if (!LabelRotationMatchesSegment(label.Rotation, segA, segB))
                        d += matchTol * 0.25;

                    if (d >= bestDist)
                        continue;

                    bestDist = d;
                    bestNominal = label.NominalMm;
                    bestId = label.Id;
                }

                if (!bestId.IsNull)
                {
                    result[si] = bestNominal;
                    usedLabels.Add(bestId);
                    continue;
                }

                for (int li = 0; li < labels.Count; li++)
                {
                    var label = labels[li];
                    if (usedLabels.Contains(label.Id)) continue;

                    double d = mid.GetDistanceTo(label.Location);
                    if (d > matchTol * 1.5 || d >= bestDist)
                        continue;

                    bestDist = d;
                    bestNominal = label.NominalMm;
                    bestId = label.Id;
                }

                if (!bestId.IsNull)
                {
                    result[si] = bestNominal;
                    usedLabels.Add(bestId);
                }
            }

            return result;
        }

        private static bool LabelRotationMatchesSegment(double labelRot, Point2d segA, Point2d segB)
        {
            double dx = segB.X - segA.X;
            double dy = segB.Y - segA.Y;
            double segRot = Math.Atan2(dy, dx);
            if (segRot > Math.PI * 0.5) segRot -= Math.PI;
            else if (segRot < -Math.PI * 0.5) segRot += Math.PI;

            double delta = Math.Abs(NormalizeAngle(labelRot - segRot));
            return delta < Math.PI * 0.35 || Math.Abs(delta - Math.PI) < Math.PI * 0.35;
        }

        private static double NormalizeAngle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static List<BranchLabelSnapshot> CollectBranchLabelsInZone(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHex)
        {
            var labels = new List<BranchLabelSnapshot>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                MText mt = null;
                try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                catch { continue; }
                if (mt == null) continue;
                if (!string.Equals(mt.Layer, SprinklerLayers.BranchLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SprinklerXData.IsTaggedBranchPipeScheduleLabel(mt)) continue;

                if (!string.IsNullOrEmpty(zoneBoundaryHex))
                {
                    if (SprinklerXData.TryGetZoneBoundaryHandle(mt, out string h) &&
                        string.Equals(h, zoneBoundaryHex, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryCreateLabelSnapshot(mt, out BranchLabelSnapshot tagged))
                            labels.Add(tagged);
                        continue;
                    }
                }

                if (!TryCreateLabelSnapshot(mt, out BranchLabelSnapshot snap))
                    continue;

                if (zoneRing != null && zoneRing.Count >= 3 && !PolygonUtils.PointInPolygon(zoneRing, snap.Location))
                    continue;

                labels.Add(snap);
            }

            return labels;
        }

        private static bool TryCreateLabelSnapshot(MText mt, out BranchLabelSnapshot snapshot)
        {
            snapshot = null;
            if (mt == null) return false;

            try
            {
                if (!TryParseNominalMmFromLabel(mt, out int nominalMm))
                    return false;

                var loc = mt.Location;
                snapshot = new BranchLabelSnapshot
                {
                    Id = mt.ObjectId,
                    Location = new Point2d(loc.X, loc.Y),
                    Rotation = mt.Rotation,
                    NominalMm = nominalMm
                };
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static List<(ObjectId id, Polyline pl, List<Point2d> pts)> CollectBranchPolylinesInZone(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing)
        {
            var branches = new List<(ObjectId id, Polyline pl, List<Point2d> pts)>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                if (pl == null) continue;
                string ln = pl.Layer ?? "";
                bool isBranch = string.Equals(ln, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(ln, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                if (!isBranch) continue;
                if (!TryPolylineToRing(pl, out List<Point2d> pts) || pts.Count < 2) continue;

                bool hasInside = false;
                foreach (var p in pts)
                {
                    if (PolygonUtils.PointInPolygon(zoneRing, p))
                    {
                        hasInside = true;
                        break;
                    }
                }

                if (!hasInside) continue;
                branches.Add((id, pl, pts));
            }

            return branches;
        }

        private static double ResolveMainPipeWidthDu(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing, Database db)
        {
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                if (pl == null) continue;
                if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer)) continue;
                if (SprinklerXData.IsTaggedTrunkCap(pl)) continue;
                if (!TryPolylineToRing(pl, out List<Point2d> pts) || pts.Count < 2) continue;

                bool hasInside = false;
                foreach (var p in pts)
                {
                    if (PolygonUtils.PointInPolygon(zoneRing, p))
                    {
                        hasInside = true;
                        break;
                    }
                }

                if (!hasInside) continue;

                if (TryGetPolylineUniformWidth(pl, out double uniformW) && uniformW > 1e-9)
                    return uniformW;
                if (TryGetPolylineAnyWidth(pl, out double anyW) && anyW > 1e-9)
                    return anyW;
                break;
            }

            return NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
        }

        private static bool TryGetPolylineUniformWidth(Polyline pl, out double width)
        {
            width = 0;
            if (pl == null) return false;
            try
            {
                width = pl.ConstantWidth;
                return width > 1e-12;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPolylineAnyWidth(Polyline pl, out double width)
        {
            width = 0;
            if (pl == null) return false;
            try
            {
                int n = pl.NumberOfVertices;
                if (n <= 0) return false;

                double best = 0;
                int limit = pl.Closed ? n : Math.Max(0, n - 1);
                for (int i = 0; i < limit; i++)
                {
                    double sw = 0, ew = 0;
                    try { sw = pl.GetStartWidthAt(i); } catch { /* ignore */ }
                    try { ew = pl.GetEndWidthAt(i); } catch { /* ignore */ }
                    best = Math.Max(best, Math.Max(sw, ew));
                }

                if (best > 1e-12)
                {
                    width = best;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseNominalMmFromLabel(MText mt, out int nominalMm)
        {
            nominalMm = 0;
            if (mt == null) return false;

            string raw = null;
            try { raw = mt.Contents; }
            catch { return false; }

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string cleaned = Regex.Replace(raw, @"\\[^\\;]*;|\\P|\{[^}]*\}", string.Empty);
            cleaned = cleaned.Replace("Ø", string.Empty);
            cleaned = cleaned.Replace("%%c", string.Empty);
            cleaned = cleaned.Replace("%%C", string.Empty);
            cleaned = cleaned.Replace("DIA", string.Empty);
            cleaned = cleaned.Replace("dia", string.Empty);
            cleaned = cleaned.Trim();

            var m = Regex.Match(cleaned, @"(\d{2,3})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out nominalMm) && nominalMm > 0)
                return true;

            return int.TryParse(cleaned, out nominalMm) && nominalMm > 0;
        }

        private static void EraseTaggedReducersInZone(Transaction tr, BlockTableRecord ms, string boundaryHandleHex)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex))
                return;

            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                if (ent == null) continue;
                if (!IsReducerSymbolLayerName(ent.Layer))
                    continue;
                if (!(ent is BlockReference))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                toErase.Add(id);
            }

            foreach (var id in toErase)
            {
                try { var e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; e?.Erase(); } catch { }
            }
        }

        private static bool IsReducerSymbolLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return false;
            return string.Equals(layerName, SprinklerLayers.McdReducerLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerLayers.BranchReducerLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static double ComputeReducerHalfArm(Database db, double tickLen, double mainTrunkW)
        {
            double a = tickLen * 0.5;
            double b = mainTrunkW * 0.45;
            double c = SprinklerLayers.BoundaryPolylineConstantWidth(db) * 0.14;
            double d = 0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.08, out double m) && m > 0)
                    d = m * 0.55;
            }
            catch { /* ignore */ }

            return Math.Max(Math.Max(Math.Max(a, b), c), d);
        }

        private static double ComputeReducerWedgeHalfLengthDu(
            double lengthDu,
            double bigPipeWidthDu,
            double smallPipeWidthDu)
        {
            double bigHalf = Math.Max(bigPipeWidthDu * 0.5, 1e-6);
            double smallHalf = Math.Max(smallPipeWidthDu * 0.5, 1e-6);
            return Math.Max(
                lengthDu * 0.5,
                Math.Max(Math.Max(bigHalf, smallHalf) * 0.9, 1e-6));
        }

        private static bool TryPolylineToRing(Polyline pl, out List<Point2d> pts)
        {
            pts = new List<Point2d>();
            if (pl == null) return false;

            try
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    pts.Add(pl.GetPoint2dAt(i));
                return pts.Count > 0;
            }
            catch
            {
                pts = null;
                return false;
            }
        }

        private static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            return ClosestOnSegment(a, b, p).GetDistanceTo(p);
        }

        private static Point2d ClosestOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }
    }
}
