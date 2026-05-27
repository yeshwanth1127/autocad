using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Agent.Planning.Validators;
using autocad_final.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.Commands
{
    /// <summary>
    /// Lets users pick sprinkler heads and draws orthogonal (axis-aligned) branch polylines from the nearest feed.
    /// Routes use Manhattan geometry only — optional multi-corner paths and shaft detours — never diagonal pipe.
    /// Optionally restricts attachment to user-picked main or branch pipe polylines; otherwise prefers existing
    /// branch polylines then mains. When feeds are explicitly picked, enters extension mode: each head attaches
    /// only to the picked set (nearest among picks if several), orthogonal routing uses that head's feed only,
    /// and row grouping uses a wider bucket so aligned heads chain together.
    /// </summary>
    public class ConnectBranchesManuallyCommand
    {
        private const double AxisSegmentTol = 1e-6;
        private const double DominantAxisRatio = 1.05;

        /// <summary>Synthetic zone key when selected heads carry no boundary handle — reconnect still chains rows.</summary>
        private const string OrphanReconnectNoZoneKey = "__SPRINKLER_RECONNECT_NOZONE__";

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

                // User picked one or more feeds: no automatic discovery; routing queue per head is only that head's feed.
                bool explicitFeedExtensionMode = userRestrictedMains;
                bool singlePickedFeed = explicitFeedExtensionMode && mains != null && mains.Count == 1;

                if (explicitFeedExtensionMode)
                {
                    ed.WriteMessage(
                        singlePickedFeed
                            ? "\nPicked-feed extension mode: all sprinklers use only the selected polyline (no other mains/branches).\n"
                            : "\nPicked-feed extension mode: each sprinkler uses the nearest of your selected polylines only (no automatic discovery).\n");
                }

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
                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out string bhx);
                    bhx = bhx ?? string.Empty;

                    PipeCandidate bestFeed = null;
                    Point3d bestAttach = default;
                    if (singlePickedFeed)
                    {
                        // User explicitly picked this feed — always honour the selection regardless of zone tag.
                        bestFeed = mains[0];
                        if (bestFeed?.Polyline == null || bestFeed.Polyline.IsErased)
                            bestFeed = null;
                        else
                        {
                            try { bestAttach = bestFeed.Polyline.GetClosestPointTo(headPt, extend: false); }
                            catch { bestFeed = null; }
                        }
                    }
                    else
                    {
                        double bestDist = double.MaxValue;
                        foreach (var m in feedCandidatesForAttach)
                        {
                            if (m?.Polyline == null || m.Polyline.IsErased)
                                continue;
                            // Zone filter applies only to auto-discovered feeds, not user-picked polylines.
                            if (!userRestrictedMains && !string.IsNullOrEmpty(bhx)
                                && !PipeCandidateMatchesHeadZone(m, bhx, zoneRing))
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
                    }

                    if (bestFeed == null)
                    {
                        if (userRestrictedMains)
                            skippedNoAttachToSelectedMain++;
                        else
                            skippedNoOrthogonalRoute++;
                        continue;
                    }

                    work.Add(new ResolvedHeadWork
                    {
                        EntityId = ent.ObjectId,
                        HeadPt = headPt,
                        BestFeed = bestFeed,
                        AttachOnFeedPreview = bestAttach,
                        ZoneRing = zoneRing,
                        ShaftObs = shaftObstacles,
                        ZoneBoundaryHandleHex = bhx,
                        ElevZ = headPt.Z,
                    });
                }

                // Zone scope: every orphan reconnect and branch erase must stay inside the zone(s)
                // of the sprinkler heads the user selected — never cross zone boundaries.
                var allowedZoneHexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var zoneRingsByHex = new Dictionary<string, List<Point2d>>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in work)
                {
                    if (w == null || string.IsNullOrEmpty(w.ZoneBoundaryHandleHex))
                        continue;
                    allowedZoneHexes.Add(w.ZoneBoundaryHandleHex);
                    if (w.ZoneRing != null && w.ZoneRing.Count >= 3
                        && !zoneRingsByHex.ContainsKey(w.ZoneBoundaryHandleHex))
                        zoneRingsByHex[w.ZoneBoundaryHandleHex] = w.ZoneRing;
                }

                foreach (string zh in allowedZoneHexes)
                    TryEnsureZoneRingLoaded(db, tr, zh, zoneRingsByHex);

                // Collect main-pipe candidates for orphan reconnect, scoped to the work zone(s).
                List<PipeCandidate> allMainsInZone = FilterPipeCandidatesByZoneScope(mains, allowedZoneHexes, zoneRingsByHex);
                if (allMainsInZone.Count == 0
                    && TryGetMainCandidates(tr, ms, db, out List<PipeCandidate> discovered, out _))
                    allMainsInZone = FilterPipeCandidatesByZoneScope(discovered, allowedZoneHexes, zoneRingsByHex);

                // User-picked feeds are always eligible for orphan reconnect in this command.
                MergeUniquePipeCandidates(allMainsInZone, mains);

                // Mains that share a zone's shaft (zone outline SHAFT_ASSIGNMENT or shaft ZONE_ASSIGNMENTS)
                // belong to that zone even when the main polyline lacks a BOUNDARY tag or sparse vertex sampling
                // misses the green outline in PolylineHasSampleInsideZoneRing.
                if (allowedZoneHexes != null && allowedZoneHexes.Count > 0)
                {
                    List<PipeCandidate> poolForShaftLink = mains;
                    if (userRestrictedMains
                        && TryGetMainCandidates(tr, ms, db, out List<PipeCandidate> discoveredForShaft, out _))
                    {
                        poolForShaftLink = discoveredForShaft;
                    }
                    MergeMainsLinkedThroughShaftAssignments(
                        tr, ms, db, allowedZoneHexes, zoneRingsByHex, poolForShaftLink, allMainsInZone);
                }

                var floorRoomOwnerships = SprinklerHeadReader2d.BuildFloorRoomOwnerships(db);
                foreach (var w in work)
                {
                    if (w == null) continue;
                    var hp2 = new Point2d(w.HeadPt.X, w.HeadPt.Y);
                    List<Point2d> routingRing = ResolveRoutingRingForHead(
                        floorRoomOwnerships, hp2, w.ZoneBoundaryHandleHex, w.ZoneRing);
                    if (routingRing != null && routingRing.Count >= 3)
                        w.ZoneRing = routingRing;
                }

                // Erase existing branch connections to the selected heads.
                // heads were served by those same polylines — they are orphaned and need new pipes.
                var selectedHeadIds = new HashSet<ObjectId>();
                foreach (var w in work)
                    selectedHeadIds.Add(w.EntityId);

                var orphanedHeadPts = new List<(Point2d pt, double elevZ, string zoneHex)>();
                int erasedExisting = EraseExistingBranchConnectionsToHeads(
                    tr, ms, db, work, selectedHeadIds, allowedZoneHexes, zoneRingsByHex, orphanedHeadPts);
                if (erasedExisting > 0)
                    ed.WriteMessage("\nRemoved " + erasedExisting + " existing branch connection(s) to selected sprinkler heads.\n");

                // Wider row bucket in explicit extension mode so slight drafting misalignment still chains on one lateral.
                double groupTol = explicitFeedExtensionMode
                    ? GetManualConnectRowBucketSizeDu(db, minTeeSpacingDu)
                    : Math.Max(minTeeSpacingDu * 0.5, 1e-3);
                var groups = new Dictionary<(ObjectId feedId, long rowKey), List<int>>();
                for (int i = 0; i < work.Count; i++)
                {
                    var h = work[i];
                    bool feedVertical = PolylineSpanIsVertical(h.BestFeed.Polyline);
                    double coord = feedVertical ? h.HeadPt.Y : h.HeadPt.X;
                    long rowKey = (long)Math.Round(coord / groupTol);
                    // Single explicit feed: grouping is alignment-only (same feed for all); keep feedId in key for multi-pick.
                    ObjectId feedKey = singlePickedFeed ? ObjectId.Null : h.BestFeed.Polyline.ObjectId;
                    var gk = (feedKey, rowKey);
                    if (!groups.TryGetValue(gk, out var bucket))
                    {
                        bucket = new List<int>();
                        groups[gk] = bucket;
                    }
                    bucket.Add(i);
                }

                foreach (var rawBucket in groups.Values)
                {
                    var bucket = new List<int>(rawBucket);
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
                            explicitFeedExtensionMode,
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

                    if (!TryGetFloorRoomKeyForPointAnyZone(
                            floorRoomOwnerships,
                            new Point2d(anchor.HeadPt.X, anchor.HeadPt.Y),
                            out long anchorRoomKey,
                            out List<Point2d> anchorRoomRing,
                            out _))
                    {
                        anchorRoomKey = -1;
                        anchorRoomRing = null;
                    }

                    if (anchorRoomKey >= 0)
                    {
                        var sameRoom = new List<int>();
                        foreach (int bi in bucket)
                        {
                            var hp = work[bi].HeadPt;
                            if (TryGetFloorRoomKeyForPointAnyZone(
                                    floorRoomOwnerships,
                                    new Point2d(hp.X, hp.Y),
                                    out long rk,
                                    out _,
                                    out _)
                                && rk == anchorRoomKey)
                                sameRoom.Add(bi);
                        }
                        bucket = sameRoom;
                    }

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
                            explicitFeedExtensionMode,
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

                    head0 = bucket[0];
                    anchor = work[head0];
                    feedVertical = PolylineSpanIsVertical(anchor.BestFeed.Polyline);
                    TryGetFloorRoomKeyForPointAnyZone(
                        floorRoomOwnerships,
                        new Point2d(anchor.HeadPt.X, anchor.HeadPt.Y),
                        out _,
                        out anchorRoomRing,
                        out _);

                    // Tee location: closest point on feed to the head nearest to the polyline (stable for this bucket).
                    int closestIdx = bucket[0];
                    double bestPerp = double.MaxValue;
                    foreach (int bi in bucket)
                    {
                        Point3d hp = work[bi].HeadPt;
                        try
                        {
                            Point3d cp = work[bi].BestFeed.Polyline.GetClosestPointTo(hp, extend: false);
                            double d = hp.DistanceTo(cp);
                            if (d < bestPerp - 1e-12)
                            {
                                bestPerp = d;
                                closestIdx = bi;
                            }
                        }
                        catch { /* skip */ }
                    }

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
                                explicitFeedExtensionMode,
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

                    // Order heads along the shared lateral from the tee so the polyline does not backtrack.
                    // Wrong order + collinear merge was dropping the farthest head (middle vertex removed).
                    OrderBucketAlongFeedLateral(bucket, work, feedVertical, attachPt);

                    double rowFixed = feedVertical ? attachPt.Y : attachPt.X;
                    var attach2d = new Point2d(attachPt.X, attachPt.Y);
                    List<Point2d> chainZoneRing = anchorRoomRing;
                    if (chainZoneRing == null || chainZoneRing.Count < 3)
                        chainZoneRing = anchor.ZoneRing;
                    if ((chainZoneRing == null || chainZoneRing.Count < 3)
                        && !string.IsNullOrEmpty(anchor.ZoneBoundaryHandleHex)
                        && zoneRingsByHex != null
                        && zoneRingsByHex.TryGetValue(anchor.ZoneBoundaryHandleHex, out List<Point2d> loadedAnchorRing)
                        && loadedAnchorRing != null && loadedAnchorRing.Count >= 3)
                        chainZoneRing = loadedAnchorRing;
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

                    verts = CollapseOrthogonalVertices(verts, mergeCollinearInterior: false);
                    if (verts == null || verts.Count < 2)
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches,
                            explicitFeedExtensionMode,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    if (verts[0].GetDistanceTo(verts[verts.Count - 1]) <= 1e-6)
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches,
                            explicitFeedExtensionMode,
                            minTeeSpacingDu, geometryMatchTolDu, usedAttachDistanceAlong, ref allowRedrawWhenDuplicateGeometry,
                            branchLayerId, ref created, ref skippedErased, ref skippedNoOrthogonalRoute,
                            ref skippedAlreadyOnSource, ref skippedDeclinedDuplicateBranch, ref skippedNoAttachToSelectedMain,
                            ref connectedFromMain, ref connectedFromBranch);
                        continue;
                    }

                    if (!ValidateOrthogonalRoute(verts, chainZoneRing, chainShaft))
                    {
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches,
                            explicitFeedExtensionMode,
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
                        FallbackBucketToSingleHeads(tr, ms, db, work, bucket, mains, branches,
                            explicitFeedExtensionMode,
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

                // Reconnect row/column neighbors with one segment each toward the manually connected head(s).
                int reconnectedOrphans = ReconnectOrphanAdjacentSegmentsOnManualRows(
                    tr, ms, db,
                    work,
                    allowedZoneHexes,
                    zoneRingsByHex,
                    floorRoomOwnerships,
                    groupTol,
                    minTeeSpacingDu,
                    branchLayerId);

                if (reconnectedOrphans > 0)
                    ed.WriteMessage("\nReconnected " + reconnectedOrphans + " orphan branch segment(s).\n");

                tr.Commit();

                ed.WriteMessage(
                    "\nManual branch connect complete. " +
                    "Created: " + created + " (from main: " + connectedFromMain + ", from branch feed: " + connectedFromBranch + ")" +
                    ", reconnected: " + reconnectedOrphans +
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
            bool explicitFeedExtensionMode,
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

            List<PipeCandidate> routeMains = mains;
            List<PipeCandidate> routeBranches = branches;
            bool routeUserRestricted = false;
            if (explicitFeedExtensionMode && w.BestFeed != null && w.BestFeed.Polyline != null && !w.BestFeed.Polyline.IsErased)
            {
                routeMains = new List<PipeCandidate> { w.BestFeed };
                routeBranches = null;
                routeUserRestricted = true;
            }

            if (!TryResolveOrthogonalRoute(
                    headPt,
                    routeMains,
                    routeBranches,
                    routeUserRestricted,
                    w.ZoneRing,
                    w.ShaftObs,
                    minTeeSpacingDu,
                    usedAttachDistanceAlong,
                    db,
                    out OrthogonalRouteResult route))
            {
                if (explicitFeedExtensionMode)
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
            bool explicitFeedExtensionMode,
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
                    explicitFeedExtensionMode,
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
        /// Row/column bucket size when extending from a single picked feed so near-aligned heads share one chain.
        /// </summary>
        private static double GetManualConnectRowBucketSizeDu(Database db, double minTeeSpacingDu)
        {
            double fromM = 0;
            if (db != null &&
                DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.025, out double du) &&
                du > 1e-12)
                fromM = du;
            return Math.Max(fromM, Math.Max(minTeeSpacingDu * 2.0, 0.05));
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

        /// <summary>
        /// Erases branch polylines that serve selected heads within the allowed zone scope only.
        /// Records other heads on those polylines that share the same zone tag.
        /// </summary>
        private static int EraseExistingBranchConnectionsToHeads(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            IReadOnlyList<ResolvedHeadWork> work,
            ISet<ObjectId> selectedHeadIds,
            ISet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<(Point2d pt, double elevZ, string zoneHex)> orphanedHeadPtsOut)
        {
            if (tr == null || ms == null || db == null || work == null || work.Count == 0)
                return 0;

            // Collect selected head positions (2D, ignoring elevation).
            var headPositions = new List<Point2d>(work.Count);
            foreach (var w in work)
                headPositions.Add(new Point2d(w.HeadPt.X, w.HeadPt.Y));

            // Tolerance: ~30 mm in drawing units; branch vertices should be essentially coincident
            // with the head, but allow for small floating-point or snap drift.
            double connTol = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.03, out double du) && du > 0)
                    connTol = du;
            }
            catch { }
            double connTol2 = connTol * connTol;

            // Snapshot all sprinkler heads once so we can find which ones shared erased laterals.
            var allHeadPts = new List<(ObjectId id, Point2d pt, double elevZ, string zoneHex)>();
            if (orphanedHeadPtsOut != null)
            {
                foreach (ObjectId hid in ms)
                {
                    if (hid.IsErased) continue;
                    Entity hent = null;
                    try { hent = tr.GetObject(hid, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (hent == null || !SprinklerLayers.IsSprinklerHeadEntity(tr, hent))
                        continue;
                    if (!TryGetHeadPoint(hent, out Point3d hp3))
                        continue;
                    SprinklerXData.TryGetZoneBoundaryHandle(hent, out string zHex);
                    allHeadPts.Add((hid, new Point2d(hp3.X, hp3.Y), hp3.Z, zHex ?? string.Empty));
                }
            }

            var orphanIds = new HashSet<ObjectId>();

            int erased = 0;
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (!IsBranchPipeLayerName(ent.Layer)) continue;
                var pl = ent as Polyline;
                if (pl == null || pl.Closed) continue;

                int nv = 0;
                try { nv = pl.NumberOfVertices; } catch { continue; }
                if (nv < 2) continue;

                bool touchesSelected = false;
                for (int vi = 0; vi < nv && !touchesSelected; vi++)
                {
                    Point3d v3;
                    try { v3 = pl.GetPoint3dAt(vi); } catch { continue; }
                    var v2 = new Point2d(v3.X, v3.Y);
                    foreach (var hp in headPositions)
                    {
                        double dx = v2.X - hp.X, dy = v2.Y - hp.Y;
                        if (dx * dx + dy * dy <= connTol2) { touchesSelected = true; break; }
                    }
                }

                if (!touchesSelected) continue;

                // Before erasing, record any heads on this polyline that were NOT selected
                // and belong to the same zone.
                if (orphanedHeadPtsOut != null)
                {
                    double elev = 0;
                    try { elev = pl.Elevation; } catch { }
                    foreach (var (hid, hpt, helev, hzoneHex) in allHeadPts)
                    {
                        if (selectedHeadIds != null && selectedHeadIds.Contains(hid))
                            continue;
                        if (orphanIds.Contains(hid))
                            continue;

                        string orphanZoneEffective = hzoneHex ?? string.Empty;
                        if (allowedZoneHexes != null && allowedZoneHexes.Count > 0)
                        {
                            if (string.IsNullOrEmpty(orphanZoneEffective) || !allowedZoneHexes.Contains(orphanZoneEffective))
                            {
                                if (!TryInferUniqueAllowedZoneAtPoint(hpt, allowedZoneHexes, zoneRingsByHex, out string inferredZh)
                                    || string.IsNullOrEmpty(inferredZh))
                                    continue;
                                orphanZoneEffective = inferredZh;
                            }
                        }

                        // Check if this head touches the polyline.
                        bool onPoly = false;
                        for (int vi = 0; vi < nv && !onPoly; vi++)
                        {
                            Point3d v3;
                            try { v3 = pl.GetPoint3dAt(vi); } catch { continue; }
                            double dx = v3.X - hpt.X, dy = v3.Y - hpt.Y;
                            if (dx * dx + dy * dy <= connTol2) { onPoly = true; }
                        }
                        if (!onPoly)
                        {
                            try
                            {
                                var cp = pl.GetClosestPointTo(new Point3d(hpt.X, hpt.Y, elev), false);
                                double dx = cp.X - hpt.X, dy = cp.Y - hpt.Y;
                                if (dx * dx + dy * dy <= connTol2) onPoly = true;
                            }
                            catch { }
                        }

                        if (onPoly)
                        {
                            orphanIds.Add(hid);
                            orphanedHeadPtsOut.Add((hpt, helev, orphanZoneEffective));
                        }
                    }
                }

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { }
            }

            return erased;
        }

        private static void MergeUniquePipeCandidates(List<PipeCandidate> into, List<PipeCandidate> add)
        {
            if (into == null || add == null)
                return;
            var seen = new HashSet<ObjectId>();
            foreach (var c in into)
            {
                if (c?.Polyline != null && !c.Polyline.ObjectId.IsNull)
                    seen.Add(c.Polyline.ObjectId);
            }
            foreach (var c in add)
            {
                if (c?.Polyline == null || c.Polyline.ObjectId.IsNull || c.Polyline.IsErased)
                    continue;
                if (seen.Add(c.Polyline.ObjectId))
                    into.Add(c);
            }
        }

        /// <summary>True when a branch polyline belongs to one of the allowed zones (tag match, or has geometry inside ring).</summary>
        private static bool BranchPolylineMatchesZoneScope(
            Polyline pl,
            ISet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex)
        {
            if (pl == null || allowedZoneHexes == null || allowedZoneHexes.Count == 0)
                return true;

            if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out string plZoneHex)
                && !string.IsNullOrEmpty(plZoneHex))
                return allowedZoneHexes.Contains(plZoneHex);

            foreach (string zh in allowedZoneHexes)
            {
                if (zoneRingsByHex != null
                    && zoneRingsByHex.TryGetValue(zh, out List<Point2d> ring)
                    && PolylineHasSampleInsideZoneRing(pl, ring))
                    return true;
            }

            return false;
        }

        private static bool PolylineHasSampleInsideZoneRing(Polyline pl, List<Point2d> zoneRing)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;
            try
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    if (PointInPolygon(zoneRing, pl.GetPoint2dAt(i)))
                        return true;
                }
                int nSeg = pl.Closed ? n : n - 1;
                for (int i = 0; i < nSeg; i++)
                {
                    var a = pl.GetPoint2dAt(i);
                    int i1 = pl.Closed ? ((i + 1) % n) : (i + 1);
                    var b = pl.GetPoint2dAt(i1);
                    var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                    if (PointInPolygon(zoneRing, mid))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when polyline has geometry strictly inside the zone ring.</summary>
        private static bool PolylineServesZoneRing(Polyline pl, List<Point2d> zoneRing, double boundaryTol)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;
            return PolylineHasSampleInsideZoneRing(pl, zoneRing);
        }

        /// <summary>Keep only pipe feeds tagged to an allowed zone (or fully inside its ring when untagged).</summary>
        private static List<PipeCandidate> FilterPipeCandidatesByZoneScope(
            List<PipeCandidate> candidates,
            ISet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex)
        {
            var filtered = new List<PipeCandidate>();
            if (candidates == null || candidates.Count == 0)
                return filtered;
            if (allowedZoneHexes == null || allowedZoneHexes.Count == 0)
                return new List<PipeCandidate>(candidates);

            foreach (var c in candidates)
            {
                var pl = c?.Polyline;
                if (pl == null || pl.IsErased)
                    continue;

                if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out string plZoneHex)
                    && !string.IsNullOrEmpty(plZoneHex))
                {
                    if (allowedZoneHexes.Contains(plZoneHex))
                        filtered.Add(c);
                    continue;
                }

                foreach (string zh in allowedZoneHexes)
                {
                    if (zoneRingsByHex != null
                        && zoneRingsByHex.TryGetValue(zh, out List<Point2d> ring)
                        && PolylineHasSampleInsideZoneRing(pl, ring))
                    {
                        filtered.Add(c);
                        break;
                    }
                }
            }

            return filtered;
        }

        /// <summary>Load boundary ring geometry from zone handle hex when cache is missing (required for containment checks).</summary>
        private static bool TryEnsureZoneRingLoaded(
            Database db,
            Transaction tr,
            string boundaryHandleHex,
            Dictionary<string, List<Point2d>> zoneRingsByHex)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex) || zoneRingsByHex == null)
                return false;
            if (zoneRingsByHex.TryGetValue(boundaryHandleHex, out List<Point2d> cached)
                && cached != null && cached.Count >= 3)
                return true;
            try
            {
                var h = new Handle(Convert.ToInt64(boundaryHandleHex, 16));
                ObjectId boundaryId = db.GetObjectId(false, h, 0);
                if (boundaryId.IsNull || boundaryId.IsErased)
                    return false;
                var boundary = tr.GetObject(boundaryId, OpenMode.ForRead, false) as Polyline;
                if (boundary == null || !boundary.Closed)
                    return false;
                List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
                if (ring == null || ring.Count < 3)
                    return false;
                zoneRingsByHex[boundaryHandleHex] = ring;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double ComputeSimplePolygonArea2d(IList<Point2d> ring)
        {
            if (ring == null || ring.Count < 3)
                return 0;
            double s = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = ring[i];
                var p1 = ring[(i + 1) % n];
                s += p0.X * p1.Y - p1.X * p0.Y;
            }
            return 0.5 * s;
        }

        /// <summary>
        /// When a sprinkler lacks zone XDATA, infer zone from containment in allowed rings.
        /// If multiple allowed outlines contain the point (boundary ambiguity), picks the smallest ring by area —
        /// typically the correct leaf zone vs a larger floor parcel.
        /// </summary>
        private static bool TryInferUniqueAllowedZoneAtPoint(
            Point2d hpt,
            ISet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            out string zoneHexOut)
        {
            zoneHexOut = null;
            if (allowedZoneHexes == null || allowedZoneHexes.Count == 0 || zoneRingsByHex == null)
                return false;

            string found = null;
            double bestArea = 0;
            bool haveArea = false;
            foreach (string zh in allowedZoneHexes.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                if (!zoneRingsByHex.TryGetValue(zh, out List<Point2d> ring)
                    || ring == null || ring.Count < 3)
                    continue;
                if (!PointInPolygon(ring, hpt))
                    continue;
                double a = Math.Abs(ComputeSimplePolygonArea2d(ring));
                if (!haveArea || a < bestArea)
                {
                    haveArea = true;
                    bestArea = a;
                    found = zh;
                }
            }

            if (found == null)
                return false;
            zoneHexOut = found;
            return true;
        }

        /// <summary>Zone inference for orphan reconnect — includes heads on/near the green boundary outline.</summary>
        private static bool TryResolveAllowedZoneHexAtPoint(
            Point2d hpt,
            ISet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            double boundaryTol,
            out string zoneHexOut)
        {
            zoneHexOut = null;
            if (allowedZoneHexes == null || allowedZoneHexes.Count == 0 || zoneRingsByHex == null)
                return false;

            string found = null;
            double bestArea = 0;
            bool haveArea = false;
            foreach (string zh in allowedZoneHexes.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                if (!zoneRingsByHex.TryGetValue(zh, out List<Point2d> ring)
                    || ring == null || ring.Count < 3)
                    continue;
                if (!PointInOrNearPolygon(ring, hpt, boundaryTol))
                    continue;
                double a = Math.Abs(ComputeSimplePolygonArea2d(ring));
                if (!haveArea || a < bestArea)
                {
                    haveArea = true;
                    bestArea = a;
                    found = zh;
                }
            }

            if (found == null)
                return false;
            zoneHexOut = found;
            return true;
        }

        private static string ResolveOrphanZoneHex(
            string zoneHex,
            Point2d pt,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex)
        {
            if (allowedZoneHexes == null || allowedZoneHexes.Count == 0)
                return OrphanReconnectNoZoneKey;

            zoneHex = zoneHex ?? string.Empty;
            if (!string.IsNullOrEmpty(zoneHex) && allowedZoneHexes.Contains(zoneHex))
                return zoneHex;

            // Strict interior containment only — near-polygon would pull in heads from adjacent zones.
            if (TryInferUniqueAllowedZoneAtPoint(pt, allowedZoneHexes, zoneRingsByHex, out string inferred)
                && !string.IsNullOrEmpty(inferred))
                return inferred;

            return null;
        }

        private static List<Point2d> ResolveZoneRingForReconnect(
            Database db,
            Transaction tr,
            string zoneHexKey,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<ResolvedHeadWork> manualWork)
        {
            if (string.IsNullOrEmpty(zoneHexKey)
                || string.Equals(zoneHexKey, OrphanReconnectNoZoneKey, StringComparison.Ordinal))
                return null;

            if (zoneRingsByHex != null
                && zoneRingsByHex.TryGetValue(zoneHexKey, out List<Point2d> cached)
                && cached != null && cached.Count >= 3)
                return cached;

            if (db != null && tr != null && zoneRingsByHex != null)
                TryEnsureZoneRingLoaded(db, tr, zoneHexKey, zoneRingsByHex);

            if (zoneRingsByHex != null
                && zoneRingsByHex.TryGetValue(zoneHexKey, out List<Point2d> loaded)
                && loaded != null && loaded.Count >= 3)
                return loaded;

            if (manualWork != null)
            {
                foreach (var w in manualWork)
                {
                    if (w?.ZoneRing == null || w.ZoneRing.Count < 3)
                        continue;
                    if (string.Equals(w.ZoneBoundaryHandleHex, zoneHexKey, StringComparison.OrdinalIgnoreCase))
                        return w.ZoneRing;
                }
            }

            return null;
        }

        private static long ComputeFloorRoomKey(List<Point2d> roomRing)
        {
            if (roomRing == null || roomRing.Count == 0)
                return -1;
            PolygonUtils.GetBoundingBox(roomRing, out double minX, out double minY, out double maxX, out double maxY);
            unchecked
            {
                long kx = (long)Math.Round(minX * 10000.0);
                long ky = (long)Math.Round(minY * 10000.0);
                long kw = (long)Math.Round((maxX - minX) * 10000.0);
                long kh = (long)Math.Round((maxY - minY) * 10000.0);
                return (kx * 73856093L) ^ (ky * 19349663L) ^ (kw * 83492791L) ^ (kh * 50331653L);
            }
        }

        /// <summary>Smallest floor-boundary room (green dashed cell) containing the point for this zone.</summary>
        private static bool TryGetFloorRoomKeyForPointInZone(
            List<SprinklerHeadReader2d.FloorRoomOwnership> rooms,
            Point2d p,
            string zoneHex,
            out long roomKey,
            out List<Point2d> roomRing)
        {
            roomKey = -1;
            roomRing = null;
            if (rooms == null || rooms.Count == 0 || string.IsNullOrWhiteSpace(zoneHex))
                return false;

            double bestArea = double.PositiveInfinity;
            foreach (var room in rooms)
            {
                if (room?.Ring == null || room.Ring.Count < 3)
                    continue;
                if (!string.Equals(room.ParentZoneHex, zoneHex.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!PointInPolygon(room.Ring, p))
                    continue;
                double area = room.Area;
                if (!(area > 0) || double.IsInfinity(area) || double.IsNaN(area))
                    area = double.PositiveInfinity;
                if (area < bestArea)
                {
                    bestArea = area;
                    roomRing = room.Ring;
                    roomKey = ComputeFloorRoomKey(room.Ring);
                }
            }

            return roomKey >= 0 && roomRing != null;
        }

        /// <summary>Smallest floor-boundary room containing the point, regardless of zone assignment.</summary>
        private static bool TryGetFloorRoomKeyForPointAnyZone(
            List<SprinklerHeadReader2d.FloorRoomOwnership> rooms,
            Point2d p,
            out long roomKey,
            out List<Point2d> roomRing,
            out string ownerZoneHex)
        {
            roomKey = -1;
            roomRing = null;
            ownerZoneHex = null;
            if (rooms == null || rooms.Count == 0)
                return false;

            double bestArea = double.PositiveInfinity;
            foreach (var room in rooms)
            {
                if (room?.Ring == null || room.Ring.Count < 3)
                    continue;
                if (!PointInPolygon(room.Ring, p))
                    continue;

                double area = room.Area;
                if (!(area > 0) || double.IsInfinity(area) || double.IsNaN(area))
                    area = double.PositiveInfinity;
                if (area < bestArea)
                {
                    bestArea = area;
                    roomRing = room.Ring;
                    ownerZoneHex = room.ParentZoneHex?.Trim();
                    roomKey = ComputeFloorRoomKey(room.Ring);
                }
            }

            return roomKey >= 0 && roomRing != null;
        }

        private static bool TryResolveZoneHexFromFloorRoomOwnership(
            List<SprinklerHeadReader2d.FloorRoomOwnership> rooms,
            Point2d p,
            HashSet<string> allowedZoneHexes,
            out string zoneHex)
        {
            zoneHex = null;
            if (!TryGetFloorRoomKeyForPointAnyZone(rooms, p, out _, out _, out string owner))
                return false;
            if (string.IsNullOrEmpty(owner))
                return false;
            if (allowedZoneHexes != null && allowedZoneHexes.Count > 0 && !allowedZoneHexes.Contains(owner))
                return false;
            zoneHex = owner;
            return true;
        }

        private static List<Point2d> ResolveRoutingRingForHead(
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRooms,
            Point2d headPt,
            string zoneHex,
            List<Point2d> parentZoneRing)
        {
            if (TryGetFloorRoomKeyForPointInZone(floorRooms, headPt, zoneHex, out _, out List<Point2d> roomRing)
                && roomRing != null && roomRing.Count >= 3)
                return roomRing;
            if (TryGetFloorRoomKeyForPointAnyZone(floorRooms, headPt, out _, out List<Point2d> anyRoomRing, out _)
                && anyRoomRing != null && anyRoomRing.Count >= 3)
                return anyRoomRing;
            return parentZoneRing;
        }

        private static bool SegmentFullyInsideRing(Point2d a, Point2d b, IList<Point2d> ring, double boundaryTol)
        {
            if (ring == null || ring.Count < 3)
                return true;
            var intervals = RingGeometry.ClipSegmentToRing(a, b, ring, boundaryTol: boundaryTol);
            if (intervals == null || intervals.Count != 1)
                return false;
            return intervals[0].t0 <= 1e-5 && intervals[0].t1 >= 1.0 - 1e-5;
        }

        /// <summary>Orthogonal collapse, shaft detour expansion, and zone/shaft validation for row laterals.</summary>
        private static bool NormalizeAndValidateRowLateralVerts(
            ref List<Point2d> verts,
            IList<(Point2d min, Point2d max)> shaftObs,
            List<Point2d> zoneRing,
            double minTeeSpacingDu)
        {
            if (verts == null)
                return false;
            verts = CollapseOrthogonalVertices(verts, mergeCollinearInterior: false);
            if (verts == null || verts.Count < 2)
                return false;
            double detourTol = Math.Max(minTeeSpacingDu * 0.05, 1e-4);
            var expanded = ExpandRouteThroughShaftDetours(verts, shaftObs, zoneRing, detourTol);
            if (expanded != null && expanded.Count >= 2)
                verts = CollapseOrthogonalVertices(expanded, mergeCollinearInterior: false);
            if (verts == null || verts.Count < 2)
                return false;
            return ValidateOrthogonalRoute(verts, zoneRing, shaftObs);
        }

        private static void MergeMainsLinkedThroughShaftAssignments(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<PipeCandidate> modelMainsPool,
            List<PipeCandidate> mergeInto)
        {
            if (tr == null || ms == null || db == null
                || allowedZoneHexes == null || allowedZoneHexes.Count == 0
                || mergeInto == null || modelMainsPool == null || zoneRingsByHex == null)
                return;

            double linkTol = 2.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 4.0, out double du) && du > 1e-6)
                    linkTol = Math.Max(linkTol, du);
            }
            catch { /* ignore */ }

            foreach (string zh in allowedZoneHexes)
            {
                TryEnsureZoneRingLoaded(db, tr, zh, zoneRingsByHex);
                if (!zoneRingsByHex.TryGetValue(zh, out List<Point2d> ring) || ring == null || ring.Count < 3)
                    continue;

                if (!TryResolveShaftInsertionWcs(tr, ms, db, zh, out Point3d shaftWcs))
                    continue;

                var shaft2d = new Point2d(shaftWcs.X, shaftWcs.Y);

                foreach (var cand in modelMainsPool)
                {
                    if (cand?.Polyline == null || cand.Polyline.IsErased || !IsEligibleMainPolyline(cand.Polyline))
                        continue;

                    Polyline pl = cand.Polyline;

                    if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out string taggedZh)
                        && !string.IsNullOrEmpty(taggedZh)
                        && !string.Equals(taggedZh, zh, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (MinOrthoDistancePointToPolyline2d(shaft2d, pl) > linkTol)
                        continue;

                    bool touchesShaftGeom = PolylineVertexWithinDistance2d(pl, shaft2d, linkTol);
                    bool servesZoneInterior = PolylineHasSampleInsideZoneRing(pl, ring);

                    if (!touchesShaftGeom && !servesZoneInterior)
                        continue;

                    MergeUniquePipeCandidates(mergeInto, new List<PipeCandidate> { cand });
                }
            }
        }

        /// <summary>Insertion point of shaft block for this zone boundary (SHAFT_ASSIGNMENT on zone, else shaft listing zone).</summary>
        private static bool TryResolveShaftInsertionWcs(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            string zoneBoundaryHandleHex,
            out Point3d shaftPosition)
        {
            shaftPosition = default;
            if (tr == null || ms == null || db == null || string.IsNullOrWhiteSpace(zoneBoundaryHandleHex))
                return false;

            Polyline zonePolyline = null;
            try
            {
                var hz = new Handle(Convert.ToInt64(zoneBoundaryHandleHex, 16));
                ObjectId zoneId = db.GetObjectId(false, hz, 0);
                if (!zoneId.IsNull && !zoneId.IsErased)
                    zonePolyline = tr.GetObject(zoneId, OpenMode.ForRead, false) as Polyline;
            }
            catch { /* ignore */ }

            if (zonePolyline != null && !zonePolyline.IsErased
                && SprinklerXData.TryGetShaftAssignmentHandle(zonePolyline, out string shaftAssignmentHex)
                && !string.IsNullOrWhiteSpace(shaftAssignmentHex))
            {
                try
                {
                    var shHandle = new Handle(Convert.ToInt64(shaftAssignmentHex.Trim(), 16));
                    ObjectId shaftId = db.GetObjectId(false, shHandle, 0);
                    if (!shaftId.IsNull && !shaftId.IsErased)
                    {
                        var br = tr.GetObject(shaftId, OpenMode.ForRead, false) as BlockReference;
                        if (br != null && !br.IsErased)
                        {
                            shaftPosition = br.Position;
                            return true;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                BlockReference br = null;
                try { br = tr.GetObject(oid, OpenMode.ForRead, false) as BlockReference; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (br == null || br.IsErased)
                    continue;

                if (!SprinklerXData.TryGetZoneAssignmentHandles(br, out List<string> zs) || zs == null)
                    continue;
                bool assignedHere = false;
                string needle = zoneBoundaryHandleHex.Trim();
                foreach (string z in zs)
                {
                    if (!string.IsNullOrEmpty(z) && string.Equals(z.Trim(), needle, StringComparison.OrdinalIgnoreCase))
                    {
                        assignedHere = true;
                        break;
                    }
                }

                if (!assignedHere)
                    continue;

                shaftPosition = br.Position;
                return true;
            }

            if (zonePolyline != null && zonePolyline.Closed)
            {
                try
                {
                    List<Point3d> inside = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, zonePolyline);
                    if (inside != null && inside.Count > 0)
                    {
                        shaftPosition = inside[0];
                        return true;
                    }
                }
                catch { /* ignore */ }
            }

            return false;
        }

        private static double MinOrthoDistancePointToPolyline2d(Point2d p, Polyline pl)
        {
            if (pl == null) return double.MaxValue;
            try
            {
                double z = pl.Elevation;
                Point3d cp = pl.GetClosestPointTo(new Point3d(p.X, p.Y, z), extend: false);
                return p.GetDistanceTo(new Point2d(cp.X, cp.Y));
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static bool PolylineVertexWithinDistance2d(Polyline pl, Point2d q, double tol)
        {
            if (pl == null || tol < 1e-12)
                return false;
            double tol2 = tol * tol;
            try
            {
                int nv = pl.NumberOfVertices;
                for (int i = 0; i < nv; i++)
                {
                    var pi = pl.GetPoint2dAt(i);
                    double dx = pi.X - q.X, dy = pi.Y - q.Y;
                    if (dx * dx + dy * dy <= tol2)
                        return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool PipeCandidateMatchesHeadZone(
            PipeCandidate candidate,
            string headZoneHex,
            List<Point2d> headZoneRing)
        {
            if (candidate?.Polyline == null || string.IsNullOrEmpty(headZoneHex))
                return true;

            if (SprinklerXData.TryGetZoneBoundaryHandle(candidate.Polyline, out string feedZoneHex)
                && !string.IsNullOrEmpty(feedZoneHex))
                return string.Equals(feedZoneHex, headZoneHex, StringComparison.OrdinalIgnoreCase);

            return headZoneRing != null && headZoneRing.Count >= 3
                && PolylineHasSampleInsideZoneRing(candidate.Polyline, headZoneRing);
        }

        /// <summary>Interior ladder heads need at least this many branch polyline edges incident at the sprinkler.</summary>
        private const int MinBranchSegmentsPerSprinkler = 2;

        /// <summary>
        /// After manual connect, any sprinkler with fewer than two branch segments on the same row/column
        /// as a selected head is treated as an orphan candidate.
        /// </summary>
        private static void ExpandOrphanCandidatesFromUnservedRowMates(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<ResolvedHeadWork> work,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            double groupTol,
            double headTol,
            List<(Point2d pt, double elevZ, string zoneHex)> orphanedHeadPts)
        {
            if (tr == null || ms == null || orphanedHeadPts == null)
                return;

            double bucket = Math.Max(groupTol, 1e-6);
            var affectedRows = new HashSet<(string zone, long roomKey, long rowKey)>();

            void NoteAffectedRow(string zoneHex, Point2d pt)
            {
                string z = ResolveOrphanZoneHex(zoneHex, pt, allowedZoneHexes, zoneRingsByHex);
                if (z == null && TryResolveZoneHexFromFloorRoomOwnership(floorRoomOwnerships, pt, allowedZoneHexes, out string roomZone))
                    z = roomZone;
                if (z == null)
                    return;
                long roomKey = -1;
                if (TryGetFloorRoomKeyForPointAnyZone(floorRoomOwnerships, pt, out long rk, out _, out string owner)
                    && (string.Equals(z, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                        || string.Equals(owner, z, StringComparison.OrdinalIgnoreCase)))
                    roomKey = rk;
                affectedRows.Add((z, roomKey, (long)Math.Round(pt.Y / bucket)));
            }

            if (work != null)
            {
                foreach (var w in work)
                {
                    if (w == null) continue;
                    NoteAffectedRow(w.ZoneBoundaryHandleHex, new Point2d(w.HeadPt.X, w.HeadPt.Y));
                }
            }

            foreach (var (pt, _, zoneHex) in orphanedHeadPts)
                NoteAffectedRow(zoneHex, pt);

            if (affectedRows.Count == 0)
                return;

            var existing = new HashSet<(long qx, long qy)>();
            foreach (var (pt, _, _) in orphanedHeadPts)
                existing.Add(((long)Math.Round(pt.X / bucket), (long)Math.Round(pt.Y / bucket)));

            foreach (ObjectId hid in ms)
            {
                if (hid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(hid, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null || !SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;
                if (!TryGetHeadPoint(ent, out Point3d hp3))
                    continue;

                var hp2 = new Point2d(hp3.X, hp3.Y);
                if (CountIncidentBranchSegmentsAtHead(tr, ms, hp2, headTol) >= MinBranchSegmentsPerSprinkler)
                    continue;

                long rowKey = (long)Math.Round(hp3.Y / bucket);
                SprinklerXData.TryGetZoneBoundaryHandle(ent, out string zHex);
                string keyZone = ResolveOrphanZoneHex(zHex, hp2, allowedZoneHexes, zoneRingsByHex);
                if (keyZone == null && TryResolveZoneHexFromFloorRoomOwnership(floorRoomOwnerships, hp2, allowedZoneHexes, out string roomZone))
                    keyZone = roomZone;
                if (keyZone == null)
                    continue;

                long roomKey = -1;
                if (TryGetFloorRoomKeyForPointAnyZone(floorRoomOwnerships, hp2, out long rk, out _, out string owner)
                    && (string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                        || string.Equals(owner, keyZone, StringComparison.OrdinalIgnoreCase)))
                    roomKey = rk;

                if (!affectedRows.Contains((keyZone, roomKey, rowKey)))
                    continue;

                if (!string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                    && zoneRingsByHex != null
                    && zoneRingsByHex.TryGetValue(keyZone, out List<Point2d> ring)
                    && ring != null && ring.Count >= 3)
                {
                    bool taggedThisZone = !string.IsNullOrEmpty(zHex)
                        && string.Equals(zHex, keyZone, StringComparison.OrdinalIgnoreCase);
                    if (!taggedThisZone && !PointInPolygon(ring, hp2))
                        continue;
                }

                var dedupeKey = ((long)Math.Round(hp2.X / bucket), (long)Math.Round(hp2.Y / bucket));
                if (!existing.Add(dedupeKey))
                    continue;

                string storeZone = string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal) ? string.Empty : keyZone;
                orphanedHeadPts.Add((hp2, hp3.Z, storeZone));
            }
        }

        /// <summary>
        /// Orphan reconnect feeds: keep everything already scoped to this command (picked mains, shaft-linked trunk,
        /// zone-tagged feeds) and drop only polylines explicitly tagged to a different zone.
        /// </summary>
        private static List<PipeCandidate> SelectOrphanReconnectFeedsForZone(
            List<PipeCandidate> allMainsInZone,
            string zoneHexKey,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            double boundaryTol,
            List<ResolvedHeadWork> manualWork)
        {
            var result = new List<PipeCandidate>();
            if (allMainsInZone == null || allMainsInZone.Count == 0)
                return result;
            if (string.IsNullOrEmpty(zoneHexKey)
                || string.Equals(zoneHexKey, OrphanReconnectNoZoneKey, StringComparison.Ordinal))
                return new List<PipeCandidate>(allMainsInZone);

            List<Point2d> zoneRing = null;
            zoneRingsByHex?.TryGetValue(zoneHexKey, out zoneRing);

            foreach (var c in allMainsInZone)
            {
                if (c?.Polyline == null || c.Polyline.IsErased)
                    continue;

                if (SprinklerXData.TryGetZoneBoundaryHandle(c.Polyline, out string tagged)
                    && !string.IsNullOrEmpty(tagged))
                {
                    if (string.Equals(tagged, zoneHexKey, StringComparison.OrdinalIgnoreCase))
                        result.Add(c);
                    continue;
                }

                if (zoneRing != null && PolylineServesZoneRing(c.Polyline, zoneRing, boundaryTol))
                    result.Add(c);
            }

            if (manualWork != null)
            {
                var picked = new List<PipeCandidate>();
                foreach (var w in manualWork)
                {
                    if (w?.BestFeed?.Polyline == null || w.BestFeed.Polyline.IsErased)
                        continue;
                    if (!string.Equals(w.ZoneBoundaryHandleHex, zoneHexKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    picked.Add(w.BestFeed);
                }
                MergeUniquePipeCandidates(result, picked);
            }

            return result;
        }

        /// <summary>
        /// For each manually connected head, link underserved neighbors on the same row/column with a single
        /// straight branch segment to the next adjacent sprinkler (ladder run only — no main tees, no extra routing).
        /// </summary>
        private static int ReconnectOrphanAdjacentSegmentsOnManualRows(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<ResolvedHeadWork> manualWork,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            double groupTol,
            double minTeeSpacingDu,
            ObjectId branchLayerId)
        {
            if (tr == null || ms == null || db == null || manualWork == null || manualWork.Count == 0)
                return 0;

            double headTol = GetBranchHeadConnectionToleranceDu(db);
            double bucket = Math.Max(groupTol, 1e-6);
            double mainRefWidth = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            double branchWidth = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidth);
            if (!(branchWidth > 1e-12))
                branchWidth = Math.Max(mainRefWidth * 0.66, 1.0);

            var processedLines = new HashSet<(string zone, long roomKey, bool chainAlongX, long lineBucket)>();
            int drawn = 0;

            foreach (var anchor in manualWork)
            {
                if (anchor?.BestFeed?.Polyline == null)
                    continue;

                var anchorPt = new Point2d(anchor.HeadPt.X, anchor.HeadPt.Y);
                string zoneHex = anchor.ZoneBoundaryHandleHex ?? string.Empty;
                if (allowedZoneHexes != null && allowedZoneHexes.Count > 0
                    && !string.IsNullOrEmpty(zoneHex) && !allowedZoneHexes.Contains(zoneHex))
                    continue;

                if (!TryGetFloorRoomKeyForPointAnyZone(
                        floorRoomOwnerships, anchorPt, out long roomKey, out List<Point2d> roomRing, out _))
                    roomKey = -1;

                bool feedVertical = PolylineSpanIsVertical(anchor.BestFeed.Polyline);
                bool chainAlongX = feedVertical;
                long lineBucket = chainAlongX
                    ? (long)Math.Round(anchor.HeadPt.Y / bucket)
                    : (long)Math.Round(anchor.HeadPt.X / bucket);

                var lineKey = (zoneHex ?? string.Empty, roomKey, chainAlongX, lineBucket);
                if (!processedLines.Add(lineKey))
                    continue;

                List<Point2d> routingRing = roomRing;
                if (routingRing == null || routingRing.Count < 3)
                {
                    routingRing = ResolveRoutingRingForHead(
                        floorRoomOwnerships, anchorPt, zoneHex, anchor.ZoneRing);
                    if ((routingRing == null || routingRing.Count < 3)
                        && !string.IsNullOrEmpty(zoneHex)
                        && zoneRingsByHex != null
                        && zoneRingsByHex.TryGetValue(zoneHex, out List<Point2d> parentRing))
                        routingRing = parentRing;
                }

                IList<(Point2d min, Point2d max)> shaftObs = BuildShaftObstaclesForZoneBoundaryHex(
                    tr, db, zoneHex, string.IsNullOrEmpty(zoneHex));

                var rowHeads = CollectSprinklersOnManualLine(
                    tr, ms, anchorPt, zoneHex, roomKey, chainAlongX, lineBucket, bucket, headTol,
                    allowedZoneHexes, zoneRingsByHex, floorRoomOwnerships, routingRing);
                if (rowHeads.Count < 2)
                    continue;

                double lineFixed = chainAlongX ? anchor.AttachOnFeedPreview.Y : anchor.AttachOnFeedPreview.X;
                if (double.IsNaN(lineFixed) || double.IsInfinity(lineFixed))
                    lineFixed = chainAlongX ? anchorPt.Y : anchorPt.X;

                var manualIndices = new HashSet<int>();
                foreach (var w in manualWork)
                {
                    if (w?.BestFeed?.Polyline == null)
                        continue;
                    var wPt = new Point2d(w.HeadPt.X, w.HeadPt.Y);
                    if (!string.Equals(w.ZoneBoundaryHandleHex ?? string.Empty, zoneHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool wFeedVertical = PolylineSpanIsVertical(w.BestFeed.Polyline);
                    if (wFeedVertical != feedVertical)
                        continue;
                    long wLineBucket = chainAlongX
                        ? (long)Math.Round(w.HeadPt.Y / bucket)
                        : (long)Math.Round(w.HeadPt.X / bucket);
                    if (wLineBucket != lineBucket)
                        continue;
                    if (roomKey >= 0
                        && (!TryGetFloorRoomKeyForPointAnyZone(
                                floorRoomOwnerships, wPt, out long wRoomKey, out _, out _)
                            || wRoomKey != roomKey))
                        continue;
                    int idx = FindClosestHeadIndex(rowHeads, wPt, headTol);
                    if (idx >= 0)
                        manualIndices.Add(idx);
                }

                if (manualIndices.Count == 0)
                    continue;

                foreach (int manualIdx in manualIndices)
                {
                    for (int j = manualIdx - 1; j >= 0; j--)
                    {
                        if (CountIncidentBranchSegmentsAtHead(tr, ms, rowHeads[j].pt, headTol) >= MinBranchSegmentsPerSprinkler)
                            continue;
                        if (TryDrawAdjacentOrphanSegment(
                                tr, ms, db, rowHeads[j], rowHeads[j + 1], chainAlongX, lineFixed,
                                branchLayerId, branchWidth, routingRing, shaftObs, minTeeSpacingDu, zoneHex))
                            drawn++;
                    }

                    for (int j = manualIdx + 1; j < rowHeads.Count; j++)
                    {
                        if (CountIncidentBranchSegmentsAtHead(tr, ms, rowHeads[j].pt, headTol) >= MinBranchSegmentsPerSprinkler)
                            continue;
                        if (TryDrawAdjacentOrphanSegment(
                                tr, ms, db, rowHeads[j - 1], rowHeads[j], chainAlongX, lineFixed,
                                branchLayerId, branchWidth, routingRing, shaftObs, minTeeSpacingDu, zoneHex))
                            drawn++;
                    }
                }
            }

            return drawn;
        }

        private static List<(Point2d pt, double elevZ)> CollectSprinklersOnManualLine(
            Transaction tr,
            BlockTableRecord ms,
            Point2d anchorPt,
            string zoneHex,
            long roomKey,
            bool chainAlongX,
            long lineBucket,
            double bucket,
            double headTol,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            List<Point2d> routingRing)
        {
            var heads = new List<(Point2d pt, double elevZ)>();
            foreach (ObjectId hid in ms)
            {
                if (hid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(hid, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null || !SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;
                if (!TryGetHeadPoint(ent, out Point3d hp3))
                    continue;

                var hp2 = new Point2d(hp3.X, hp3.Y);
                long headLineBucket = chainAlongX
                    ? (long)Math.Round(hp3.Y / bucket)
                    : (long)Math.Round(hp3.X / bucket);
                if (headLineBucket != lineBucket)
                    continue;

                SprinklerXData.TryGetZoneBoundaryHandle(ent, out string zHex);
                if (!string.IsNullOrEmpty(zoneHex)
                    && !string.IsNullOrEmpty(zHex)
                    && !string.Equals(zHex, zoneHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (allowedZoneHexes != null && allowedZoneHexes.Count > 0 && !string.IsNullOrEmpty(zHex)
                    && !allowedZoneHexes.Contains(zHex))
                    continue;

                if (roomKey >= 0)
                {
                    if (!TryGetFloorRoomKeyForPointAnyZone(
                            floorRoomOwnerships, hp2, out long headRoomKey, out _, out _)
                        || headRoomKey != roomKey)
                        continue;
                }

                if (routingRing != null && routingRing.Count >= 3 && !PointInPolygon(routingRing, hp2))
                    continue;

                bool dup = false;
                foreach (var (ep, _) in heads)
                {
                    if (ep.GetDistanceTo(hp2) <= headTol) { dup = true; break; }
                }
                if (!dup)
                    heads.Add((hp2, hp3.Z));
            }

            if (chainAlongX)
                heads.Sort((a, b) => a.pt.X.CompareTo(b.pt.X));
            else
                heads.Sort((a, b) => a.pt.Y.CompareTo(b.pt.Y));

            return heads;
        }

        private static int FindClosestHeadIndex(List<(Point2d pt, double elevZ)> heads, Point2d target, double tol)
        {
            if (heads == null || heads.Count == 0)
                return -1;
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < heads.Count; i++)
            {
                double d = heads[i].pt.GetDistanceTo(target);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return bestDist <= Math.Max(tol, 1e-6) * 3.0 ? best : -1;
        }

        private static bool TryDrawAdjacentOrphanSegment(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            (Point2d pt, double elevZ) aHead,
            (Point2d pt, double elevZ) bHead,
            bool chainAlongX,
            double lineFixed,
            ObjectId branchLayerId,
            double branchWidth,
            List<Point2d> routingRing,
            IList<(Point2d min, Point2d max)> shaftObs,
            double minTeeSpacingDu,
            string zoneHexTag)
        {
            Point2d a = chainAlongX
                ? new Point2d(aHead.pt.X, lineFixed)
                : new Point2d(lineFixed, aHead.pt.Y);
            Point2d b = chainAlongX
                ? new Point2d(bHead.pt.X, lineFixed)
                : new Point2d(lineFixed, bHead.pt.Y);

            if (a.GetDistanceTo(b) <= 1e-6)
                return false;

            double elevZ = aHead.elevZ;
            return TryAppendOrphanBranchSegment(
                tr, ms, db, a, b, elevZ, branchLayerId, branchWidth,
                routingRing, shaftObs, minTeeSpacingDu, zoneHexTag);
        }

        private static bool TryGetFloorRoomRingByKey(
            List<SprinklerHeadReader2d.FloorRoomOwnership> rooms,
            long roomKey,
            string zoneHex,
            out List<Point2d> roomRing)
        {
            roomRing = null;
            if (roomKey < 0 || rooms == null || string.IsNullOrWhiteSpace(zoneHex))
                return false;
            foreach (var room in rooms)
            {
                if (room?.Ring == null || room.Ring.Count < 3)
                    continue;
                if (!string.Equals(room.ParentZoneHex, zoneHex.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ComputeFloorRoomKey(room.Ring) == roomKey)
                {
                    roomRing = room.Ring;
                    return true;
                }
            }
            return false;
        }

        private static int ReconnectOrphansAsRowLaterals(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<(Point2d pt, double elevZ, string zoneHex)> orphanedHeadPts,
            List<ResolvedHeadWork> manualWork,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            List<PipeCandidate> allMainsInZone,
            double groupTol,
            double minTeeSpacingDu,
            ObjectId branchLayerId,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong)
        {
            if (tr == null || ms == null || db == null)
                return 0;

            if (manualWork != null && zoneRingsByHex != null)
            {
                foreach (var w in manualWork)
                {
                    if (w?.ZoneRing == null || w.ZoneRing.Count < 3 || string.IsNullOrEmpty(w.ZoneBoundaryHandleHex))
                        continue;
                    zoneRingsByHex[w.ZoneBoundaryHandleHex] = w.ZoneRing;
                }
            }

            if (allowedZoneHexes != null && zoneRingsByHex != null)
            {
                foreach (string zh in allowedZoneHexes)
                    TryEnsureZoneRingLoaded(db, tr, zh, zoneRingsByHex);
            }

            double headTol = GetBranchHeadConnectionToleranceDu(db);
            double bucket = Math.Max(groupTol, 1e-6);
            double boundaryTol = Math.Max(headTol, bucket * 0.5);

            var rowGroups = BuildOrphanRowGroups(
                tr, ms, orphanedHeadPts, manualWork, allowedZoneHexes, zoneRingsByHex,
                floorRoomOwnerships, bucket, headTol, boundaryTol);
            if (rowGroups.Count == 0)
                return 0;

            double mainRefWidth = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            double branchWidth = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidth);
            if (!(branchWidth > 1e-12))
                branchWidth = Math.Max(mainRefWidth * 0.66, 1.0);

            int drawn = 0;
            foreach (var kv in rowGroups)
            {
                string zoneHexKey = kv.Key.zone;
                long groupRoomKey = kv.Key.roomKey;
                var heads = kv.Value;
                if (heads == null || heads.Count == 0)
                    continue;

                bool reconnectNoZone = string.Equals(zoneHexKey, OrphanReconnectNoZoneKey, StringComparison.Ordinal);
                List<Point2d> zoneRing = ResolveZoneRingForReconnect(db, tr, zoneHexKey, zoneRingsByHex, manualWork);
                List<Point2d> routingRing = zoneRing;
                if (groupRoomKey >= 0
                    && TryGetFloorRoomRingByKey(floorRoomOwnerships, groupRoomKey, zoneHexKey, out List<Point2d> roomRing)
                    && roomRing != null && roomRing.Count >= 3)
                    routingRing = roomRing;

                if (routingRing != null && routingRing.Count >= 3)
                    heads.RemoveAll(h => !PointInPolygon(routingRing, h.pt));
                else if (!reconnectNoZone && zoneRing != null)
                    heads.RemoveAll(h => !PointInPolygon(zoneRing, h.pt));
                if (heads.Count == 0)
                    continue;

                bool anyNeeding = false;
                foreach (var (pt, _) in heads)
                {
                    if (CountIncidentBranchSegmentsAtHead(tr, ms, pt, headTol) < MinBranchSegmentsPerSprinkler)
                    {
                        anyNeeding = true;
                        break;
                    }
                }
                if (!anyNeeding)
                    continue;

                IList<(Point2d min, Point2d max)> shaftObs = BuildShaftObstaclesForZoneBoundaryHex(tr, db, zoneHexKey, reconnectNoZone);

                List<PipeCandidate> zoneMains = reconnectNoZone || allowedZoneHexes == null || allowedZoneHexes.Count == 0
                    ? allMainsInZone ?? new List<PipeCandidate>()
                    : SelectOrphanReconnectFeedsForZone(
                        allMainsInZone, zoneHexKey, zoneRingsByHex, boundaryTol, manualWork);

                bool feedVertical = true;
                if (manualWork != null)
                {
                    foreach (var w in manualWork)
                    {
                        if (w?.BestFeed?.Polyline == null)
                            continue;
                        var wpt = new Point2d(w.HeadPt.X, w.HeadPt.Y);
                        bool onRow = false;
                        foreach (var (pt, _) in heads)
                        {
                            if (Math.Abs(pt.Y - wpt.Y) <= bucket * 1.5)
                            {
                                onRow = true;
                                break;
                            }
                        }
                        if (!onRow)
                            continue;
                        feedVertical = PolylineSpanIsVertical(w.BestFeed.Polyline);
                        break;
                    }
                }

                bool chainAlongX = feedVertical;
                if (chainAlongX)
                    heads.Sort((a, b) => a.pt.X.CompareTo(b.pt.X));
                else
                    heads.Sort((a, b) => a.pt.Y.CompareTo(b.pt.Y));

                double lineFixed = chainAlongX ? heads[0].pt.Y : heads[0].pt.X;
                if (!TryResolveRowLateralFixedFromManualOrBranch(tr, ms, manualWork, heads, headTol, bucket, out lineFixed, out _))
                {
                    if (chainAlongX)
                    {
                        double sum = 0;
                        foreach (var (pt, _) in heads) sum += pt.Y;
                        lineFixed = sum / heads.Count;
                    }
                    else
                    {
                        double sum = 0;
                        foreach (var (pt, _) in heads) sum += pt.X;
                        lineFixed = sum / heads.Count;
                    }
                }

                var inZoneRuns = SplitInZoneRowRuns(heads, routingRing, chainAlongX, lineFixed, reconnectNoZone, boundaryTol);
                foreach (var run in inZoneRuns)
                {
                    if (run == null || run.Count == 0)
                        continue;

                    double elevZ = run[0].elevZ;
                    var lateralPts = new List<Point2d>();
                    foreach (var (pt, _) in run)
                    {
                        lateralPts.Add(chainAlongX
                            ? new Point2d(pt.X, lineFixed)
                            : new Point2d(lineFixed, pt.Y));
                    }

                    for (int i = 0; i < lateralPts.Count - 1; i++)
                    {
                        Point2d a = lateralPts[i], b = lateralPts[i + 1];
                        if (a.GetDistanceTo(b) <= headTol)
                            continue;

                        if (TryAppendOrphanBranchSegment(
                                tr, ms, db, a, b, elevZ, branchLayerId, branchWidth,
                                routingRing, shaftObs, minTeeSpacingDu, reconnectNoZone ? null : zoneHexKey))
                            drawn++;
                    }

                    for (int i = 0; i < run.Count; i++)
                    {
                        var headPt = run[i].pt;
                        var latPt = lateralPts[i];
                        if (headPt.GetDistanceTo(latPt) <= headTol)
                            continue;
                        if (CountIncidentBranchSegmentsAtHead(tr, ms, headPt, headTol) >= MinBranchSegmentsPerSprinkler)
                            continue;

                        if (TryAppendOrphanBranchSegment(
                                tr, ms, db, latPt, headPt, elevZ, branchLayerId, branchWidth,
                                routingRing, shaftObs, minTeeSpacingDu, reconnectNoZone ? null : zoneHexKey))
                            drawn++;
                    }

                    Point2d chainEnd = lateralPts[0];
                    Point2d chainEndHead = run[0].pt;
                    if (CountIncidentBranchSegmentsAtHead(tr, ms, chainEndHead, headTol) < MinBranchSegmentsPerSprinkler
                        && zoneMains != null && zoneMains.Count > 0)
                    {
                        drawn += TryDrawMainTeeToChainEnd(
                            tr, ms, db, zoneMains, chainEnd, chainAlongX, lineFixed, elevZ, branchLayerId, branchWidth,
                            routingRing, shaftObs, minTeeSpacingDu, boundaryTol, reconnectNoZone ? null : zoneHexKey,
                            usedAttachDistanceAlong);
                    }

                    chainEnd = lateralPts[lateralPts.Count - 1];
                    chainEndHead = run[run.Count - 1].pt;
                    if (run.Count > 1
                        && CountIncidentBranchSegmentsAtHead(tr, ms, chainEndHead, headTol) < MinBranchSegmentsPerSprinkler
                        && zoneMains != null && zoneMains.Count > 0)
                    {
                        drawn += TryDrawMainTeeToChainEnd(
                            tr, ms, db, zoneMains, chainEnd, chainAlongX, lineFixed, elevZ, branchLayerId, branchWidth,
                            routingRing, shaftObs, minTeeSpacingDu, boundaryTol, reconnectNoZone ? null : zoneHexKey,
                            usedAttachDistanceAlong);
                    }
                }
            }

            return drawn;
        }

        private static List<List<(Point2d pt, double elevZ)>> SplitInZoneRowRuns(
            IList<(Point2d pt, double elevZ)> orderedHeads,
            List<Point2d> zoneRing,
            bool chainAlongX,
            double lineFixed,
            bool skipZoneCheck,
            double boundaryTol)
        {
            var runs = new List<List<(Point2d pt, double elevZ)>>();
            if (orderedHeads == null || orderedHeads.Count == 0)
                return runs;
            if (skipZoneCheck || zoneRing == null || zoneRing.Count < 3)
            {
                runs.Add(new List<(Point2d pt, double elevZ)>(orderedHeads));
                return runs;
            }

            var run = new List<(Point2d pt, double elevZ)> { orderedHeads[0] };
            for (int i = 1; i < orderedHeads.Count; i++)
            {
                var prev = orderedHeads[i - 1].pt;
                var curr = orderedHeads[i].pt;
                Point2d a = chainAlongX ? new Point2d(prev.X, lineFixed) : new Point2d(lineFixed, prev.Y);
                Point2d b = chainAlongX ? new Point2d(curr.X, lineFixed) : new Point2d(lineFixed, curr.Y);

                if (PointInPolygon(zoneRing, curr)
                    && SegmentFullyInsideRing(a, b, zoneRing, boundaryTol))
                {
                    run.Add(orderedHeads[i]);
                }
                else
                {
                    if (run.Count > 0)
                        runs.Add(run);
                    run = new List<(Point2d pt, double elevZ)> { orderedHeads[i] };
                }
            }

            if (run.Count > 0)
                runs.Add(run);
            return runs;
        }

        private static bool ConnectionInsideZone2d(Point2d from, Point2d to, List<Point2d> zoneRing)
        {
            return ConnectionInsideZone(
                new Point3d(from.X, from.Y, 0),
                new Point3d(to.X, to.Y, 0),
                zoneRing);
        }

        private static Dictionary<(string zone, long roomKey, long rowKey), List<(Point2d pt, double elevZ)>>
            BuildOrphanRowGroups(
                Transaction tr,
                BlockTableRecord ms,
                List<(Point2d pt, double elevZ, string zoneHex)> orphanedHeadPts,
                List<ResolvedHeadWork> manualWork,
                HashSet<string> allowedZoneHexes,
                Dictionary<string, List<Point2d>> zoneRingsByHex,
                List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
                double bucket,
                double headTol,
                double boundaryTol)
        {
            var affectedRows = new HashSet<(string zone, long roomKey, long rowKey)>();
            var groups = new Dictionary<(string zone, long roomKey, long rowKey), List<(Point2d pt, double elevZ)>>();

            void NoteAffectedRow(string zoneHex, Point2d pt)
            {
                string z = ResolveOrphanZoneHex(zoneHex, pt, allowedZoneHexes, zoneRingsByHex);
                if (z == null && TryResolveZoneHexFromFloorRoomOwnership(floorRoomOwnerships, pt, allowedZoneHexes, out string roomZone))
                    z = roomZone;
                if (z == null)
                    return;
                long roomKey = -1;
                if (TryGetFloorRoomKeyForPointAnyZone(floorRoomOwnerships, pt, out long rk, out _, out string owner)
                    && (string.Equals(z, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                        || string.Equals(owner, z, StringComparison.OrdinalIgnoreCase)))
                    roomKey = rk;
                affectedRows.Add((z, roomKey, (long)Math.Round(pt.Y / bucket)));
            }

            if (orphanedHeadPts != null)
            {
                foreach (var (pt, _, zh) in orphanedHeadPts)
                    NoteAffectedRow(zh, pt);
            }

            if (manualWork != null)
            {
                foreach (var w in manualWork)
                {
                    if (w == null) continue;
                    NoteAffectedRow(w.ZoneBoundaryHandleHex, new Point2d(w.HeadPt.X, w.HeadPt.Y));
                }
            }

            if (affectedRows.Count == 0)
                return groups;

            foreach (ObjectId hid in ms)
            {
                if (hid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(hid, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null || !SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;
                if (!TryGetHeadPoint(ent, out Point3d hp3))
                    continue;

                var hp2 = new Point2d(hp3.X, hp3.Y);
                SprinklerXData.TryGetZoneBoundaryHandle(ent, out string zHex);
                string keyZone = ResolveOrphanZoneHex(zHex, hp2, allowedZoneHexes, zoneRingsByHex);
                if (keyZone == null && TryResolveZoneHexFromFloorRoomOwnership(floorRoomOwnerships, hp2, allowedZoneHexes, out string roomZone))
                    keyZone = roomZone;
                if (keyZone == null)
                    continue;

                long roomKey = -1;
                List<Point2d> routingRing = null;
                if (TryGetFloorRoomKeyForPointAnyZone(floorRoomOwnerships, hp2, out long rk, out List<Point2d> anyRoom, out string owner))
                {
                    if (string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                        || string.Equals(owner, keyZone, StringComparison.OrdinalIgnoreCase))
                    {
                        roomKey = rk;
                        routingRing = anyRoom;
                    }
                }

                if (routingRing == null && !string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal))
                {
                    TryGetFloorRoomKeyForPointInZone(floorRoomOwnerships, hp2, keyZone, out roomKey, out routingRing);
                    if (routingRing != null && routingRing.Count >= 3 && !PointInPolygon(routingRing, hp2))
                        continue;
                }

                if (routingRing == null
                    && !string.Equals(keyZone, OrphanReconnectNoZoneKey, StringComparison.Ordinal)
                    && zoneRingsByHex != null
                    && zoneRingsByHex.TryGetValue(keyZone, out List<Point2d> ring)
                    && ring != null && ring.Count >= 3)
                {
                    bool taggedThisZone = !string.IsNullOrEmpty(zHex)
                        && string.Equals(zHex, keyZone, StringComparison.OrdinalIgnoreCase);
                    if (!taggedThisZone && !PointInPolygon(ring, hp2))
                        continue;
                }

                long rowKey = (long)Math.Round(hp3.Y / bucket);
                if (!affectedRows.Contains((keyZone, roomKey, rowKey)))
                    continue;

                var dictKey = (keyZone, roomKey, rowKey);
                if (!groups.TryGetValue(dictKey, out var list))
                {
                    list = new List<(Point2d, double)>();
                    groups[dictKey] = list;
                }

                bool dup = false;
                foreach (var (ep, _) in list)
                {
                    if (ep.GetDistanceTo(hp2) <= headTol) { dup = true; break; }
                }
                if (!dup)
                    list.Add((hp2, hp3.Z));
            }

            return groups;
        }

        private static IList<(Point2d min, Point2d max)> BuildShaftObstaclesForZoneBoundaryHex(
            Transaction tr, Database db, string zoneHexKey, bool reconnectNoZone)
        {
            if (reconnectNoZone || string.IsNullOrEmpty(zoneHexKey) || tr == null || db == null)
                return null;
            try
            {
                var h = new Handle(Convert.ToInt64(zoneHexKey, 16));
                ObjectId boundaryId = db.GetObjectId(false, h, 0);
                if (boundaryId.IsNull || boundaryId.IsErased)
                    return null;
                var boundary = tr.GetObject(boundaryId, OpenMode.ForRead, false) as Polyline;
                if (boundary != null && boundary.Closed)
                    return BuildShaftObstaclesForZone(db, boundary);
            }
            catch { /* ignore */ }
            return null;
        }

        private static bool TryAppendOrphanBranchSegment(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            Point2d a,
            Point2d b,
            double elevZ,
            ObjectId branchLayerId,
            double branchWidth,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObs,
            double minTeeSpacingDu,
            string zoneHexTag)
        {
            if (tr == null || ms == null || db == null)
                return false;
            if (BranchSegmentExistsBetween(tr, ms, a, b, GetBranchHeadConnectionToleranceDu(db)))
                return false;

            var verts = new List<Point2d> { a, b };
            if (!NormalizeAndValidateRowLateralVerts(ref verts, shaftObs, zoneRing, minTeeSpacingDu))
                return false;

            Polyline seg = CreateOrthogonalBranchPolyline(db, verts, elevZ, branchLayerId, branchWidth);
            if (seg.NumberOfVertices < 2)
                return false;

            if (!string.IsNullOrEmpty(zoneHexTag))
                SprinklerXData.ApplyZoneBoundaryTag(seg, zoneHexTag);
            ms.AppendEntity(seg);
            tr.AddNewlyCreatedDBObject(seg, true);
            return true;
        }

        private static bool TryAppendValidatedBranchSegment(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            Point2d a,
            Point2d b,
            double elevZ,
            ObjectId branchLayerId,
            double branchWidth,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObs,
            double minTeeSpacingDu,
            string zoneHexTag)
        {
            if (tr == null || ms == null || db == null)
                return false;
            if (BranchSegmentExistsBetween(tr, ms, a, b, GetBranchHeadConnectionToleranceDu(db)))
                return false;

            var verts = new List<Point2d> { a, b };
            if (!NormalizeAndValidateRowLateralVerts(ref verts, shaftObs, zoneRing, minTeeSpacingDu))
                return false;

            Polyline seg = CreateOrthogonalBranchPolyline(db, verts, elevZ, branchLayerId, branchWidth);
            if (seg.NumberOfVertices < 2)
                return false;

            if (!string.IsNullOrEmpty(zoneHexTag))
                SprinklerXData.ApplyZoneBoundaryTag(seg, zoneHexTag);
            ms.AppendEntity(seg);
            tr.AddNewlyCreatedDBObject(seg, true);
            return true;
        }

        /// <summary>
        /// Connects a row-lateral chain end to the nearest main feed. At a trunk end, extends only
        /// outward along the lateral axis from the end vertex — no perpendicular stub on the main.
        /// </summary>
        private static int TryDrawMainTeeToChainEnd(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<PipeCandidate> zoneMains,
            Point2d chainEnd,
            bool chainAlongX,
            double lineFixed,
            double elevZ,
            ObjectId branchLayerId,
            double branchWidth,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObs,
            double minTeeSpacingDu,
            double boundaryTol,
            string zoneHexTag,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong)
        {
            if (zoneMains == null || zoneMains.Count == 0)
                return 0;

            double headTol = GetBranchHeadConnectionToleranceDu(db);

            // Prefer an outward lateral bar from a main end vertex (matches end-aux routing elsewhere).
            PipeCandidate outwardMain = null;
            Point2d outwardEndVtx = default;
            Point2d outwardLateralAnchor = default;
            double bestOutwardDist = double.MaxValue;

            foreach (var m in zoneMains)
            {
                if (m?.Polyline == null || m.Polyline.IsErased)
                    continue;
                if (!TryReadCollapsedPolylineVertices2d(m.Polyline, out List<Point2d> mainPts))
                    continue;

                Point2d start = mainPts[0];
                Point2d end = mainPts[mainPts.Count - 1];
                foreach (var endVtx in new[] { start, end })
                {
                    double outwardSign = TrunkEndOutwardSignFromPts(mainPts, endVtx, axisIsX: chainAlongX);
                    bool outward = chainAlongX
                        ? IsOutwardAlongAxis(chainEnd.X, endVtx.X, outwardSign)
                        : IsOutwardAlongAxis(chainEnd.Y, endVtx.Y, outwardSign);
                    if (!outward)
                        continue;

                    Point2d lateralAnchor = chainAlongX
                        ? new Point2d(endVtx.X, lineFixed)
                        : new Point2d(lineFixed, endVtx.Y);

                    if (zoneRing != null && zoneRing.Count >= 3)
                    {
                        if (!PointInOrNearPolygon(zoneRing, endVtx, boundaryTol))
                            continue;
                        if (!ConnectionInsideZone2d(lateralAnchor, chainEnd, zoneRing))
                            continue;
                    }

                    double d = lateralAnchor.GetDistanceTo(chainEnd);
                    if (d < bestOutwardDist)
                    {
                        bestOutwardDist = d;
                        outwardMain = m;
                        outwardEndVtx = endVtx;
                        outwardLateralAnchor = lateralAnchor;
                    }
                }
            }

            if (outwardMain != null)
            {
                int drawn = 0;
                if (outwardLateralAnchor.GetDistanceTo(chainEnd) > headTol)
                {
                    if (TryAppendOrphanBranchSegment(
                            tr, ms, db, outwardLateralAnchor, chainEnd, elevZ, branchLayerId, branchWidth,
                            zoneRing, shaftObs, minTeeSpacingDu, zoneHexTag))
                        drawn = 1;
                }
                else
                {
                    drawn = 1;
                }

                if (drawn > 0)
                {
                    var endAttach3d = new Point3d(outwardEndVtx.X, outwardEndVtx.Y, elevZ);
                    if (TryGetDistanceAlongPolylineToPoint(outwardMain.Polyline, endAttach3d, out double distAlong, out _))
                        RegisterTeeDistanceAlong(usedAttachDistanceAlong, outwardMain.Polyline.ObjectId, distAlong);
                }

                return drawn;
            }

            // Interior tap: perpendicular foot on main, then L along the lateral line to chainEnd.
            PipeCandidate bestMain = null;
            Point2d bestTap = default;
            double bestDist = double.MaxValue;
            foreach (var m in zoneMains)
            {
                if (m?.Polyline == null || m.Polyline.IsErased)
                    continue;
                if (!TryReadCollapsedPolylineVertices2d(m.Polyline, out List<Point2d> mainPts))
                    continue;
                if (!TryPickPerpendicularTapOnMainPolyline(mainPts, chainEnd, chainAlongX, out Point2d tap))
                    continue;

                if (zoneRing != null && zoneRing.Count >= 3)
                {
                    if (!PointInOrNearPolygon(zoneRing, tap, boundaryTol))
                        continue;
                    if (!ConnectionInsideZone2d(tap, chainEnd, zoneRing))
                        continue;
                }

                double d = tap.GetDistanceTo(chainEnd);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestMain = m;
                    bestTap = tap;
                }
            }

            if (bestMain == null)
                return 0;

            Point2d corner = chainAlongX
                ? new Point2d(bestTap.X, lineFixed)
                : new Point2d(lineFixed, bestTap.Y);

            int segmentsDrawn = 0;
            if (bestTap.GetDistanceTo(corner) > headTol)
            {
                if (TryAppendOrphanBranchSegment(
                        tr, ms, db, bestTap, corner, elevZ, branchLayerId, branchWidth,
                        zoneRing, shaftObs, minTeeSpacingDu, zoneHexTag))
                    segmentsDrawn++;
            }

            if (corner.GetDistanceTo(chainEnd) > headTol)
            {
                if (TryAppendOrphanBranchSegment(
                        tr, ms, db, corner, chainEnd, elevZ, branchLayerId, branchWidth,
                        zoneRing, shaftObs, minTeeSpacingDu, zoneHexTag))
                    segmentsDrawn++;
            }

            if (segmentsDrawn > 0)
            {
                var tap3d = new Point3d(bestTap.X, bestTap.Y, elevZ);
                if (TryGetDistanceAlongPolylineToPoint(bestMain.Polyline, tap3d, out double distAlong, out _))
                    RegisterTeeDistanceAlong(usedAttachDistanceAlong, bestMain.Polyline.ObjectId, distAlong);
            }

            return segmentsDrawn > 0 ? 1 : 0;
        }

        private static bool IsOutwardAlongAxis(double coord, double anchor, double outwardSign)
        {
            if (outwardSign > 0)
                return coord >= anchor - 1e-6;
            return coord <= anchor + 1e-6;
        }

        private static double TrunkEndOutwardSignFromPts(List<Point2d> mainPts, Point2d endVertex, bool axisIsX)
        {
            if (mainPts == null || mainPts.Count < 2)
                return 1;

            bool atStart = endVertex.GetDistanceTo(mainPts[0])
                <= endVertex.GetDistanceTo(mainPts[mainPts.Count - 1]);
            Point2d towardCenter = atStart ? mainPts[1] : mainPts[mainPts.Count - 2];

            double sign = axisIsX
                ? Math.Sign(endVertex.X - towardCenter.X)
                : Math.Sign(endVertex.Y - towardCenter.Y);
            if (Math.Abs(sign) < 1e-9)
                sign = atStart ? -1 : 1;
            return sign;
        }

        private static bool TryPickPerpendicularTapOnMainPolyline(
            List<Point2d> mainPts,
            Point2d chainEnd,
            bool chainAlongX,
            out Point2d tapOnMain)
        {
            tapOnMain = default;
            if (mainPts == null || mainPts.Count < 2)
                return false;

            var hits = new List<Point2d>();
            if (chainAlongX)
                CollectVerticalCutsWithMainPolyline(mainPts, chainEnd.X, hits);
            else
                CollectHorizontalCutsWithMainPolyline(mainPts, chainEnd.Y, hits);

            if (hits.Count == 0)
                return false;

            tapOnMain = hits[0];
            double best = chainEnd.GetDistanceTo(tapOnMain);
            for (int i = 1; i < hits.Count; i++)
            {
                double d = chainEnd.GetDistanceTo(hits[i]);
                if (d < best)
                {
                    best = d;
                    tapOnMain = hits[i];
                }
            }

            return true;
        }

        private static void CollectVerticalCutsWithMainPolyline(List<Point2d> mainPts, double xLine, List<Point2d> hitsOut)
        {
            if (mainPts == null || hitsOut == null)
                return;

            const double eps = 1e-6;
            for (int i = 0; i + 1 < mainPts.Count; i++)
            {
                var a = mainPts[i];
                var b = mainPts[i + 1];
                double dx = b.X - a.X;
                if (Math.Abs(dx) <= eps)
                {
                    if (Math.Abs(a.X - xLine) <= eps)
                    {
                        hitsOut.Add(new Point2d(xLine, Math.Min(a.Y, b.Y)));
                        hitsOut.Add(new Point2d(xLine, Math.Max(a.Y, b.Y)));
                    }
                    continue;
                }

                double t = (xLine - a.X) / dx;
                if (t < -eps || t > 1.0 + eps)
                    continue;
                t = Math.Max(0, Math.Min(1, t));
                hitsOut.Add(new Point2d(xLine, a.Y + t * (b.Y - a.Y)));
            }
        }

        private static void CollectHorizontalCutsWithMainPolyline(List<Point2d> mainPts, double yLine, List<Point2d> hitsOut)
        {
            if (mainPts == null || hitsOut == null)
                return;

            const double eps = 1e-6;
            for (int i = 0; i + 1 < mainPts.Count; i++)
            {
                var a = mainPts[i];
                var b = mainPts[i + 1];
                double dy = b.Y - a.Y;
                if (Math.Abs(dy) <= eps)
                {
                    if (Math.Abs(a.Y - yLine) <= eps)
                    {
                        hitsOut.Add(new Point2d(Math.Min(a.X, b.X), yLine));
                        hitsOut.Add(new Point2d(Math.Max(a.X, b.X), yLine));
                    }
                    continue;
                }

                double t = (yLine - a.Y) / dy;
                if (t < -eps || t > 1.0 + eps)
                    continue;
                t = Math.Max(0, Math.Min(1, t));
                hitsOut.Add(new Point2d(a.X + t * (b.X - a.X), yLine));
            }
        }

        private static int TryReconnectRemainingOrphansIndividually(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            List<(Point2d pt, double elevZ, string zoneHex)> orphanedHeadPts,
            List<PipeCandidate> allMainsInZone,
            HashSet<string> allowedZoneHexes,
            Dictionary<string, List<Point2d>> zoneRingsByHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            List<ResolvedHeadWork> manualWork,
            double minTeeSpacingDu,
            ObjectId branchLayerId,
            Dictionary<ObjectId, List<double>> usedAttachDistanceAlong)
        {
            if (tr == null || ms == null || db == null || orphanedHeadPts == null || orphanedHeadPts.Count == 0)
                return 0;

            double headTol = GetBranchHeadConnectionToleranceDu(db);
            double bucket = Math.Max(minTeeSpacingDu * 0.5, 1e-3);
            double boundaryTol = Math.Max(headTol, bucket * 0.5);
            double mainRefWidth = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            double branchWidth = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidth);
            if (!(branchWidth > 1e-12))
                branchWidth = Math.Max(mainRefWidth * 0.66, 1.0);

            int drawn = 0;
            var seen = new HashSet<(long qx, long qy)>();

            foreach (var (pt, elevZ, zoneHex) in orphanedHeadPts)
            {
                var dedupe = ((long)Math.Round(pt.X / bucket), (long)Math.Round(pt.Y / bucket));
                if (!seen.Add(dedupe))
                    continue;

                if (CountIncidentBranchSegmentsAtHead(tr, ms, pt, headTol) >= MinBranchSegmentsPerSprinkler)
                    continue;

                string zoneHexKey = string.IsNullOrEmpty(zoneHex) ? OrphanReconnectNoZoneKey : zoneHex;
                List<Point2d> parentRing = ResolveZoneRingForReconnect(db, tr, zoneHexKey, zoneRingsByHex, manualWork);
                List<Point2d> routingRing = ResolveRoutingRingForHead(floorRoomOwnerships, pt, zoneHex, parentRing);

                if (routingRing != null && routingRing.Count >= 3 && !PointInPolygon(routingRing, pt))
                    continue;

                IList<(Point2d min, Point2d max)> shaftObs = BuildShaftObstaclesForZoneBoundaryHex(
                    tr, db, zoneHex, string.IsNullOrEmpty(zoneHex));

                List<PipeCandidate> zoneMains = allowedZoneHexes == null || allowedZoneHexes.Count == 0
                    ? allMainsInZone ?? new List<PipeCandidate>()
                    : SelectOrphanReconnectFeedsForZone(
                        allMainsInZone, zoneHexKey, zoneRingsByHex, boundaryTol, manualWork);
                if (zoneMains == null || zoneMains.Count == 0)
                    continue;

                var headPt = new Point3d(pt.X, pt.Y, elevZ);
                if (!TryResolveOrthogonalRoute(
                        headPt,
                        zoneMains,
                        branches: null,
                        userRestrictedMains: false,
                        routingRing,
                        shaftObs,
                        minTeeSpacingDu,
                        usedAttachDistanceAlong,
                        db,
                        out OrthogonalRouteResult route))
                    continue;

                Polyline seg = CreateOrthogonalBranchPolyline(db, route.Vertices2d, elevZ, branchLayerId, branchWidth);
                if (seg.NumberOfVertices < 2)
                    continue;

                if (!string.IsNullOrEmpty(zoneHex))
                    SprinklerXData.ApplyZoneBoundaryTag(seg, zoneHex);

                ms.AppendEntity(seg);
                tr.AddNewlyCreatedDBObject(seg, true);
                RegisterTeeDistanceAlong(usedAttachDistanceAlong, route.SourcePolylineId, route.RegisteredDistanceAlong);
                drawn++;
            }

            return drawn;
        }

        private static bool BranchSegmentExistsBetween(
            Transaction tr,
            BlockTableRecord ms,
            Point2d a,
            Point2d b,
            double tol)
        {
            if (tr == null || ms == null)
                return false;
            double tol2 = tol * tol;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed || !IsBranchPipeLayerName(pl.Layer))
                    continue;

                try
                {
                    int nv = pl.NumberOfVertices;
                    for (int i = 0; i < nv - 1; i++)
                    {
                        var p0 = pl.GetPoint2dAt(i);
                        var p1 = pl.GetPoint2dAt(i + 1);
                        if ((p0.GetDistanceTo(a) <= tol && p1.GetDistanceTo(b) <= tol)
                            || (p0.GetDistanceTo(b) <= tol && p1.GetDistanceTo(a) <= tol))
                            return true;
                    }
                }
                catch { /* ignore */ }
            }

            return false;
        }

        /// <summary>Counts branch polyline edges that touch the sprinkler (endpoint or on-segment within tolerance).</summary>
        private static int CountIncidentBranchSegmentsAtHead(
            Transaction tr,
            BlockTableRecord ms,
            Point2d headPt,
            double tol)
        {
            if (tr == null || ms == null)
                return 0;

            double tol2 = tol * tol;
            int count = 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed || !IsBranchPipeLayerName(pl.Layer))
                    continue;

                try
                {
                    int nv = pl.NumberOfVertices;
                    for (int i = 0; i < nv - 1; i++)
                    {
                        var a = pl.GetPoint2dAt(i);
                        var b = pl.GetPoint2dAt(i + 1);
                        if (HeadTouchesBranchSegment(headPt, a, b, tol2))
                            count++;
                    }
                }
                catch { /* ignore */ }
            }

            return count;
        }

        private static bool HeadTouchesBranchSegment(Point2d headPt, Point2d a, Point2d b, double tol2)
        {
            double dxa = headPt.X - a.X, dya = headPt.Y - a.Y;
            if (dxa * dxa + dya * dya <= tol2)
                return true;
            double dxb = headPt.X - b.X, dyb = headPt.Y - b.Y;
            if (dxb * dxb + dyb * dyb <= tol2)
                return true;
            return DistancePointToSegment2d(headPt, a, b) <= tol2;
        }

        /// <summary>Row lateral elevation from manual connect tee, or from branch geometry at a served head in this row.</summary>
        private static bool TryResolveRowLateralFixedFromManualOrBranch(
            Transaction tr,
            BlockTableRecord ms,
            List<ResolvedHeadWork> manualWork,
            List<(Point2d pt, double elevZ)> heads,
            double headTol,
            double bucket,
            out double rowFixed,
            out bool feedVertical)
        {
            rowFixed = 0;
            feedVertical = true;

            if (manualWork != null)
            {
                foreach (var w in manualWork)
                {
                    if (w?.BestFeed?.Polyline == null)
                        continue;
                    var wpt = new Point2d(w.HeadPt.X, w.HeadPt.Y);
                    bool onRow = false;
                    foreach (var (pt, _) in heads)
                    {
                        if (pt.GetDistanceTo(wpt) <= Math.Max(headTol, bucket * 1.5)
                            || Math.Abs(pt.Y - wpt.Y) <= bucket * 1.5
                            || Math.Abs(pt.X - wpt.X) <= bucket * 1.5)
                        {
                            onRow = true;
                            break;
                        }
                    }
                    if (!onRow)
                        continue;

                    feedVertical = PolylineSpanIsVertical(w.BestFeed.Polyline);
                    rowFixed = feedVertical ? w.AttachOnFeedPreview.Y : w.AttachOnFeedPreview.X;
                    return true;
                }
            }

            Point2d servedPt = default;
            bool haveServed = false;
            foreach (var (pt, _) in heads)
            {
                if (CountIncidentBranchSegmentsAtHead(tr, ms, pt, headTol) >= MinBranchSegmentsPerSprinkler)
                {
                    servedPt = pt;
                    haveServed = true;
                    break;
                }
            }

            if (!haveServed)
                return false;

            if (TryGetRowFixedFromBranchAtHead(tr, ms, servedPt, headTol, out rowFixed))
                return true;

            return false;
        }

        private static bool TryGetRowFixedFromBranchAtHead(
            Transaction tr,
            BlockTableRecord ms,
            Point2d headPt,
            double tol,
            out double rowFixed)
        {
            rowFixed = headPt.Y;
            if (tr == null || ms == null)
                return false;

            double tol2 = tol * tol;
            Polyline bestPl = null;
            double bestDist = double.MaxValue;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed || !IsBranchPipeLayerName(pl.Layer))
                    continue;

                try
                {
                    var head3 = new Point3d(headPt.X, headPt.Y, pl.Elevation);
                    Point3d cp = pl.GetClosestPointTo(head3, extend: false);
                    double dx = cp.X - headPt.X, dy = cp.Y - headPt.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 <= tol2 && d2 < bestDist)
                    {
                        bestDist = d2;
                        bestPl = pl;
                    }
                }
                catch { /* ignore */ }
            }

            if (bestPl == null)
                return false;

            try
            {
                int nv = bestPl.NumberOfVertices;
                for (int i = 0; i < nv; i++)
                {
                    var v = bestPl.GetPoint2dAt(i);
                    double dx = v.X - headPt.X, dy = v.Y - headPt.Y;
                    if (dx * dx + dy * dy <= tol2)
                    {
                        rowFixed = v.Y;
                        return true;
                    }
                }

                for (int i = 0; i < nv - 1; i++)
                {
                    var a = bestPl.GetPoint2dAt(i);
                    var b = bestPl.GetPoint2dAt(i + 1);
                    if (Math.Abs(a.Y - b.Y) <= 1e-6)
                    {
                        double xmin = Math.Min(a.X, b.X) - tol;
                        double xmax = Math.Max(a.X, b.X) + tol;
                        if (headPt.X >= xmin && headPt.X <= xmax
                            && Math.Abs(headPt.Y - a.Y) <= tol * 2)
                        {
                            rowFixed = a.Y;
                            return true;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static double GetBranchHeadConnectionToleranceDu(Database db)
        {
            double tol = 1.0;
            try
            {
                if (db != null && DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.03, out double du) && du > 0)
                    tol = du;
            }
            catch { /* ignore */ }
            return tol;
        }

        private static bool HeadIsServedByBranch(Transaction tr, BlockTableRecord ms, Point2d headPt, double tol)
        {
            if (tr == null || ms == null)
                return false;

            double tol2 = tol * tol;
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed || !IsBranchPipeLayerName(pl.Layer))
                    continue;

                int nv = 0;
                try { nv = pl.NumberOfVertices; } catch { continue; }
                double elevZ = 0;
                try { elevZ = pl.Elevation; } catch { }

                for (int vi = 0; vi < nv; vi++)
                {
                    Point3d v3;
                    try { v3 = pl.GetPoint3dAt(vi); } catch { continue; }
                    double dx = v3.X - headPt.X, dy = v3.Y - headPt.Y;
                    if (dx * dx + dy * dy <= tol2)
                        return true;
                }

                try
                {
                    var head3 = new Point3d(headPt.X, headPt.Y, elevZ);
                    Point3d cp = pl.GetClosestPointTo(head3, extend: false);
                    double dx = cp.X - headPt.X, dy = cp.Y - headPt.Y;
                    if (dx * dx + dy * dy <= tol2)
                        return true;
                }
                catch { /* ignore */ }
            }

            return false;
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

            List<Point2d> target = FullyCollapseOrthogonalPolyline(route.Vertices2d);
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

                if (!OrthogonalPathListsDuplicateSameGeometry(target, existingVerts, vertexTolDu))
                    continue;

                double headTol = Math.Max(vertexTolDu * 10.0, 0.001);
                double dHead = MinDistanceToAnyVertex2d(head2d, existingVerts);
                if (dHead > headTol)
                    continue;

                return true;
            }

            return false;
        }

        private static double MinDistanceToAnyVertex2d(Point2d p, IList<Point2d> verts)
        {
            if (verts == null || verts.Count == 0)
                return double.MaxValue;
            double m = double.MaxValue;
            for (int i = 0; i < verts.Count; i++)
                m = Math.Min(m, p.GetDistanceTo(verts[i]));
            return m;
        }

        /// <summary>
        /// Repeatedly merges collinear runs so two drawings with different CAD vertex counts can still compare equal.
        /// </summary>
        private static List<Point2d> FullyCollapseOrthogonalPolyline(IList<Point2d> verts)
        {
            if (verts == null || verts.Count == 0)
                return null;
            var cur = CollapseOrthogonalVertices(new List<Point2d>(verts), mergeCollinearInterior: true);
            if (cur == null)
                return null;
            for (int g = 0; g < 64; g++)
            {
                var next = CollapseOrthogonalVertices(cur, mergeCollinearInterior: true);
                if (next == null || next.Count == cur.Count)
                    return cur;
                cur = next;
            }
            return cur;
        }

        /// <summary>
        /// True when two orthogonal vertex lists describe the same pipe path (exact match, or same prefix/suffix after collapse).
        /// Branch-fed routes often have extra corners vs main-fed L’s; strict equal vertex counts missed duplicates before.
        /// </summary>
        private static bool OrthogonalPathListsDuplicateSameGeometry(
            IList<Point2d> aIn,
            IList<Point2d> bIn,
            double tol)
        {
            var a = FullyCollapseOrthogonalPolyline(aIn);
            var b = FullyCollapseOrthogonalPolyline(bIn);
            if (a == null || b == null || a.Count < 2 || b.Count < 2)
                return false;
            if (OrthogonalVertexListsMatch(a, b, tol))
                return true;
            if (OrthogonalPolylinesDuplicateByPrefixSuffix(a, b, tol))
                return true;
            var bRev = ReverseVertexList(b);
            if (OrthogonalVertexListsMatch(a, bRev, tol))
                return true;
            return OrthogonalPolylinesDuplicateByPrefixSuffix(a, bRev, tol);
        }

        private static bool OrthogonalPolylinesDuplicateByPrefixSuffix(IList<Point2d> a, IList<Point2d> b, double tol)
        {
            if (a.Count <= b.Count)
            {
                if (IsOrthogonalPrefixAligned(a, b, tol)) return true;
                if (IsOrthogonalSuffixAligned(a, b, tol)) return true;
            }
            else
            {
                if (IsOrthogonalPrefixAligned(b, a, tol)) return true;
                if (IsOrthogonalSuffixAligned(b, a, tol)) return true;
            }
            return false;
        }

        private static bool IsOrthogonalPrefixAligned(IList<Point2d> shorter, IList<Point2d> longer, double tol)
        {
            if (shorter == null || longer == null || shorter.Count > longer.Count || shorter.Count < 2)
                return false;
            for (int i = 0; i < shorter.Count; i++)
            {
                if (shorter[i].GetDistanceTo(longer[i]) > tol)
                    return false;
            }
            return true;
        }

        private static bool IsOrthogonalSuffixAligned(IList<Point2d> shorter, IList<Point2d> longer, double tol)
        {
            if (shorter == null || longer == null || shorter.Count > longer.Count || shorter.Count < 2)
                return false;
            int off = longer.Count - shorter.Count;
            for (int i = 0; i < shorter.Count; i++)
            {
                if (shorter[i].GetDistanceTo(longer[i + off]) > tol)
                    return false;
            }
            return true;
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
                verts = FullyCollapseOrthogonalPolyline(raw);
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
            Database db,
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
                                minTeeSpacingDu, usedOnThisPoly, db, out route))
                            placed = true;
                    }
                    else
                    {
                        double up = distAlongRaw + ring * minTeeSpacingDu;
                        if (TryOrthogonalRouteFromPipeAtDistance(
                                pl, entry, up, head2, zoneRing, shaftObstacles,
                                minTeeSpacingDu, usedOnThisPoly, db, out route))
                            placed = true;
                        else
                        {
                            double dn = distAlongRaw - ring * minTeeSpacingDu;
                            if (TryOrthogonalRouteFromPipeAtDistance(
                                    pl, entry, dn, head2, zoneRing, shaftObstacles,
                                    minTeeSpacingDu, usedOnThisPoly, db, out route))
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
            Database db,
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

            if (!TryGetPolylineSegmentDirection(pl, attachPt, db, out SegmentAxisKind axisKind))
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

        private static bool TryGetPolylineSegmentDirection(Polyline pl, Point3d onCurve, Database db, out SegmentAxisKind axisKind)
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

            double onCurveTol = 0.05;
            if (db != null && DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double tDu) && tDu > 0)
                onCurveTol = tDu;
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

        /// <summary>
        /// Reorders bucket indices so head taps follow the shared lateral from the tee without reversing
        /// (avoids collinear-collapse removing the farthest sprinkler when it lands in the middle of the list).
        /// </summary>
        private static void OrderBucketAlongFeedLateral(
            List<int> bucket,
            List<ResolvedHeadWork> work,
            bool feedVertical,
            Point3d attachPt)
        {
            if (bucket == null || bucket.Count <= 1 || work == null)
                return;

            // Sort all heads by their lateral coordinate in one monotonic sweep (left → right or bottom → top).
            // This avoids the backtracking that occurred when concatenating near→far left with near→far right:
            // the chain crossed back past the tee after reaching the far-left end.
            bucket.Sort((a, b) =>
            {
                double la = feedVertical ? work[a].HeadPt.X : work[a].HeadPt.Y;
                double lb = feedVertical ? work[b].HeadPt.X : work[b].HeadPt.Y;
                return la.CompareTo(lb);
            });
        }

        private static List<Point2d> CollapseOrthogonalVertices(IList<Point2d> verts, bool mergeCollinearInterior = true)
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
            while (mergeCollinearInterior && r.Count >= 3)
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

        private static bool ConnectionInsideZoneOrNearBoundary(
            Point2d from,
            Point2d to,
            IList<Point2d> zoneRing,
            double nearTol)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return true;

            const int samples = 10;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                double x = from.X + (to.X - from.X) * t;
                double y = from.Y + (to.Y - from.Y) * t;
                if (!PointInOrNearPolygon(zoneRing, new Point2d(x, y), nearTol))
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

        /// <summary>Inside the ring, or within <paramref name="nearTol"/> DU of any ring edge (boundary sprinklers).</summary>
        private static bool PointInOrNearPolygon(IList<Point2d> ring, Point2d p, double nearTol)
        {
            if (ring == null || ring.Count < 3)
                return true;
            if (PointInPolygon(ring, p))
                return true;
            if (!(nearTol > 1e-12))
                return false;

            double tol2 = nearTol * nearTol;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if (DistancePointToSegment2d(p, a, b) <= tol2)
                    return true;
            }

            return false;
        }

        private static double DistancePointToSegment2d(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 1e-18)
                return p.GetDistanceTo(a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            double qx = a.X + t * dx, qy = a.Y + t * dy;
            double ex = p.X - qx, ey = p.Y - qy;
            return ex * ex + ey * ey;
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
