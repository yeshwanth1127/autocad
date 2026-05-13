using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.Commands
{
    /// <summary>
    /// Lets users pick sprinkler heads and draws orthogonal (axis-aligned) branch polylines from the nearest feed.
    /// Routes use Manhattan geometry only — optional multi-corner paths and shaft detours — never diagonal pipe.
    /// Optionally restricts attachment to user-picked main or branch pipe polylines; otherwise prefers existing
    /// branch polylines then mains. When feeds are explicitly picked, branch fallback is disabled.
    /// </summary>
    public class ConnectBranchesManuallyCommand
    {
        private const double AxisSegmentTol = 1e-6;
        private const double DominantAxisRatio = 1.05;

        [CommandMethod("SPRINKLERCONNECTBRANCHESMANUALLY", CommandFlags.Modal)]
        public void ConnectBranchesManually()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // Grid snap makes the pick cursor jump to grid intersections; turn it off for this command only
            // (user can re-enable with F9 or SNAP) so pipe/head picks are not forced to grid points.
            object previousSnapMode = null;
            bool shouldRestoreSnapMode = false;
            try
            {
                previousSnapMode = AcApp.GetSystemVariable("SNAPMODE");
                AcApp.SetSystemVariable("SNAPMODE", 0);
                shouldRestoreSnapMode = true;
            }
            catch
            {
                /* ignore if system variable is unavailable */
            }

            try { doc.Window.Focus(); }
            catch { /* ignore when host has no MDI window */ }

            try
            {
                ed.WriteMessage(
                    "\nConnect branches: optionally pick main or branch pipe polylines first, then sprinkler heads.\n");

                var pickedMainIds = new List<ObjectId>();
                var peoMain = new PromptEntityOptions(string.Empty)
                {
                    AllowNone = true
                };
                peoMain.SetRejectMessage("\nPlease select a polyline.\n");
                peoMain.AddAllowedClass(typeof(Polyline), exactMatch: true);

                bool userRestrictedMains = false;
                while (true)
                {
                    bool firstPass = pickedMainIds.Count == 0;
                    peoMain.Message = firstPass
                        ? "\nSelect main or branch pipe polyline (or press Enter to use nearest among all mains and branches): "
                        : "\nSelect another main or branch pipe polyline (or press Enter when done): ";

                    var perMain = ed.GetEntity(peoMain);
                    if (perMain.Status == PromptStatus.Cancel)
                        return;
                    if (perMain.Status == PromptStatus.None)
                    {
                        if (firstPass)
                        {
                            userRestrictedMains = false;
                            pickedMainIds.Clear();
                        }
                        else
                        {
                            userRestrictedMains = true;
                        }
                        ApplyPickedMainImpliedHighlight(ed, pickedMainIds);
                        break;
                    }
                    if (perMain.Status != PromptStatus.OK)
                        return;

                    if (!pickedMainIds.Contains(perMain.ObjectId))
                        pickedMainIds.Add(perMain.ObjectId);
                    ApplyPickedMainImpliedHighlight(ed, pickedMainIds);
                }

                var pso = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect one or more sprinkler heads to connect: ",
                    SingleOnly = false
                };

                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT,CIRCLE")
                });

                var psr = ed.GetSelection(pso, filter);
                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                    return;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                SprinklerXData.EnsureRegApp(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                ObjectId branchLayerId = SprinklerLayers.EnsureBranchPipeLayer(tr, db);
                List<PipeCandidate> mains;
                if (userRestrictedMains)
                {
                    if (!TryBuildMainCandidatesFromPickedIds(tr, db, pickedMainIds, out mains, out string pickErr))
                    {
                        ed.WriteMessage("\n" + (pickErr ?? "Invalid pipe selection.") + "\n");
                        return;
                    }
                }
                else
                {
                    if (!TryGetMainCandidates(tr, ms, db, out mains, out string mainErr))
                    {
                        ed.WriteMessage("\n" + (mainErr ?? "No main pipe polylines found.") + "\n");
                        return;
                    }
                }

                List<PipeCandidate> branches = null;
                if (!userRestrictedMains)
                    TryGetBranchCandidates(tr, ms, db, out branches);

                double minTeeSpacingDu = GetMinTeeSpacingDrawingUnits(db);
                double geometryMatchTolDu = GetGeometryMatchToleranceDu(db);
                var usedAttachDistanceAlong = new Dictionary<ObjectId, List<double>>();
                bool? allowRedrawWhenDuplicateGeometry = null;

                int created = 0;
                int skippedNonSprinkler = 0;
                int skippedNoAttachToSelectedMain = 0;
                int skippedAlreadyOnSource = 0;
                int skippedNoOrthogonalRoute = 0;
                int skippedErased = 0;
                int skippedDeclinedDuplicateBranch = 0;
                int connectedFromMain = 0;
                int connectedFromBranch = 0;

                var feedCandidatesForAttach = new List<PipeCandidate>(mains);
                if (!userRestrictedMains && branches != null && branches.Count > 0)
                    feedCandidatesForAttach.AddRange(branches);

                var work = new List<ResolvedHeadWork>();

                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null || so.ObjectId.IsNull || so.ObjectId.IsErased)
                    {
                        skippedErased++;
                        continue;
                    }

                    Entity ent = null;
                    try { ent = tr.GetObject(so.ObjectId, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased)
                    {
                        skippedErased++;
                        continue;
                    }
                    if (ent == null)
                    {
                        skippedErased++;
                        continue;
                    }

                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    {
                        skippedNonSprinkler++;
                        continue;
                    }

                    if (!TryGetHeadPoint(ent, out Point3d headPt))
                    {
                        skippedNonSprinkler++;
                        continue;
                    }

                    TryResolveZoneForSprinkler(ent, db, tr, out var zoneRing, out var zoneBoundary);
                    var shaftObstacles = BuildShaftObstaclesForZone(db, zoneBoundary);

                    PipeCandidate bestFeed = null;
                    Point3d bestAttach = default;
                    double bestDist = double.MaxValue;
                    foreach (var m in feedCandidatesForAttach)
                    {
                        if (m?.Polyline == null || m.Polyline.IsErased)
                            continue;
                        Point3d cp;
                        try { cp = m.Polyline.GetClosestPointTo(headPt, extend: false); }
                        catch { continue; }
                        double d = headPt.DistanceTo(cp);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestFeed = m;
                            bestAttach = cp;
                        }
                    }

                    if (bestFeed == null)
                    {
                        if (userRestrictedMains)
                            skippedNoAttachToSelectedMain++;
                        else
                            skippedNoOrthogonalRoute++;
                        continue;
                    }

                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out string bhx);
                    work.Add(new ResolvedHeadWork
                    {
                        EntityId = ent.ObjectId,
                        HeadPt = headPt,
                        BestFeed = bestFeed,
                        AttachOnFeedPreview = bestAttach,
                        ZoneRing = zoneRing,
                        ShaftObs = shaftObstacles,
                        ZoneBoundaryHandleHex = bhx ?? string.Empty,
                        ElevZ = headPt.Z,
                    });
                }

                double groupTol = Math.Max(minTeeSpacingDu * 0.5, 1e-3);
                var groups = new Dictionary<(ObjectId feedId, long rowKey), List<int>>();
                for (int i = 0; i < work.Count; i++)
                {
                    var h = work[i];
                    bool feedVertical = PolylineSpanIsVertical(h.BestFeed.Polyline);
                    double coord = feedVertical ? h.HeadPt.Y : h.HeadPt.X;
                    long rowKey = (long)Math.Round(coord / groupTol);
                    var gk = (h.BestFeed.Polyline.ObjectId, rowKey);
                    if (!groups.TryGetValue(gk, out var bucket))
                    {
                        bucket = new List<int>();
                        groups[gk] = bucket;
                    }
                    bucket.Add(i);
                }

                foreach (var bucket in groups.Values)
                {
                    if (bucket.Count == 0)
                        continue;

                    if (bucket.Count == 1)
                    {
                        int idx = bucket[0];
                        Entity entOne = tr.GetObject(work[idx].EntityId, OpenMode.ForRead, false) as Entity;
                        if (entOne == null)
                        {
                            skippedErased++;
                            continue;
                        }

                        TryDrawSingleHead(
                            tr,
                            ms,
                            db,
                            work,
                            idx,
                            mains,
                            branches,
                            userRestrictedMains,
                            minTeeSpacingDu,
                            geometryMatchTolDu,
                            usedAttachDistanceAlong,
                            ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId,
                            ref created,
                            ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource,
                            ref skippedDeclinedDuplicateBranch,
                            ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain,
                            ref connectedFromBranch);
                        continue;
                    }

                    int head0 = bucket[0];
                    var anchor = work[head0];
                    bool feedVertical = PolylineSpanIsVertical(anchor.BestFeed.Polyline);

                    bucket.Sort((a, b) =>
                    {
                        var ha = work[a].HeadPt;
                        var hb = work[b].HeadPt;
                        double da = feedVertical
                            ? Math.Abs(ha.Y - anchor.AttachOnFeedPreview.Y)
                            : Math.Abs(ha.X - anchor.AttachOnFeedPreview.X);
                        double distB = feedVertical
                            ? Math.Abs(hb.Y - anchor.AttachOnFeedPreview.Y)
                            : Math.Abs(hb.X - anchor.AttachOnFeedPreview.X);
                        return da.CompareTo(distB);
                    });

                    int closestIdx = bucket[0];
                    Point3d attachPt;
                    try
                    {
                        attachPt = work[closestIdx].BestFeed.Polyline.GetClosestPointTo(work[closestIdx].HeadPt, extend: false);
                    }
                    catch
                    {
                        foreach (int idx in bucket)
                        {
                            Entity entF = tr.GetObject(work[idx].EntityId, OpenMode.ForRead, false) as Entity;
                            if (entF == null)
                            {
                                skippedErased++;
                                continue;
                            }

                            TryDrawSingleHead(
                                tr,
                                ms,
                                db,
                                work,
                                idx,
                                mains,
                                branches,
                                userRestrictedMains,
                                minTeeSpacingDu,
                                geometryMatchTolDu,
                                usedAttachDistanceAlong,
                                ref allowRedrawWhenDuplicateGeometry,
                                branchLayerId,
                                ref created,
                                ref skippedNoOrthogonalRoute,
                                ref skippedAlreadyOnSource,
                                ref skippedDeclinedDuplicateBranch,
                                ref skippedNoAttachToSelectedMain,
                                ref connectedFromMain,
                                ref connectedFromBranch);
                        }

                        continue;
                    }

                    double rowFixed = feedVertical ? attachPt.Y : attachPt.X;
                    var attach2d = new Point2d(attachPt.X, attachPt.Y);
                    List<Point2d> chainZoneRing = anchor.ZoneRing;
                    IList<(Point2d min, Point2d max)> chainShaft = anchor.ShaftObs;

                    var verts = new List<Point2d> { attach2d };
                    foreach (int idx in bucket)
                    {
                        var hp = work[idx].HeadPt;
                        verts.Add(feedVertical
                            ? new Point2d(hp.X, rowFixed)
                            : new Point2d(rowFixed, hp.Y));
                    }

                    double detourTol = Math.Max(minTeeSpacingDu * 0.05, 1e-4);
                    var expandedChain = ExpandRouteThroughShaftDetours(verts, chainShaft, chainZoneRing, detourTol);
                    if (expandedChain != null && expandedChain.Count >= 2)
                        verts = expandedChain;

                    verts = CollapseOrthogonalVertices(verts);
                    if (verts == null || verts.Count < 2)
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches, userRestrictedMains,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    if (verts[0].GetDistanceTo(verts[verts.Count - 1]) <= 1e-6)
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches, userRestrictedMains,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    if (!ValidateOrthogonalRoute(verts, chainZoneRing, chainShaft))
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches, userRestrictedMains,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    var dupProbe = new OrthogonalRouteResult
                    {
                        Vertices2d = verts,
                        TotalPathLength = ManhattanPathLength(verts),
                        SourcePolylineId = anchor.BestFeed.Polyline.ObjectId,
                        FromMain = anchor.BestFeed.FeedIsMainPipeLayer,
                        SourceWidth = anchor.BestFeed.Width,
                        RegisteredDistanceAlong = 0
                    };

                    if (ExistingBranchPolylineMatchesResolvedRoute(tr, ms, dupProbe, anchor.HeadPt, geometryMatchTolDu))
                    {
                        if (allowRedrawWhenDuplicateGeometry == null)
                        {
                            DialogResult dr = MessageBox.Show(
                                "Selected sprinkler(s) appear to already be connected to the same pipe with an identical branch.\n\n" +
                                "Do you want to draw duplicate branches anyway?",
                                "Connect branches manually",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);
                            allowRedrawWhenDuplicateGeometry = dr == DialogResult.Yes;
                        }

                        if (allowRedrawWhenDuplicateGeometry == false)
                        {
                            skippedDeclinedDuplicateBranch++;
                            continue;
                        }
                    }

                    double mainRefWidthChain = anchor.BestFeed.Width > 1e-12
                        ? anchor.BestFeed.Width
                        : NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                    double branchWidthChain = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidthChain);
                    if (!(branchWidthChain > 1e-12))
                        branchWidthChain = Math.Max(mainRefWidthChain * 0.66, 1.0);

                    Polyline segChain = CreateOrthogonalBranchPolyline(db, verts, anchor.ElevZ, branchLayerId, branchWidthChain);
                    if (segChain.NumberOfVertices < 2)
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches, userRestrictedMains,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(anchor.ZoneBoundaryHandleHex))
                        SprinklerXData.ApplyZoneBoundaryTag(segChain, anchor.ZoneBoundaryHandleHex);

                    ms.AppendEntity(segChain);
                    tr.AddNewlyCreatedDBObject(segChain, true);

                    if (TryGetDistanceAlongPolylineToPoint(anchor.BestFeed.Polyline, attachPt, out double distAlongChain, out _))
                    {
                        RegisterTeeDistanceAlong(usedAttachDistanceAlong, anchor.BestFeed.Polyline.ObjectId, distAlongChain);
                    }

                    created++;
                    if (anchor.BestFeed.FeedIsMainPipeLayer)
                        connectedFromMain++;
                    else
                        connectedFromBranch++;
                }

                tr.Commit();

                ed.WriteMessage(
                    "\nManual branch connect complete. " +
                    "Created: " + created + " (from main: " + connectedFromMain + ", from branch feed: " + connectedFromBranch + ")" +
                    ", skipped non-sprinkler: " + skippedNonSprinkler +
                    ", skipped erased: " + skippedErased +
                    ", skipped no orthogonal route / source: " + skippedNoOrthogonalRoute +
                    ", skipped no attach to selected feed: " + skippedNoAttachToSelectedMain +
                    ", skipped already on source: " + skippedAlreadyOnSource +
                    ", skipped declined duplicate branch: " + skippedDeclinedDuplicateBranch + ".\n");
            }
            }
            finally
            {
                if (shouldRestoreSnapMode)
                {
                    try
                    {
                        AcApp.SetSystemVariable("SNAPMODE", previousSnapMode);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                ClearPickedMainImpliedHighlight(ed);
            }
        }

        /// <summary>
        /// Shows picked main polylines as the implied selection (grip highlight) while the user continues the command.
        /// </summary>
        private static void ApplyPickedMainImpliedHighlight(Editor ed, IList<ObjectId> pickedMainIds)
        {
            if (ed == null)
                return;
            try
            {
                if (pickedMainIds == null || pickedMainIds.Count == 0)
                {
                    ed.SetImpliedSelection(Array.Empty<ObjectId>());
                    return;
                }
                var arr = new ObjectId[pickedMainIds.Count];
                for (int i = 0; i < pickedMainIds.Count; i++)
                    arr[i] = pickedMainIds[i];
                ed.SetImpliedSelection(arr);
            }
            catch { /* ignore */ }
        }

        private static void ClearPickedMainImpliedHighlight(Editor ed)
        {
            ApplyPickedMainImpliedHighlight(ed, null);
        }

        /// <summary>True when the feed polyline spans Y more than X (column mains vs row mains).</summary>
        private static bool PolylineSpanIsVertical(Polyline pl)
        {
            if (pl == null || pl.IsErased)
                return false;
            try
            {
                Extents3d ext = pl.GeometricExtents;
                double dx = ext.MaxPoint.X - ext.MinPoint.X;
                double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                return dy >= dx;
            }
            catch
            {
                return false;
            }
        }

        private static void RegisterTeeDistanceAlong(Dictionary<ObjectId, List<double>> dict, ObjectId polyId, double distAlong)
        {
            if (dict == null || polyId.IsNull)
                return;
            if (!dict.TryGetValue(polyId, out var list))
            {
                list = new List<double>();
                dict[polyId] = list;
            }
            list.Add(distAlong);
        }

        private static void TryDrawSingleHead(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<ResolvedHeadWork> work,
            int idx,
            List<PipeCandidate> mains,
            List<PipeCandidate> branches,
            bool userRestrictedMains,
            double minTeeSpacingDu,
            double geometryMatchTolDu,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong,
            ref bool? allowRedrawWhenDuplicateGeometry,
            ObjectId branchLayerId,
            ref int created,
            ref int skippedNoOrthogonalRoute,
            ref int skippedAlreadyOnSource,
            ref int skippedDeclinedDuplicateBranch,
            ref int skippedNoAttachToSelectedMain,
            ref int connectedFromMain,
            ref int connectedFromBranch)
        {
            var w = work[idx];
            Point3d headPt = w.HeadPt;

            if (!TryResolveOrthogonalRoute(
                    headPt,
                    mains,
                    branches,
                    userRestrictedMains,
                    w.ZoneRing,
                    w.ShaftObs,
                    minTeeSpacingDu,
                    usedAttachDistanceAlong,
                    out OrthogonalRouteResult route))
            {
                if (userRestrictedMains)
                    skippedNoAttachToSelectedMain++;
                else
                    skippedNoOrthogonalRoute++;
                return;
            }

            if (ExistingBranchPolylineMatchesResolvedRoute(tr, ms, route, headPt, geometryMatchTolDu))
            {
                if (allowRedrawWhenDuplicateGeometry == null)
                {
                    DialogResult dr = MessageBox.Show(
                        "Selected sprinkler(s) appear to already be connected to the same pipe with an identical branch.\n\n" +
                        "Do you want to draw duplicate branches anyway?",
                        "Connect branches manually",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    allowRedrawWhenDuplicateGeometry = dr == DialogResult.Yes;
                }

                if (allowRedrawWhenDuplicateGeometry == false)
                {
                    skippedDeclinedDuplicateBranch++;
                    return;
                }
            }

            double mainRefWidth = route.SourceWidth > 1e-12
                ? route.SourceWidth
                : NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            double branchWidth = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidth);
            if (!(branchWidth > 1e-12))
                branchWidth = Math.Max(mainRefWidth * 0.66, 1.0);

            Polyline seg = CreateOrthogonalBranchPolyline(db, route.Vertices2d, headPt.Z, branchLayerId, branchWidth);
            if (seg.NumberOfVertices < 2)
            {
                skippedNoOrthogonalRoute++;
                return;
            }

            if (!string.IsNullOrEmpty(w.ZoneBoundaryHandleHex))
                SprinklerXData.ApplyZoneBoundaryTag(seg, w.ZoneBoundaryHandleHex);

            ms.AppendEntity(seg);
            tr.AddNewlyCreatedDBObject(seg, true);

            RegisterTeeDistanceAlong(usedAttachDistanceAlong, route.SourcePolylineId, route.RegisteredDistanceAlong);

            created++;
            if (route.FromMain)
                connectedFromMain++;
            else
                connectedFromBranch++;
        }

        private static void FallbackBucketToSingleHeads(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<ResolvedHeadWork> work,
            List<int> bucket,
            List<PipeCandidate> mains,
            List<PipeCandidate> branches,
            bool userRestrictedMains,
            double minTeeSpacingDu,
            double geometryMatchTolDu,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong,
            ref bool? allowRedrawWhenDuplicateGeometry,
            ObjectId branchLayerId,
            ref int created,
            ref int skippedErased,
            ref int skippedNoOrthogonalRoute,
            ref int skippedAlreadyOnSource,
            ref int skippedDeclinedDuplicateBranch,
            ref int skippedNoAttachToSelectedMain,
            ref int connectedFromMain,
            ref int connectedFromBranch)
        {
            if (bucket == null)
                return;
            foreach (int idx in bucket)
            {
                Entity entF = tr.GetObject(work[idx].EntityId, OpenMode.ForRead, false) as Entity;
                if (entF == null)
                {
                    skippedErased++;
                    continue;
                }

                TryDrawSingleHead(
                    tr,
                    ms,
                    db,
                    work,
                    idx,
                    mains,
                    branches,
                    userRestrictedMains,
                    minTeeSpacingDu,
                    geometryMatchTolDu,
                    usedAttachDistanceAlong,
                    ref allowRedrawWhenDuplicateGeometry,
                    branchLayerId,
                    ref created,
                    ref skippedNoOrthogonalRoute,
                    ref skippedAlreadyOnSource,
                    ref skippedDeclinedDuplicateBranch,
                    ref skippedNoAttachToSelectedMain,
                    ref connectedFromMain,
                    ref connectedFromBranch);
            }
        }

        /// <summary>
        /// Vertex equality tolerance when comparing a resolved orthogonal route to existing branch polylines (about 2 mm in drawing units).
        /// </summary>
        private static double GetGeometryMatchToleranceDu(Database db)
        {
            if (db != null &&
                DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.002, out double du) &&
                du > 1e-12)
                return Math.Max(du, 1e-6);
            return 0.01;
        }

        private static bool IsBranchPipeLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return false;
            return string.Equals(layerName, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when an open branch-layer polyline already matches the resolved route vertices (forward or reverse) at this head.
        /// </summary>
        private static bool ExistingBranchPolylineMatchesResolvedRoute(
            Transaction tr,
            BlockTableRecord ms,
            OrthogonalRouteResult route,
            Point3d headPt,
            double vertexTolDu)
        {
            if (tr == null || ms == null || route?.Vertices2d == null || route.Vertices2d.Count < 2)
                return false;

            List<Point2d> target = CollapseOrthogonalVertices(new List<Point2d>(route.Vertices2d));
            if (target == null || target.Count < 2)
                return false;

            var head2d = new Point2d(headPt.X, headPt.Y);

            foreach (ObjectId id in ms)
            {
                if (id.IsNull || id.IsErased || id == route.SourcePolylineId)
                    continue;

                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }

                if (pl == null || pl.Closed || !IsBranchPipeLayerName(pl.Layer))
                    continue;

                if (!TryReadCollapsedPolylineVertices2d(pl, out List<Point2d> existingVerts))
                    continue;

                if (!OrthogonalVertexListsMatch(target, existingVerts, vertexTolDu))
                    continue;

                int last = existingVerts.Count - 1;
                double dHead = Math.Min(
                    head2d.GetDistanceTo(existingVerts[0]),
                    head2d.GetDistanceTo(existingVerts[last]));
                double headTol = Math.Max(vertexTolDu * 10.0, 0.001);
                if (dHead > headTol)
                    continue;

                return true;
            }

            return false;
        }

        private static bool TryReadCollapsedPolylineVertices2d(Polyline pl, out List<Point2d> verts)
        {
            verts = null;
            if (pl == null)
                return false;
            try
            {
                int n = pl.NumberOfVertices;
                if (n < 2)
                    return false;
                var raw = new List<Point2d>(n);
                for (int i = 0; i < n; i++)
                    raw.Add(pl.GetPoint2dAt(i));
                verts = CollapseOrthogonalVertices(raw);
                return verts != null && verts.Count >= 2;
            }
            catch
            {
                return false;
            }
        }

        private static bool OrthogonalVertexListsMatch(IList<Point2d> a, IList<Point2d> b, double tol)
        {
            if (a == null || b == null || a.Count != b.Count || a.Count < 2)
                return false;
            if (VerticesAlignedForward(a, b, tol))
                return true;
            return VerticesAlignedForward(a, ReverseVertexList(b), tol);
        }

        private static List<Point2d> ReverseVertexList(IList<Point2d> b)
        {
            var r = new List<Point2d>(b.Count);
            for (int i = b.Count - 1; i >= 0; i--)
                r.Add(b[i]);
            return r;
        }

        private static bool VerticesAlignedForward(IList<Point2d> a, IList<Point2d> b, double tol)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].GetDistanceTo(b[i]) > tol)
                    return false;
            }
            return true;
        }

        private sealed class ResolvedHeadWork
        {
            public ObjectId EntityId;
            public Point3d HeadPt;
            public PipeCandidate BestFeed;
            public Point3d AttachOnFeedPreview;
            public List<Point2d> ZoneRing;
            public IList<(Point2d min, Point2d max)> ShaftObs;
            public string ZoneBoundaryHandleHex;
            public double ElevZ;
        }

        private sealed class PipeCandidate
        {
            public Polyline Polyline;
            public double Width;
            /// <summary>False when the feed polyline is on a branch pipe layer (statistics / queue labeling).</summary>
            public bool FeedIsMainPipeLayer;
        }

        private static bool IsEligibleMainPolyline(Polyline pl)
        {
            if (pl == null || pl.IsErased)
                return false;
            if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer))
                return false;
            if (SprinklerXData.IsTaggedTrunkCap(pl))
                return false;
            // Connector runs on a main pipe layer are valid attach targets (same layer rule as trunk).
            return true;
        }

        /// <summary>Open branch-layer polyline the user may pick as a feed (same routing as from main).</summary>
        private static bool IsEligiblePickedBranchFeedPolyline(Polyline pl)
        {
            if (pl == null || pl.IsErased || pl.Closed)
                return false;
            if (!IsBranchPipeLayerName(pl.Layer))
                return false;
            try
            {
                return pl.NumberOfVertices >= 2;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildMainCandidatesFromPickedIds(
            Transaction tr,
            Database db,
            IList<ObjectId> pickedIds,
            out List<PipeCandidate> mains,
            out string errorMessage)
        {
            mains = new List<PipeCandidate>();
            errorMessage = null;
            if (tr == null || db == null || pickedIds == null || pickedIds.Count == 0)
            {
                errorMessage = "No pipe polylines were selected.";
                return false;
            }

            var seen = new HashSet<ObjectId>();
            foreach (ObjectId id in pickedIds)
            {
                if (id.IsNull || id.IsErased || !seen.Add(id))
                    continue;

                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }

                if (pl == null)
                {
                    errorMessage = "A selected object is not a polyline.";
                    return false;
                }

                bool feedIsMain;
                if (IsEligibleMainPolyline(pl))
                    feedIsMain = true;
                else if (IsEligiblePickedBranchFeedPolyline(pl))
                    feedIsMain = false;
                else
                {
                    errorMessage =
                        "A selected polyline is not a valid main or branch pipe: " +
                        "use a main-pipe layer polyline (not a trunk cap) or an open polyline on a branch pipe layer.";
                    return false;
                }

                mains.Add(new PipeCandidate
                {
                    Polyline = pl,
                    Width = ReadPolylineWidthOrDefault(pl, db),
                    FeedIsMainPipeLayer = feedIsMain
                });
            }

            if (mains.Count == 0)
            {
                errorMessage = "No valid main or branch pipe polylines among the selection.";
                return false;
            }

            return true;
        }

        private static bool TryGetMainCandidates(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            out List<PipeCandidate> mains,
            out string errorMessage)
        {
            mains = new List<PipeCandidate>();
            errorMessage = null;
            if (tr == null || ms == null || db == null)
            {
                errorMessage = "Invalid drawing context.";
                return false;
            }

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (!IsEligibleMainPolyline(pl))
                    continue;

                mains.Add(new PipeCandidate
                {
                    Polyline = pl,
                    Width = ReadPolylineWidthOrDefault(pl, db),
                    FeedIsMainPipeLayer = true
                });
            }

            if (mains.Count == 0)
            {
                errorMessage = "No main pipe found. Route main pipe first.";
                return false;
            }

            return true;
        }

        private static bool TryGetBranchCandidates(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            out List<PipeCandidate> branches)
        {
            branches = new List<PipeCandidate>();
            if (tr == null || ms == null || db == null)
                return false;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed) continue;

                if (!IsBranchPipeLayerName(pl.Layer))
                    continue;

                branches.Add(new PipeCandidate
                {
                    Polyline = pl,
                    Width = ReadPolylineWidthOrDefault(pl, db),
                    FeedIsMainPipeLayer = false
                });
            }

            return branches.Count > 0;
        }

        private static double ReadPolylineWidthOrDefault(Polyline pl, Database db)
        {
            double w = 0;
            try { w = pl.ConstantWidth; } catch { w = 0; }
            if (w > 1e-12) return w;

            try
            {
                int n = pl.NumberOfVertices;
                int limit = pl.Closed ? n : Math.Max(0, n - 1);
                for (int i = 0; i < limit; i++)
                {
                    double sw = 0, ew = 0;
                    try { sw = pl.GetStartWidthAt(i); } catch { /* ignore */ }
                    try { ew = pl.GetEndWidthAt(i); } catch { /* ignore */ }
                    w = Math.Max(w, Math.Max(sw, ew));
                }
            }
            catch { /* ignore */ }

            if (w > 1e-12) return w;
            return NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
        }

        private static bool TryGetHeadPoint(Entity ent, out Point3d point)
        {
            point = default;
            if (ent is BlockReference br)
            {
                point = br.Position;
                return true;
            }
            if (ent is Circle c)
            {
                point = c.Center;
                return true;
            }
            return false;
        }

        private sealed class OrthogonalRouteResult
        {
            public List<Point2d> Vertices2d;
            public double TotalPathLength;
            public bool FromMain;
            public double SourceWidth;
            public ObjectId SourcePolylineId;
            public double RegisteredDistanceAlong;
        }

        private static double GetMinTeeSpacingDrawingUnits(Database db)
        {
            if (db != null &&
                DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.15, out double du) &&
                du > 1e-9)
                return du;
            return 1.0;
        }

        private static Polyline CreateOrthogonalBranchPolyline(
            Database db,
            IList<Point2d> vertices2d,
            double elevationZ,
            ObjectId branchLayerId,
            double branchWidth)
        {
            var seg = new Polyline();
            seg.SetDatabaseDefaults(db);
            seg.LayerId = branchLayerId;
            seg.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            seg.ConstantWidth = branchWidth;
            seg.Elevation = elevationZ;
            seg.Closed = false;
            int vi = 0;
            for (int i = 0; i < vertices2d.Count; i++)
            {
                var p = vertices2d[i];
                if (vi > 0)
                {
                    var prev = seg.GetPoint2dAt(vi - 1);
                    if (prev.GetDistanceTo(p) <= 1e-9)
                        continue;
                }
                seg.AddVertexAt(vi++, p, 0, 0, 0);
            }
            return seg;
        }

        private static bool TryResolveOrthogonalRoute(
            Point3d headPt,
            List<PipeCandidate> mains,
            List<PipeCandidate> branches,
            bool userRestrictedMains,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObstacles,
            double minTeeSpacingDu,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong,
            out OrthogonalRouteResult route)
        {
            route = null;
            if (mains == null || mains.Count == 0)
                return false;

            var queue = BuildOrderedPipeQueue(headPt, mains, branches, userRestrictedMains);
            if (queue.Count == 0)
                return false;

            var head2 = new Point2d(headPt.X, headPt.Y);

            for (int qi = 0; qi < queue.Count; qi++)
            {
                var entry = queue[qi];
                var pl = entry.Candidate.Polyline;
                if (pl == null || pl.IsErased)
                    continue;

                Point3d rawClosest;
                try { rawClosest = pl.GetClosestPointTo(headPt, extend: false); }
                catch { continue; }

                if (!TryGetDistanceAlongPolylineToPoint(pl, rawClosest, out double distAlongRaw, out _))
                    continue;

                usedAttachDistanceAlong.TryGetValue(pl.ObjectId, out var usedOnThisPoly);

                const int maxRing = 24;
                bool placed = false;

                for (int ring = 0; ring <= maxRing && !placed; ring++)
                {
                    if (ring == 0)
                    {
                        if (TryOrthogonalRouteFromPipeAtDistance(
                                pl, entry, distAlongRaw, head2, zoneRing, shaftObstacles,
                                minTeeSpacingDu, usedOnThisPoly, out route))
                            placed = true;
                    }
                    else
                    {
                        double up = distAlongRaw + ring * minTeeSpacingDu;
                        if (TryOrthogonalRouteFromPipeAtDistance(
                                pl, entry, up, head2, zoneRing, shaftObstacles,
                                minTeeSpacingDu, usedOnThisPoly, out route))
                            placed = true;
                        else
                        {
                            double dn = distAlongRaw - ring * minTeeSpacingDu;
                            if (TryOrthogonalRouteFromPipeAtDistance(
                                    pl, entry, dn, head2, zoneRing, shaftObstacles,
                                    minTeeSpacingDu, usedOnThisPoly, out route))
                                placed = true;
                        }
                    }
                }

                if (placed)
                    return true;
            }

            return false;
        }

        private static bool TryOrthogonalRouteFromPipeAtDistance(
            Polyline pl,
            PipeQueueEntry entry,
            double distanceAlong,
            Point2d head2,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObstacles,
            double minTeeSpacingDu,
            IList<double> usedOnThisPoly,
            out OrthogonalRouteResult route)
        {
            route = null;
            if (pl == null || pl.IsErased)
                return false;

            if (!TryGetTotalPolylineLength(pl, out double totalLen) || totalLen <= 1e-9)
                return false;

            if (distanceAlong < -1e-9 || distanceAlong > totalLen + 1e-9)
                return false;

            if (usedOnThisPoly != null)
            {
                for (int u = 0; u < usedOnThisPoly.Count; u++)
                {
                    if (Math.Abs(usedOnThisPoly[u] - distanceAlong) < minTeeSpacingDu - 1e-9)
                        return false;
                }
            }

            if (!TryPointAtDistanceAlongPolyline(pl, distanceAlong, out Point3d attachPt))
                return false;

            if (!TryGetPolylineSegmentDirection(pl, attachPt, out SegmentAxisKind axisKind))
                return false;

            if (!TryBuildOrthogonalCandidates(attachPt, head2, axisKind, out List<List<Point2d>> baseCandidates))
                return false;

            var candidates = new List<List<Point2d>>();
            for (int i = 0; i < baseCandidates.Count; i++)
                candidates.Add(baseCandidates[i]);
            AppendStairStepOrthogonalCandidates(
                new Point2d(attachPt.X, attachPt.Y),
                head2,
                minTeeSpacingDu,
                candidates);

            double detourTol = Math.Max(minTeeSpacingDu * 0.05, 1e-4);
            List<Point2d> bestVerts = null;
            double bestLen = double.MaxValue;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                var expanded = ExpandRouteThroughShaftDetours(candidates[ci], shaftObstacles, zoneRing, detourTol);
                if (expanded == null || expanded.Count < 2)
                    continue;
                var verts = CollapseOrthogonalVertices(expanded);
                if (verts == null || verts.Count < 2)
                    continue;
                if (!ValidateOrthogonalRoute(verts, zoneRing, shaftObstacles))
                    continue;
                double len = ManhattanPathLength(verts);
                if (len < bestLen)
                {
                    bestLen = len;
                    bestVerts = verts;
                }
            }

            if (bestVerts == null)
                return false;

            route = new OrthogonalRouteResult
            {
                Vertices2d = bestVerts,
                TotalPathLength = bestLen,
                FromMain = entry.IsMain,
                SourceWidth = entry.Candidate.Width,
                SourcePolylineId = pl.ObjectId,
                RegisteredDistanceAlong = distanceAlong
            };
            return true;
        }

        private struct PipeQueueEntry
        {
            public PipeCandidate Candidate;
            public bool IsMain;
            public double SortDistance;
        }

        private static List<PipeQueueEntry> BuildOrderedPipeQueue(
            Point3d headPt,
            List<PipeCandidate> mains,
            List<PipeCandidate> branches,
            bool userRestrictedMains)
        {
            var list = new List<PipeQueueEntry>();
            if (!userRestrictedMains && branches != null && branches.Count > 0)
            {
                for (int i = 0; i < branches.Count; i++)
                {
                    var c = branches[i];
                    var pl = c?.Polyline;
                    if (pl == null || pl.IsErased) continue;
                    Point3d cp;
                    try { cp = pl.GetClosestPointTo(headPt, extend: false); }
                    catch { continue; }
                    list.Add(new PipeQueueEntry
                    {
                        Candidate = c,
                        IsMain = c.FeedIsMainPipeLayer,
                        SortDistance = headPt.DistanceTo(cp)
                    });
                }
            }

            for (int i = 0; i < mains.Count; i++)
            {
                var c = mains[i];
                var pl = c?.Polyline;
                if (pl == null || pl.IsErased) continue;
                Point3d cp;
                try { cp = pl.GetClosestPointTo(headPt, extend: false); }
                catch { continue; }
                list.Add(new PipeQueueEntry
                {
                    Candidate = c,
                    IsMain = c.FeedIsMainPipeLayer,
                    SortDistance = headPt.DistanceTo(cp)
                });
            }

            list.Sort((a, b) => a.SortDistance.CompareTo(b.SortDistance));
            return list;
        }

        private enum SegmentAxisKind
        {
            Horizontal,
            Vertical,
            Ambiguous
        }

        private static bool TryGetPolylineSegmentDirection(Polyline pl, Point3d onCurve, out SegmentAxisKind axisKind)
        {
            axisKind = SegmentAxisKind.Ambiguous;
            if (pl == null) return false;
            int nv = pl.NumberOfVertices;
            if (nv < 2) return false;

            int nSeg = pl.Closed ? nv : nv - 1;
            double bestDist = double.MaxValue;
            SegmentAxisKind bestAxis = SegmentAxisKind.Ambiguous;

            for (int i = 0; i < nSeg; i++)
            {
                var a = pl.GetPoint3dAt(i);
                int i1 = pl.Closed ? ((i + 1) % nv) : (i + 1);
                var b = pl.GetPoint3dAt(i1);
                if (!TryClosestPointOnSegment3d(onCurve, a, b, out Point3d segClosest, out double distToSeg))
                    continue;
                if (distToSeg < bestDist - 1e-12)
                {
                    bestDist = distToSeg;
                    double dx = Math.Abs(b.X - a.X);
                    double dy = Math.Abs(b.Y - a.Y);
                    if (dy <= AxisSegmentTol && dx > AxisSegmentTol)
                        bestAxis = SegmentAxisKind.Horizontal;
                    else if (dx <= AxisSegmentTol && dy > AxisSegmentTol)
                        bestAxis = SegmentAxisKind.Vertical;
                    else if (dx > AxisSegmentTol && dy > AxisSegmentTol)
                    {
                        if (dx >= dy * DominantAxisRatio)
                            bestAxis = SegmentAxisKind.Horizontal;
                        else if (dy >= dx * DominantAxisRatio)
                            bestAxis = SegmentAxisKind.Vertical;
                        else
                            bestAxis = SegmentAxisKind.Ambiguous;
                    }
                    else
                        bestAxis = SegmentAxisKind.Ambiguous;
                }
            }

            const double onCurveTol = 0.05;
            if (bestDist > onCurveTol)
                return false;

            axisKind = bestAxis;
            return true;
        }

        private static bool TryClosestPointOnSegment3d(
            Point3d p,
            Point3d a,
            Point3d b,
            out Point3d closest,
            out double dist)
        {
            closest = default;
            dist = double.MaxValue;
            Vector3d ab = b - a;
            double len2 = ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z;
            if (len2 < 1e-20)
            {
                closest = a;
                dist = p.DistanceTo(a);
                return false;
            }
            double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y + (p.Z - a.Z) * ab.Z) / len2;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            closest = new Point3d(a.X + ab.X * t, a.Y + ab.Y * t, a.Z + ab.Z * t);
            double vx = p.X - closest.X;
            double vy = p.Y - closest.Y;
            double vz = p.Z - closest.Z;
            dist = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            return true;
        }

        private static bool TryBuildOrthogonalCandidates(
            Point3d attach,
            Point2d head,
            SegmentAxisKind axisKind,
            out List<List<Point2d>> candidates)
        {
            candidates = new List<List<Point2d>>();
            double sx = attach.X, sy = attach.Y;
            double hx = head.X, hy = head.Y;

            var vertFirst = new List<Point2d>
            {
                new Point2d(sx, sy),
                new Point2d(sx, hy),
                new Point2d(hx, hy)
            };
            var horizFirst = new List<Point2d>
            {
                new Point2d(sx, sy),
                new Point2d(hx, sy),
                new Point2d(hx, hy)
            };

            switch (axisKind)
            {
                case SegmentAxisKind.Horizontal:
                    candidates.Add(vertFirst);
                    break;
                case SegmentAxisKind.Vertical:
                    candidates.Add(horizFirst);
                    break;
                default:
                    candidates.Add(vertFirst);
                    candidates.Add(horizFirst);
                    break;
            }

            return candidates.Count > 0;
        }

        /// <summary>
        /// Adds double-corner (Z-shaped) Manhattan routes between attach and head so routing can jog off-axis
        /// while staying orthogonal — used when a simple L fails validation or shaft detours need extra corners.
        /// </summary>
        private static void AppendStairStepOrthogonalCandidates(
            Point2d attach,
            Point2d head,
            double minTeeSpacingDu,
            List<List<Point2d>> into)
        {
            if (into == null)
                return;

            double sx = attach.X, sy = attach.Y, hx = head.X, hy = head.Y;
            if (Math.Abs(sx - hx) < 1e-9 && Math.Abs(sy - hy) < 1e-9)
                return;

            double step = Math.Max(minTeeSpacingDu, 1e-6);
            var frac = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
            var extraOff = new[] { 0.0, step, -step, 2 * step, -2 * step };

            foreach (double f in frac)
            {
                double ym = sy + (hy - sy) * f;
                into.Add(new List<Point2d>
                {
                    attach,
                    new Point2d(sx, ym),
                    new Point2d(hx, ym),
                    head
                });
            }

            foreach (double off in extraOff)
            {
                double ym = 0.5 * (sy + hy) + off;
                into.Add(new List<Point2d>
                {
                    attach,
                    new Point2d(sx, ym),
                    new Point2d(hx, ym),
                    head
                });
            }

            foreach (double f in frac)
            {
                double xm = sx + (hx - sx) * f;
                into.Add(new List<Point2d>
                {
                    attach,
                    new Point2d(xm, sy),
                    new Point2d(xm, hy),
                    head
                });
            }

            foreach (double off in extraOff)
            {
                double xm = 0.5 * (sx + hx) + off;
                into.Add(new List<Point2d>
                {
                    attach,
                    new Point2d(xm, sy),
                    new Point2d(xm, hy),
                    head
                });
            }
        }

        /// <summary>
        /// Replaces each axis-aligned leg with shaft-aware orthogonal detours (same helper as batch branch routing).
        /// </summary>
        private static List<Point2d> ExpandRouteThroughShaftDetours(
            IList<Point2d> corners,
            IList<(Point2d min, Point2d max)> shaftObstacles,
            List<Point2d> zoneRing,
            double tol)
        {
            if (corners == null || corners.Count < 2)
                return null;

            var zoneRings = new List<IList<Point2d>>();
            if (zoneRing != null && zoneRing.Count >= 3)
                zoneRings.Add(zoneRing);

            var merged = new List<Point2d>();
            double te = tol > 0 ? tol : 1e-6;

            for (int i = 0; i + 1 < corners.Count; i++)
            {
                var leg = BranchPipeShaftDetour2d.AxisAlignedWaypointsAvoidingBoxes(
                    corners[i],
                    corners[i + 1],
                    shaftObstacles,
                    zoneRings,
                    te);

                if (merged.Count == 0)
                {
                    merged.AddRange(leg);
                }
                else
                {
                    int start = 0;
                    if (leg.Count > 0 && merged[merged.Count - 1].GetDistanceTo(leg[0]) <= te * 10.0)
                        start = 1;
                    for (int j = start; j < leg.Count; j++)
                        merged.Add(leg[j]);
                }
            }

            return merged.Count >= 2 ? merged : null;
        }

        private static List<Point2d> CollapseOrthogonalVertices(IList<Point2d> verts)
        {
            if (verts == null || verts.Count == 0)
                return null;
            var r = new List<Point2d>();
            for (int i = 0; i < verts.Count; i++)
            {
                var p = verts[i];
                if (r.Count == 0)
                {
                    r.Add(p);
                    continue;
                }
                var prev = r[r.Count - 1];
                if (prev.GetDistanceTo(p) <= 1e-9)
                    continue;
                r.Add(p);
            }
            while (r.Count >= 3)
            {
                var a = r[r.Count - 3];
                var b = r[r.Count - 2];
                var c = r[r.Count - 1];
                bool col =
                    (Math.Abs(a.X - b.X) <= 1e-9 && Math.Abs(b.X - c.X) <= 1e-9) ||
                    (Math.Abs(a.Y - b.Y) <= 1e-9 && Math.Abs(b.Y - c.Y) <= 1e-9);
                if (col)
                    r.RemoveAt(r.Count - 2);
                else
                    break;
            }
            return r;
        }

        private static bool ValidateOrthogonalRoute(
            IList<Point2d> verts,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObstacles)
        {
            if (verts == null || verts.Count < 2)
                return false;

            for (int i = 0; i < verts.Count - 1; i++)
            {
                var a = verts[i];
                var b = verts[i + 1];
                double leg = a.GetDistanceTo(b);
                if (leg <= 1e-9)
                    return false;
                double dx = Math.Abs(b.X - a.X);
                double dy = Math.Abs(b.Y - a.Y);
                if (dx > 1e-9 && dy > 1e-9)
                    return false;

                var a3 = new Point3d(a.X, a.Y, 0);
                var b3 = new Point3d(b.X, b.Y, 0);
                if (!ConnectionInsideZone(a3, b3, zoneRing))
                    return false;
                if (SegmentIntersectsAnyBox(a, b, shaftObstacles))
                    return false;
            }

            return true;
        }

        private static double ManhattanPathLength(IList<Point2d> verts)
        {
            double s = 0;
            for (int i = 0; i < verts.Count - 1; i++)
                s += verts[i].GetDistanceTo(verts[i + 1]);
            return s;
        }

        private static bool TryGetTotalPolylineLength(Polyline pl, out double length)
        {
            length = 0;
            if (pl == null) return false;
            int nv = pl.NumberOfVertices;
            int nSeg = pl.Closed ? nv : nv - 1;
            for (int i = 0; i < nSeg; i++)
            {
                var a = pl.GetPoint3dAt(i);
                int i1 = pl.Closed ? ((i + 1) % nv) : (i + 1);
                var b = pl.GetPoint3dAt(i1);
                length += a.DistanceTo(b);
            }
            return length > 1e-9;
        }

        private static bool TryGetDistanceAlongPolylineToPoint(Polyline pl, Point3d ptOnCurve, out double distanceAlong, out int segmentIndex)
        {
            distanceAlong = 0;
            segmentIndex = -1;
            if (pl == null) return false;
            int nv = pl.NumberOfVertices;
            int nSeg = pl.Closed ? nv : nv - 1;
            double acc = 0;
            double bestDist = double.MaxValue;

            for (int i = 0; i < nSeg; i++)
            {
                var a = pl.GetPoint3dAt(i);
                int i1 = pl.Closed ? ((i + 1) % nv) : (i + 1);
                var b = pl.GetPoint3dAt(i1);
                if (!TryClosestPointOnSegment3d(ptOnCurve, a, b, out Point3d segClosest, out double dOrtho))
                    continue;
                double dAlongSeg = a.DistanceTo(segClosest);
                if (dOrtho < bestDist)
                {
                    bestDist = dOrtho;
                    distanceAlong = acc + dAlongSeg;
                    segmentIndex = i;
                }
                acc += a.DistanceTo(b);
            }

            return bestDist <= 0.05;
        }

        private static bool TryPointAtDistanceAlongPolyline(Polyline pl, double targetDist, out Point3d point)
        {
            point = default;
            if (pl == null || !TryGetTotalPolylineLength(pl, out double total) || total <= 1e-9)
                return false;
            double d = Math.Max(0, Math.Min(targetDist, total));
            int nv = pl.NumberOfVertices;
            int nSeg = pl.Closed ? nv : nv - 1;
            double acc = 0;
            for (int i = 0; i < nSeg; i++)
            {
                var a = pl.GetPoint3dAt(i);
                int i1 = pl.Closed ? ((i + 1) % nv) : (i + 1);
                var b = pl.GetPoint3dAt(i1);
                double segLen = a.DistanceTo(b);
                if (segLen < 1e-12)
                    continue;
                if (acc + segLen >= d - 1e-9)
                {
                    double t = (d - acc) / segLen;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    point = new Point3d(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t,
                        a.Z + (b.Z - a.Z) * t);
                    return true;
                }
                acc += segLen;
            }
            point = pl.GetPoint3dAt(nv - 1);
            return true;
        }

        private static void TryResolveZoneForSprinkler(
            Entity sprinklerEnt,
            Database db,
            Transaction tr,
            out List<Point2d> zoneRing,
            out Polyline zoneBoundary)
        {
            zoneRing = null;
            zoneBoundary = null;
            if (sprinklerEnt == null || db == null || tr == null)
                return;
            if (!SprinklerXData.TryGetZoneBoundaryHandle(sprinklerEnt, out string boundaryHandleHex) ||
                string.IsNullOrWhiteSpace(boundaryHandleHex))
                return;

            ObjectId boundaryId = ObjectId.Null;
            try
            {
                var h = new Handle(Convert.ToInt64(boundaryHandleHex, 16));
                boundaryId = db.GetObjectId(false, h, 0);
            }
            catch { boundaryId = ObjectId.Null; }
            if (boundaryId.IsNull || boundaryId.IsErased)
                return;

            Polyline boundary = null;
            try { boundary = tr.GetObject(boundaryId, OpenMode.ForRead, false) as Polyline; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { boundary = null; }
            if (boundary == null || !boundary.Closed)
                return;
            zoneBoundary = boundary;

            try { zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary); }
            catch { zoneRing = null; }
            if (zoneRing == null || zoneRing.Count < 3)
                zoneRing = null;
        }

        private static List<(Point2d min, Point2d max)> BuildShaftObstaclesForZone(Database db, Polyline zoneBoundary)
        {
            if (db == null || zoneBoundary == null)
                return null;

            double clearanceDu = 0.05;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.08, out double sc) && sc > 0)
                    clearanceDu = sc;
            }
            catch { /* ignore */ }

            try
            {
                return BranchPipeShaftDetour2d.BuildShaftObstacles(db, zoneBoundary, clearanceDu);
            }
            catch
            {
                return null;
            }
        }

        private static bool ConnectionInsideZone(Point3d from, Point3d to, List<Point2d> zoneRing)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return true;

            const int samples = 10;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                double x = from.X + (to.X - from.X) * t;
                double y = from.Y + (to.Y - from.Y) * t;
                if (!PointInPolygon(zoneRing, new Point2d(x, y)))
                    return false;
            }
            return true;
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
    }
}
