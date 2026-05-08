using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Drops sprinkler grid points that lie inside excluded "room" footprints.
    /// A room footprint is detected as a closed polyline inside the floor boundary that contains a text label (DBText/MText).
    /// If the label contains any excluded room-type token, sprinkler points inside that room outline are removed.
    /// </summary>
    public static class SprinklerRoomFootprintExclusion
    {
        private sealed class TextItem
        {
            public Point2d Position { get; set; }
            public string Text { get; set; }
        }

        private sealed class RoomItem
        {
            public List<Point2d> Ring { get; set; }
            public double MinX { get; set; }
            public double MinY { get; set; }
            public double MaxX { get; set; }
            public double MaxY { get; set; }
            public string Label { get; set; }
        }

        // Default excluded room-type tokens (case-insensitive; token-based match, not substring).
        // Example: "BEDROOM" does NOT match "ROOM" unless the label is split into tokens "BED" "ROOM".
        private static readonly HashSet<string> ExcludedRoomTypeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ROOM",
            "WC",
            "W.C",
            "TOILET",
            "BATH",
            "BATHROOM",
            "ELECTRICAL",
            "ELECTRIC",
            "SERVER",
            "IT",
            "PUMP",
            "STAIR",
            "STAIRS",
            "LIFT",
            "ELEVATOR",
            "ELEV",
            "SHAFT"
        };

        public static List<Point2d> RemovePointsInsideExcludedRooms(
            Database db,
            Polyline floorBoundary,
            IEnumerable<Point2d> points,
            out int excludedRoomCount,
            out int removedPointCount)
        {
            excludedRoomCount = 0;
            removedPointCount = 0;

            if (points == null)
                return new List<Point2d>();
            if (db == null || floorBoundary == null)
                return new List<Point2d>(points);

            var floorRing = GetRingPoints2d(floorBoundary);
            if (floorRing.Count < 3)
                return new List<Point2d>(points);

            double floorAreaAbs = 0.0;
            try { floorAreaAbs = Math.Abs(floorBoundary.Area); } catch { floorAreaAbs = 0.0; }

            var excludedRooms = new List<RoomItem>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var texts = new List<TextItem>();
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent is DBText dbText)
                    {
                        string txt = (dbText.TextString ?? string.Empty).Trim();
                        if (txt.Length == 0) continue;
                        texts.Add(new TextItem
                        {
                            Position = new Point2d(dbText.Position.X, dbText.Position.Y),
                            Text = txt
                        });
                    }
                    else if (ent is MText mText)
                    {
                        string txt = (mText.Text ?? mText.Contents ?? string.Empty).Trim();
                        if (txt.Length == 0) continue;
                        texts.Add(new TextItem
                        {
                            Position = new Point2d(mText.Location.X, mText.Location.Y),
                            Text = txt
                        });
                    }
                }

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is Polyline pl)) continue;
                    if (!pl.Closed || pl.NumberOfVertices < 3) continue;
                    if (pl.ObjectId == floorBoundary.ObjectId) continue;

                    // Skip "room candidates" that are essentially the floor itself.
                    if (floorAreaAbs > 0)
                    {
                        try
                        {
                            double a = Math.Abs(pl.Area);
                            if (a > floorAreaAbs * 0.95)
                                continue;
                        }
                        catch { /* ignore */ }
                    }

                    var ring = GetRingPoints2d(pl);
                    if (ring.Count < 3) continue;

                    // Ensure the room outline is inside the floor boundary.
                    bool allInside = true;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        if (!FindShaftsInsideBoundary.IsPointInPolygonRing(floorRing, ring[i]))
                        {
                            allInside = false;
                            break;
                        }
                    }
                    if (!allInside) continue;

                    GetBbox(ring, out double minX, out double minY, out double maxX, out double maxY);

                    // Find a label inside the room outline.
                    string label = null;
                    for (int ti = 0; ti < texts.Count; ti++)
                    {
                        var p = texts[ti].Position;
                        if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY)
                            continue;
                        if (FindShaftsInsideBoundary.IsPointInPolygonRing(ring, p))
                        {
                            label = texts[ti].Text;
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(label)) continue;

                    bool excluded = false;
                    foreach (var token in Tokenize(label))
                    {
                        if (ExcludedRoomTypeTokens.Contains(token))
                        {
                            excluded = true;
                            break;
                        }
                    }
                    if (!excluded) continue;

                    excludedRooms.Add(new RoomItem
                    {
                        Ring = ring,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        Label = label
                    });
                }

                tr.Commit();
            }

            excludedRoomCount = excludedRooms.Count;
            if (excludedRooms.Count == 0)
                return new List<Point2d>(points);

            var kept = new List<Point2d>();
            foreach (var p in points)
            {
                bool insideExcluded = false;
                for (int i = 0; i < excludedRooms.Count; i++)
                {
                    var r = excludedRooms[i];
                    if (p.X < r.MinX || p.X > r.MaxX || p.Y < r.MinY || p.Y > r.MaxY)
                        continue;
                    if (FindShaftsInsideBoundary.IsPointInPolygonRing(r.Ring, p))
                    {
                        insideExcluded = true;
                        break;
                    }
                }

                if (insideExcluded)
                {
                    removedPointCount++;
                    continue;
                }

                kept.Add(p);
            }

            return kept;
        }

        private static List<Point2d> GetRingPoints2d(Polyline pl)
        {
            var ring = new List<Point2d>(Math.Max(0, pl?.NumberOfVertices ?? 0));
            if (pl == null) return ring;
            for (int i = 0; i < pl.NumberOfVertices; i++)
                ring.Add(pl.GetPoint2dAt(i));
            return ring;
        }

        private static void GetBbox(List<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.PositiveInfinity;
            maxX = maxY = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            if (double.IsInfinity(minX)) { minX = minY = 0; maxX = maxY = 0; }
        }

        private static IEnumerable<string> Tokenize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                yield break;

            // Basic cleanup for common MText formatting.
            string s = raw.Replace("\\P", " ").Replace("\r", " ").Replace("\n", " ");

            var buf = new char[s.Length];
            int bi = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool isWord = char.IsLetterOrDigit(c);
                if (isWord)
                {
                    buf[bi++] = char.ToUpperInvariant(c);
                    continue;
                }

                if (bi > 0)
                {
                    yield return new string(buf, 0, bi);
                    bi = 0;
                }
            }

            if (bi > 0)
                yield return new string(buf, 0, bi);
        }
    }
}

