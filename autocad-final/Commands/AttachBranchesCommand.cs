using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
// Runtime also defines Exception — keep unambiguous for catch blocks.
using Exception = System.Exception;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Agent;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.Agent.Planning.Validators;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class AttachBranchesCommand
    {
        [CommandMethod("SPRINKLERATTACHBRANCHES", CommandFlags.Modal)]
        public void AttachBranches()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelect(ed, out var zone, out ObjectId boundaryEntityId))
                return;

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                boundaryHandleHex = tr0.GetObject(boundaryEntityId, OpenMode.ForRead).Handle.ToString();
                tr0.Commit();
            }

            try
            {
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }
                if (!TryAttachBranchesForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string err, routeBranchPipesFromConnectorFirst: false))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err ?? "Attach branches failed.", MessageBoxIcon.Warning);
                    return;
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        [CommandMethod("SPRINKLERROUTEBRANCHPIPES", CommandFlags.Modal)]
        public void RouteBranchPipes()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
            {
                if (!string.IsNullOrWhiteSpace(shaftErr))
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, MessageBoxIcon.Warning);
                return;
            }

            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "No zone is assigned to this shaft.\nUse \"Assign shaft to zone\" to link this shaft to a zone first.",
                    MessageBoxIcon.Warning);
                return;
            }

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                boundaryHandleHex = tr0.GetObject(boundaryEntityId, OpenMode.ForRead).Handle.ToString();
                tr0.Commit();
            }

            try
            {
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }
                if (!TryAttachBranchesForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string err, routeBranchPipesFromConnectorFirst: false))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err ?? "Route branch pipes failed.", MessageBoxIcon.Warning);
                    return;
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        [CommandMethod("SPRINKLERROUTEBRANCHPIPES2", CommandFlags.Modal)]
        public void RouteBranchPipes2()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
            {
                if (!string.IsNullOrWhiteSpace(shaftErr))
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, MessageBoxIcon.Warning);
                return;
            }

            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "No zone is assigned to this shaft.\nUse \"Assign shaft to zone\" to link this shaft to a zone first.",
                    MessageBoxIcon.Warning);
                return;
            }

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                boundaryHandleHex = tr0.GetObject(boundaryEntityId, OpenMode.ForRead).Handle.ToString();
                tr0.Commit();
            }

            try
            {
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }
                if (!TryAttachBranchesForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string err, routeBranchPipesFromConnectorFirst: true))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err ?? "Route branch pipes 2 failed.", MessageBoxIcon.Warning);
                    return;
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }


        [CommandMethod("SPRINKLERPLACEREDUCERS", CommandFlags.Modal)]
        public void PlaceReducers()
        {
            PlaceReducersCore(routeBranchPipesFromConnectorFirst: false);
        }

        [CommandMethod("SPRINKLERPLACEREDUCERSCONNECTORFIRST", CommandFlags.Modal)]
        public void PlaceReducersConnectorFirst()
        {
            PlaceReducersCore(routeBranchPipesFromConnectorFirst: true);
        }

        /// <summary>
        /// Prompts for the main pipe polyline, builds a work envelope from it, resolves branches from that trunk, and places reducer blocks.
        /// </summary>
        private void PlaceReducersCore(bool routeBranchPipesFromConnectorFirst)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            var opt = new PromptEntityOptions("\nSelect main pipe polyline: ");
            opt.SetRejectMessage("\nEntity must be a polyline.");
            opt.AddAllowedClass(typeof(Polyline), true);
            var per = ed.GetEntity(opt);
            if (per.Status != PromptStatus.OK)
                return;

            ObjectId mainPipeId = per.ObjectId;
            Polyline stubZone = null;

            try
            {
                double marginDu = 35.0;
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 35.0, out double mdu) && mdu > 0)
                        marginDu = mdu;
                }
                catch { /* ignore */ }

                List<Point2d> zoneRing;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    Polyline mainPl = null;
                    try { mainPl = tr.GetObject(mainPipeId, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { mainPl = null; }
                    if (mainPl == null)
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid polyline.", MessageBoxIcon.Warning);
                        return;
                    }

                    if (!SprinklerLayers.IsMainPipeLayerName(mainPl.Layer))
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "Selected polyline must be on a main pipe layer (for example \"MCD - main pipe\").",
                            MessageBoxIcon.Warning);
                        return;
                    }

                    zoneRing = BuildExpandedRectangleRingAroundPolyline(mainPl, marginDu);
                    stubZone = CreateStubZonePolylineForElevation(mainPl);
                    tr.Commit();
                }

                if (!TryPlaceReducersForZone(
                        doc,
                        db,
                        stubZone,
                        zoneRing,
                        zoneBoundaryHandleHex: null,
                        routeBranchPipesFromConnectorFirst,
                        mainPipeId,
                        out string err))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err ?? "Place reducers failed.", MessageBoxIcon.Warning);
                    return;
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { stubZone?.Dispose(); } catch { /* ignore */ }
            }
        }

        [CommandMethod("SPRINKLERLABELMAINPIPE", CommandFlags.Modal)]
        public void LabelMainPipe()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
            {
                if (!string.IsNullOrWhiteSpace(shaftErr))
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, MessageBoxIcon.Warning);
                return;
            }

            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "No zone is assigned to this shaft.\nUse \"Assign shaft to zone\" to link this shaft to a zone first.",
                    MessageBoxIcon.Warning);
                return;
            }

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                boundaryHandleHex = tr0.GetObject(boundaryEntityId, OpenMode.ForRead).Handle.ToString();
                tr0.Commit();
            }

            try
            {
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }
                if (!TryLabelMainPipeForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string err))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err ?? "Label main pipe failed.", MessageBoxIcon.Warning);
                    return;
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        private static bool TryLabelMainPipeForZone(
            Document doc,
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            out string errorMessage)
        {
            errorMessage = null;
            if (!TryComputeLateralsForZone(
                    db,
                    zone,
                    zoneRing,
                    zoneBoundaryHandleHex,
                    routeBranchPipesFromConnectorFirst: false,
                    ObjectId.Null,
                    out List<Lateral> laterals,
                    out bool trunkHorizontal,
                    out double trunkAxis,
                    out double trunkMin,
                    out double trunkMax,
                    out bool downstreamPositive,
                    out double mainW,
                    out double clusterTol,
                    out _,
                    out errorMessage))
                return false;

            // Find the connector pipe in the zone so we can label it too.
            List<Point2d> connPath = null;
            TryGetConnectorPathInZone(db, zoneRing, out connPath, out _);

            // Total sprinkler load for the zone — the connector carries all of it.
            int totalZoneLoad = 0;
            foreach (var lat in laterals)
            {
                totalZoneLoad += (lat.Sprinklers?.Count ?? 0) + (lat.SubSprinklers?.Count ?? 0);
            }

            double tickLen = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0)
                    tickLen = t;
            }
            catch { /* ignore */ }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);
                EraseMainPipeScheduleLabelsInZone(tr, db, zoneBoundaryHandleHex);
                if (!TryDrawMainPipeScheduleLabels(
                        db,
                        tr,
                        zone,
                        laterals,
                        trunkHorizontal,
                        trunkAxis,
                        trunkMin,
                        trunkMax,
                        downstreamPositive,
                        clusterTol,
                        tickLen,
                        zoneBoundaryHandleHex,
                        out errorMessage))
                    return false;

                EraseMainPipeScheduleLabelsNearBranchTapsOnMain(
                    tr,
                    db,
                    zoneBoundaryHandleHex,
                    laterals,
                    clusterTol,
                    trunkHorizontal);

                if (connPath != null && connPath.Count >= 2 && totalZoneLoad > 0)
                {
                    TryDrawConnectorPipeScheduleLabels(
                        db, tr, zone, totalZoneLoad, connPath,
                        clusterTol, tickLen, zoneBoundaryHandleHex);
                }

                tr.Commit();
            }

            return true;
        }

        private static void ErasePriorBranchPipingForZone(Transaction tr, Database db, string boundaryHandleHex, List<Point2d> zoneRing)
        {
            if (tr == null || db == null) return;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (ent.IsErased) continue;

                string layer = ent.Layer ?? string.Empty;
                bool isBranchLayer =
                    string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.BranchMarkerLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                if (!isBranchLayer) continue;

                // Prefer tag-based match; fall back to spatial for untagged leftovers.
                bool shouldErase = false;
                if (!string.IsNullOrEmpty(boundaryHandleHex) &&
                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) &&
                    string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                {
                    shouldErase = true;
                }
                else if (zoneRing != null && zoneRing.Count >= 3)
                {
                    shouldErase = EntityHasSampleInsideZone(ent, zoneRing);
                }

                if (!shouldErase) continue;
                ent.UpgradeOpen();
                try { ent.Erase(); } catch { /* ignore */ }
            }
        }

        private static bool EntityHasSampleInsideZone(Entity ent, List<Point2d> zoneRing)
        {
            if (ent is Polyline pl) return PolylineHasSampleInsideZone(pl, zoneRing);
            if (ent is Line ln)
            {
                var a = ln.StartPoint; var b = ln.EndPoint;
                return PointInPolygon(zoneRing, new Point2d(a.X, a.Y)) ||
                       PointInPolygon(zoneRing, new Point2d(b.X, b.Y));
            }
            if (ent is MText mt) return PointInPolygon(zoneRing, new Point2d(mt.Location.X, mt.Location.Y));
            return false;
        }

        private static void EraseMainPipeScheduleLabelsInZone(Transaction tr, Database db, string boundaryHandleHex)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex))
                return;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                MText mt = null;
                try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (mt == null) continue;
                if (!string.Equals(mt.Layer, SprinklerLayers.McdLabelLayer, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(mt, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                toErase.Add(id);
            }

            foreach (var id in toErase)
            {
                if (id.IsErased) continue;
                Entity e = null;
                try { e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                e?.Erase();
            }
        }

        /// <summary>
        /// Removes main-pipe schedule MText at trunk branch taps (junctions). Labels are offset perpendicular to
        /// the main, so we match by distance along the main (same as mid-on-tap logic in TryDrawMainPipeScheduleLabels).
        /// </summary>
        private static void EraseMainPipeScheduleLabelsNearBranchTapsOnMain(
            Transaction tr,
            Database db,
            string boundaryHandleHex,
            List<Lateral> laterals,
            double clusterTol,
            bool trunkHorizontal)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex) || laterals == null || laterals.Count == 0)
                return;

            var taps = new List<Point2d>();
            foreach (var lat in laterals)
            {
                if (lat.FromConnectorFeed)
                    continue;
                int n = (lat.Sprinklers?.Count ?? 0) + (lat.SubSprinklers?.Count ?? 0);
                if (n <= 0)
                    continue;
                taps.Add(lat.AttachPoint);
            }

            if (taps.Count == 0)
                return;

            double bw = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double tapTol = Math.Max(clusterTol * 0.75, bw * 0.03);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                MText mt = null;
                try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (mt == null) continue;
                if (!string.Equals(mt.Layer, SprinklerLayers.McdLabelLayer, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!SprinklerXData.IsTaggedMainPipeScheduleLabel(mt))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(mt, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ins = new Point2d(mt.Location.X, mt.Location.Y);
                bool nearTap = false;
                for (int ti = 0; ti < taps.Count; ti++)
                {
                    double alongDist =
                        trunkHorizontal
                            ? Math.Abs(ins.X - taps[ti].X)
                            : Math.Abs(ins.Y - taps[ti].Y);
                    if (alongDist <= tapTol)
                    {
                        nearTap = true;
                        break;
                    }
                }

                if (nearTap)
                    toErase.Add(id);
            }

            foreach (var id in toErase)
            {
                if (id.IsErased) continue;
                Entity e = null;
                try { e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                e?.Erase();
            }
        }

        private static bool TryDrawMainPipeScheduleLabels(
            Database db,
            Transaction tr,
            Polyline zone,
            List<Lateral> laterals,
            bool trunkHorizontal,
            double trunkAxis,
            double trunkMin,
            double trunkMax,
            bool downstreamPositive,
            double clusterTol,
            double tickLen,
            string zoneBoundaryHandleHex,
            out string errorMessage)
        {
            errorMessage = null;

            var raw = new List<(double along, int load)>();
            foreach (var lat in laterals)
            {
                if (lat.FromConnectorFeed)
                    continue;
                int n = (lat.Sprinklers?.Count ?? 0) + (lat.SubSprinklers?.Count ?? 0);
                if (n <= 0)
                    continue;
                double along = trunkHorizontal ? lat.AttachPoint.X : lat.AttachPoint.Y;
                raw.Add((along, n));
            }

            if (raw.Count == 0)
                return true;

            raw.Sort((a, b) => a.along.CompareTo(b.along));

            var merged = new List<(double along, int load)>();
            foreach (var item in raw)
            {
                if (merged.Count == 0)
                {
                    merged.Add(item);
                    continue;
                }

                var last = merged[merged.Count - 1];
                if (Math.Abs(item.along - last.along) <= clusterTol)
                    merged[merged.Count - 1] = (last.along, last.load + item.load);
                else
                    merged.Add(item);
            }

            int m = merged.Count;
            var upAlong = new double[m];
            var w = new int[m];
            for (int i = 0; i < m; i++)
            {
                int ui = downstreamPositive ? i : (m - 1 - i);
                upAlong[ui] = merged[i].along;
                w[ui] = merged[i].load;
            }

            double upEnd = downstreamPositive ? trunkMin : trunkMax;
            double dnEnd = downstreamPositive ? trunkMax : trunkMin;

            var segPlans = new List<(double a0, double a1, int totalPlan)>();

            int sumAll = 0;
            for (int i = 0; i < m; i++)
                sumAll += w[i];
            segPlans.Add((upEnd, upAlong[0], sumAll));

            for (int k = 0; k < m - 1; k++)
            {
                int segSum = 0;
                for (int j = k + 1; j < m; j++)
                    segSum += w[j];
                segPlans.Add((upAlong[k], upAlong[k + 1], segSum));
            }

            segPlans.Add((upAlong[m - 1], dnEnd, w[m - 1]));

            foreach (var (_, _, tp) in segPlans)
            {
                if (tp > 0 && !NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(tp, out _))
                {
                    errorMessage =
                        "Main pipe schedule: could not resolve nominal pipe for cumulative plan sprinkler count " +
                        tp + ".";
                    return false;
                }
            }

            double boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double tapHitTol = Math.Max(clusterTol * 0.75, boundaryW * 0.03);
            var tapPtsOnMain = new List<Point2d>(m);
            for (int ti = 0; ti < m; ti++)
            {
                double a = merged[ti].along;
                tapPtsOnMain.Add(trunkHorizontal ? new Point2d(a, trunkAxis) : new Point2d(trunkAxis, a));
            }

            double labelOffsetDu = Math.Max(tickLen * 0.65, boundaryW * 0.08);
            double labelTextHeight = Math.Max(boundaryW * 0.22, tickLen * 0.55);
            bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHandleHex);

            ObjectId labelLayerId = SprinklerLayers.EnsureMcdLabelLayer(tr, db);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var (a0, b0, totalPlan) in segPlans)
            {
                if (totalPlan <= 0)
                    continue;
                if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(totalPlan, out int nominalMm))
                {
                    errorMessage =
                        "Main pipe schedule: could not resolve nominal pipe for cumulative plan sprinkler count " +
                        totalPlan + ".";
                    return false;
                }

                double lo = Math.Min(a0, b0);
                double hi = Math.Max(a0, b0);
                if (hi - lo < clusterTol * 0.25)
                    continue;

                Point2d mid;
                double dx;
                double dy;
                if (trunkHorizontal)
                {
                    double mx = 0.5 * (lo + hi);
                    mid = new Point2d(mx, trunkAxis);
                    dx = hi - lo;
                    dy = 0;
                }
                else
                {
                    double my = 0.5 * (lo + hi);
                    mid = new Point2d(trunkAxis, my);
                    dx = 0;
                    dy = hi - lo;
                }

                bool midOnBranchTap = false;
                for (int ti = 0; ti < tapPtsOnMain.Count; ti++)
                {
                    if (mid.GetDistanceTo(tapPtsOnMain[ti]) <= tapHitTol)
                    {
                        midOnBranchTap = true;
                        break;
                    }
                }

                if (midOnBranchTap)
                    continue;

                double len = Math.Sqrt(dx * dx + dy * dy);
                double px = 0, py = 1;
                if (len > 1e-9)
                {
                    px = -dy / len;
                    py = dx / len;
                }

                var pA = new Point2d(mid.X + px * labelOffsetDu, mid.Y + py * labelOffsetDu);
                var pB = new Point2d(mid.X - px * labelOffsetDu, mid.Y - py * labelOffsetDu);
                var ins2d = pA.Y >= pB.Y ? pA : pB;

                var mt = new MText();
                mt.SetDatabaseDefaults(db);
                mt.LayerId = labelLayerId;
                mt.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                mt.Location = new Point3d(ins2d.X, ins2d.Y, zone.Elevation);
                mt.Attachment = AttachmentPoint.MiddleCenter;
                mt.TextHeight = labelTextHeight;
                mt.Contents = "Ø" + nominalMm.ToString();
                double rot = Math.Atan2(dy, dx);
                if (rot > Math.PI * 0.5) rot -= Math.PI;
                else if (rot < -Math.PI * 0.5) rot += Math.PI;
                mt.Rotation = rot;
                SprinklerXData.TagAsMainPipeScheduleLabel(mt);
                if (tagZone)
                    SprinklerXData.ApplyZoneBoundaryTag(mt, zoneBoundaryHandleHex);
                ms.AppendEntity(mt);
                tr.AddNewlyCreatedDBObject(mt, true);
            }

            return true;
        }

        /// <summary>
        /// Labels each segment of the connector pipe (shaft→main) with the total zone sprinkler load,
        /// since the entire flow passes through the connector to reach the main.
        /// </summary>
        private static void TryDrawConnectorPipeScheduleLabels(
            Database db,
            Transaction tr,
            Polyline zone,
            int totalZoneLoad,
            List<Point2d> connPath,
            double clusterTol,
            double tickLen,
            string zoneBoundaryHandleHex)
        {
            if (connPath == null || connPath.Count < 2 || totalZoneLoad <= 0)
                return;

            if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(totalZoneLoad, out int nominalMm))
                return;

            double boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double labelOffsetDu = Math.Max(tickLen * 0.65, boundaryW * 0.08);
            double labelTextHeight = Math.Max(boundaryW * 0.22, tickLen * 0.55);
            bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHandleHex);

            ObjectId labelLayerId = SprinklerLayers.EnsureMcdLabelLayer(tr, db);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            int segCount = connPath.Count - 1;
            for (int si = 0; si < segCount; si++)
            {
                var a = connPath[si];
                var b = connPath[si + 1];
                var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < clusterTol * 0.25)
                    continue;

                double px = 0, py = 1;
                if (len > 1e-9)
                {
                    px = -dy / len;
                    py = dx / len;
                }

                var pA = new Point2d(mid.X + px * labelOffsetDu, mid.Y + py * labelOffsetDu);
                var pB = new Point2d(mid.X - px * labelOffsetDu, mid.Y - py * labelOffsetDu);
                var ins2d = pA.Y >= pB.Y ? pA : pB;

                var mt = new MText();
                mt.SetDatabaseDefaults(db);
                mt.LayerId = labelLayerId;
                mt.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                mt.Location = new Point3d(ins2d.X, ins2d.Y, zone.Elevation);
                mt.Attachment = AttachmentPoint.MiddleCenter;
                mt.TextHeight = labelTextHeight;
                mt.Contents = "Ø" + nominalMm.ToString();
                double rot = Math.Atan2(dy, dx);
                if (rot > Math.PI * 0.5) rot -= Math.PI;
                else if (rot < -Math.PI * 0.5) rot += Math.PI;
                mt.Rotation = rot;
                SprinklerXData.TagAsMainPipeScheduleLabel(mt);
                if (tagZone)
                    SprinklerXData.ApplyZoneBoundaryTag(mt, zoneBoundaryHandleHex);
                ms.AppendEntity(mt);
                tr.AddNewlyCreatedDBObject(mt, true);
            }
        }

        /// <summary>
        /// Builds branch laterals the same path as attach / route branch pipes, without drawing geometry.
        /// </summary>
        private static bool TryComputeLateralsForZone(
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            bool routeBranchPipesFromConnectorFirst,
            ObjectId explicitMainPipePolylineId,
            out List<Lateral> laterals,
            out bool trunkHorizontal,
            out double trunkAxis,
            out double trunkMin,
            out double trunkMax,
            out bool downstreamPositive,
            out double mainW,
            out double clusterTol,
            out int skippedOutsideTrunkSpan,
            out string errorMessage)
        {
            errorMessage = null;
            laterals = null;
            trunkHorizontal = true;
            trunkAxis = 0;
            trunkMin = 0;
            trunkMax = 0;
            downstreamPositive = true;
            mainW = 0;
            clusterTol = 0;
            skippedOutsideTrunkSpan = 0;

            if (db == null || zone == null || zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            double spacingDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, RuntimeSettings.Load().SprinklerSpacingM, out double s) && s > 0)
                    spacingDu = s;
            }
            catch { /* ignore */ }

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            clusterTol = Math.Max(tol * 10.0, spacingDu * 0.35);
            double trunkSpanTol = Math.Max(tol * 20.0, spacingDu * 0.2);

            if (!TryReadSprinklersInZone(db, zoneRing, zoneBoundaryHandleHex, clusterTol, out var sprinklers, out string sprErr))
            {
                errorMessage = sprErr ?? "Could not read sprinklers inside zone.";
                return false;
            }

            if (sprinklers.Count == 0)
            {
                errorMessage = "No sprinklers found inside this zone. Run Apply sprinklers first.";
                return false;
            }

            // ── Main pipe discovery (supports slanted / multi-vertex / multiple mains) ────────────────
            // We still keep the legacy axis-aligned trunk logic as a fast path when possible, because
            // it produces cleaner “grid” laterals. Otherwise we fall back to polyline-trunk laterals.
            var trunkIds = new List<ObjectId>();
            if (!explicitMainPipePolylineId.IsNull)
            {
                trunkIds.Add(explicitMainPipePolylineId);
            }
            else
            {
                if (!TryFindMainPipePolylinesInZone(db, zoneRing, out trunkIds, out string trunkFindErr))
                {
                    errorMessage = trunkFindErr ?? "Main pipe detection failed.";
                    return false;
                }
            }

            // Use the legacy orthogonal path for straight horizontal/vertical trunks — it produces clean
            // deterministic vertical/horizontal drops directly from the trunk axis with no tangent
            // inference or exit offsets. Only fall back to the polyline path for slanted/bent trunks.
            bool canUseLegacyAxisAligned =
                trunkIds.Count == 1 &&
                TryReadPolyline(trunkIds[0], db, out var trunkPlAxis, out _) &&
                IsStraightAxisAligned(trunkPlAxis, tol);

            List<(double lo, double hi)> trunkAlongSpansInZone = null;
            if (canUseLegacyAxisAligned)
            {
                // Re-run the existing resolver so all span filtering / connector-feed logic stays intact.
                if (!explicitMainPipePolylineId.IsNull)
                {
                    if (!TryResolveMainPipeFromExplicitPolyline(
                            db,
                            explicitMainPipePolylineId,
                            zoneRing,
                            out trunkHorizontal,
                            out trunkAxis,
                            out trunkMin,
                            out trunkMax,
                            out trunkAlongSpansInZone,
                            out downstreamPositive,
                            out mainW,
                            out errorMessage))
                        return false;
                }
                else
                {
                    if (!TryFindMainPipeInZone(
                            db,
                            zoneRing,
                            out trunkHorizontal,
                            out trunkAxis,
                            out trunkMin,
                            out trunkMax,
                            out trunkAlongSpansInZone,
                            out downstreamPositive,
                            out mainW,
                            out string pipeErr))
                    {
                        errorMessage = pipeErr ?? "Main pipe detection failed.";
                        return false;
                    }
                }
            }
            else
            {
                // Polyline-trunk mode (slanted/bent/multiple mains): we do not use axis-span filtering.
                skippedOutsideTrunkSpan = 0;
                trunkAlongSpansInZone = null;
                trunkHorizontal = true;
                trunkAxis = double.NaN;
                trunkMin = 0;
                trunkMax = 0;
                downstreamPositive = true;

                if (!TryComputeMainPipeDisplayWidth(db, trunkIds, out mainW))
                    mainW = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                if (!(mainW > 0))
                    mainW = 1.0;

                // Route branch pipe 2: allow connector-fed laterals even when trunk is non-axis-aligned.
                List<Point2d> connPathPoly = null;
                bool useConnectorFeedPoly =
                    routeBranchPipesFromConnectorFirst &&
                    TryGetConnectorPathInZone(db, zoneRing, out connPathPoly, out _) &&
                    connPathPoly != null &&
                    connPathPoly.Count >= 2;

                if (useConnectorFeedPoly)
                {
                    SplitSprinklersByConnectorProximity(
                        db, trunkIds, connPathPoly, zoneRing, sprinklers, clusterTol,
                        trunkSpanTol, tol,
                        out var connectorServed, out var trunkServed);

                    var connectorLaterals = BuildLateralsFromPolylinePath(
                        zoneRing, connPathPoly, connectorServed, clusterTol, spacingDu, fromConnectorFeed: true);

                    var trunkLaterals = BuildLateralsFromPolylineTrunks(
                        db, zoneRing, trunkIds, trunkServed, clusterTol);

                    laterals = new List<Lateral>();
                    if (connectorLaterals != null && connectorLaterals.Count > 0)
                        laterals.AddRange(connectorLaterals);
                    if (trunkLaterals != null && trunkLaterals.Count > 0)
                        laterals.AddRange(trunkLaterals);
                }
                else
                {
                    laterals = BuildLateralsFromPolylineTrunks(
                        db,
                        zoneRing,
                        trunkIds,
                        sprinklers,
                        clusterTol);
                }

                if (laterals == null || laterals.Count == 0)
                {
                    errorMessage = "No branch laterals could be constructed from the main pipe path(s).";
                    return false;
                }

                return true;
            }

            List<Point2d> connPath = null;
            bool useConnectorFeed =
                routeBranchPipesFromConnectorFirst &&
                TryGetConnectorPathInZone(db, zoneRing, out connPath, out _) &&
                connPath != null &&
                connPath.Count >= 2;

            List<Point2d> routingPool;
            if (useConnectorFeed)
            {
                routingPool = new List<Point2d>(sprinklers);
            }
            else
            {
                int beforeCt = sprinklers.Count;
                routingPool = FilterSprinklersOnTrunkSpans(sprinklers, trunkHorizontal, trunkAlongSpansInZone, trunkSpanTol);
                skippedOutsideTrunkSpan = beforeCt - routingPool.Count;
            }

            if (useConnectorFeed)
            {
                laterals = BuildLateralsConnectorThenTrunk(
                    routingPool,
                    connPath,
                    trunkHorizontal,
                    trunkAxis,
                    trunkAlongSpansInZone,
                    trunkSpanTol,
                    clusterTol,
                    spacingDu,
                    downstreamPositive,
                    tol);
            }
            else if (trunkHorizontal)
            {
                if (routingPool.Count == 0)
                {
                    errorMessage =
                        "No sprinklers lie within the main pipe span for orthogonal branches." +
                        (skippedOutsideTrunkSpan > 0
                            ? " (" + skippedOutsideTrunkSpan + " head(s) skipped — outside trunk coverage along the main.)"
                            : string.Empty);
                    return false;
                }

                laterals = BuildVerticalLateralsFromSprinklers(
                    routingPool,
                    trunkAxis,
                    clusterTol,
                    downstreamPositive);
                CollapseMicroLateralsIntoSubBranches(laterals, trunkAxis, spacingDu, clusterTol);
            }
            else
            {
                if (routingPool.Count == 0)
                {
                    errorMessage =
                        "No sprinklers lie within the main pipe span for orthogonal branches." +
                        (skippedOutsideTrunkSpan > 0
                            ? " (" + skippedOutsideTrunkSpan + " head(s) skipped — outside trunk coverage along the main.)"
                            : string.Empty);
                    return false;
                }

                var rs = new List<Point2d>(routingPool.Count);
                for (int si = 0; si < routingPool.Count; si++)
                    rs.Add(Rotate90CcwWcs(routingPool[si]));

                double rTrunkY = trunkAxis;

                laterals = BuildVerticalLateralsFromSprinklers(rs, rTrunkY, clusterTol, downstreamPositive);
                CollapseMicroLateralsIntoSubBranches(laterals, rTrunkY, spacingDu, clusterTol);

                for (int li = 0; li < laterals.Count; li++)
                {
                    laterals[li].AttachPoint = Rotate90CwWcs(laterals[li].AttachPoint);
                    for (int pj = 0; pj < laterals[li].Sprinklers.Count; pj++)
                        laterals[li].Sprinklers[pj] = Rotate90CwWcs(laterals[li].Sprinklers[pj]);
                    for (int pj = 0; pj < laterals[li].SubSprinklers.Count; pj++)
                        laterals[li].SubSprinklers[pj] = Rotate90CwWcs(laterals[li].SubSprinklers[pj]);
                }
            }

            if (laterals.Count == 0)
            {
                errorMessage = "No branch laterals could be constructed (sprinklers do not align to a grid).";
                return false;
            }

            // Reroute any branch pipes that would cross shaft obstacles via a sub-main spine bypass.
            // branchHorizontal is inferred from the actual laterals (not trunkHorizontal) because
            // the polyline-trunk path forces trunkHorizontal=true regardless of real trunk angle.
            ApplyShaftBypassSpines(db, zone, zoneRing, laterals, clusterTol);

            foreach (var lat in laterals)
            {
                int m = lat.Sprinklers.Count;
                if (m == 0)
                    continue;
            }

            return true;
        }

        private static bool TryReadPolyline(ObjectId id, Database db, out Polyline pl, out string errorMessage)
        {
            pl = null;
            errorMessage = null;
            if (db == null || id.IsNull)
            {
                errorMessage = "Invalid polyline id.";
                return false;
            }
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (pl == null)
                    {
                        errorMessage = "Selected entity is not a polyline.";
                        return false;
                    }
                    tr.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool IsStraightAxisAligned(Polyline pl, double tol)
        {
            if (pl == null) return false;
            int nv;
            try { nv = pl.NumberOfVertices; } catch { return false; }
            if (nv < 2) return false;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < nv; i++)
            {
                var p = pl.GetPoint3dAt(i);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double spanX = maxX - minX;
            double spanY = maxY - minY;
            bool isVertical = spanX <= tol * 10.0 && spanY > tol * 10.0;
            bool isHorizontal = spanY <= tol * 10.0 && spanX > tol * 10.0;
            return isVertical || isHorizontal;
        }

        private static bool TryFindMainPipePolylinesInZone(Database db, List<Point2d> zoneRing, out List<ObjectId> trunkIds, out string errorMessage)
        {
            trunkIds = new List<ObjectId>();
            errorMessage = null;
            if (db == null || zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            var tagged = new List<(ObjectId id, double len)>();
            var candidates = new List<(ObjectId id, double len)>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;
                    if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                        continue;
                    if (!(ent is Polyline pl))
                        continue;
                    if (SprinklerXData.IsTaggedTrunkCap(pl))
                        continue;
                    // Connector is not a trunk; never attach branch laterals to it.
                    if (SprinklerXData.IsTaggedConnector(pl))
                        continue;
                    if (!PolylineHasSampleInsideZone(pl, zoneRing))
                        continue;

                    double len = 0;
                    try { len = pl.Length; } catch { len = 0; }

                    candidates.Add((id, len));
                    if (SprinklerXData.IsTaggedTrunk(pl))
                        tagged.Add((id, len));
                }
                tr.Commit();
            }

            // Tagged trunks first (auto-routed mains), then untagged polylines on the same layer
            // inside the zone (manually drawn mains). BuildLateralsFromPolylineTrunks assigns each
            // sprinkler to the nearest trunk among these ids.
            tagged.Sort((a, b) => b.len.CompareTo(a.len));

            var taggedIdSet = new HashSet<ObjectId>();
            for (int i = 0; i < tagged.Count; i++)
                taggedIdSet.Add(tagged[i].id);

            var untagged = new List<(ObjectId id, double len)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!taggedIdSet.Contains(c.id))
                    untagged.Add(c);
            }
            untagged.Sort((a, b) => b.len.CompareTo(a.len));

            for (int i = 0; i < tagged.Count; i++)
                trunkIds.Add(tagged[i].id);
            for (int i = 0; i < untagged.Count; i++)
                trunkIds.Add(untagged[i].id);

            if (trunkIds.Count > 0)
                return true;

            errorMessage = "No main pipe found inside this zone. Draw on the main pipe layer inside the zone or run 'Route main pipe' first.";
            return false;
        }

        private static bool TryComputeMainPipeDisplayWidth(Database db, List<ObjectId> trunkIds, out double width)
        {
            width = 0;
            if (db == null || trunkIds == null || trunkIds.Count == 0)
                return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < trunkIds.Count; i++)
                {
                    if (trunkIds[i].IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(trunkIds[i], OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;

                    double w = TryGetPolylineUniformWidth(pl, out double u) ? u : 0;
                    if (!(w > 0)) w = TryGetPolylineAnyWidth(pl, out u) ? u : 0;
                    if (w > width) width = w;
                }
                tr.Commit();
            }

            return width > 0;
        }

        /// <summary>
        /// True when the trunk polyline's bbox is taller than wide — same classification as
        /// <c>|tangent.X| &lt;= |tangent.Y|</c> for axis-aligned mains (vertical run → horizontal branches, grid by Y).
        /// </summary>
        private static bool TrunkRunsMostlyVertical(List<Point2d> pts, double tol)
        {
            if (pts == null || pts.Count < 2)
                return true;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double te = tol > 0 ? tol : 1e-6;
            if (spanX <= te && spanY <= te)
                return true;
            return spanY >= spanX;
        }

        private static List<Lateral> BuildLateralsFromPolylineTrunks(
            Database db,
            List<Point2d> zoneRing,
            List<ObjectId> trunkIds,
            List<Point2d> sprinklers,
            double clusterTol)
        {
            var laterals = new List<Lateral>();
            if (db == null || zoneRing == null || zoneRing.Count < 3 || trunkIds == null || trunkIds.Count == 0 || sprinklers == null || sprinklers.Count == 0)
                return laterals;

            // Preload trunk polylines into simple 2D vertex lists.
            var trunks = new List<(ObjectId id, List<Point2d> pts)>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < trunkIds.Count; i++)
                {
                    if (trunkIds[i].IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(trunkIds[i], OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    var pts = PolylineToPoint2dList(pl);
                    if (pts.Count >= 2)
                        trunks.Add((trunkIds[i], pts));
                }
                tr.Commit();
            }
            if (trunks.Count == 0)
                return laterals;

            // Assign each sprinkler to the nearest trunk polyline, then group by grid row/column.
            // Branch orientation for grouping must come from the trunk as a whole — using each head's
            // local tangent can flip branchHorizontal between sprinklers on the same row (numeric jitter
            // or tiny segments), splitting gridKey between Y and X and producing one parallel branch per head.
            int n = sprinklers.Count;
            var assigned = new List<(int trunkIdx, bool branchHorizontal, double gridKey, Point2d attach, Point2d spr, double dist)>(n);
            for (int i = 0; i < n; i++)
            {
                var s = sprinklers[i];
                int bestTi = 0;
                double bestD = double.MaxValue;
                Point2d bestAttach = s;
                for (int ti = 0; ti < trunks.Count; ti++)
                {
                    var tPts = trunks[ti].pts;
                    ClosestPointOnPolylineInsideRing(tPts, zoneRing, s, out var cp, out _, out double d);
                    if (d < bestD)
                    {
                        bestD = d;
                        bestTi = ti;
                        bestAttach = cp;
                    }
                }

                var winPts = trunks[bestTi].pts;
                bool branchHorizontal = TrunkRunsMostlyVertical(winPts, clusterTol);
                double gridKey = branchHorizontal ? s.Y : s.X;
                assigned.Add((bestTi, branchHorizontal, gridKey, bestAttach, s, bestD));
            }

            // For each trunk, group sprinklers by grid row/column to form axis-aligned lateral taps.
            // Rows whose grid line does not intersect the trunk (endpoint-snapped rows) are collected
            // into Group B and served through a perpendicular sub-main spine from the trunk tip instead
            // of being fanned diagonally back to the same endpoint.
            for (int ti = 0; ti < trunks.Count; ti++)
            {
                var tPoly = trunks[ti].pts;
                Point2d trunkStart = tPoly[0];
                Point2d trunkEnd   = tPoly[tPoly.Count - 1];

                for (int axisMode = 0; axisMode < 2; axisMode++)
                {
                    bool branchHorizontal = axisMode == 0;
                    var list = new List<(double gridKey, Point2d attach, Point2d spr, double dist)>();
                    for (int i = 0; i < assigned.Count; i++)
                    {
                        if (assigned[i].trunkIdx != ti || assigned[i].branchHorizontal != branchHorizontal) continue;
                        list.Add((assigned[i].gridKey, assigned[i].attach, assigned[i].spr, assigned[i].dist));
                    }
                    if (list.Count == 0) continue;

                    list.Sort((a, b) => a.gridKey.CompareTo(b.gridKey));
                    double tol = clusterTol > 0 ? clusterTol : 1e-6;

                    // Group B buckets: rows that snap to the start or end trunk endpoint.
                    // Keep a sample of the on-trunk attach points so the spine can be anchored to a
                    // point that is actually on the in-zone trunk geometry (prevents visible gaps).
                    var groupBStart = new List<(double gridKey, Point2d spr)>();
                    var groupBEnd   = new List<(double gridKey, Point2d spr)>();
                    var groupBStartAttachSamples = new List<Point2d>();
                    var groupBEndAttachSamples   = new List<Point2d>();

                    int start = 0;
                    while (start < list.Count)
                    {
                        int end = start;
                        double a0 = list[start].gridKey;
                        while (end + 1 < list.Count && Math.Abs(list[end + 1].gridKey - a0) <= tol)
                            end++;

                        int mid = (start + end) / 2;
                        double gridKey = list[mid].gridKey;

                        var sprs = new List<Point2d>(end - start + 1);
                        for (int i = start; i <= end; i++)
                            sprs.Add(list[i].spr);

                        Point2d attach;
                        if (!TryFindGridAxisAttachOnPolyline(tPoly, zoneRing, branchHorizontal, gridKey, sprs, tol, out attach))
                        {
                            // This row's level does not cross the trunk — it is Group B.
                            // Assign it to the nearer trunk endpoint so the spine starts there.
                            attach = list[mid].attach;
                            double dS2 = Dist2(attach, trunkStart);
                            double dE2 = Dist2(attach, trunkEnd);
                            foreach (var sp in sprs)
                            {
                                if (dS2 <= dE2)
                                {
                                    groupBStart.Add((gridKey, sp));
                                    groupBStartAttachSamples.Add(attach);
                                }
                                else
                                {
                                    groupBEnd.Add((gridKey, sp));
                                    groupBEndAttachSamples.Add(attach);
                                }
                            }
                            start = end + 1;
                            continue;
                        }

                        // Group A: trunk intersects this row — build a normal lateral.
                        Vector2d tangent = PolylineTangentNearPoint(tPoly, attach, tol);
                        Vector2d baseAxis = branchHorizontal ? new Vector2d(1, 0) : new Vector2d(0, 1);
                        var positiveSide = new List<Point2d>();
                        var negativeSide = new List<Point2d>();
                        for (int si = 0; si < sprs.Count; si++)
                        {
                            double side = ProjectAlong(baseAxis, attach, sprs[si]);
                            if (side >= -tol)
                                positiveSide.Add(sprs[si]);
                            else
                                negativeSide.Add(sprs[si]);
                        }

                        AddGridSideLaterals(laterals, attach, positiveSide, baseAxis, gridKey, tangent);
                        AddGridSideLaterals(laterals, attach, negativeSide, -baseAxis, gridKey, tangent);

                        start = end + 1;
                    }

                    // Build perpendicular sub-main spine laterals for endpoint-snapped rows.
                    if (groupBStart.Count > 0)
                    {
                        var anchor = trunkStart;
                        if (groupBStartAttachSamples.Count > 0)
                        {
                            double sx = 0, sy = 0;
                            for (int ai = 0; ai < groupBStartAttachSamples.Count; ai++)
                            {
                                sx += groupBStartAttachSamples[ai].X;
                                sy += groupBStartAttachSamples[ai].Y;
                            }
                            anchor = new Point2d(sx / groupBStartAttachSamples.Count, sy / groupBStartAttachSamples.Count);
                        }
                        AppendSubMainSpineLaterals(laterals, anchor, groupBStart, branchHorizontal, clusterTol);
                    }
                    if (groupBEnd.Count > 0)
                    {
                        var anchor = trunkEnd;
                        if (groupBEndAttachSamples.Count > 0)
                        {
                            double sx = 0, sy = 0;
                            for (int ai = 0; ai < groupBEndAttachSamples.Count; ai++)
                            {
                                sx += groupBEndAttachSamples[ai].X;
                                sy += groupBEndAttachSamples[ai].Y;
                            }
                            anchor = new Point2d(sx / groupBEndAttachSamples.Count, sy / groupBEndAttachSamples.Count);
                        }
                        AppendSubMainSpineLaterals(laterals, anchor, groupBEnd, branchHorizontal, clusterTol);
                    }
                }
            }

            return laterals;
        }

        private static void AddGridSideLaterals(
            List<Lateral> laterals,
            Point2d attach,
            List<Point2d> sprinklers,
            Vector2d branchAxis,
            double gridKey,
            Vector2d tangent,
            BranchRole role = BranchRole.PrimaryBranch)
        {
            if (laterals == null || sprinklers == null || sprinklers.Count == 0)
                return;

            sprinklers.Sort((p, q) => ProjectAlong(branchAxis, attach, p).CompareTo(ProjectAlong(branchAxis, attach, q)));
            laterals.Add(new Lateral
            {
                AttachPoint = attach,
                Sprinklers = sprinklers,
                IsTopOrRight = true,
                AxisValue = gridKey,
                FromConnectorFeed = false,
                TrunkTangent = tangent,
                BranchAxis = branchAxis,
                Role = role,
            });
        }

        /// <summary>Squared distance between two 2D points (avoids sqrt for comparison).</summary>
        private static double Dist2(Point2d a, Point2d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Builds a clean two-level sub-main tree for Group B sprinklers — rows whose grid line
        /// does not intersect the slanted trunk (their perpendicular foot fell past the trunk tip).
        /// <para>
        /// Instead of fanning every row diagonally to the same trunk endpoint, a single perpendicular
        /// spine runs from <paramref name="trunkEndpoint"/> through one T-junction per row.  Each row
        /// then branches off that spine with a normal orthogonal lateral.
        /// </para>
        /// </summary>
        private static void AppendSubMainSpineLaterals(
            List<Lateral> laterals,
            Point2d trunkEndpoint,
            List<(double gridKey, Point2d spr)> groupB,
            bool branchHorizontal,
            double clusterTol)
        {
            if (groupB == null || groupB.Count == 0) return;
            double tol = clusterTol > 0 ? clusterTol : 1e-6;

            groupB.Sort((a, b) => a.gridKey.CompareTo(b.gridKey));

            // For horizontal rows (branchHorizontal=true): rows run ±X, spine runs ±Y.
            // For vertical   rows (branchHorizontal=false): rows run ±Y, spine runs ±X.
            double spineFixed    = branchHorizontal ? trunkEndpoint.X : trunkEndpoint.Y;
            double trunkCrossKey = branchHorizontal ? trunkEndpoint.Y : trunkEndpoint.X;
            Vector2d branchAxis = branchHorizontal ? new Vector2d(1, 0) : new Vector2d(0, 1);
            Vector2d spineAxis  = branchHorizontal ? new Vector2d(0, 1) : new Vector2d(1, 0);

            // Determine which side of the trunk endpoint Group B rows sit on.
            double midKey = (groupB[0].gridKey + groupB[groupB.Count - 1].gridKey) * 0.5;
            Vector2d spineDir = (midKey >= trunkCrossKey) ? spineAxis : -spineAxis;

            // Group sprinklers by row (gridKey) to produce one T-junction and one horizontal
            // lateral per row.
            var tjunctions    = new List<Point2d>();
            var rowHeadCounts = new List<int>();
            int start = 0;
            while (start < groupB.Count)
            {
                int end = start;
                double key0 = groupB[start].gridKey;
                while (end + 1 < groupB.Count && Math.Abs(groupB[end + 1].gridKey - key0) <= tol)
                    end++;

                double gridKey = (groupB[start].gridKey + groupB[end].gridKey) * 0.5;

                // Spine T-junction for this row at the fixed spine coordinate.
                Point2d tJunction = branchHorizontal
                    ? new Point2d(spineFixed, gridKey)
                    : new Point2d(gridKey, spineFixed);
                tjunctions.Add(tJunction);
                rowHeadCounts.Add(end - start + 1);

                // Horizontal laterals for this row's sprinklers, branching off the T-junction.
                var positiveSide = new List<Point2d>();
                var negativeSide = new List<Point2d>();
                for (int i = start; i <= end; i++)
                {
                    var s = groupB[i].spr;
                    double side = ProjectAlong(branchAxis, tJunction, s);
                    if (side >= -tol) positiveSide.Add(s);
                    else              negativeSide.Add(s);
                }
                AddGridSideLaterals(laterals, tJunction, positiveSide,  branchAxis, gridKey, spineDir);
                AddGridSideLaterals(laterals, tJunction, negativeSide, -branchAxis, gridKey, spineDir);

                start = end + 1;
            }

            if (tjunctions.Count == 0) return;

            // Sort T-junctions in spine direction (closest to trunk endpoint first).
            var combined = new List<(Point2d tj, int cnt)>(tjunctions.Count);
            for (int i = 0; i < tjunctions.Count; i++)
                combined.Add((tjunctions[i], rowHeadCounts[i]));
            combined.Sort((a, b) =>
                ProjectAlong(spineDir, trunkEndpoint, a.tj)
                .CompareTo(ProjectAlong(spineDir, trunkEndpoint, b.tj)));

            var sortedTj  = new List<Point2d>(combined.Count);
            var sortedCnt = new List<int>(combined.Count);
            foreach (var (tj, cnt) in combined) { sortedTj.Add(tj); sortedCnt.Add(cnt); }

            // Cumulative head count per spine segment: segment i carries all rows from i onward.
            int totalHeads = 0;
            foreach (var c in sortedCnt) totalHeads += c;
            var spineHeadCounts = new int[sortedTj.Count];
            int rem = totalHeads;
            for (int i = 0; i < sortedTj.Count; i++) { spineHeadCounts[i] = rem; rem -= sortedCnt[i]; }

            // Add the spine as a special lateral: AttachPoint is the trunk endpoint, Sprinklers
            // are the T-junction points (not actual heads), BranchAxis runs along the spine.
            laterals.Add(new Lateral
            {
                AttachPoint            = trunkEndpoint,
                Sprinklers             = sortedTj,
                SubSprinklers          = new List<Point2d>(),
                IsTopOrRight           = true,
                AxisValue              = branchHorizontal ? trunkEndpoint.X : trunkEndpoint.Y,
                FromConnectorFeed      = false,
                TrunkTangent           = spineDir,
                BranchAxis             = spineDir,
                IsSubMainSpine         = true,
                SpineSegmentHeadCounts = spineHeadCounts,
            });
        }

        // ── Shaft-bypass sub-main spine ──────────────────────────────────────────────

        /// <summary>
        /// Returns true when line segment a→b passes through the axis-aligned obstacle box.
        /// The box already includes clearance (expanded by the caller).
        /// </summary>
        private static bool SegmentCrossesObstacleBox(Point2d a, Point2d b,
            Point2d sMin, Point2d sMax, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;
            double bx0 = Math.Min(sMin.X, sMax.X) - te, bx1 = Math.Max(sMin.X, sMax.X) + te;
            double by0 = Math.Min(sMin.Y, sMax.Y) - te, by1 = Math.Max(sMin.Y, sMax.Y) + te;
            // Quick AABB reject
            if (Math.Max(a.X, b.X) < bx0 || Math.Min(a.X, b.X) > bx1) return false;
            if (Math.Max(a.Y, b.Y) < by0 || Math.Min(a.Y, b.Y) > by1) return false;
            // Endpoint inside box
            if (a.X >= bx0 && a.X <= bx1 && a.Y >= by0 && a.Y <= by1) return true;
            if (b.X >= bx0 && b.X <= bx1 && b.Y >= by0 && b.Y <= by1) return true;
            // Parametric clip (Liang–Barsky)
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double tmin = 0, tmax = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { a.X - bx0, bx1 - a.X, a.Y - by0, by1 - a.Y };
            for (int k = 0; k < 4; k++)
            {
                if (Math.Abs(p[k]) < 1e-14) { if (q[k] < 0) return false; continue; }
                double t = q[k] / p[k];
                if (p[k] < 0) tmin = Math.Max(tmin, t);
                else           tmax = Math.Min(tmax, t);
                if (tmin > tmax) return false;
            }
            return tmin <= tmax;
        }

        /// <summary>
        /// Post-processes the lateral list to handle sprinklers whose direct branch path
        /// would cross a shaft obstacle.
        ///
        /// Root causes of previous diagonal output:
        ///   Bug 1 — host selection accepted diagonal PrimaryBranch laterals.
        ///   Bug 2 — row grouping used abs(t) rounded to tol≈1e-6, producing one T-junction per sprinkler.
        ///   Bug 3 — fanout/spine directions were computed from host segment geometry instead of hardcoded axes.
        ///
        /// Fix: hardcode branch/subMain directions from dominant axis family. Only accept
        /// axis-aligned PrimaryBranch hosts. Group rows by actual sprinkler coordinate (Y or X).
        /// </summary>
        private static void ApplyShaftBypassSpines(
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            List<Lateral> laterals,
            double clusterTol)
        {
            if (db == null || zone == null || laterals == null || laterals.Count == 0) return;

            double shaftClearDu = 0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.12, out double sc) && sc > 0)
                    shaftClearDu = sc;
            }
            catch { }
            if (shaftClearDu <= 0)
                shaftClearDu = Math.Max(clusterTol > 0 ? clusterTol * 2.0 : 1e-3, 1e-4);

            var shaftBoxes = BranchPipeShaftDetour2d.BuildShaftObstacles(db, zone, shaftClearDu);
            if (shaftBoxes.Count == 0) return;

            double tol = clusterTol > 0 ? clusterTol : 1e-6;

            // ── Determine dominant branch orientation FIRST (needed for diagonal detection) ──
            int snapshot = laterals.Count;
            int hCnt = 0, vCnt = 0;
            for (int li = 0; li < snapshot; li++)
            {
                var lat = laterals[li];
                if (lat.Role != BranchRole.PrimaryBranch || lat.IsSubMainSpine) continue;
                if (lat.Sprinklers == null || lat.Sprinklers.Count == 0) continue;
                double ddx = Math.Abs(lat.Sprinklers[lat.Sprinklers.Count - 1].X - lat.AttachPoint.X);
                double ddy = Math.Abs(lat.Sprinklers[lat.Sprinklers.Count - 1].Y - lat.AttachPoint.Y);
                if (ddy > ddx) vCnt++; else hCnt++;
            }
            bool branchesVertical = vCnt >= hCnt;
            Vector2d branchDir  = branchesVertical ? new Vector2d(0, 1) : new Vector2d(1, 0);
            Vector2d subMainDir = branchesVertical ? new Vector2d(1, 0) : new Vector2d(0, 1);

            // ── Step 1: Collect blocked sprinklers ──
            // A sprinkler is "blocked" if its branch leg either:
            //   (a) crosses a shaft obstacle box, OR
            //   (b) is significantly diagonal (>30° off the dominant branch axis).
            var blockedSprinklers = new List<(int sourceLateral, Point2d sprinkler, int shaftIndex)>();
            for (int li = 0; li < snapshot; li++)
            {
                var lat = laterals[li];
                if (lat.IsSubMainSpine || lat.Sprinklers.Count == 0) continue;

                var clear = new List<Point2d>();
                Point2d prev = lat.AttachPoint;
                foreach (var spr in lat.Sprinklers)
                {
                    // Check shaft crossing
                    int shaftIdx = -1;
                    for (int si = 0; si < shaftBoxes.Count; si++)
                    {
                        var (sMin, sMax) = shaftBoxes[si];
                        if (SegmentCrossesObstacleBox(prev, spr, sMin, sMax, tol))
                        { shaftIdx = si; break; }
                    }

                    // Check diagonal: segment must be roughly aligned with branchDir
                    bool diagonal = false;
                    if (shaftIdx < 0)
                    {
                        double segDx = spr.X - prev.X;
                        double segDy = spr.Y - prev.Y;
                        double segLen = Math.Sqrt(segDx * segDx + segDy * segDy);
                        if (segLen > tol)
                        {
                            double dot = Math.Abs((segDx * branchDir.X + segDy * branchDir.Y) / segLen);
                            // cos(30°) ≈ 0.866 — reject segments more than 30° off the branch axis
                            if (dot < 0.75) diagonal = true;
                        }
                    }

                    bool blocked = shaftIdx >= 0 || diagonal;
                    if (blocked)
                        blockedSprinklers.Add((li, spr, shaftIdx));
                    else
                    {
                        clear.Add(spr);
                        prev = spr;
                    }
                }
                if (clear.Count == lat.Sprinklers.Count) continue;
                lat.Sprinklers = clear;
            }

            if (blockedSprinklers.Count == 0) return;

            // ── For each blocked sprinkler, find nearest axis-aligned PrimaryBranch
            //    lateral and add as SubSprinkler. The drawing code already handles
            //    SubSprinklers: it taps onto the host branch run and draws a pipe. ──
            for (int bi = 0; bi < blockedSprinklers.Count; bi++)
            {
                var bSpr = blockedSprinklers[bi].sprinkler;
                int srcLi = blockedSprinklers[bi].sourceLateral;

                int bestLi = -1;
                double bestD2 = double.MaxValue;

                for (int li = 0; li < snapshot; li++)
                {
                    var lat = laterals[li];
                    if (lat.Role != BranchRole.PrimaryBranch || lat.IsSubMainSpine) continue;
                    if (lat.Sprinklers == null || lat.Sprinklers.Count == 0) continue;
                    // Only consider axis-aligned branches
                    double ddx = Math.Abs(lat.Sprinklers[lat.Sprinklers.Count - 1].X - lat.AttachPoint.X);
                    double ddy = Math.Abs(lat.Sprinklers[lat.Sprinklers.Count - 1].Y - lat.AttachPoint.Y);
                    if ((ddy > ddx) != branchesVertical) continue;

                    // Distance from this branch run to the blocked sprinkler
                    var run = new List<Point2d>(lat.Sprinklers.Count + 1) { lat.AttachPoint };
                    run.AddRange(lat.Sprinklers);
                    ClosestPointOnPolyline(run, bSpr, out _, out _, out double dist);
                    double d2 = dist * dist;
                    if (d2 < bestD2) { bestD2 = d2; bestLi = li; }
                }

                if (bestLi >= 0)
                {
                    var host = laterals[bestLi];
                    if (host.SubSprinklers == null)
                        host.SubSprinklers = new List<Point2d>();
                    host.SubSprinklers.Add(bSpr);
                }
                else if (srcLi >= 0 && srcLi < snapshot)
                {
                    laterals[srcLi].Sprinklers.Add(bSpr);
                }
            }
        }

        private static List<Lateral> BuildLateralsFromPolylinePath(
            List<Point2d> zoneRing,
            List<Point2d> pathPts,
            List<Point2d> sprinklers,
            double clusterTol,
            double spacingDu,
            bool fromConnectorFeed)
        {
            var laterals = new List<Lateral>();
            if (zoneRing == null || zoneRing.Count < 3 || pathPts == null || pathPts.Count < 2 || sprinklers == null || sprinklers.Count == 0)
                return laterals;

            double tol = clusterTol > 0 ? clusterTol : 1e-6;
            double rowMergeTol = Math.Max(clusterTol * 4.0, spacingDu > 0 ? spacingDu * 0.45 : clusterTol * 4.0);

            double pMinX = double.MaxValue, pMinY = double.MaxValue, pMaxX = double.MinValue, pMaxY = double.MinValue;
            for (int pi = 0; pi < pathPts.Count; pi++)
            {
                var pp = pathPts[pi];
                if (pp.X < pMinX) pMinX = pp.X;
                if (pp.X > pMaxX) pMaxX = pp.X;
                if (pp.Y < pMinY) pMinY = pp.Y;
                if (pp.Y > pMaxY) pMaxY = pp.Y;
            }
            double pathSpanX = pMaxX - pMinX;
            double pathSpanY = pMaxY - pMinY;
            bool groupRowsByY = pathSpanY >= pathSpanX;

            var assigned = new List<(double along, Point2d attach, Point2d spr, double dist)>(sprinklers.Count);
            for (int i = 0; i < sprinklers.Count; i++)
            {
                var s = sprinklers[i];
                ClosestPointOnPolylineInsideRing(pathPts, zoneRing, s, out var cp, out double along, out double d);
                assigned.Add((along, cp, s, d));
            }

            assigned.Sort((a, b) =>
            {
                double ka = groupRowsByY ? a.spr.Y : a.spr.X;
                double kb = groupRowsByY ? b.spr.Y : b.spr.X;
                int c = ka.CompareTo(kb);
                return c != 0 ? c : a.along.CompareTo(b.along);
            });

            int start = 0;
            while (start < assigned.Count)
            {
                int end = start;
                double k0 = groupRowsByY ? assigned[start].spr.Y : assigned[start].spr.X;
                while (end + 1 < assigned.Count)
                {
                    double kn = groupRowsByY ? assigned[end + 1].spr.Y : assigned[end + 1].spr.X;
                    if (Math.Abs(kn - k0) <= rowMergeTol)
                        end++;
                    else
                        break;
                }

                double ax = 0, ay = 0;
                double meanAlong = 0;
                int nGrp = end - start + 1;
                for (int i = start; i <= end; i++)
                {
                    ax += assigned[i].attach.X;
                    ay += assigned[i].attach.Y;
                    meanAlong += assigned[i].along;
                }
                ax /= nGrp;
                ay /= nGrp;
                meanAlong /= nGrp;
                Point2d attach = new Point2d(ax, ay);

                var sprs = new List<Point2d>(nGrp);
                for (int i = start; i <= end; i++)
                    sprs.Add(assigned[i].spr);

                Vector2d tangent = PolylineTangentNearPoint(pathPts, attach, tol);
                Vector2d branchAxis = PickBranchAxisFromTangent(attach, sprs, tangent, tol);
                sprs.Sort((p, q) => ProjectAlong(branchAxis, attach, p).CompareTo(ProjectAlong(branchAxis, attach, q)));

                laterals.Add(new Lateral
                {
                    AttachPoint = attach,
                    Sprinklers = sprs,
                    IsTopOrRight = true,
                    AxisValue = meanAlong,
                    FromConnectorFeed = fromConnectorFeed,
                    TrunkTangent = tangent,
                    BranchAxis = branchAxis,
                    Role = BranchRole.PrimaryBranch,
                });

                start = end + 1;
            }

            return laterals;
        }

        private static void SplitSprinklersByConnectorProximity(
            Database db,
            List<ObjectId> trunkIds,
            List<Point2d> connectorPath,
            List<Point2d> zoneRing,
            List<Point2d> sprinklers,
            double clusterTol,
            double trunkSpanTol,
            double geomTol,
            out List<Point2d> connectorServed,
            out List<Point2d> trunkServed)
        {
            connectorServed = new List<Point2d>();
            trunkServed = new List<Point2d>();
            if (db == null || trunkIds == null || trunkIds.Count == 0 || zoneRing == null || zoneRing.Count < 3 || connectorPath == null || connectorPath.Count < 2 || sprinklers == null)
            {
                if (sprinklers != null) trunkServed.AddRange(sprinklers);
                return;
            }

            // Preload trunk polylines into vertex lists.
            var trunks = new List<List<Point2d>>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < trunkIds.Count; i++)
                {
                    if (trunkIds[i].IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(trunkIds[i], OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    var pts = PolylineToPoint2dList(pl);
                    if (pts.Count >= 2)
                        trunks.Add(pts);
                }
                tr.Commit();
            }

            double te = clusterTol > 0 ? clusterTol : 1e-6;
            double spanT = trunkSpanTol > 0 ? trunkSpanTol : te;
            double g = geomTol > 0 ? geomTol : 1e-6;
            double bias = te * 0.75; // gentle preference for connector when roughly equally close

            for (int i = 0; i < sprinklers.Count; i++)
            {
                var s = sprinklers[i];

                bool onConnectorSpan = false;
                for (int k = 0; k + 1 < connectorPath.Count; k++)
                {
                    if (ConnectorSegmentClaimsSprinkler(s, connectorPath[k], connectorPath[k + 1], spanT, g))
                    {
                        onConnectorSpan = true;
                        break;
                    }
                }

                if (onConnectorSpan)
                {
                    connectorServed.Add(s);
                    continue;
                }

                ClosestPointOnPolylineInsideRing(connectorPath, zoneRing, s, out _, out _, out double dConn);
                double dTrunk = double.MaxValue;
                for (int t = 0; t < trunks.Count; t++)
                {
                    ClosestPointOnPolylineInsideRing(trunks[t], zoneRing, s, out _, out _, out double d);
                    if (d < dTrunk) dTrunk = d;
                }

                if (dConn + bias < dTrunk)
                    connectorServed.Add(s);
                else
                    trunkServed.Add(s);
            }
        }

        private static void ClosestPointOnPolylineInsideRing(
            List<Point2d> poly,
            List<Point2d> zoneRing,
            Point2d p,
            out Point2d closest,
            out double alongDistance,
            out double distance)
        {
            // If no ring is available, fall back to the raw polyline closest point.
            if (zoneRing == null || zoneRing.Count < 3)
            {
                ClosestPointOnPolyline(poly, p, out closest, out alongDistance, out distance);
                return;
            }

            closest = p;
            alongDistance = 0;
            distance = double.MaxValue;
            if (poly == null || poly.Count < 2)
            {
                distance = 0;
                return;
            }

            double bestD2 = double.MaxValue;
            double bestAlong = 0;
            Point2d best = poly[0];

            double accum = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                if (len2 <= 1e-18)
                    continue;

                double len = Math.Sqrt(len2);
                var clips = RingGeometry.ClipSegmentToRing(a, b, zoneRing);
                if (clips == null || clips.Count == 0)
                {
                    accum += len;
                    continue;
                }

                foreach (var (t0, t1) in clips)
                {
                    double lo = Math.Max(0.0, Math.Min(1.0, Math.Min(t0, t1)));
                    double hi = Math.Max(0.0, Math.Min(1.0, Math.Max(t0, t1)));
                    if (hi - lo <= 1e-12)
                        continue;

                    // Closest point on the clipped subsegment.
                    double ax = a.X + lo * dx;
                    double ay = a.Y + lo * dy;
                    double bx = a.X + hi * dx;
                    double by = a.Y + hi * dy;
                    double sdx = bx - ax;
                    double sdy = by - ay;
                    double slen2 = sdx * sdx + sdy * sdy;
                    if (slen2 <= 1e-18)
                        continue;

                    double u = ((p.X - ax) * sdx + (p.Y - ay) * sdy) / slen2;
                    if (u < 0) u = 0;
                    if (u > 1) u = 1;

                    double t = lo + u * (hi - lo);
                    var q = new Point2d(a.X + t * dx, a.Y + t * dy);

                    double ddx = q.X - p.X;
                    double ddy = q.Y - p.Y;
                    double d2 = ddx * ddx + ddy * ddy;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        best = q;
                        bestAlong = accum + len * t;
                    }
                }

                accum += len;
            }

            if (bestD2 >= double.MaxValue / 2)
            {
                // No zone-clipped segment found (trunk fully inside zone but ClipSegmentToRing returned
                // nothing, or zone ring was degenerate). Fall back to the raw closest point so we never
                // return poly[0] as a spurious attach for every sprinkler.
                ClosestPointOnPolyline(poly, p, out closest, out alongDistance, out distance);
                return;
            }

            closest = best;
            alongDistance = bestAlong;
            distance = Math.Sqrt(bestD2);
        }

        private static List<Point2d> PolylineToPoint2dList(Polyline pl)
        {
            var pts = new List<Point2d>();
            if (pl == null) return pts;
            int nv;
            try { nv = pl.NumberOfVertices; } catch { return pts; }
            for (int i = 0; i < nv; i++)
            {
                var p = pl.GetPoint3dAt(i);
                pts.Add(new Point2d(p.X, p.Y));
            }
            // For open polylines, do not close.
            return pts;
        }

        private static void ClosestPointOnPolyline(
            List<Point2d> poly,
            Point2d p,
            out Point2d closest,
            out double alongDistance,
            out double distance)
        {
            closest = p;
            alongDistance = 0;
            distance = double.MaxValue;
            if (poly == null || poly.Count < 2)
            {
                distance = 0;
                return;
            }

            double bestD2 = double.MaxValue;
            double bestAlong = 0;
            Point2d best = poly[0];

            double accum = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                if (len2 <= 1e-18)
                    continue;

                double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                var q = new Point2d(a.X + t * dx, a.Y + t * dy);
                double d2 = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = q;
                    bestAlong = accum + Math.Sqrt(len2) * t;
                }

                accum += Math.Sqrt(len2);
            }

            closest = best;
            alongDistance = bestAlong;
            distance = Math.Sqrt(bestD2);
        }


        private static bool IsReducerSymbolLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return false;
            return string.Equals(layerName, SprinklerLayers.McdReducerLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerLayers.BranchReducerLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static void EraseTaggedReducersInZone(Transaction tr, Database db, string boundaryHandleHex)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex))
                return;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (!IsReducerSymbolLayerName(ent.Layer))
                    continue;
                if (ent is Polyline pl && !pl.Closed)
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                toErase.Add(id);
            }

            foreach (var id in toErase)
            {
                if (id.IsErased) continue;
                Entity e = null;
                try { e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                e?.Erase();
            }
        }

        internal static bool TryPlaceReducersForZone(
            Document doc,
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            bool routeBranchPipesFromConnectorFirst,
            ObjectId explicitMainPipePolylineId,
            out string errorMessage)
        {
            errorMessage = null;
            if (doc == null || db == null || zone == null || zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            if (!TryComputeLateralsForZone(
                    db,
                    zone,
                    zoneRing,
                    zoneBoundaryHandleHex,
                    routeBranchPipesFromConnectorFirst,
                    explicitMainPipePolylineId,
                    out List<Lateral> laterals,
                    out bool trunkHorizontal,
                    out double trunkAxis,
                    out _,
                    out _,
                    out _,
                    out double mainW,
                    out double clusterTol,
                    out int skippedOutsideTrunkSpan,
                    out errorMessage))
                return false;

            double tickLen = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0)
                    tickLen = t;
            }
            catch { /* ignore */ }

            double headRadiusM = RuntimeSettings.Load().SprinklerHeadRadiusM;
            double radiusDu;
            try
            {
                if (!DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, headRadiusM, out radiusDu) || radiusDu <= 0)
                    radiusDu = Math.Max(tickLen * 0.5, 1e-6);
            }
            catch
            {
                radiusDu = Math.Max(tickLen * 0.5, 1e-6);
            }

            double reducerHalf = ComputeReducerHalfArm(db, tickLen, mainW);
            bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHandleHex);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);
                EraseTaggedReducersInZone(tr, db, zoneBoundaryHandleHex);

                ObjectId reducerLayerId = SprinklerLayers.EnsureMcdReducerLayer(tr, db);
                if (!ReducerBlockInsert.TryGetBlockDefinitionId(tr, db, out ObjectId reducerBlockDefId, out string reducerBlockErr))
                {
                    errorMessage = reducerBlockErr ?? "Reducer block is missing.";
                    return false;
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int reducersDrawn = 0;

                foreach (var lat in laterals)
                {
                    if (lat.Sprinklers.Count == 0)
                        continue;

                    int m = lat.Sprinklers.Count;
                    Point2d attach = lat.AttachPoint;

                    bool useConnectorSingleHeadStep =
                        lat.FromConnectorFeed &&
                        m == 1 &&
                        (lat.SubSprinklers == null || lat.SubSprinklers.Count == 0);
                    if (useConnectorSingleHeadStep)
                        continue;

                    // For sub-main spine laterals skip reducer placement — the spine is a distribution
                    // pipe, not a branch; its T-junction points are not sprinkler heads.
                    if (lat.IsSubMainSpine)
                        continue;

                    var nomIn = new int[m];
                    for (int i = 0; i < m; i++)
                    {
                        int planHeadsServedOnSegment = PlanSprinklersServedOnBranchSegmentFromTip(i, m);
                        if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(planHeadsServedOnSegment, out nomIn[i]))
                        {
                            errorMessage =
                                "Branch sizing: could not resolve nominal pipe for plan sprinkler count " +
                                planHeadsServedOnSegment + " on this segment.";
                            return false;
                        }
                    }

                    int mainNomMm = ApproximateTrunkNominalMmFromDisplayWidth(db, mainW, mainW);
                    bool allowMainOuterFiberPlacement = !double.IsNaN(trunkAxis);

                    void PlaceOneReducer(
                        Point2d wedgeCenterNonTee,
                        double beforeW,
                        double afterW,
                        double halfOutPlaceForWedgeLen,
                        bool teeFiberPlacement,
                        Point2d prevPt,
                        Point2d curPt)
                    {
                        bool smallerIsAfter = afterW < beforeW;
                        var axisDir = smallerIsAfter
                            ? new Vector2d(curPt.X - prevPt.X, curPt.Y - prevPt.Y)
                            : new Vector2d(prevPt.X - curPt.X, prevPt.Y - curPt.Y);

                        double bigW = Math.Max(beforeW, afterW);
                        double smallW = Math.Min(beforeW, afterW);

                        double dxSeg = curPt.X - prevPt.X;
                        double dySeg = curPt.Y - prevPt.Y;
                        double lenSeg = Math.Sqrt(dxSeg * dxSeg + dySeg * dySeg);
                        double suxL = lenSeg > 1e-9 ? dxSeg / lenSeg : 1.0;
                        double suyL = lenSeg > 1e-9 ? dySeg / lenSeg : 0.0;

                        double ax = axisDir.X;
                        double ay = axisDir.Y;
                        double alen = Math.Sqrt(ax * ax + ay * ay);
                        if (alen > 1e-9)
                        {
                            double nx = ax / alen;
                            double ny = ay / alen;
                            if (suxL * nx + suyL * ny < 0)
                            {
                                suxL = -suxL;
                                suyL = -suyL;
                            }
                        }

                        double wedgeLen = Math.Max(
                            reducerHalf * 2.0,
                            Math.Max(
                                Math.Max(bigW, smallW) * 1.35 + Math.Abs(bigW - smallW) * 0.25,
                                teeFiberPlacement ? Math.Max(mainW * 1.1, halfOutPlaceForWedgeLen * 2.8) : 0));

                        double halfLenAlong = ComputeReducerWedgeHalfLengthDu(wedgeLen, bigW, smallW);
                        Point2d center = wedgeCenterNonTee;
                        if (teeFiberPlacement)
                        {
                            Point2d fiber = OffsetReducerToMainOuterFiber(prevPt, curPt, trunkHorizontal, trunkAxis, halfOutPlaceForWedgeLen);
                            halfLenAlong = Math.Max(halfLenAlong, halfOutPlaceForWedgeLen * 1.35);
                            center = new Point2d(
                                fiber.X + suxL * halfLenAlong,
                                fiber.Y + suyL * halfLenAlong);
                        }

                        var insPt = new Point3d(center.X, center.Y, zone.Elevation);
                        var br = new BlockReference(insPt, reducerBlockDefId);
                        br.SetDatabaseDefaults(db);
                        br.LayerId = reducerLayerId;
                        br.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        br.Rotation = Math.Atan2(suyL, suxL) + Math.PI * 0.5;
                        try
                        {
                            br.Normal = zone.Normal;
                        }
                        catch
                        {
                            /* ignore */
                        }

                        if (tagZone)
                            SprinklerXData.ApplyZoneBoundaryTag(br, zoneBoundaryHandleHex);
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        reducersDrawn++;
                    }

                    // Main tee: only when trunk nominal differs from first branch segment (or display width still mismatched).
                    if (m >= 1 && !lat.FromConnectorFeed)
                    {
                        double w0 = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nomIn[0], mainW);
                        if (mainNomMm != nomIn[0] || BranchWidthDiffersFromTrunk(w0, mainW))
                        {
                            double halfOutTee = ReducerPlacementHalfOutFromCenterline(db, tickLen, mainW);
                            Point2d cur0 = lat.Sprinklers[0];
                            Point2d wedgeTee = new Point2d(attach.X, attach.Y);
                            PlaceOneReducer(
                                wedgeTee,
                                mainW,
                                w0,
                                halfOutTee,
                                teeFiberPlacement: allowMainOuterFiberPlacement,
                                attach,
                                cur0);
                        }
                    }

                    // Branch joints at s[j] between segment into s[j] and segment s[j]→s[j+1] (skip tip: no downstream segment).
                    for (int j = 0; j < m - 1; j++)
                    {
                        if (nomIn[j] == nomIn[j + 1])
                            continue;

                        Point2d upstreamPt = j == 0 ? attach : lat.Sprinklers[j - 1];
                        Point2d joint = lat.Sprinklers[j];

                        double beforeWj = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nomIn[j], mainW);
                        double afterWj = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nomIn[j + 1], mainW);

                        double dxAlong = joint.X - upstreamPt.X;
                        double dyAlong = joint.Y - upstreamPt.Y;
                        double lenAlong = Math.Sqrt(dxAlong * dxAlong + dyAlong * dyAlong);
                        double ux = lenAlong > 1e-9 ? dxAlong / lenAlong : 1.0;
                        double uy = lenAlong > 1e-9 ? dyAlong / lenAlong : 0.0;

                        bool smallerDownstream = nomIn[j + 1] < nomIn[j];
                        double svx;
                        double svy;
                        if (smallerDownstream)
                        {
                            var nxt = lat.Sprinklers[j + 1];
                            svx = nxt.X - joint.X;
                            svy = nxt.Y - joint.Y;
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

                        // On the head circle, on the side of the smaller pipe; block center lies toward the bigger pipe.
                        double halfLenPre = ComputeReducerWedgeHalfLengthDu(
                            Math.Max(
                                reducerHalf * 2.0,
                                Math.Max(Math.Max(beforeWj, afterWj) * 1.35 + Math.Abs(beforeWj - afterWj) * 0.25, 0)),
                            beforeWj,
                            afterWj);
                        Point2d radiusPt = new Point2d(
                            joint.X + svx * radiusDu,
                            joint.Y + svy * radiusDu);
                        Point2d wedgeJoint = new Point2d(
                            radiusPt.X - svx * halfLenPre,
                            radiusPt.Y - svy * halfLenPre);

                        PlaceOneReducer(
                            wedgeJoint,
                            beforeWj,
                            afterWj,
                            halfOutPlaceForWedgeLen: 0,
                            teeFiberPlacement: false,
                            upstreamPt,
                            joint);
                    }
                }

                tr.Commit();
                errorMessage = "Reducers placed: " + reducersDrawn + ".";
                return true;
            }
        }

        internal static bool TryAttachBranchesForZone(
            Document doc,
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            out string errorMessage,
            bool routeBranchPipesFromConnectorFirst = false)
        {
            errorMessage = null;
            if (doc == null || db == null || zone == null || zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            if (!TryComputeLateralsForZone(
                    db,
                    zone,
                    zoneRing,
                    zoneBoundaryHandleHex,
                    routeBranchPipesFromConnectorFirst,
                    ObjectId.Null,
                    out List<Lateral> laterals,
                    out bool trunkHorizontal,
                    out double trunkAxis,
                    out _,
                    out _,
                    out bool downstreamPositive,
                    out double mainW,
                    out double clusterTol,
                    out int skippedOutsideTrunkSpan,
                    out errorMessage))
                return false;

            List<IList<Point2d>> branchClipRings = BuildBranchClipRingsUnion(db, zoneRing, zoneBoundaryHandleHex);

            double tickLen = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0)
                    tickLen = t;
            }
            catch { /* ignore */ }

            SprinklerHeadReader2d.TryEnumerateSprinklerHeadEntitiesInZoneForRouting(db, zoneRing, zoneBoundaryHandleHex, out var allHeadEntities, out _);
            List<ObjectId> skippedNoBranchIds = ComputeHeadsWithoutBranchConnectivity(allHeadEntities, laterals, clusterTol);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                // Erase any branches from previous runs before drawing fresh ones.
                ErasePriorBranchPipingForZone(tr, db, zoneBoundaryHandleHex, zoneRing);

                EraseMainPipeScheduleLabelsNearBranchTapsOnMain(
                    tr,
                    db,
                    zoneBoundaryHandleHex,
                    laterals,
                    clusterTol,
                    trunkHorizontal);

                ObjectId highlightNoBranchLayerId = SprinklerLayers.EnsureMcdNoBranchPipeHighlightLayer(tr, db);
                ObjectId defaultSprinklerLayerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);

                if (allHeadEntities != null)
                {
                    foreach (var (_, oid) in allHeadEntities)
                    {
                        if (oid.IsErased) continue;
                        Entity he = null;
                        try { he = tr.GetObject(oid, OpenMode.ForWrite, false) as Entity; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                        if (he == null)
                            continue;
                        if (SprinklerLayers.IsNoBranchPipeHighlightLayerName(he.Layer))
                            he.LayerId = defaultSprinklerLayerId;
                        he.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    }
                }

                ObjectId branchMainLayerId = SprinklerLayers.EnsureBranchPipeLayer(tr, db);
                ObjectId branchConnectorLayerId = SprinklerLayers.EnsureMcdConnectorBranchPipeLayer(tr, db);
                ObjectId mainPipeLayerId = SprinklerLayers.EnsureMcdMainPipeLayer(tr, db);
                ObjectId branchLabelLayerId = SprinklerLayers.EnsureBranchLabelLayer(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int segmentsDrawn = 0;
                int ticksDrawn = 0;
                int labelsDrawn = 0;
                bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHandleHex);
                double boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db);
                double labelOffsetDu = Math.Max(tickLen * 0.65, boundaryW * 0.08);
                double labelTextHeight = Math.Max(boundaryW * 0.22, tickLen * 0.55);

                double shaftClearDu = Math.Max(tickLen * 0.35, 1e-6);
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.08, out double sc) && sc > shaftClearDu)
                        shaftClearDu = sc;
                }
                catch { /* ignore */ }
                var shaftObstacles = BranchPipeShaftDetour2d.BuildShaftObstacles(db, zone, shaftClearDu);

                foreach (var lat in laterals)
                {
                    if (lat.Sprinklers.Count == 0)
                        continue;

                    ObjectId pipeLayerId = lat.FromConnectorFeed ? branchConnectorLayerId : branchMainLayerId;

                    int m = lat.Sprinklers.Count;
                    Point2d attachPt = lat.AttachPoint;
                    Point2d prev = attachPt;
                    int lastNominalMm = -1;
                    double exitDu = ComputeBranchExitDistanceDu(tickLen, mainW, clusterTol);

                    bool useConnectorSingleHeadStep =
                        lat.FromConnectorFeed &&
                        m == 1 &&
                        (lat.SubSprinklers == null || lat.SubSprinklers.Count == 0);

                    if (useConnectorSingleHeadStep)
                    {
                        Point2d cur = lat.Sprinklers[0];

                        var stepWpts = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                            prev, cur, shaftObstacles, branchClipRings, clusterTol);
                        int stepSegmentsDrawn = 0;
                        for (int wi = 0; wi + 1 < stepWpts.Count; wi++)
                        {
                            int drew = DrawClippedBranchSegment(
                                ms, tr, db, branchClipRings, zone, tagZone, zoneBoundaryHandleHex,
                                stepWpts[wi], stepWpts[wi + 1], mainPipeLayerId, mainW, tagAsConnector: true, shaftObstacles: shaftObstacles);
                            segmentsDrawn += drew;
                            stepSegmentsDrawn += drew;
                        }

                        // If nothing was drawn (fully clipped/blocked), do not leave a floating tick.
                        if (stepSegmentsDrawn <= 0)
                            continue;

                        Point2d tickFrom = attachPt;
                        if (stepWpts.Count >= 2)
                            tickFrom = stepWpts[stepWpts.Count - 2];
                        double sdxS = cur.X - tickFrom.X;
                        double sdyS = cur.Y - tickFrom.Y;
                        bool stepSegVertical = Math.Abs(sdyS) >= Math.Abs(sdxS);
                        var tickStep = MakePerpendicularTick(cur, stepSegVertical, tickLen, zone.Elevation);
                        tickStep.SetDatabaseDefaults(db);
                        tickStep.LayerId = mainPipeLayerId;
                        tickStep.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        if (tagZone)
                            SprinklerXData.ApplyZoneBoundaryTag(tickStep, zoneBoundaryHandleHex);
                        ms.AppendEntity(tickStep);
                        tr.AddNewlyCreatedDBObject(tickStep, true);
                        ticksDrawn++;
                    }
                    else
                    for (int i = 0; i < m; i++)
                    {
                        Point2d cur = lat.Sprinklers[i];
                        // For sub-main spine laterals the "sprinklers" are T-junction points; use the
                        // pre-computed cumulative head count so the spine is sized correctly.
                        int planHeadsServedOnSegment =
                            lat.IsSubMainSpine && lat.SpineSegmentHeadCounts != null && i < lat.SpineSegmentHeadCounts.Length
                            ? lat.SpineSegmentHeadCounts[i]
                            : PlanSprinklersServedOnBranchSegmentFromTip(i, m);

                        if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(planHeadsServedOnSegment, out int nominalMm))
                        {
                            errorMessage =
                                "Branch sizing: could not resolve nominal pipe for plan sprinkler count " +
                                planHeadsServedOnSegment + " on this segment.";
                            return false;
                        }

                        double w = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm, mainW);

                        var legWpts = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                            prev, cur, shaftObstacles, branchClipRings, clusterTol);
                        int legSegmentsDrawn = 0;
                        for (int wi = 0; wi + 1 < legWpts.Count; wi++)
                        {
                            int drew = DrawClippedBranchSegment(
                                ms, tr, db, branchClipRings, zone, tagZone, zoneBoundaryHandleHex,
                                legWpts[wi], legWpts[wi + 1], pipeLayerId, w, tagAsConnector: false, shaftObstacles: shaftObstacles);
                            segmentsDrawn += drew;
                            legSegmentsDrawn += drew;
                        }

                        // Avoid orphan labels/ticks and broken chain advance when this leg produced no geometry.
                        if (legSegmentsDrawn <= 0)
                            continue;

                        {
                            var mid = new Point2d((prev.X + cur.X) * 0.5, (prev.Y + cur.Y) * 0.5);
                            double dx = cur.X - prev.X;
                            double dy = cur.Y - prev.Y;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            double px = 0, py = 1;
                            if (len > 1e-9)
                            {
                                // Keep branch Ø labels consistently on one side:
                                // - horizontal segments: above (+Y)
                                // - vertical segments: right (+X)
                                bool segVertical = Math.Abs(dy) >= Math.Abs(dx);
                                if (segVertical)
                                {
                                    px = 1;
                                    py = 0;
                                }
                                else
                                {
                                    px = 0;
                                    py = 1;
                                }
                            }

                            var pA = new Point2d(mid.X + px * labelOffsetDu, mid.Y + py * labelOffsetDu);
                            var pB = new Point2d(mid.X - px * labelOffsetDu, mid.Y - py * labelOffsetDu);
                            var ins2d = pA;

                            var mt = new MText();
                            mt.SetDatabaseDefaults(db);
                            mt.LayerId = branchLabelLayerId;
                            mt.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                            mt.Location = new Point3d(ins2d.X, ins2d.Y, zone.Elevation);
                            mt.Attachment = AttachmentPoint.MiddleCenter;
                            mt.TextHeight = labelTextHeight;
                            mt.Contents = "Ø" + nominalMm.ToString();
                            double rot = Math.Atan2(dy, dx);
                            if (rot > Math.PI * 0.5) rot -= Math.PI;
                            else if (rot < -Math.PI * 0.5) rot += Math.PI;
                            mt.Rotation = rot;
                            SprinklerXData.TagAsBranchPipeScheduleLabel(mt);
                            if (tagZone)
                                SprinklerXData.ApplyZoneBoundaryTag(mt, zoneBoundaryHandleHex);
                            ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            labelsDrawn++;
                        }

                        double sdx = cur.X - prev.X;
                        double sdy = cur.Y - prev.Y;
                        bool branchSegVertical = Math.Abs(sdy) >= Math.Abs(sdx);
                        var tick = MakePerpendicularTick(cur, branchSegVertical, tickLen, zone.Elevation);
                        tick.SetDatabaseDefaults(db);
                        tick.LayerId = pipeLayerId;
                        tick.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        if (tagZone)
                            SprinklerXData.ApplyZoneBoundaryTag(tick, zoneBoundaryHandleHex);
                        ms.AppendEntity(tick);
                        tr.AddNewlyCreatedDBObject(tick, true);
                        ticksDrawn++;

                        lastNominalMm = nominalMm;
                        prev = cur;
                    }

                    if (lat.SubSprinklers.Count > 0)
                    {
                        int nSub = Math.Min(lat.SubSprinklers.Count, 6);
                        int subNominalMm;
                        if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(nSub, out subNominalMm))
                            subNominalMm = Math.Max(lastNominalMm, 25);
                        double wSub = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, subNominalMm, mainW);

                        for (int si = 0; si < lat.SubSprinklers.Count; si++)
                        {
                            var sp = lat.SubSprinklers[si];
                            var tap = PickTapPointOnLateral(lat, sp);

                            var subWpts = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                                tap, sp, shaftObstacles, branchClipRings, clusterTol);
                            for (int wi = 0; wi + 1 < subWpts.Count; wi++)
                            {
                                int drew = DrawClippedBranchSegment(
                                    ms, tr, db, branchClipRings, zone, tagZone, zoneBoundaryHandleHex,
                                    subWpts[wi], subWpts[wi + 1], pipeLayerId, wSub, tagAsConnector: false, shaftObstacles: shaftObstacles);
                                segmentsDrawn += drew;
                            }

                            double sdxSub = sp.X - tap.X;
                            double sdySub = sp.Y - tap.Y;
                            bool subSegVertical = Math.Abs(sdySub) >= Math.Abs(sdxSub);
                            var tickSub = MakePerpendicularTick(sp, subSegVertical, tickLen, zone.Elevation);
                            tickSub.SetDatabaseDefaults(db);
                            tickSub.LayerId = pipeLayerId;
                            tickSub.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                            if (tagZone)
                                SprinklerXData.ApplyZoneBoundaryTag(tickSub, zoneBoundaryHandleHex);
                            ms.AppendEntity(tickSub);
                            tr.AddNewlyCreatedDBObject(tickSub, true);
                            ticksDrawn++;
                        }
                    }
                }

                var headsRecoveredByRoomSubMain = new HashSet<ObjectId>();
                TryAutoRoomSubMainForSkippedHeadsInRooms(
                    doc,
                    tr,
                    db,
                    ms,
                    zoneRing,
                    zoneBoundaryHandleHex,
                    skippedNoBranchIds,
                    headsRecoveredByRoomSubMain,
                    out int subMainRoomsOk,
                    out int subMainFeederVerts,
                    out int subMainBranchPls);

                foreach (var oid in skippedNoBranchIds)
                {
                    if (headsRecoveredByRoomSubMain.Contains(oid))
                        continue;
                    if (oid.IsErased) continue;
                    Entity he = null;
                    try { he = tr.GetObject(oid, OpenMode.ForWrite, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (he != null)
                    {
                        he.LayerId = highlightNoBranchLayerId;
                        he.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    }
                }

                tr.Commit();
                errorMessage =
                    "Branches attached. Laterals: " + laterals.Count +
                    ", segments: " + segmentsDrawn +
                    ", markers: " + ticksDrawn +
                    ", labels: " + labelsDrawn + "." +
                    (skippedOutsideTrunkSpan > 0
                        ? " Skipped " + skippedOutsideTrunkSpan + " sprinkler(s) outside main pipe span (highlighted yellow)."
                        : string.Empty) +
                    (subMainRoomsOk > 0
                        ? " Room sub-main: " + subMainRoomsOk + " room outline(s) routed (feeder verts≈" +
                          subMainFeederVerts + ", branch polylines=" + subMainBranchPls + ")."
                        : string.Empty) +
                    (skippedNoBranchIds.Count > 0
                        ? " " + (skippedNoBranchIds.Count - headsRecoveredByRoomSubMain.Count) +
                          " head(s) still have no branch pipe — placed on layer \"" +
                          SprinklerLayers.McdNoBranchPipeHighlightLayer + "\" (yellow)."
                        : string.Empty);
                return true;
            }
        }

        /// <summary>
        /// Heads that normal laterals did not assign: if they lie in an <see cref="SprinklerLayers.McdFloorBoundaryLayer"/>
        /// room inside the zone, attempts an L-shaped feeder from the existing trunk main plus in-room branches.
        /// </summary>
        private static void TryAutoRoomSubMainForSkippedHeadsInRooms(
            Document doc,
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            List<ObjectId> skippedNoBranchIds,
            HashSet<ObjectId> headIdsRecoveredFromSubMain,
            out int roomsRoutedOk,
            out int feederVertsSum,
            out int branchPolylinesSum)
        {
            roomsRoutedOk = 0;
            feederVertsSum = 0;
            branchPolylinesSum = 0;
            if (skippedNoBranchIds == null || skippedNoBranchIds.Count == 0) return;
            if (headIdsRecoveredFromSubMain == null) return;
            if (string.IsNullOrWhiteSpace(zoneBoundaryHandleHex)) return;

            if (!TryFindMainPipePolylinesInZone(db, zoneRing, out List<ObjectId> trunkIdsSnapshot, out _) ||
                trunkIdsSnapshot == null || trunkIdsSnapshot.Count == 0)
                return;

            var tapCandidates = new List<ObjectId>(trunkIdsSnapshot);

            var skippedPts = new List<(ObjectId id, Point2d pt)>();
            foreach (var oid in skippedNoBranchIds)
            {
                if (oid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(oid, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent)) continue;

                Point2d p = default;
                if (ent is Circle c)
                    p = new Point2d(c.Center.X, c.Center.Y);
                else if (ent is BlockReference br)
                {
                    if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br)) continue;
                    p = new Point2d(br.Position.X, br.Position.Y);
                }
                else
                    continue;

                skippedPts.Add((oid, p));
            }

            if (skippedPts.Count == 0) return;

            var floorRooms = new List<(ObjectId id, Polyline pl, List<Point2d> ring, double area)>();
            foreach (ObjectId rid in ms)
            {
                if (rid.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(rid, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || !pl.Closed || pl.NumberOfVertices < 3) continue;
                string lay = pl.Layer ?? string.Empty;
                if (!string.Equals(lay.Trim(), SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                if (ring == null || ring.Count < 3) continue;

                Point2d cen = RingCentroid2d(ring);
                if (!FindShaftsInsideBoundary.IsPointInPolygonRing(zoneRing, cen))
                    continue;

                double a = double.PositiveInfinity;
                try { a = Math.Abs(pl.Area); } catch { a = double.PositiveInfinity; }
                if (!(a > 0) || double.IsInfinity(a)) continue;

                floorRooms.Add((rid, pl, ring, a));
            }

            if (floorRooms.Count == 0) return;

            var roomToHeads = new Dictionary<ObjectId, HashSet<ObjectId>>();
            foreach (var (id, pt) in skippedPts)
            {
                ObjectId bestRid = ObjectId.Null;
                double bestArea = double.PositiveInfinity;
                for (int ri = 0; ri < floorRooms.Count; ri++)
                {
                    var fr = floorRooms[ri];
                    if (!FindShaftsInsideBoundary.IsPointInPolygonRing(fr.ring, pt))
                        continue;
                    if (fr.area < bestArea)
                    {
                        bestArea = fr.area;
                        bestRid = fr.id;
                    }
                }

                if (bestRid.IsNull) continue;

                if (!roomToHeads.TryGetValue(bestRid, out var hs))
                {
                    hs = new HashSet<ObjectId>();
                    roomToHeads[bestRid] = hs;
                }

                hs.Add(id);
            }

            Editor ed = doc?.Editor;
            foreach (var kvp in roomToHeads)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;

                Polyline roomPl = null;
                try { roomPl = tr.GetObject(kvp.Key, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { roomPl = null; }
                if (roomPl == null) continue;

                List<Point2d> roomRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(roomPl);
                if (roomRing == null || roomRing.Count < 3) continue;

                Point2d anchor = RingCentroid2d(roomRing);
                var anchor3 = new Point3d(anchor.X, anchor.Y, roomPl.Elevation);

                Polyline bestMain = null;
                double bestD2 = double.PositiveInfinity;
                foreach (var mid in tapCandidates)
                {
                    if (mid.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(mid, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (!RoomSubMainBranchRouting.IsEligibleTapMain(pl)) continue;
                    Point3d cp = pl.GetClosestPointTo(anchor3, false);
                    double dx = cp.X - anchor.X;
                    double dy = cp.Y - anchor.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestMain = pl;
                    }
                }

                if (bestMain == null) continue;

                if (!RoomSubMainBranchRouting.TryRouteFeederAndBranches(
                        tr,
                        db,
                        ms,
                        roomPl,
                        bestMain,
                        roomRing,
                        zoneRing,
                        zoneBoundaryHandleHex,
                        kvp.Value,
                        out int fv,
                        out int bc,
                        out List<ObjectId> recv,
                        out string err))
                {
                    if (ed != null && !string.IsNullOrWhiteSpace(err))
                        ed.WriteMessage("\nRoom sub-main (auto) skipped one room: " + err + "\n");
                    continue;
                }

                if (bc <= 0) continue;

                roomsRoutedOk++;
                feederVertsSum += fv;
                branchPolylinesSum += bc;
                if (recv != null)
                {
                    for (int i = 0; i < recv.Count; i++)
                        headIdsRecoveredFromSubMain.Add(recv[i]);
                }
            }
        }

        private static Point2d RingCentroid2d(List<Point2d> ring)
        {
            double sx = 0, sy = 0;
            int n = ring?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                sx += ring[i].X;
                sy += ring[i].Y;
            }

            double d = Math.Max(1, n);
            return new Point2d(sx / d, sy / d);
        }

        private enum BranchRole
        {
            PrimaryBranch,
            SubBranch,
            Spine,
            BypassSpine,
            FanoutArm,
        }

        private sealed class Lateral
        {
            public Point2d AttachPoint;
            public List<Point2d> Sprinklers;
            public List<Point2d> SubSprinklers = new List<Point2d>();
            public bool IsTopOrRight; // true for top (above trunk) in rotated space; false for bottom/left
            public double AxisValue;  // X (for vertical laterals in build space); used to find neighbor host
            /// <summary>True when this lateral was built from the shaft→main connector (Route branch pipe 2); drawn white.</summary>
            public bool FromConnectorFeed;
            /// <summary>Local trunk tangent at the attach point (WCS plan), for polyline trunks.</summary>
            public Vector2d TrunkTangent;
            /// <summary>Branch progression axis (WCS plan). Sprinklers are sorted by projection along this axis.</summary>
            public Vector2d BranchAxis;
            /// <summary>
            /// True when this lateral is the perpendicular sub-main spine that connects the trunk
            /// endpoint to Group B rows whose grid line does not intersect the slanted trunk.
            /// The <see cref="Sprinklers"/> list contains spine T-junction points (not actual heads).
            /// </summary>
            public bool IsSubMainSpine;
            /// <summary>
            /// For spine laterals only: cumulative actual head count served by each spine segment.
            /// <c>SpineSegmentHeadCounts[i]</c> is the total number of sprinkler heads carried from
            /// spine segment i to the end of the spine, used for correct NFPA pipe sizing.
            /// </summary>
            public int[] SpineSegmentHeadCounts;
            /// <summary>
            /// Hierarchical role of this lateral. Only <see cref="BranchRole.PrimaryBranch"/>
            /// laterals are eligible as parent hosts for shaft-bypass rerouting.
            /// </summary>
            public BranchRole Role = BranchRole.PrimaryBranch;
        }

        /// <summary>
        /// After routing, heads whose insertion point does not match any served sprinkler on a lateral (main or sub-branch).
        /// </summary>
        private static List<ObjectId> ComputeHeadsWithoutBranchConnectivity(
            List<(Point2d pt, ObjectId id)> allHeads,
            List<Lateral> laterals,
            double matchTol)
        {
            var ids = new List<ObjectId>();
            if (allHeads == null || allHeads.Count == 0 || laterals == null)
                return ids;

            var served = new List<Point2d>();
            foreach (var lat in laterals)
            {
                // Spine laterals hold T-junction points, not actual head positions — skip them.
                if (lat?.IsSubMainSpine == true)
                    continue;

                if (lat?.Sprinklers != null)
                {
                    foreach (var p in lat.Sprinklers)
                        served.Add(p);
                }

                if (lat?.SubSprinklers != null)
                {
                    foreach (var p in lat.SubSprinklers)
                        served.Add(p);
                }
            }

            double t = matchTol > 0 ? matchTol : 1e-6;
            foreach (var (pt, oid) in allHeads)
            {
                bool matched = false;
                for (int si = 0; si < served.Count; si++)
                {
                    if (pt.GetDistanceTo(served[si]) <= t)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    ids.Add(oid);
            }

            return ids;
        }

        /// <summary>
        /// <para><paramref name="sprinklersAlongBranch"/> is ordered from the tap on the main toward the branch tip.</para>
        /// <para>For the segment ending at <paramref name="segmentEndIndex"/> (that sprinkler and all farther toward the tip),
        /// returns how many plan sprinklers are fed through this pipe when sizing from the <strong>end</strong> of the branch
        /// back toward the main (tip segment = 1 head, each step toward main adds one). Used directly as the pipe schedule count.</para>
        /// </summary>
        private static int PlanSprinklersServedOnBranchSegmentFromTip(int segmentEndIndex, int sprinklersAlongBranch)
        {
            return sprinklersAlongBranch - segmentEndIndex;
        }

        /// <summary>Plan CCW 90° about origin: (x,y) → (-y, x).</summary>
        private static Point2d Rotate90CcwWcs(Point2d p) => new Point2d(-p.Y, p.X);

        /// <summary>Inverse (CW 90°): (x,y) → (y, -x).</summary>
        private static Point2d Rotate90CwWcs(Point2d p) => new Point2d(p.Y, -p.X);

        private static double ComputeBranchExitDistanceDu(double tickLen, double mainW, double clusterTol)
        {
            // Keep this small but visible; it just establishes correct tee direction on slanted trunks.
            double a = Math.Max(1e-6, tickLen * 0.35);
            double b = Math.Max(1e-6, mainW * 1.50);
            double c = Math.Max(1e-6, clusterTol * 1.25);
            return Math.Max(a, Math.Max(b, c));
        }

        private static Point2d ComputeBranchExitPoint(Point2d attach, Point2d toward, Vector2d branchAxis, double exitDu, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;
            Vector2d dir = branchAxis;
            if (dir.Length <= te)
            {
                var v = toward - attach;
                if (v.Length <= te)
                    return attach;
                dir = v.GetNormal();
            }
            else
            {
                dir = dir.GetNormal();
            }

            var toSpr = toward - attach;
            if (toSpr.Length > te && dir.DotProduct(toSpr) < 0)
                dir = -dir;

            double step = Math.Min(Math.Max(exitDu, te), Math.Max(toSpr.Length, exitDu));
            return attach + dir.MultiplyBy(step);
        }

        private static List<IList<Point2d>> BuildBranchClipRingsUnion(Database db, List<Point2d> zoneRing, string zoneBoundaryHandleHex)
        {
            var clip = new List<IList<Point2d>> { zoneRing };
            if (string.IsNullOrWhiteSpace(zoneBoundaryHandleHex))
                return clip;
            if (SprinklerHeadReader2d.TryGetFloorRoomRingsParentedToZone(db, zoneRing, zoneBoundaryHandleHex.Trim(), out var rooms) &&
                rooms != null &&
                rooms.Count > 0)
            {
                for (int i = 0; i < rooms.Count; i++)
                    clip.Add(rooms[i]);
            }

            return clip;
        }

        private static int DrawClippedBranchSegment(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            IList<IList<Point2d>> clipRings,
            Polyline zone,
            bool tagZone,
            string zoneBoundaryHandleHex,
            Point2d a,
            Point2d b,
            ObjectId layerId,
            double widthDu,
            bool tagAsConnector,
            IList<(Point2d min, Point2d max)> shaftObstacles = null)
        {
            int created = 0;
            var clipIntervals = RingGeometry.ClipSegmentToRingsUnion(a, b, clipRings);
            if (clipIntervals == null || clipIntervals.Count == 0)
                clipIntervals = new List<(double, double)> { (0.0, 1.0) };

            double segDx = b.X - a.X;
            double segDy = b.Y - a.Y;

            foreach (var (t0, t1) in clipIntervals)
            {
                var p0 = new Point2d(a.X + t0 * segDx, a.Y + t0 * segDy);
                var p1 = new Point2d(a.X + t1 * segDx, a.Y + t1 * segDy);
                if (SegmentIntersectsAnyBox(p0, p1, shaftObstacles))
                    continue;

                var seg = new Polyline();
                seg.SetDatabaseDefaults(db);
                seg.LayerId = layerId;
                seg.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                seg.ConstantWidth = widthDu;
                seg.Elevation = zone.Elevation;
                seg.Normal = zone.Normal;
                seg.AddVertexAt(0, p0, 0, 0, 0);
                seg.AddVertexAt(1, p1, 0, 0, 0);
                seg.Closed = false;
                if (tagAsConnector)
                    SprinklerXData.TagAsConnector(seg);
                if (tagZone)
                    SprinklerXData.ApplyZoneBoundaryTag(seg, zoneBoundaryHandleHex);
                ms.AppendEntity(seg);
                tr.AddNewlyCreatedDBObject(seg, true);
                created++;
            }
            return created;
        }

        private static bool SegmentIntersectsAnyBox(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes)
        {
            if (boxes == null || boxes.Count == 0)
                return false;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (SegmentIntersectsAabb(a, b, xmin, xmax, ymin, ymax))
                    return true;
            }
            return false;
        }

        private static bool SegmentIntersectsAabb(
            Point2d a,
            Point2d b,
            double xmin,
            double xmax,
            double ymin,
            double ymax)
        {
            if (PointInAabb(a, xmin, xmax, ymin, ymax) || PointInAabb(b, xmin, xmax, ymin, ymax))
                return true;

            var c0 = new Point2d(xmin, ymin);
            var c1 = new Point2d(xmax, ymin);
            var c2 = new Point2d(xmax, ymax);
            var c3 = new Point2d(xmin, ymax);

            return SegmentsIntersect(a, b, c0, c1) ||
                   SegmentsIntersect(a, b, c1, c2) ||
                   SegmentsIntersect(a, b, c2, c3) ||
                   SegmentsIntersect(a, b, c3, c0);
        }

        private static bool PointInAabb(Point2d p, double xmin, double xmax, double ymin, double ymax)
            => p.X >= xmin && p.X <= xmax && p.Y >= ymin && p.Y <= ymax;

        private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double o1 = Orientation(a, b, c);
            double o2 = Orientation(a, b, d);
            double o3 = Orientation(c, d, a);
            double o4 = Orientation(c, d, b);

            if ((o1 > 0 && o2 < 0 || o1 < 0 && o2 > 0) &&
                (o3 > 0 && o4 < 0 || o3 < 0 && o4 > 0))
                return true;

            const double eps = 1e-9;
            if (Math.Abs(o1) <= eps && OnSegment(a, b, c)) return true;
            if (Math.Abs(o2) <= eps && OnSegment(a, b, d)) return true;
            if (Math.Abs(o3) <= eps && OnSegment(c, d, a)) return true;
            if (Math.Abs(o4) <= eps && OnSegment(c, d, b)) return true;
            return false;
        }

        private static double Orientation(Point2d a, Point2d b, Point2d c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static bool OnSegment(Point2d a, Point2d b, Point2d p)
        {
            const double eps = 1e-9;
            return p.X >= Math.Min(a.X, b.X) - eps &&
                   p.X <= Math.Max(a.X, b.X) + eps &&
                   p.Y >= Math.Min(a.Y, b.Y) - eps &&
                   p.Y <= Math.Max(a.Y, b.Y) + eps;
        }

        private static double ProjectAlong(Vector2d axis, Point2d origin, Point2d p)
        {
            if (axis.Length <= 1e-9)
                return p.GetDistanceTo(origin);
            var v = p - origin;
            return axis.DotProduct(v);
        }

        private static Vector2d OrientBranchAxisTowardSprinklers(Vector2d axis, Point2d attach, List<Point2d> sprinklers, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;
            if (axis.Length <= te)
                return new Vector2d(1, 0);

            var a = axis.GetNormal();
            double sum = 0;
            int n = 0;
            if (sprinklers != null)
            {
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    var v = sprinklers[i] - attach;
                    if (v.Length <= te) continue;
                    sum += a.DotProduct(v);
                    n++;
                }
            }

            return n > 0 && sum < 0 ? -a : a;
        }

        private static bool TryFindGridAxisAttachOnPolyline(
            List<Point2d> poly,
            List<Point2d> zoneRing,
            bool branchHorizontal,
            double gridKey,
            List<Point2d> sprinklers,
            double tol,
            out Point2d attach)
        {
            attach = default;
            double te = tol > 0 ? tol : 1e-6;
            if (poly == null || poly.Count < 2)
                return false;

            double target = 0;
            int count = 0;
            if (sprinklers != null)
            {
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    target += branchHorizontal ? sprinklers[i].X : sprinklers[i].Y;
                    count++;
                }
            }
            if (count > 0)
                target /= count;

            bool found = false;
            double best = double.MaxValue;
            Point2d bestPoint = default;

            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                Point2d p;
                if (branchHorizontal)
                {
                    double dyA = a.Y - gridKey;
                    double dyB = b.Y - gridKey;
                    if (Math.Abs(dyA) <= te && Math.Abs(dyB) <= te)
                    {
                        double lo = Math.Min(a.X, b.X);
                        double hi = Math.Max(a.X, b.X);
                        double x = Math.Max(lo, Math.Min(hi, target));
                        p = new Point2d(x, gridKey);
                    }
                    else if ((dyA < -te && dyB < -te) || (dyA > te && dyB > te))
                    {
                        continue;
                    }
                    else
                    {
                        double denom = b.Y - a.Y;
                        if (Math.Abs(denom) <= te)
                            continue;
                        double u = (gridKey - a.Y) / denom;
                        if (u < -1e-9 || u > 1.0 + 1e-9)
                            continue;
                        p = new Point2d(a.X + (b.X - a.X) * u, gridKey);
                    }

                    double d = Math.Abs(p.X - target);
                    if (zoneRing != null && zoneRing.Count >= 3 && !PointInPolygon(zoneRing, p))
                        d += te * 100.0;
                    if (d < best)
                    {
                        best = d;
                        bestPoint = p;
                        found = true;
                    }
                }
                else
                {
                    double dxA = a.X - gridKey;
                    double dxB = b.X - gridKey;
                    if (Math.Abs(dxA) <= te && Math.Abs(dxB) <= te)
                    {
                        double lo = Math.Min(a.Y, b.Y);
                        double hi = Math.Max(a.Y, b.Y);
                        double y = Math.Max(lo, Math.Min(hi, target));
                        p = new Point2d(gridKey, y);
                    }
                    else if ((dxA < -te && dxB < -te) || (dxA > te && dxB > te))
                    {
                        continue;
                    }
                    else
                    {
                        double denom = b.X - a.X;
                        if (Math.Abs(denom) <= te)
                            continue;
                        double u = (gridKey - a.X) / denom;
                        if (u < -1e-9 || u > 1.0 + 1e-9)
                            continue;
                        p = new Point2d(gridKey, a.Y + (b.Y - a.Y) * u);
                    }

                    double d = Math.Abs(p.Y - target);
                    if (zoneRing != null && zoneRing.Count >= 3 && !PointInPolygon(zoneRing, p))
                        d += te * 100.0;
                    if (d < best)
                    {
                        best = d;
                        bestPoint = p;
                        found = true;
                    }
                }
            }

            if (!found)
                return false;

            attach = bestPoint;
            return true;
        }

        private static Vector2d PickBranchAxisFromTangent(Point2d attach, List<Point2d> sprinklers, Vector2d tangent, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;

            Vector2d t = tangent;
            if (t.Length <= te)
            {
                // Fallback: infer direction from sprinkler spread.
                if (sprinklers != null && sprinklers.Count > 0)
                {
                    var v = sprinklers[0] - attach;
                    if (v.Length > te)
                        return v.GetNormal();
                }
                return new Vector2d(0, 1);
            }
            t = t.GetNormal();
            var perp = new Vector2d(-t.Y, t.X); // CCW 90°

            double sum = 0;
            int n = 0;
            if (sprinklers != null)
            {
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    var v = sprinklers[i] - attach;
                    if (v.Length <= te) continue;
                    sum += perp.DotProduct(v);
                    n++;
                }
            }

            if (n > 0 && sum < 0)
                perp = -perp;
            return perp;
        }

        private static Vector2d PolylineTangentAtAlong(List<Point2d> poly, double along, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;
            if (poly == null || poly.Count < 2)
                return new Vector2d(1, 0);

            double target = Math.Max(0, along);
            double accum = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                var v = b - a;
                double len = v.Length;
                if (len <= te)
                    continue;
                if (accum + len >= target)
                    return v.GetNormal();
                accum += len;
            }

            // Past end: last non-degenerate segment.
            for (int i = poly.Count - 2; i >= 0; i--)
            {
                var v = poly[i + 1] - poly[i];
                if (v.Length > te)
                    return v.GetNormal();
            }
            return new Vector2d(1, 0);
        }

        private static Vector2d PolylineTangentNearPoint(List<Point2d> poly, Point2d p, double tol)
        {
            double te = tol > 0 ? tol : 1e-6;
            if (poly == null || poly.Count < 2)
                return new Vector2d(1, 0);

            double bestD2 = double.MaxValue;
            Vector2d bestT = new Vector2d(1, 0);

            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                if (len2 <= te * te)
                    continue;

                double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                if (t < 0) t = 0;
                if (t > 1) t = 1;

                double qx = a.X + t * dx;
                double qy = a.Y + t * dy;
                double ddx = qx - p.X;
                double ddy = qy - p.Y;
                double d2 = ddx * ddx + ddy * ddy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    bestT = new Vector2d(dx, dy).GetNormal();
                }
            }

            if (bestT.Length <= te)
                return new Vector2d(1, 0);
            return bestT;
        }

        /// <summary>
        /// Keeps only sprinklers whose orthogonal foot on the trunk lies on a clipped main segment inside the zone
        /// (same notion as main-pipe orthogonal coverage along the trunk axis).
        /// </summary>
        private static List<Point2d> FilterSprinklersOnTrunkSpans(
            List<Point2d> sprinklers,
            bool trunkHorizontal,
            List<(double lo, double hi)> trunkAlongSpansInZone,
            double spanTol)
        {
            if (sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();
            if (trunkAlongSpansInZone == null || trunkAlongSpansInZone.Count == 0)
                return new List<Point2d>();

            var kept = new List<Point2d>(sprinklers.Count);
            foreach (var p in sprinklers)
            {
                double along = trunkHorizontal ? p.X : p.Y;
                if (AlongCoordinateWithinAnySpan(along, trunkAlongSpansInZone, spanTol))
                    kept.Add(p);
            }
            return kept;
        }

        private static bool AlongCoordinateWithinAnySpan(double along, List<(double lo, double hi)> spans, double tol)
        {
            for (int i = 0; i < spans.Count; i++)
            {
                double lo = Math.Min(spans[i].lo, spans[i].hi) - tol;
                double hi = Math.Max(spans[i].lo, spans[i].hi) + tol;
                if (along >= lo && along <= hi)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the shaft→main connector polyline (tagged CONNECTOR) that lies in the zone; vertices define routing order.
        /// </summary>
        private static bool TryGetConnectorPathInZone(
            Database db,
            List<Point2d> zoneRing,
            out List<Point2d> path,
            out string errorMessage)
        {
            path = null;
            errorMessage = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                List<Point2d> bestPts = null;
                double bestLen = -1.0;
                foreach (ObjectId id in ms)
                {
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer))
                        continue;
                    if (!SprinklerXData.IsTaggedConnector(pl))
                        continue;
                    if (!PolylineHasSampleInsideZone(pl, zoneRing))
                        continue;

                    var pts = new List<Point2d>(pl.NumberOfVertices);
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var p3 = pl.GetPoint3dAt(i);
                        pts.Add(new Point2d(p3.X, p3.Y));
                    }

                    if (pts.Count < 2)
                        continue;

                    double len = SafeLength(pl);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestPts = pts;
                    }
                }

                if (bestPts != null)
                {
                    path = bestPts;
                    tr.Commit();
                    return true;
                }

                tr.Commit();
            }

            return false;
        }

        /// <summary>
        /// For Route branch pipe 2: a head is served by the connector if it falls within the along-span
        /// coverage of any axis-aligned connector segment (same window as <see cref="ConnectorSegmentClaimsSprinkler"/>).
        /// </summary>
        private static bool PointWouldRouteFromConnectorFirst(
            Point2d p,
            List<Point2d> connPath,
            double trunkSpanTol,
            double geomTol)
        {
            if (connPath == null || connPath.Count < 2)
                return false;
            double t = geomTol > 0 ? geomTol : 1e-6;
            for (int i = 0; i < connPath.Count - 1; i++)
            {
                if (ConnectorSegmentClaimsSprinkler(p, connPath[i], connPath[i + 1], trunkSpanTol, t))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an axis-aligned connector segment claims this sprinkler for Route branch pipe 2.
        /// Uses span along the segment only so all drops from a connector leg stay on the white connector-branch layer,
        /// even when the main trunk is closer in the perpendicular direction.
        /// </summary>
        private static bool ConnectorSegmentClaimsSprinkler(
            Point2d p,
            Point2d a,
            Point2d b,
            double spanTol,
            double geomTol)
        {
            double t = geomTol > 0 ? geomTol : 1e-6;
            bool horiz = Math.Abs(a.Y - b.Y) <= t * 10.0 && Math.Abs(a.X - b.X) > t * 10.0;
            bool vert = Math.Abs(a.X - b.X) <= t * 10.0 && Math.Abs(a.Y - b.Y) > t * 10.0;
            if (horiz)
            {
                double xa = Math.Min(a.X, b.X);
                double xb = Math.Max(a.X, b.X);
                if (p.X < xa - spanTol || p.X > xb + spanTol)
                    return false;
                return true;
            }

            if (vert)
            {
                double ya = Math.Min(a.Y, b.Y);
                double yb = Math.Max(a.Y, b.Y);
                if (p.Y < ya - spanTol || p.Y > yb + spanTol)
                    return false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Route branches off orthogonal connector segments first (in vertex order), then remaining heads off the main trunk.
        /// </summary>
        private static List<Lateral> BuildLateralsConnectorThenTrunk(
            List<Point2d> routingPool,
            List<Point2d> connPath,
            bool trunkHorizontal,
            double trunkAxis,
            List<(double lo, double hi)> trunkAlongSpansInZone,
            double trunkSpanTol,
            double clusterTol,
            double spacingDu,
            bool downstreamPositive,
            double geomTol)
        {
            var result = new List<Lateral>();
            var pool = new List<Point2d>(routingPool);
            double t = geomTol > 0 ? geomTol : 1e-6;

            for (int si = 0; si < connPath.Count - 1; si++)
            {
                var a = connPath[si];
                var b = connPath[si + 1];
                bool horiz = Math.Abs(a.Y - b.Y) <= t * 10.0 && Math.Abs(a.X - b.X) > t * 10.0;
                bool vert = Math.Abs(a.X - b.X) <= t * 10.0 && Math.Abs(a.Y - b.Y) > t * 10.0;
                if (!horiz && !vert)
                    continue;

                var onSeg = new List<Point2d>();
                var rest = new List<Point2d>(pool.Count);
                foreach (var p in pool)
                {
                    bool on = ConnectorSegmentClaimsSprinkler(p, a, b, trunkSpanTol, t);
                    if (on)
                        onSeg.Add(p);
                    else
                        rest.Add(p);
                }

                pool = rest;
                if (onSeg.Count == 0)
                    continue;

                if (horiz)
                {
                    double segY = 0.5 * (a.Y + b.Y);
                    var lat = BuildVerticalLateralsFromSprinklers(onSeg, segY, clusterTol, downstreamPositive);
                    CollapseMicroLateralsIntoSubBranches(lat, segY, spacingDu, clusterTol);
                    int zMark = result.Count;
                    result.AddRange(lat);
                    for (int zi = zMark; zi < result.Count; zi++)
                        result[zi].FromConnectorFeed = true;
                }
                else
                {
                    double xSeg = 0.5 * (a.X + b.X);
                    var rs = new List<Point2d>(onSeg.Count);
                    for (int j = 0; j < onSeg.Count; j++)
                        rs.Add(Rotate90CcwWcs(onSeg[j]));

                    var latR = BuildVerticalLateralsFromSprinklers(rs, xSeg, clusterTol, downstreamPositive);
                    CollapseMicroLateralsIntoSubBranches(latR, xSeg, spacingDu, clusterTol);
                    for (int li = 0; li < latR.Count; li++)
                    {
                        latR[li].AttachPoint = Rotate90CwWcs(latR[li].AttachPoint);
                        for (int pj = 0; pj < latR[li].Sprinklers.Count; pj++)
                            latR[li].Sprinklers[pj] = Rotate90CwWcs(latR[li].Sprinklers[pj]);
                        for (int pj = 0; pj < latR[li].SubSprinklers.Count; pj++)
                            latR[li].SubSprinklers[pj] = Rotate90CwWcs(latR[li].SubSprinklers[pj]);
                    }

                    int zMark2 = result.Count;
                    result.AddRange(latR);
                    for (int zi = zMark2; zi < result.Count; zi++)
                        result[zi].FromConnectorFeed = true;
                }
            }

            var trunkPool = FilterSprinklersOnTrunkSpans(pool, trunkHorizontal, trunkAlongSpansInZone, trunkSpanTol);
            if (trunkPool.Count == 0)
                return result;

            if (trunkHorizontal)
            {
                var latT = BuildVerticalLateralsFromSprinklers(trunkPool, trunkAxis, clusterTol, downstreamPositive);
                CollapseMicroLateralsIntoSubBranches(latT, trunkAxis, spacingDu, clusterTol);
                result.AddRange(latT);
            }
            else
            {
                var rs = new List<Point2d>(trunkPool.Count);
                for (int si = 0; si < trunkPool.Count; si++)
                    rs.Add(Rotate90CcwWcs(trunkPool[si]));

                double rTrunkY = trunkAxis;
                var latT = BuildVerticalLateralsFromSprinklers(rs, rTrunkY, clusterTol, downstreamPositive);
                CollapseMicroLateralsIntoSubBranches(latT, rTrunkY, spacingDu, clusterTol);
                for (int li = 0; li < latT.Count; li++)
                {
                    latT[li].AttachPoint = Rotate90CwWcs(latT[li].AttachPoint);
                    for (int pj = 0; pj < latT[li].Sprinklers.Count; pj++)
                        latT[li].Sprinklers[pj] = Rotate90CwWcs(latT[li].Sprinklers[pj]);
                    for (int pj = 0; pj < latT[li].SubSprinklers.Count; pj++)
                        latT[li].SubSprinklers[pj] = Rotate90CwWcs(latT[li].SubSprinklers[pj]);
                }

                result.AddRange(latT);
            }

            return result;
        }

        private static List<Point2d> BuildExpandedRectangleRingAroundPolyline(Polyline pl, double marginDu)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            int nv = pl.NumberOfVertices;
            for (int i = 0; i < nv; i++)
            {
                var p = pl.GetPoint3dAt(i);
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }

            minX -= marginDu;
            maxX += marginDu;
            minY -= marginDu;
            maxY += marginDu;
            return new List<Point2d>
            {
                new Point2d(minX, minY),
                new Point2d(maxX, minY),
                new Point2d(maxX, maxY),
                new Point2d(minX, maxY),
            };
        }

        private static Polyline CreateStubZonePolylineForElevation(Polyline mainPl)
        {
            var z = new Polyline();
            z.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            z.AddVertexAt(1, new Point2d(1, 0), 0, 0, 0);
            z.AddVertexAt(2, new Point2d(1, 1), 0, 0, 0);
            z.AddVertexAt(3, new Point2d(0, 1), 0, 0, 0);
            z.Closed = true;
            z.Elevation = mainPl.Elevation;
            z.Normal = mainPl.Normal;
            return z;
        }

        private static bool TryResolveMainPipeFromExplicitPolyline(
            Database db,
            ObjectId trunkId,
            List<Point2d> zoneRing,
            out bool trunkHorizontal,
            out double trunkAxis,
            out double trunkMin,
            out double trunkMax,
            out List<(double lo, double hi)> trunkAlongSpansInZone,
            out bool downstreamPositive,
            out double trunkWidth,
            out string errorMessage)
        {
            trunkHorizontal = true;
            trunkAxis = 0;
            trunkMin = 0;
            trunkMax = 0;
            trunkAlongSpansInZone = null;
            downstreamPositive = true;
            trunkWidth = 0;
            errorMessage = null;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Polyline pl = null;
                try { pl = tr.GetObject(trunkId, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { pl = null; }
                if (pl == null)
                {
                    errorMessage = "Selected entity is not a polyline.";
                    return false;
                }

                if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer))
                {
                    errorMessage = "Selected polyline is not on a main pipe layer.";
                    return false;
                }

                if (SprinklerXData.IsTaggedTrunkCap(pl))
                {
                    errorMessage = "Select the main run, not a trunk cap.";
                    return false;
                }

                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                if (tol <= 0) tol = 1e-6;

                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                int nv = pl.NumberOfVertices;
                for (int i = 0; i < nv; i++)
                {
                    var p = pl.GetPoint3dAt(i);
                    minX = Math.Min(minX, p.X);
                    maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxY = Math.Max(maxY, p.Y);
                }

                double spanX = maxX - minX;
                double spanY = maxY - minY;
                bool vert = spanX <= tol * 10.0 && spanY > tol * 10.0;
                bool horiz = spanY <= tol * 10.0 && spanX > tol * 10.0;
                if (!vert && !horiz)
                {
                    errorMessage = "Main pipe must be a straight horizontal or vertical run.";
                    return false;
                }

                bool isHorizontal = horiz;
                double axis = isHorizontal ? 0.5 * (minY + maxY) : 0.5 * (minX + maxX);
                double aMin = isHorizontal ? minX : minY;
                double aMax = isHorizontal ? maxX : maxY;

                var clip = isHorizontal
                    ? ClipHorizontalSegmentToZoneRing(axis, aMin, aMax, zoneRing, tol)
                    : ClipVerticalSegmentToZoneRing(axis, aMin, aMax, zoneRing, tol);

                if (clip == null || clip.Count == 0)
                {
                    errorMessage = "Main pipe does not overlap the expanded work rectangle.";
                    return false;
                }

                trunkHorizontal = isHorizontal;
                trunkAxis = axis;
                trunkAlongSpansInZone = clip;
                trunkMin = clip[0].lo;
                trunkMax = clip[0].hi;
                for (int si = 1; si < clip.Count; si++)
                {
                    if (clip[si].lo < trunkMin) trunkMin = clip[si].lo;
                    if (clip[si].hi > trunkMax) trunkMax = clip[si].hi;
                }

                trunkWidth = TryGetPolylineUniformWidth(pl, out double w) ? w : 0;
                if (!(trunkWidth > 0))
                    trunkWidth = TryGetPolylineAnyWidth(pl, out w) ? w : 0;
                if (!(trunkWidth > 0))
                    trunkWidth = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                if (!(trunkWidth > 0))
                    trunkWidth = 1.0;

                var t0 = pl.GetPoint3dAt(0);
                var t1 = pl.GetPoint3dAt(Math.Max(0, nv - 1));
                if (SprinklerXData.IsTaggedTrunk(pl))
                    downstreamPositive = trunkHorizontal ? (t1.X >= t0.X) : (t1.Y >= t0.Y);
                else
                    downstreamPositive = trunkHorizontal ? (t1.X >= t0.X) : (t1.Y >= t0.Y);

                tr.Commit();
                return true;
            }
        }

        private static bool TryFindMainPipeInZone(
            Database db,
            List<Point2d> zoneRing,
            out bool trunkHorizontal,
            out double trunkAxis,
            out double trunkMin,
            out double trunkMax,
            out List<(double lo, double hi)> trunkAlongSpansInZone,
            out bool downstreamPositive,
            out double trunkWidth,
            out string errorMessage)
        {
            trunkHorizontal = true;
            trunkAxis = 0;
            trunkMin = 0;
            trunkMax = 0;
            trunkAlongSpansInZone = null;
            downstreamPositive = true;
            trunkWidth = 0;
            errorMessage = null;

            var candidates = new List<ObjectId>();
            var taggedTrunks = new List<ObjectId>();
            var connectors = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;
                    if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                        continue;
                    if (ent is Polyline pl)
                    {
                        if (SprinklerXData.IsTaggedTrunkCap(pl))
                            continue;
                        if (SprinklerXData.IsTaggedConnector(pl))
                        {
                            if (PolylineHasSampleInsideZone(pl, zoneRing))
                                connectors.Add(id);
                            continue;
                        }
                        if (PolylineHasSampleInsideZone(pl, zoneRing))
                            candidates.Add(id);
                        if (SprinklerXData.IsTaggedTrunk(pl))
                            taggedTrunks.Add(id);
                    }
                }
                if (candidates.Count == 0)
                {
                    errorMessage = "No main pipe found inside this zone. Run Route main pipe first.";
                    return false;
                }

                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                if (tol <= 0) tol = 1e-6;

                var trunkPool = taggedTrunks.Count > 0 ? taggedTrunks : candidates;
                var tryOrder = OrderTrunkCandidatesByPreference(tr, trunkPool, taggedTrunks);

                ObjectId trunkId = ObjectId.Null;
                Polyline resolvedPl = null;
                List<(double lo, double hi)> spans = null;
                bool isHorizontal = true;
                double axis = 0, tMin = 0, tMax = 0;

                foreach (var id in tryOrder)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    if (taggedTrunks.Count == 0 && pl.NumberOfVertices != 2) continue;

                    double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                    int nv = pl.NumberOfVertices;
                    for (int i = 0; i < nv; i++)
                    {
                        var p = pl.GetPoint3dAt(i);
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }
                    double spanX = maxX - minX;
                    double spanY = maxY - minY;
                    bool vert = spanX <= tol * 10.0 && spanY > tol * 10.0;
                    bool horiz = spanY <= tol * 10.0 && spanX > tol * 10.0;
                    if (!vert && !horiz)
                        continue;

                    isHorizontal = horiz;
                    axis = isHorizontal ? 0.5 * (minY + maxY) : 0.5 * (minX + maxX);
                    double aMin = isHorizontal ? minX : minY;
                    double aMax = isHorizontal ? maxX : maxY;

                    var clip = isHorizontal
                        ? ClipHorizontalSegmentToZoneRing(axis, aMin, aMax, zoneRing, tol)
                        : ClipVerticalSegmentToZoneRing(axis, aMin, aMax, zoneRing, tol);

                    if (clip == null || clip.Count == 0)
                        continue;

                    trunkId = id;
                    resolvedPl = pl;
                    spans = clip;
                    tMin = clip[0].lo;
                    tMax = clip[0].hi;
                    for (int si = 1; si < clip.Count; si++)
                    {
                        if (clip[si].lo < tMin) tMin = clip[si].lo;
                        if (clip[si].hi > tMax) tMax = clip[si].hi;
                    }
                    trunkHorizontal = isHorizontal;
                    trunkAxis = axis;
                    trunkMin = tMin;
                    trunkMax = tMax;
                    trunkAlongSpansInZone = spans;
                    break;
                }

                if (trunkId.IsNull || resolvedPl == null || spans == null || spans.Count == 0)
                {
                    errorMessage =
                        "No main pipe segment lies in the interior of this zone boundary. " +
                        "Each zone needs its own main run inside the green boundary, or trim the main so only the piece inside the zone is used.";
                    return false;
                }

                var trunkPl = resolvedPl;
                int nv2 = trunkPl.NumberOfVertices;
                var t0 = trunkPl.GetPoint3dAt(0);
                var t1 = trunkPl.GetPoint3dAt(Math.Max(0, nv2 - 1));
                trunkWidth = TryGetPolylineUniformWidth(trunkPl, out double w) ? w : 0;
                if (!(trunkWidth > 0))
                    trunkWidth = TryGetPolylineAnyWidth(trunkPl, out w) ? w : 0;
                if (!(trunkWidth > 0))
                    trunkWidth = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                if (!(trunkWidth > 0))
                    trunkWidth = 1.0;

                bool trunkWasTagged = taggedTrunks.Count > 0;

                Point3d connOnTrunk = t0;
                bool haveConnector = false;
                double bestD = double.MaxValue;
                foreach (var id in connectors)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    double d = MinEndpointToTrunkDistance(pl, trunkPl, out var onTrunk);
                    if (d < bestD)
                    {
                        bestD = d;
                        connOnTrunk = onTrunk;
                        haveConnector = true;
                    }
                }

                if (trunkWasTagged)
                {
                    downstreamPositive = trunkHorizontal ? (t1.X >= t0.X) : (t1.Y >= t0.Y);
                }
                else
                {
                    if (!haveConnector)
                        connOnTrunk = t0;

                    bool nearIsStart = t0.DistanceTo(connOnTrunk) <= t1.DistanceTo(connOnTrunk);
                    var near = nearIsStart ? t0 : t1;
                    var far = nearIsStart ? t1 : t0;
                    downstreamPositive = trunkHorizontal ? (far.X >= near.X) : (far.Y >= near.Y);
                }

                tr.Commit();
                return true;
            }
        }

        /// <summary>Tagged trunks first (longest first), then untagged (longest first).</summary>
        private static List<ObjectId> OrderTrunkCandidatesByPreference(
            Transaction tr,
            List<ObjectId> trunkPool,
            List<ObjectId> taggedTrunks)
        {
            var taggedSet = new HashSet<ObjectId>(taggedTrunks);
            var list = new List<(ObjectId id, bool isTagged, double len)>();
            foreach (var id in trunkPool)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null) continue;
                double len = SafeLength(pl);
                list.Add((id, taggedSet.Contains(id), len));
            }
            list.Sort((a, b) =>
            {
                int c = b.isTagged.CompareTo(a.isTagged);
                if (c != 0) return c;
                return b.len.CompareTo(a.len);
            });
            var ids = new List<ObjectId>(list.Count);
            for (int i = 0; i < list.Count; i++)
                ids.Add(list[i].id);
            return ids;
        }

        /// <summary>Portion(s) of a horizontal segment y=<paramref name="y"/> from x=<paramref name="xa"/>..<paramref name="xb"/> that lie inside the zone.</summary>
        private static List<(double lo, double hi)> ClipHorizontalSegmentToZoneRing(
            double y,
            double xa,
            double xb,
            List<Point2d> ring,
            double eps)
        {
            double a = Math.Min(xa, xb);
            double b = Math.Max(xa, xb);
            var xs = new List<double> { a, b };
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = ring[i];
                var p1 = ring[(i + 1) % n];
                if (TryIntersectHorizontalLineWithSegment(y, p0, p1, eps, out double xHit))
                {
                    if (xHit >= a - eps && xHit <= b + eps)
                        xs.Add(xHit);
                }
            }
            xs.Sort();
            var uniq = new List<double>();
            for (int i = 0; i < xs.Count; i++)
            {
                if (uniq.Count == 0 || Math.Abs(xs[i] - uniq[uniq.Count - 1]) > eps * 2)
                    uniq.Add(xs[i]);
            }
            var intervals = new List<(double lo, double hi)>();
            for (int i = 0; i + 1 < uniq.Count; i++)
            {
                double u0 = uniq[i];
                double u1 = uniq[i + 1];
                if (u1 - u0 <= eps * 2)
                    continue;
                double mx = 0.5 * (u0 + u1);
                if (PointInPolygon(ring, new Point2d(mx, y)))
                    intervals.Add((u0, u1));
            }
            return intervals;
        }

        private static List<(double lo, double hi)> ClipVerticalSegmentToZoneRing(
            double x,
            double ya,
            double yb,
            List<Point2d> ring,
            double eps)
        {
            double a = Math.Min(ya, yb);
            double b = Math.Max(ya, yb);
            var ys = new List<double> { a, b };
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = ring[i];
                var p1 = ring[(i + 1) % n];
                if (TryIntersectVerticalLineWithSegment(x, p0, p1, eps, out double yHit))
                {
                    if (yHit >= a - eps && yHit <= b + eps)
                        ys.Add(yHit);
                }
            }
            ys.Sort();
            var uniq = new List<double>();
            for (int i = 0; i < ys.Count; i++)
            {
                if (uniq.Count == 0 || Math.Abs(ys[i] - uniq[uniq.Count - 1]) > eps * 2)
                    uniq.Add(ys[i]);
            }
            var intervals = new List<(double lo, double hi)>();
            for (int i = 0; i + 1 < uniq.Count; i++)
            {
                double u0 = uniq[i];
                double u1 = uniq[i + 1];
                if (u1 - u0 <= eps * 2)
                    continue;
                double my = 0.5 * (u0 + u1);
                if (PointInPolygon(ring, new Point2d(x, my)))
                    intervals.Add((u0, u1));
            }
            return intervals;
        }

        private static bool TryIntersectHorizontalLineWithSegment(double y, Point2d a, Point2d b, double eps, out double xHit)
        {
            xHit = 0;
            double dy = b.Y - a.Y;
            if (Math.Abs(dy) <= eps * 5)
                return false;
            double t = (y - a.Y) / dy;
            if (t < -eps || t > 1.0 + eps)
                return false;
            xHit = a.X + t * (b.X - a.X);
            return true;
        }

        private static bool TryIntersectVerticalLineWithSegment(double x, Point2d a, Point2d b, double eps, out double yHit)
        {
            yHit = 0;
            double dx = b.X - a.X;
            if (Math.Abs(dx) <= eps * 5)
                return false;
            double t = (x - a.X) / dx;
            if (t < -eps || t > 1.0 + eps)
                return false;
            yHit = a.Y + t * (b.Y - a.Y);
            return true;
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

        private static bool TryReadSprinklersInZone(
            Database db,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            double dedupeTol,
            out List<Point2d> sprinklers,
            out string errorMessage)
        {
            return SprinklerHeadReader2d.TryReadSprinklerHeadPointsForZoneRouting(db, zoneRing, zoneBoundaryHandleHex, dedupeTol, out sprinklers, out errorMessage);
        }

        private static bool TrySelectShaftBlock(Editor ed, Database db, out Point3d shaftPoint, out string errorMessage)
            => TrySelectShaftBlock(ed, db, out shaftPoint, out _, out errorMessage);

        private static bool TrySelectShaftBlock(Editor ed, Database db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string errorMessage)
        {
            shaftPoint = default;
            shaftEntityId = ObjectId.Null;
            errorMessage = null;
            if (ed == null || db == null)
            {
                errorMessage = "No active document.";
                return false;
            }

            var peo = new PromptEntityOptions("\nSelect shaft block: ");
            peo.SetRejectMessage("\nPlease select a block reference.\n");
            peo.AddAllowedClass(typeof(BlockReference), exactMatch: true);
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                Entity ent = null;
                try { ent = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased)
                {
                    errorMessage = "Selected shaft was erased. Select a live shaft block.";
                    return false;
                }
                if (!(ent is BlockReference br))
                {
                    errorMessage = "Selected entity is not a block reference.";
                    return false;
                }

                string blockName = GetBlockName(br, tr);
                bool isShaft =
                    (!string.IsNullOrWhiteSpace(blockName) && blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(br.Layer) && br.Layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isShaft)
                {
                    errorMessage = "Selected block does not look like a shaft (name/layer must contain \"shaft\").";
                    return false;
                }

                shaftPoint = br.Position;
                shaftEntityId = per.ObjectId;
                tr.Commit();
                return true;
            }
        }

        private static bool TryFindZoneOutlineContainingPoint(
            Database db,
            Point2d point,
            out ObjectId zoneBoundaryEntityId,
            out Polyline zoneClone,
            out string errorMessage)
        {
            zoneBoundaryEntityId = ObjectId.Null;
            zoneClone = null;
            errorMessage = null;

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                ObjectId bestId = ObjectId.Null;
                Polyline bestPl = null;
                double bestArea = double.PositiveInfinity;
                bool bestTaggedSelf = false;

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    if (!pl.Closed)
                        continue;
                    if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer))
                        continue;

                    bool taggedSelf = false;
                    if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out var h)
                        && !string.IsNullOrWhiteSpace(h)
                        && string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        taggedSelf = true;
                    }

                    var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                    if (ring == null || ring.Count < 3)
                        continue;
                    if (!PointInPolygon(ring, point))
                        continue;

                    double area = double.PositiveInfinity;
                    try { area = Math.Abs(pl.Area); } catch { /* ignore */ }
                    if (double.IsNaN(area) || double.IsInfinity(area) || area <= 0)
                        area = double.PositiveInfinity;

                    bool choose =
                        bestId.IsNull
                        || (taggedSelf && !bestTaggedSelf)
                        || (taggedSelf == bestTaggedSelf && area < bestArea);

                    if (!choose)
                        continue;

                    bestId = id;
                    bestPl = pl;
                    bestArea = area;
                    bestTaggedSelf = taggedSelf;
                }

                if (bestId.IsNull || bestPl == null)
                {
                    errorMessage =
                        "No zone is assigned to this shaft.\n" +
                        "Use \"Assign shaft to zone\" to link this shaft to a zone first.";
                    return false;
                }

                try { zoneClone = (Polyline)bestPl.Clone(); }
                catch
                {
                    errorMessage = "Failed to clone zone boundary polyline.";
                    return false;
                }

                zoneBoundaryEntityId = bestId;
                tr.Commit();
                return true;
            }
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            if (br == null || tr == null) return string.Empty;
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (!br.IsDynamicBlock)
                    return btr.Name ?? string.Empty;
                if (!br.DynamicBlockTableRecord.IsNull)
                {
                    var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                    return dyn.Name ?? string.Empty;
                }
                return btr.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetSprinklerBlockName(BlockReference br, Transaction tr)
        {
            if (br == null)
                return string.Empty;
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (!br.IsDynamicBlock)
                    return btr.Name ?? string.Empty;
                if (!br.DynamicBlockTableRecord.IsNull)
                {
                    var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                    return dyn.Name ?? string.Empty;
                }

                return btr.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddDedupe(List<Point2d> pts, Point2d p, double tol)
        {
            double t = tol > 0 ? tol : 1e-6;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].GetDistanceTo(p) <= t)
                    return;
            }
            pts.Add(p);
        }

        private static List<Lateral> BuildVerticalLateralsFromSprinklers(
            List<Point2d> sprinklers,
            double trunkY,
            double clusterTol,
            bool downstreamPositive)
        {
            var byX = new List<(double x, List<Point2d> pts)>();
            var sorted = new List<Point2d>(sprinklers);
            sorted.Sort((a, b) => a.X.CompareTo(b.X));

            foreach (var p in sorted)
            {
                bool placed = false;
                for (int i = 0; i < byX.Count; i++)
                {
                    if (Math.Abs(byX[i].x - p.X) <= clusterTol)
                    {
                        byX[i].pts.Add(p);
                        // update representative
                        byX[i] = (0.5 * (byX[i].x + p.X), byX[i].pts);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    byX.Add((p.X, new List<Point2d> { p }));
            }

            // Sort laterals along downstream direction.
            byX.Sort((a, b) => downstreamPositive ? a.x.CompareTo(b.x) : b.x.CompareTo(a.x));

            var laterals = new List<Lateral>();
            foreach (var g in byX)
            {
                var top = new List<Point2d>();
                var bottom = new List<Point2d>();
                foreach (var p in g.pts)
                {
                    // Keep actual sprinkler coordinates so branch/reducer align with block insertion points.
                    if (p.Y >= trunkY) top.Add(new Point2d(p.X, p.Y));
                    if (p.Y <= trunkY) bottom.Add(new Point2d(p.X, p.Y));
                }

                if (top.Count > 0)
                {
                    top.Sort((a, b) => Math.Abs(a.Y - trunkY).CompareTo(Math.Abs(b.Y - trunkY)));
                    double attachX = top[0].X;
                    laterals.Add(new Lateral
                    {
                        AttachPoint = new Point2d(attachX, trunkY),
                        Sprinklers = top,
                        SubSprinklers = new List<Point2d>(),
                        IsTopOrRight = true,
                        AxisValue = attachX
                    });
                }
                if (bottom.Count > 0)
                {
                    bottom.Sort((a, b) => Math.Abs(a.Y - trunkY).CompareTo(Math.Abs(b.Y - trunkY)));
                    double attachX = bottom[0].X;
                    laterals.Add(new Lateral
                    {
                        AttachPoint = new Point2d(attachX, trunkY),
                        Sprinklers = bottom,
                        SubSprinklers = new List<Point2d>(),
                        IsTopOrRight = false,
                        AxisValue = attachX
                    });
                }
            }

            return laterals;
        }

        private static void CollapseMicroLateralsIntoSubBranches(
            List<Lateral> laterals,
            double trunkY,
            double spacingDu,
            double clusterTol)
        {
            if (laterals == null || laterals.Count == 0)
                return;

            // A micro lateral is 1–2 sprinklers that would create a whole new branch line.
            // Prefer extending a nearby established lateral and connect these as sub-branches.
            double maxAxisDelta = Math.Max(clusterTol * 2.0, spacingDu * 0.85);

            // Only collapse into laterals that are "established" (>=3 sprinklers on the main line).
            // Spine laterals are never hosts — their "sprinklers" are T-junctions, not heads.
            var established = new List<int>();
            for (int i = 0; i < laterals.Count; i++)
            {
                if (laterals[i]?.Sprinklers != null && laterals[i].Sprinklers.Count >= 3 && !laterals[i].IsSubMainSpine)
                    established.Add(i);
            }
            if (established.Count == 0)
                return;

            // Iterate from end so removals are safe.
            for (int i = laterals.Count - 1; i >= 0; i--)
            {
                var lat = laterals[i];
                if (lat == null || lat.Sprinklers == null)
                    continue;
                // Never collapse a spine lateral — it is a distribution pipe, not a micro-lateral.
                if (lat.IsSubMainSpine)
                    continue;
                if (lat.Sprinklers.Count == 0)
                    continue;
                if (lat.Sprinklers.Count > 2)
                    continue;

                int bestHost = -1;
                double bestDx = double.MaxValue;
                for (int ej = 0; ej < established.Count; ej++)
                {
                    int h = established[ej];
                    if (h == i) continue;
                    var host = laterals[h];
                    if (host == null || host.Sprinklers == null || host.Sprinklers.Count < 3)
                        continue;
                    if (host.IsTopOrRight != lat.IsTopOrRight)
                        continue;
                    double dx = Math.Abs(host.AxisValue - lat.AxisValue);
                    if (dx < bestDx)
                    {
                        bestDx = dx;
                        bestHost = h;
                    }
                }

                if (bestHost < 0 || bestDx > maxAxisDelta)
                    continue;

                var hostLat = laterals[bestHost];
                if (hostLat.SubSprinklers == null)
                    hostLat.SubSprinklers = new List<Point2d>();

                // Add all micro-lateral sprinklers as sub-branch endpoints.
                for (int s = 0; s < lat.Sprinklers.Count; s++)
                    hostLat.SubSprinklers.Add(lat.Sprinklers[s]);

                // Remove the micro lateral (no dedicated branch line).
                laterals.RemoveAt(i);
            }
        }

        private static Point2d PickTapPointOnLateral(Lateral host, Point2d subSprinkler)
        {
            // Pick tap directly on the existing host branch geometry (attach -> sprinkler chain),
            // so sub-branches always emerge from an actual branch pipe, not a boundary projection.
            if (host == null || host.Sprinklers == null || host.Sprinklers.Count == 0)
                return host != null ? host.AttachPoint : subSprinkler;

            var run = new List<Point2d>(host.Sprinklers.Count + 1) { host.AttachPoint };
            run.AddRange(host.Sprinklers);
            ClosestPointOnPolyline(run, subSprinkler, out var tap, out _, out _);
            return tap;
        }

        private static List<Lateral> BuildHorizontalLateralsFromSprinklers(
            List<Point2d> sprinklers,
            double trunkX,
            double trunkYMin,
            double trunkYMax,
            double clusterTol,
            bool downstreamPositive)
        {
            var byY = new List<(double y, List<Point2d> pts)>();
            var sorted = new List<Point2d>(sprinklers);
            sorted.Sort((a, b) => a.Y.CompareTo(b.Y));

            foreach (var p in sorted)
            {
                bool placed = false;
                for (int i = 0; i < byY.Count; i++)
                {
                    if (Math.Abs(byY[i].y - p.Y) <= clusterTol)
                    {
                        byY[i].pts.Add(p);
                        byY[i] = (0.5 * (byY[i].y + p.Y), byY[i].pts);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    byY.Add((p.Y, new List<Point2d> { p }));
            }

            byY.Sort((a, b) => downstreamPositive ? a.y.CompareTo(b.y) : b.y.CompareTo(a.y));

            var laterals = new List<Lateral>();
            foreach (var g in byY)
            {
                var right = new List<Point2d>();
                var left = new List<Point2d>();
                foreach (var p in g.pts)
                {
                    if (p.X >= trunkX) right.Add(new Point2d(p.X, p.Y));
                    if (p.X <= trunkX) left.Add(new Point2d(p.X, p.Y));
                }

                if (right.Count > 0)
                {
                    right.Sort((a, b) => Math.Abs(a.X - trunkX).CompareTo(Math.Abs(b.X - trunkX)));
                    double attachY = right[0].Y;
                    laterals.Add(new Lateral { AttachPoint = new Point2d(trunkX, attachY), Sprinklers = right });
                }
                if (left.Count > 0)
                {
                    left.Sort((a, b) => Math.Abs(a.X - trunkX).CompareTo(Math.Abs(b.X - trunkX)));
                    double attachY = left[0].Y;
                    laterals.Add(new Lateral { AttachPoint = new Point2d(trunkX, attachY), Sprinklers = left });
                }
            }

            return laterals;
        }

        /// <summary>
        /// Distance from main centerline to outer fiber used only when placing reducer graphics.
        /// Many mains carry ConstantWidth ≈ 0 in model space — without a floor the reducer collapses onto the tee point.
        /// </summary>
        private static double ReducerPlacementHalfOutFromCenterline(Database db, double tickLen, double mainTrunkWidthDu)
        {
            double fromPolylineHalf = Math.Max(mainTrunkWidthDu * 0.5, 1e-9);
            double bw = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double floor = Math.Max(bw * 0.52, tickLen * 1.05);
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.06, out double du) && du > 0)
                    floor = Math.Max(floor, du * 0.48);
            }
            catch { /* ignore */ }

            return Math.Max(fromPolylineHalf, floor);
        }

        /// <summary>
        /// Moves the reducer anchor from the main centerline to the outer fiber on the branch side so the large opening
        /// sits on the trunk envelope (plan-view), matching standard reducer-at-main-wall placement.
        /// </summary>
        private static Point2d OffsetReducerToMainOuterFiber(
            Point2d attachOnMainCenterline,
            Point2d towardBranch,
            bool trunkHorizontal,
            double trunkAxisCoordinate,
            double halfFromCenterlineToOuterFiber)
        {
            double half = Math.Max(halfFromCenterlineToOuterFiber, 1e-9);

            if (trunkHorizontal)
            {
                double ny = towardBranch.Y - trunkAxisCoordinate;
                if (Math.Abs(ny) < 1e-9)
                    ny = towardBranch.Y - attachOnMainCenterline.Y;
                if (Math.Abs(ny) < 1e-9)
                    ny = 1.0;
                double sy = Math.Sign(ny);
                return new Point2d(attachOnMainCenterline.X, trunkAxisCoordinate + sy * half);
            }

            double nx = towardBranch.X - trunkAxisCoordinate;
            if (Math.Abs(nx) < 1e-9)
                nx = towardBranch.X - attachOnMainCenterline.X;
            if (Math.Abs(nx) < 1e-9)
                nx = 1.0;
            double sx = Math.Sign(nx);
            return new Point2d(trunkAxisCoordinate + sx * half, attachOnMainCenterline.Y);
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

        private static bool BranchWidthDiffersFromTrunk(double branchWidth, double trunkWidth)
        {
            double s = Math.Max(Math.Max(branchWidth, trunkWidth), 1e-9);
            return Math.Abs(branchWidth - trunkWidth) > s * 0.005;
        }

        /// <summary>Best-match schedule nominal (mm) for a trunk polyline display width, using the same width model as branches.</summary>
        private static int ApproximateTrunkNominalMmFromDisplayWidth(Database db, double trunkDisplayDu, double mainWRef)
        {
            int best = 40;
            double bestErr = double.MaxValue;
            int[] cands = { 150, 100, 80, 65, 50, 40, 32, 25 };
            foreach (int cand in cands)
            {
                double w = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, cand, mainWRef);
                double err = Math.Abs(w - trunkDisplayDu);
                if (err < bestErr)
                {
                    bestErr = err;
                    best = cand;
                }
            }

            return best;
        }

        /// <summary>Half-length along the branch axis used when positioning reducer block inserts.</summary>
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

        private static Line MakePerpendicularTick(Point2d at, bool branchIsVertical, double len, double elevation)
        {
            double h = len * 0.5;
            if (branchIsVertical)
            {
                var a = new Point3d(at.X - h, at.Y, elevation);
                var b = new Point3d(at.X + h, at.Y, elevation);
                return new Line(a, b);
            }
            else
            {
                var a = new Point3d(at.X, at.Y - h, elevation);
                var b = new Point3d(at.X, at.Y + h, elevation);
                return new Line(a, b);
            }
        }

        private static bool PolylineHasSampleInsideZone(Polyline pl, List<Point2d> zoneRing)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;

            try
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    var v = pl.GetPoint3dAt(i);
                    if (PointInPolygon(zoneRing, new Point2d(v.X, v.Y)))
                        return true;
                    if (i + 1 < n)
                    {
                        var v2 = pl.GetPoint3dAt(i + 1);
                        var mid = new Point2d((v.X + v2.X) * 0.5, (v.Y + v2.Y) * 0.5);
                        if (PointInPolygon(zoneRing, mid))
                            return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                bool intersect =
                    ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static double MinEndpointToTrunkDistance(Polyline candidate, Polyline trunk, out Point3d pointOnTrunk)
        {
            pointOnTrunk = trunk.StartPoint;
            double best = double.MaxValue;
            try
            {
                var a = candidate.StartPoint;
                var b = candidate.EndPoint;
                var pa = trunk.GetClosestPointTo(a, false);
                var pb = trunk.GetClosestPointTo(b, false);
                double da = a.DistanceTo(pa);
                double db = b.DistanceTo(pb);
                if (da < best) { best = da; pointOnTrunk = pa; }
                if (db < best) { best = db; pointOnTrunk = pb; }
            }
            catch
            {
                // ignore
            }
            return best;
        }

        private static double SafeLength(Polyline pl)
        {
            try { return pl.Length; } catch { return 0; }
        }
    }
}

