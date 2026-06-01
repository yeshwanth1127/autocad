using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.Licensing;
using autocad_final.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

namespace autocad_final.Commands
{
    /// <summary>
    /// Fixes a common post-routing defect: a short horizontal/vertical "stub" segment that goes opposite to the
    /// dominant branch direction near a shaft, causing a missing last row/column branch.
    /// </summary>
    public sealed class FixBranchingCommand
    {
        [CommandMethod("FIXBRANCHING", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;

            try
            {
                ExecuteCore(doc);
            }
            catch (Exception ex)
            {
                Agent.AgentLog.Write("FixBranching", "unhandled: " + ex);
                PaletteCommandErrorUi.ShowDialogThenCommandLine(
                    ed,
                    "Fix branching failed unexpectedly:\n" + ex.Message,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        private static void ExecuteCore(Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            if (!AttachBranchesCommand.TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
            {
                if (!string.IsNullOrWhiteSpace(shaftErr))
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, System.Windows.Forms.MessageBoxIcon.Warning);
                return;
            }

            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !AttachBranchesCommand.TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "No zone is assigned to this shaft.\nUse \"Assign shaft to zone\" to link this shaft to a zone first.",
                    System.Windows.Forms.MessageBoxIcon.Warning);
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
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                double tol = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-6);

                // Slanted / bent / multi-main zones: use polyline-trunk routing (auxiliary spine + row branches).
                // Do not rely on the longest trunk only — that is often the straight run while a shorter diagonal
                // leg from the shaft still needs Group-B sub-main routing.
                bool needsPolyline = AttachBranchesCommand.ZoneNeedsPolylineTrunkBranchRouting(db, zoneRing, tol);
                if (needsPolyline)
                {
                    ed.WriteMessage(
                        "\nFix branching: slanted or multi-segment main detected — routing auxiliary sub-main and uncovered rows...");

                    if (!AttachBranchesCommand.TryAttachBranchesForZone(
                            doc, db, zone, zoneRing, boundaryHandleHex,
                            routeBranchPipesFromConnectorFirst: false,
                            explicitMainPipePolylineId: ObjectId.Null,
                            drawBranchScheduleLabels: false,
                            out string routeErr))
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                            routeErr ?? "Failed to route branches for slanted main.",
                            System.Windows.Forms.MessageBoxIcon.Warning);
                        return;
                    }

                    try { ed.Regen(); } catch { /* ignore */ }
                    ed.WriteMessage("\nFix branching: slanted-main auxiliary branches routed.");
                    return;
                }

                if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out string trunkErr, boundaryHandleHex))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, trunkErr ?? "No trunk found in this zone.", System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                double dedupeTol = Math.Max(tol * 10.0, 1e-3);

                if (!SprinklerHeadReader2d.TryReadSprinklerHeadPointsForZoneRouting(db, zoneRing, boundaryHandleHex, dedupeTol, out var sprinklers, out string sprErr))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, sprErr ?? "Failed to read sprinklers in zone.", System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }
                if (sprinklers == null || sprinklers.Count == 0)
                {
                    ed.WriteMessage("\nNo sprinklers found in this zone.");
                    return;
                }

                using (var docLock = doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);

                    if (!DbObjectSafeAccess.TryGetObject(tr, trunkId, OpenMode.ForRead, out Polyline trunkPl))
                    {
                        ed.WriteMessage("\nTrunk was erased. Re-route main pipe and retry.");
                        return;
                    }

                    var trunkPts = PolylineToPoint2dList(trunkPl);
                    if (trunkPts.Count < 2)
                    {
                        ed.WriteMessage("\nTrunk polyline is invalid.");
                        return;
                    }

                    bool trunkHorizontal = InferTrunkHorizontal(db, boundaryHandleHex, trunkPts);
                    bool branchAxisIsY = trunkHorizontal; // trunk horizontal => branches go vertical (Y)
                    double trunkAxisCoord = trunkHorizontal ? Average(trunkPts.Select(p => p.Y)) : Average(trunkPts.Select(p => p.X));

                    double sprMedBranchAxis = branchAxisIsY ? Median(sprinklers.Select(s => s.Y)) : Median(sprinklers.Select(s => s.X));
                    int awaySign = SignNonZero(sprMedBranchAxis - trunkAxisCoord);

                    // Grid spacing along trunk axis (used as a "small stub" threshold).
                    double[] trunkAxisCoords = sprinklers.Select(p => trunkHorizontal ? p.X : p.Y).ToArray();
                    double spacing = EstimateGridSpacing(trunkAxisCoords, dedupeTol);
                    double mainW = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                    double branchW = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainW);

                    // A "stub" is typically a short wrong-axis jog (parallel to trunk axis) that should have been a full run.
                    // Keep this permissive: we prefer a false-positive candidate that still gets rejected by sprinkler-row checks.
                    double stubMaxLen = spacing > 0
                        ? Math.Max(spacing * 0.90, branchW * 4.0)
                        : Math.Max(branchW * 8.0, SprinklerLayers.BoundaryPolylineConstantWidth(db) * 2.0);
                    double axisTol = Math.Max(dedupeTol * 0.5, tol * 5.0);

                    ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);

                    var branches = CollectBranchPolylinesInZone(tr, db, zoneRing, boundaryHandleHex);
                    if (branches.Count == 0)
                    {
                        ed.WriteMessage("\nNo branch polylines found in this zone.");
                        tr.Commit();
                        return;
                    }

                    int candidateWrongAxisSegments = 0;
                    int candidateAcceptedStubs = 0;
                    var shortestWrongAxis = new List<double>();

                    int fixes = 0;
                    var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    foreach (var pl in branches)
                    {
                        var verts = PolylineToPoint2dList(pl);
                        if (verts.Count < 2) continue;

                        // Must have at least one long segment in expected "branch axis" orientation.
                        if (!HasLongExpectedAxisSegment(verts, branchAxisIsY, axisTol, stubMaxLen))
                            continue;

                        if (!TryFindWrongAxisStub(verts, trunkHorizontal, branchAxisIsY, axisTol, stubMaxLen,
                                awaySign, out var stub, ref candidateWrongAxisSegments, shortestWrongAxis))
                            continue;

                        candidateAcceptedStubs++;

                        // Nearest row/column coordinate on trunk axis, derived from the attachment point
                        // (the end connected to a long expected-axis segment).
                        double keyCoord = trunkHorizontal ? stub.Attach.X : stub.Attach.Y;
                        double nearestGrid = FindNearest(trunkAxisCoords, keyCoord);

                        // Pick sprinklers in that row/column.
                        double clusterTol = spacing > 0 ? Math.Max(spacing * 0.25, dedupeTol * 10.0) : Math.Max(dedupeTol * 30.0, 10.0);
                        var inLine = sprinklers.Where(s =>
                        {
                            double v = trunkHorizontal ? s.X : s.Y;
                            return Math.Abs(v - nearestGrid) <= clusterTol;
                        }).ToList();
                        if (inLine.Count == 0)
                            continue;

                        // Choose the "last" sprinkler along branch axis away from the trunk.
                        double extreme = branchAxisIsY
                            ? (awaySign > 0 ? inLine.Max(s => s.Y) : inLine.Min(s => s.Y))
                            : (awaySign > 0 ? inLine.Max(s => s.X) : inLine.Min(s => s.X));

                        Point2d target = trunkHorizontal
                            ? new Point2d(nearestGrid, extreme)
                            : new Point2d(extreme, nearestGrid);

                        if (stub.Outer.GetDistanceTo(target) <= dedupeTol)
                            continue;

                        if (BranchAlreadyReaches(branches, trunkHorizontal, nearestGrid, target, dedupeTol))
                            continue;

                        // Build an axis-aligned extension: align to the grid-line first, then run along branch axis.
                        var align = trunkHorizontal
                            ? new Point2d(nearestGrid, stub.Outer.Y)
                            : new Point2d(stub.Outer.X, nearestGrid);

                        var path = CollapseCollinear(new List<Point2d> { stub.Outer, align, target }, dedupeTol);
                        if (path.Count < 2) continue;

                        var newPl = new Polyline();
                        newPl.SetDatabaseDefaults(db);
                        newPl.LayerId = branchLayerId;
                        newPl.ConstantWidth = branchW;
                        newPl.Closed = false;
                        for (int i = 0; i < path.Count; i++)
                            newPl.AddVertexAt(i, path[i], 0, 0, 0);

                        SprinklerXData.ApplyZoneBoundaryTag(newPl, boundaryHandleHex?.Trim() ?? "");
                        ms.AppendEntity(newPl);
                        tr.AddNewlyCreatedDBObject(newPl, true);
                        fixes++;
                    }

                    tr.Commit();

                    if (fixes == 0)
                    {
                        shortestWrongAxis.Sort();
                        string few = shortestWrongAxis.Count == 0
                            ? "none"
                            : string.Join(", ", shortestWrongAxis.Take(5).Select(d => d.ToString("F2")));
                        ed.WriteMessage(
                            "\nFix branching: no wrong-axis stubs accepted.\n" +
                            $"  trunkHorizontal={trunkHorizontal}, branchAxis={(branchAxisIsY ? "Y" : "X")}, awaySign={awaySign}\n" +
                            $"  sprinklerGridSpacing≈{spacing:F2}, axisTol≈{axisTol:F2}, stubMaxLen≈{stubMaxLen:F2}\n" +
                            $"  branchPolylinesInZone={branches.Count}, wrongAxisSegmentsChecked={candidateWrongAxisSegments}, stubsMatchedPattern={candidateAcceptedStubs}\n" +
                            $"  shortestWrongAxisSegments=[{few}]");
                    }
                    else
                        ed.WriteMessage($"\nFix branching: created {fixes} branch extension(s).");
                }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        private static bool InferTrunkHorizontal(Database db, string zoneBoundaryHandleHex, List<Point2d> trunkPts)
        {
            try
            {
                var segs = MainPipeDetector.FindMainPipes(db, zoneBoundaryHandleHex);
                double a = MainPipeDetector.DominantAngleDeg(segs);
                if (!double.IsNaN(a))
                {
                    // Treat as horizontal if closer to 0° than 90° (normalised to [0,180)).
                    a = a % 180.0;
                    if (a < 0) a += 180.0;
                    double toHoriz = Math.Min(Math.Abs(a - 0.0), Math.Abs(a - 180.0));
                    double toVert = Math.Abs(a - 90.0);
                    return toHoriz <= toVert;
                }
            }
            catch
            {
                // ignore and fall back
            }

            // Fallback: bbox span.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in trunkPts)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return (maxX - minX) >= (maxY - minY);
        }

        private static List<Polyline> CollectBranchPolylinesInZone(Transaction tr, Database db, List<Point2d> zoneRing, string zoneBoundaryHandleHex)
        {
            var result = new List<Polyline>();
            if (tr == null || db == null) return result;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            string zoneHex = string.IsNullOrWhiteSpace(zoneBoundaryHandleHex) ? null : zoneBoundaryHandleHex.Trim();

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                var pl = ent as Polyline;
                if (pl == null) continue;

                if (!SprinklerLayers.IsBranchPipeGeometryLayerName(pl.Layer))
                    continue;
                if (SprinklerXData.IsTaggedTrunk(pl) || SprinklerXData.IsTaggedTrunkCap(pl) || SprinklerXData.IsTaggedConnector(pl))
                    continue;

                if (!string.IsNullOrEmpty(zoneHex) &&
                    SprinklerXData.TryGetZoneBoundaryHandle(pl, out var zh) &&
                    !string.IsNullOrWhiteSpace(zh) &&
                    !string.Equals(zh.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PolylineHasSampleInsideZone(pl, zoneRing))
                    continue;

                result.Add(pl);
            }

            return result;
        }

        private static bool PolylineHasSampleInsideZone(Polyline pl, List<Point2d> zoneRing)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3) return false;
            try
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    var v = pl.GetPoint3dAt(i);
                    if (PolygonUtils.PointInPolygon(zoneRing, new Point2d(v.X, v.Y)))
                        return true;
                    if (i + 1 < n)
                    {
                        var v2 = pl.GetPoint3dAt(i + 1);
                        var mid = new Point2d((v.X + v2.X) * 0.5, (v.Y + v2.Y) * 0.5);
                        if (PolygonUtils.PointInPolygon(zoneRing, mid))
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static (int domHoriz, int domVert) ComputeDominantDirections(List<Polyline> branches, double axisTol, double stubMaxLen)
        {
            double sumHx = 0;
            double sumVy = 0;

            foreach (var pl in branches)
            {
                var v = PolylineToPoint2dList(pl);
                for (int i = 0; i + 1 < v.Count; i++)
                {
                    var a = v[i];
                    var b = v[i + 1];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double adx = Math.Abs(dx);
                    double ady = Math.Abs(dy);
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len <= stubMaxLen) continue; // ignore micro/jog segments for dominance

                    bool horiz = ady <= axisTol && adx > axisTol;
                    bool vert = adx <= axisTol && ady > axisTol;
                    if (horiz) sumHx += Math.Sign(dx) * len;
                    else if (vert) sumVy += Math.Sign(dy) * len;
                }
            }

            int domHoriz = SignNonZero(sumHx);
            int domVert = SignNonZero(sumVy);
            return (domHoriz, domVert);
        }

        private static bool HasLongExpectedAxisSegment(List<Point2d> verts, bool branchAxisIsY, double axisTol, double stubMaxLen)
        {
            for (int i = 0; i + 1 < verts.Count; i++)
            {
                var a = verts[i];
                var b = verts[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double adx = Math.Abs(dx);
                double ady = Math.Abs(dy);
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= stubMaxLen) continue;

                bool horiz = ady <= axisTol && adx > axisTol;
                bool vert = adx <= axisTol && ady > axisTol;
                if (branchAxisIsY && vert) return true;
                if (!branchAxisIsY && horiz) return true;
            }
            return false;
        }

        private readonly struct StubInfo
        {
            public readonly Point2d Attach;
            public readonly Point2d Outer;
            public StubInfo(Point2d attach, Point2d outer)
            {
                Attach = attach;
                Outer = outer;
            }
        }

        /// <summary>
        /// Finds a short "wrong-axis" jog (parallel to trunk axis) that is attached to a long segment running
        /// in the expected branch axis. This matches the defect in the user's screenshots (tiny horizontal stub
        /// on otherwise-vertical grid, or vice versa).
        /// </summary>
        private static bool TryFindWrongAxisStub(
            List<Point2d> verts,
            bool trunkHorizontal,
            bool branchAxisIsY,
            double axisTol,
            double stubMaxLen,
            int awaySign,
            out StubInfo stub,
            ref int wrongAxisSegmentsChecked,
            List<double> shortestWrongAxisLens)
        {
            stub = default;
            if (verts == null || verts.Count < 2) return false;

            bool IsHoriz(Point2d a, Point2d b)
            {
                double dx = Math.Abs(b.X - a.X);
                double dy = Math.Abs(b.Y - a.Y);
                return dy <= axisTol && dx > axisTol;
            }

            bool IsVert(Point2d a, Point2d b)
            {
                double dx = Math.Abs(b.X - a.X);
                double dy = Math.Abs(b.Y - a.Y);
                return dx <= axisTol && dy > axisTol;
            }

            bool IsExpectedAxisLong(int segIdx)
            {
                if (segIdx < 0 || segIdx + 1 >= verts.Count) return false;
                var a = verts[segIdx];
                var b = verts[segIdx + 1];
                double len = a.GetDistanceTo(b);
                if (len <= stubMaxLen) return false;
                return branchAxisIsY ? IsVert(a, b) : IsHoriz(a, b);
            }

            bool IsWrongAxisShort(int segIdx, out double len)
            {
                len = 0;
                if (segIdx < 0 || segIdx + 1 >= verts.Count) return false;
                var a = verts[segIdx];
                var b = verts[segIdx + 1];
                len = a.GetDistanceTo(b);
                if (len < 1e-9 || len > stubMaxLen) return false;
                // Wrong axis means parallel to trunk axis (horizontal if trunk horizontal; vertical if trunk vertical).
                if (trunkHorizontal)
                    return IsHoriz(a, b);
                return IsVert(a, b);
            }

            // Scan all segments: the defect stub can appear mid-polyline, not only at the ends.
            for (int i = 0; i + 1 < verts.Count; i++)
            {
                if (!IsWrongAxisShort(i, out double len))
                    continue;

                wrongAxisSegmentsChecked++;
                if (shortestWrongAxisLens != null)
                {
                    shortestWrongAxisLens.Add(len);
                    if (shortestWrongAxisLens.Count > 64)
                        shortestWrongAxisLens.RemoveAt(0);
                }

                // Determine which endpoint is attached to a long expected-axis segment.
                // Segment i is between P[i] and P[i+1].
                bool leftAttached = IsExpectedAxisLong(i - 1);   // segment before shares P[i]
                bool rightAttached = IsExpectedAxisLong(i + 1);  // segment after shares P[i+1]
                if (leftAttached == rightAttached)
                    continue; // ambiguous jog or isolated short segment

                var attach = leftAttached ? verts[i] : verts[i + 1];
                var outer = leftAttached ? verts[i + 1] : verts[i];

                // Ensure "outer" is actually away from the trunk (so we extend outward, not back toward main).
                double attachBranch = branchAxisIsY ? attach.Y : attach.X;
                double outerBranch = branchAxisIsY ? outer.Y : outer.X;
                int dir = Math.Sign(outerBranch - attachBranch);
                if (dir == 0) dir = awaySign;
                if (dir != awaySign)
                    continue;

                stub = new StubInfo(attach, outer);
                return true;
            }
            return false;
        }

        private static bool BranchAlreadyReaches(List<Polyline> branches, bool trunkHorizontal, double trunkAxisCoord, Point2d target, double tol)
        {
            // Skip if any existing branch already has a vertex near the target and lies on the same trunk-axis "grid line".
            foreach (var pl in branches)
            {
                var v = PolylineToPoint2dList(pl);
                for (int i = 0; i < v.Count; i++)
                {
                    if (v[i].GetDistanceTo(target) > tol) continue;
                    double axis = trunkHorizontal ? v[i].X : v[i].Y;
                    if (Math.Abs(axis - trunkAxisCoord) <= tol * 5.0)
                        return true;
                }
            }
            return false;
        }

        private static List<Point2d> CollapseCollinear(List<Point2d> verts, double tol)
        {
            if (verts == null || verts.Count == 0) return new List<Point2d>();

            var res = new List<Point2d>();
            for (int i = 0; i < verts.Count; i++)
            {
                if (res.Count == 0 || res[res.Count - 1].GetDistanceTo(verts[i]) > tol)
                    res.Add(verts[i]);
            }

            if (res.Count <= 2) return res;

            // Remove middle points that are collinear.
            var outPts = new List<Point2d> { res[0] };
            for (int i = 1; i + 1 < res.Count; i++)
            {
                var a = outPts[outPts.Count - 1];
                var b = res[i];
                var c = res[i + 1];

                double abx = b.X - a.X, aby = b.Y - a.Y;
                double bcx = c.X - b.X, bcy = c.Y - b.Y;
                bool abHoriz = Math.Abs(aby) <= tol && Math.Abs(abx) > tol;
                bool abVert = Math.Abs(abx) <= tol && Math.Abs(aby) > tol;
                bool bcHoriz = Math.Abs(bcy) <= tol && Math.Abs(bcx) > tol;
                bool bcVert = Math.Abs(bcx) <= tol && Math.Abs(bcy) > tol;

                if ((abHoriz && bcHoriz) || (abVert && bcVert))
                    continue; // drop b

                outPts.Add(b);
            }
            outPts.Add(res[res.Count - 1]);
            return outPts;
        }

        private static List<Point2d> PolylineToPoint2dList(Polyline pl)
        {
            var pts = new List<Point2d>();
            if (pl == null) return pts;
            int n = 0;
            try { n = pl.NumberOfVertices; }
            catch { return pts; }
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var p = pl.GetPoint2dAt(i);
                    pts.Add(new Point2d(p.X, p.Y));
                }
                catch
                {
                    // ignore bad vertex
                }
            }
            return pts;
        }

        private static double Average(IEnumerable<double> xs)
        {
            double sum = 0;
            long n = 0;
            foreach (var x in xs)
            {
                sum += x;
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        private static double Median(IEnumerable<double> xs)
        {
            if (xs == null) return 0;
            var arr = xs.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
            if (arr.Length == 0) return 0;
            Array.Sort(arr);
            int mid = arr.Length / 2;
            if ((arr.Length & 1) == 1) return arr[mid];
            return 0.5 * (arr[mid - 1] + arr[mid]);
        }

        private static int SignNonZero(double v)
        {
            if (v > 1e-12) return 1;
            if (v < -1e-12) return -1;
            return 1;
        }

        private static double EstimateGridSpacing(double[] coords, double eps)
        {
            if (coords == null || coords.Length < 2) return 0;
            var arr = coords.Where(c => !double.IsNaN(c) && !double.IsInfinity(c)).ToArray();
            if (arr.Length < 2) return 0;
            Array.Sort(arr);

            var diffs = new List<double>();
            for (int i = 1; i < arr.Length; i++)
            {
                double d = arr[i] - arr[i - 1];
                if (d > eps)
                    diffs.Add(d);
            }
            if (diffs.Count == 0) return 0;
            diffs.Sort();
            int mid = diffs.Count / 2;
            return (diffs.Count & 1) == 1 ? diffs[mid] : 0.5 * (diffs[mid - 1] + diffs[mid]);
        }

        private static double FindNearest(double[] coords, double value)
        {
            if (coords == null || coords.Length == 0) return value;
            double best = coords[0];
            double bestD = Math.Abs(best - value);
            for (int i = 1; i < coords.Length; i++)
            {
                double d = Math.Abs(coords[i] - value);
                if (d < bestD)
                {
                    bestD = d;
                    best = coords[i];
                }
            }
            return best;
        }
    }
}

