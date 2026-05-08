using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Finds a closed floor/building outline in model space that contains all shaft sites, when the agent passes
    /// <c>floor_boundary_handle: "auto"</c> to <see cref="autocad_final.Agent.AgentWriteTools.CreateSprinklerZones"/>.
    /// </summary>
    public static class FloorBoundaryAutoDiscovery
    {
        private static int LayerPreferenceScore(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return 0;
            var s = layerName.ToUpperInvariant();
            if (s.Contains("WALL") || s.Contains("A-WALL") || s.Contains("EXTERIOR") || s.Contains("FOOTPRINT")
                || s.Contains("BOUND") || s.Contains("OUTLINE") || s.Contains("LIMIT") || s.Contains("PERIM")
                || s.Contains("B-BND") || s.Contains("FLOOR") || s.Contains("SLAB") || s.Contains("SITE")
                || s.Contains("ARCH") || s.Contains("BUILD"))
                return 2;
            if (s == "0" || s.Contains("DEFPOINTS"))
                return 0;
            return 1;
        }

        /// <summary>
        /// Picks the best closed LW polyline in model space that contains every shaft site (2D), preferring
        /// architectural layer names then the smallest-area valid polygon (tightest outline around the sites).
        /// Optionally clones the winner onto <see cref="SprinklerLayers.WorkLayer"/> so read tools that filter by layer can see it.
        /// </summary>
        public static bool TryAutoResolveFloorBoundary(
            Document doc,
            IList<Point2d> shaftSites,
            bool cloneToWorkLayer,
            out string floorBoundaryHandleHex,
            out string detailMessage)
        {
            floorBoundaryHandleHex = null;
            detailMessage = null;

            if (doc == null || shaftSites == null || shaftSites.Count < 2)
            {
                detailMessage = "Need at least two shaft block inserts or shaft hints to auto-detect a floor boundary.";
                return false;
            }

            var db = doc.Database;
            var candidates = new List<(string handleHex, string layer, double areaAbs, int layerScore)>();

            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    DBObject dbo = null;
                    try
                    {
                        dbo = tr.GetObject(id, OpenMode.ForRead, false);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!(dbo is Polyline pl) || !pl.Closed)
                    {
                        try { dbo?.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    var ring = new List<Point2d>(pl.NumberOfVertices);
                    try
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                            ring.Add(pl.GetPoint2dAt(i));
                    }
                    catch
                    {
                        try { dbo.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    if (ring.Count < 3)
                    {
                        try { dbo.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    bool allInside = true;
                    foreach (var s in shaftSites)
                    {
                        if (!FindShaftsInsideBoundary.IsPointInPolygonRing(ring, s))
                        {
                            allInside = false;
                            break;
                        }
                    }

                    if (!allInside)
                    {
                        try { dbo.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    double areaAbs = 0;
                    try
                    {
                        areaAbs = Math.Abs(pl.Area);
                    }
                    catch
                    {
                        areaAbs = 0;
                    }

                    string layer = pl.Layer ?? string.Empty;
                    string hx = pl.Handle.ToString();
                    int score = LayerPreferenceScore(layer);
                    candidates.Add((hx, layer, areaAbs, score));
                    try { dbo.Dispose(); } catch { /* ignore */ }
                }

                tr.Commit();
            }

            if (candidates.Count == 0)
            {
                detailMessage =
                    "No closed polyline in model space contains all shaft/hint sites. " +
                    "Draw a single closed polyline around the building on any layer, or move shaft blocks/hints inside an existing outline.";
                return false;
            }

            var best = candidates
                .OrderByDescending(c => c.layerScore)
                .ThenBy(c => c.areaAbs)
                .First();

            string chosenHex = best.handleHex;
            detailMessage =
                "Detected floor outline handle=" + chosenHex + " layer=\"" + best.layer + "\" area≈" +
                best.areaAbs.ToString("F2", CultureInfo.InvariantCulture) +
                " (score=" + best.layerScore.ToString(CultureInfo.InvariantCulture) + ").";

            if (!cloneToWorkLayer)
            {
                floorBoundaryHandleHex = chosenHex;
                return true;
            }

            if (string.Equals(best.layer, SprinklerLayers.WorkLayer, StringComparison.OrdinalIgnoreCase))
            {
                floorBoundaryHandleHex = chosenHex;
                detailMessage += " Already on floor boundary layer — not cloned.";
                return true;
            }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!long.TryParse(chosenHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                {
                    detailMessage = "Invalid handle from auto-detection.";
                    return false;
                }

                var hid = new Handle(hv);
                if (!db.TryGetObjectId(hid, out ObjectId srcId) || srcId.IsNull)
                {
                    detailMessage = "Could not re-open detected boundary entity.";
                    return false;
                }

                var src = tr.GetObject(srcId, OpenMode.ForRead, false) as Polyline;
                if (src == null || !src.Closed)
                {
                    detailMessage = "Detected boundary is not a closed polyline.";
                    return false;
                }

                SprinklerXData.EnsureRegApp(tr, db);
                ObjectId workLid = SprinklerLayers.EnsureWorkLayer(tr, db);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var clone = (Polyline)src.Clone();
                clone.LayerId = workLid;
                clone.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);

                ms.AppendEntity(clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                // Do not tag the floor parcel with ApplyZoneBoundaryTag — that would make the outer
                // outline look like a sprinkler "zone" in list_zones (subzones are the split polylines inside).

                floorBoundaryHandleHex = clone.Handle.ToString();
                detailMessage += " Cloned to layer \"" + SprinklerLayers.WorkLayer + "\" as handle=" + floorBoundaryHandleHex + ".";
                tr.Commit();
            }

            return true;
        }
    }
}
