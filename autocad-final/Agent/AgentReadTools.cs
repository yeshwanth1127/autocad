using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent
{
    public static class AgentReadTools
    {
        public static string GetZoneScorecard(Document doc, string boundaryHandleHex, DrawingSnapshot snapshot)
        {
            if (doc == null)
                return JsonSupport.Serialize(new { status = "error", message = "No active drawing." });
            if (string.IsNullOrWhiteSpace(boundaryHandleHex))
                return JsonSupport.Serialize(new { status = "error", message = "boundary_handle is required." });

            ZoneSnapshot z = snapshot?.Zones != null
                ? snapshot.Zones.FirstOrDefault(x => string.Equals(x.BoundaryHandle, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                : null;

            var db = doc.Database;
            double areaDu = z?.AreaDrawingUnits ?? 0;
            double? areaM2 = z?.AreaM2;
            double minX = 0, minY = 0, maxX = 0, maxY = 0;

            try
            {
                long v = Convert.ToInt64(boundaryHandleHex.Trim(), 16);
                var handle = new Handle(v);
                if (db.TryGetObjectId(handle, out ObjectId id))
                {
                    using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (ent != null)
                        {
                            // Use Bounds (cached) instead of GeometricExtents (recalculates,
                            // deadlocks display subsystem on MText in non-rendering contexts).
                            try
                            {
                                var b = ent.Bounds;
                                if (b.HasValue)
                                {
                                    minX = b.Value.MinPoint.X; minY = b.Value.MinPoint.Y;
                                    maxX = b.Value.MaxPoint.X; maxY = b.Value.MaxPoint.Y;
                                }
                            }
                            catch { /* ignore */ }
                        }
                        tr.Commit();
                    }
                }
            }
            catch { /* ignore */ }

            double dx = maxX - minX, dy = maxY - minY;
            double bboxArea = (dx > 0 && dy > 0) ? dx * dy : 0;
            double fillRatio = (bboxArea > 0 && areaDu > 0) ? Math.Min(1.0, Math.Abs(areaDu) / bboxArea) : 0;
            double aspect = (dy > 0) ? Math.Abs(dx / dy) : 0;
            if (aspect > 0 && aspect < 1) aspect = 1.0 / aspect;

            return JsonSupport.Serialize(new
            {
                status = "ok",
                boundary_handle = boundaryHandleHex,
                coverage = new
                {
                    expected = z?.ExpectedHeadCount ?? 0,
                    actual = z?.HeadCount ?? 0,
                    gap_count = z?.CoverageGaps ?? 0,
                    ok = (z?.CoverageGaps ?? 0) <= 0
                },
                routing = new
                {
                    main_pipe_count = z?.MainPipeCount ?? 0,
                    branch_count = z?.BranchCount ?? 0,
                    trunk_tagged_count = z?.TrunkTaggedCount ?? 0,
                    total_pipe_entities = z?.TotalPipeEntities ?? 0
                },
                geometry = new
                {
                    area_du = areaDu,
                    area_m2 = areaM2,
                    bbox_min_x = minX,
                    bbox_min_y = minY,
                    bbox_max_x = maxX,
                    bbox_max_y = maxY,
                    bbox_aspect_ratio = aspect,
                    bbox_fill_ratio = fillRatio,
                    concavity_proxy = fillRatio > 0 ? (1.0 - fillRatio) : 0
                },
                summary = z?.Summary
            });
        }
        // ── Existing snapshot-based read tools ────────────────────────────────

        public static string ListZones(Document doc, ProjectMemory memory)
        {
            var snapshot = BuildSnapshot(doc, memory);
            return ToJson(new DrawingSnapshot
            {
                DrawingName = snapshot.DrawingName,
                Zones = snapshot.Zones
            });
        }

        public static string GetZoneGeometry(Document doc, string boundaryHandleHex)
        {
            var zone = FindZone(BuildSnapshot(doc, ProjectMemory.LoadFor(doc)), boundaryHandleHex);
            if (zone == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "Zone boundary not found." });

            return ToJson(new ZoneGeometrySnapshot
            {
                BoundaryHandle   = zone.BoundaryHandle,
                Layer            = zone.Layer,
                AreaDrawingUnits = zone.AreaDrawingUnits,
                AreaM2           = zone.AreaM2,
                PerimeterDrawingUnits = zone.PerimeterDrawingUnits,
                VertexCount      = zone.VertexCount,
                CentroidX        = zone.CentroidX,
                CentroidY        = zone.CentroidY
            });
        }

        public static string GetShaftLocation(Document doc)
        {
            var snapshot = BuildSnapshot(doc, ProjectMemory.LoadFor(doc));
            return ToJson(new DrawingSnapshot
            {
                DrawingName = snapshot.DrawingName,
                Shafts = snapshot.Shafts
            });
        }

        public static string ValidateCoverage(Document doc, string boundaryHandleHex)
        {
            var zone = FindZone(BuildSnapshot(doc, ProjectMemory.LoadFor(doc)), boundaryHandleHex);
            if (zone == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "Zone boundary not found." });

            return ToJson(new CoverageSnapshot
            {
                BoundaryHandle = zone.BoundaryHandle,
                CoverageOk     = zone.CoverageGaps <= 0,
                ExpectedCount  = zone.ExpectedHeadCount,
                ActualCount    = zone.HeadCount,
                GapCount       = zone.CoverageGaps,
                Summary        = zone.Summary
            });
        }

        public static string GetXDataTags(Document doc, string boundaryHandleHex)
        {
            if (string.IsNullOrEmpty(boundaryHandleHex))
                return ToJson(new ErrorSnapshot { Status = "error", Message = "Zone boundary not found." });

            var tags = CollectZoneTags(doc, boundaryHandleHex);
            return ToJson(new ZoneTagListSnapshot
            {
                BoundaryHandle = boundaryHandleHex,
                Tags = tags
            });
        }

        public static string GetPipeSummary(Document doc, string boundaryHandleHex)
        {
            var snapshot = BuildSnapshot(doc, ProjectMemory.LoadFor(doc));
            if (!string.IsNullOrEmpty(boundaryHandleHex))
            {
                var zone = snapshot.Zones.FirstOrDefault(z =>
                    string.Equals(z.BoundaryHandle, boundaryHandleHex, StringComparison.OrdinalIgnoreCase));
                if (zone != null)
                {
                    return ToJson(new PipeSummarySnapshot
                    {
                        BoundaryHandle     = boundaryHandleHex,
                        MainPipeCount      = zone.MainPipeCount,
                        BranchCount        = zone.BranchCount,
                        TrunkTaggedCount   = zone.TrunkTaggedCount,
                        TotalPipeEntities  = zone.TotalPipeEntities,
                        Zones              = new List<ZoneSnapshot> { zone }
                    });
                }
            }

            return ToJson(new PipeSummarySnapshot
            {
                BoundaryHandle    = boundaryHandleHex,
                MainPipeCount     = snapshot.Zones.Sum(z => z.MainPipeCount),
                BranchCount       = snapshot.Zones.Sum(z => z.BranchCount),
                TrunkTaggedCount  = snapshot.Zones.Sum(z => z.TrunkTaggedCount),
                TotalPipeEntities = snapshot.Zones.Sum(z => z.TotalPipeEntities),
                Zones             = snapshot.Zones
            });
        }

        // ── New full-drawing read tools ────────────────────────────────────────

        /// <summary>
        /// Returns a fresh DrawingCensus: every layer with entity-type breakdown,
        /// all block type names with counts, closed-polyline/text/dimension/hatch counts,
        /// and drawing extents.  This is also embedded in the initial snapshot.
        /// </summary>
        public static string GetDrawingCensus(Document doc)
        {
            if (doc == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "No active document." });

            var census = BuildCensus(doc);
            return ToJson(new { status = "ok", census });
        }

        /// <summary>
        /// Returns closed polylines on sprinkler-relevant layers only (floor boundary, SPRK-BOUNDARY,
        /// <c>sprinkler - zone</c> / <c>sprinkler - zoner</c>) — not the entire drawing.
        /// Results sorted by area descending; capped at 300 entries (largest first).
        /// </summary>
        public static string GetAllClosedPolylines(Document doc)
        {
            if (doc == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "No active document." });

            const int MaxResults = 300;
            var db      = doc.Database;
            var results = new List<ClosedPolylineEntry>();

            // Build a set of known zone boundary handles for flagging
            var zoneBoundaryHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snapshot = BuildSnapshot(doc, ProjectMemory.LoadFor(doc));
            if (snapshot.Zones != null)
                foreach (var z in snapshot.Zones)
                    if (!string.IsNullOrEmpty(z.BoundaryHandle))
                        zoneBoundaryHandles.Add(z.BoundaryHandle);

            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var poly = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (poly == null || !poly.Closed) continue;
                    if (!SprinklerLayers.IsZoneOutlineDiscoveryLayerName(poly.Layer ?? string.Empty))
                        continue;

                    double area = 0, perimeter = 0;
                    try { area = poly.Area; } catch { }
                    try { perimeter = poly.Length; } catch { }

                    double? areaM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, area, out _);

                    var ring    = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(poly);
                    var centroid = ring != null && ring.Count > 0 ? ComputeCentroid(ring) : new Point2d(0, 0);

                    var handleHex = poly.Handle.ToString();
                    results.Add(new ClosedPolylineEntry
                    {
                        Handle         = handleHex,
                        Layer          = poly.Layer,
                        AreaDu         = area,
                        AreaM2         = areaM2,
                        PerimeterDu    = perimeter,
                        CentroidX      = Math.Round(centroid.X, 3),
                        CentroidY      = Math.Round(centroid.Y, 3),
                        VertexCount    = poly.NumberOfVertices,
                        IsZoneBoundary = zoneBoundaryHandles.Contains(handleHex)
                    });
                }

                tr.Commit();
            }

            results.Sort((a, b) => b.AreaDu.CompareTo(a.AreaDu));
            int total = results.Count;
            if (results.Count > MaxResults)
                results = results.GetRange(0, MaxResults);

            return ToJson(new
            {
                status         = "ok",
                total_count    = total,
                returned_count = results.Count,
                note           = (total > MaxResults ? "Truncated to " + MaxResults + " largest polylines. " : null) +
                    "Only layers: floor boundary, SPRK-BOUNDARY, sprinkler - zone, sprinkler - zoner.",
                polylines      = results
            });
        }

        /// <summary>
        /// Returns every text and MText entity in the drawing with content, position, and layer.
        /// Lets the AI read all room names, labels, annotations, and dimensions text.
        /// </summary>
        public static string GetTextContent(Document doc)
        {
            if (doc == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "No active document." });

            const int MaxResults = 500;
            var db      = doc.Database;
            var results = new List<TextEntityEntry>();

            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false);

                    if (ent is DBText dbText)
                    {
                        results.Add(new TextEntityEntry
                        {
                            Handle  = dbText.Handle.ToString(),
                            Layer   = dbText.Layer,
                            Type    = "DBText",
                            Content = dbText.TextString ?? string.Empty,
                            X       = Math.Round(dbText.Position.X, 3),
                            Y       = Math.Round(dbText.Position.Y, 3)
                        });
                    }
                    else if (ent is MText mText)
                    {
                        results.Add(new TextEntityEntry
                        {
                            Handle  = mText.Handle.ToString(),
                            Layer   = mText.Layer,
                            Type    = "MText",
                            Content = mText.Contents ?? string.Empty,
                            X       = Math.Round(mText.Location.X, 3),
                            Y       = Math.Round(mText.Location.Y, 3)
                        });
                    }
                }

                tr.Commit();
            }

            int total = results.Count;
            if (results.Count > MaxResults)
                results = results.GetRange(0, MaxResults);

            return ToJson(new
            {
                status         = "ok",
                total_count    = total,
                returned_count = results.Count,
                note           = total > MaxResults ? "Truncated to first " + MaxResults + " entries." : null,
                texts          = results
            });
        }

        /// <summary>
        /// Lists all entities on the specified layer (case-insensitive).
        /// Returns handle, entity type, position, and a brief summary for each.
        /// Capped at 200 entries when a layer has many entities.
        /// </summary>
        public static string ListEntitiesOnLayer(Document doc, string layerName)
        {
            if (doc == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "No active document." });
            if (string.IsNullOrWhiteSpace(layerName))
                return ToJson(new ErrorSnapshot { Status = "error", Message = "layer_name is required." });

            const int MaxResults = 200;
            var db      = doc.Database;
            var results = new List<EntityOnLayerEntry>();

            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // Cache block table record names to avoid re-opening same BTR repeatedly
                var btrNameCache = new Dictionary<ObjectId, string>();

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    if (!string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;

                    var entry = new EntityOnLayerEntry
                    {
                        Handle = ent.Handle.ToString(),
                        Type   = ent.GetType().Name,
                        Layer  = ent.Layer
                    };

                    // Position + summary by entity type
                    if (ent is Polyline poly)
                    {
                        if (poly.NumberOfVertices > 0)
                        {
                            var pt = poly.GetPoint3dAt(0);
                            entry.X = Math.Round(pt.X, 3);
                            entry.Y = Math.Round(pt.Y, 3);
                        }
                        double area = 0;
                        try { area = poly.Area; } catch { }
                        entry.Summary = (poly.Closed ? "closed" : "open") +
                                        ", vertices=" + poly.NumberOfVertices +
                                        (poly.Closed ? ", area=" + area.ToString("F2", CultureInfo.InvariantCulture) : string.Empty);
                    }
                    else if (ent is BlockReference br)
                    {
                        entry.X = Math.Round(br.Position.X, 3);
                        entry.Y = Math.Round(br.Position.Y, 3);
                        string blockName = "?";
                        if (!btrNameCache.TryGetValue(br.BlockTableRecord, out blockName))
                        {
                            var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                            blockName = btr?.Name ?? "?";
                            btrNameCache[br.BlockTableRecord] = blockName;
                        }
                        entry.Summary = "block=" + blockName;
                    }
                    else if (ent is DBText dbt)
                    {
                        entry.X       = Math.Round(dbt.Position.X, 3);
                        entry.Y       = Math.Round(dbt.Position.Y, 3);
                        entry.Summary = "\"" + Truncate(dbt.TextString, 80) + "\"";
                    }
                    else if (ent is MText mt)
                    {
                        entry.X       = Math.Round(mt.Location.X, 3);
                        entry.Y       = Math.Round(mt.Location.Y, 3);
                        entry.Summary = "\"" + Truncate(mt.Contents, 80) + "\"";
                    }
                    else if (ent is Line line)
                    {
                        entry.X       = Math.Round(line.StartPoint.X, 3);
                        entry.Y       = Math.Round(line.StartPoint.Y, 3);
                        entry.Summary = string.Format(CultureInfo.InvariantCulture,
                            "from ({0:F2},{1:F2}) to ({2:F2},{3:F2})",
                            line.StartPoint.X, line.StartPoint.Y,
                            line.EndPoint.X, line.EndPoint.Y);
                    }
                    else if (ent is Arc arc)
                    {
                        entry.X       = Math.Round(arc.Center.X, 3);
                        entry.Y       = Math.Round(arc.Center.Y, 3);
                        entry.Summary = "center=(" + arc.Center.X.ToString("F2", CultureInfo.InvariantCulture) +
                                        "," + arc.Center.Y.ToString("F2", CultureInfo.InvariantCulture) +
                                        ") r=" + arc.Radius.ToString("F2", CultureInfo.InvariantCulture);
                    }
                    else if (ent is Circle circle)
                    {
                        entry.X       = Math.Round(circle.Center.X, 3);
                        entry.Y       = Math.Round(circle.Center.Y, 3);
                        entry.Summary = "center=(" + circle.Center.X.ToString("F2", CultureInfo.InvariantCulture) +
                                        "," + circle.Center.Y.ToString("F2", CultureInfo.InvariantCulture) +
                                        ") r=" + circle.Radius.ToString("F2", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        // Use Bounds (cached) instead of GeometricExtents (recalculates,
                        // deadlocks display subsystem on MText in non-rendering contexts).
                        try
                        {
                            var b = ent.Bounds;
                            if (b.HasValue)
                            {
                                entry.X = Math.Round((b.Value.MinPoint.X + b.Value.MaxPoint.X) / 2.0, 3);
                                entry.Y = Math.Round((b.Value.MinPoint.Y + b.Value.MaxPoint.Y) / 2.0, 3);
                            }
                        }
                        catch { }
                    }

                    results.Add(entry);
                    if (results.Count >= MaxResults) break;
                }

                tr.Commit();
            }

            return ToJson(new
            {
                status         = "ok",
                layer          = layerName,
                returned_count = results.Count,
                note           = results.Count >= MaxResults
                                    ? "Reached " + MaxResults + " entity limit. Use a more specific query if needed."
                                    : null,
                entities       = results
            });
        }

        /// <summary>
        /// Returns comprehensive details for any entity identified by its hex handle.
        /// For polylines: all vertices.  For blocks: name, position, attributes.
        /// For text: full content.  For lines/arcs/circles: geometry.
        /// Use this to deeply inspect any entity the AI found in a census or layer listing.
        /// </summary>
        public static string GetEntityDetails(Document doc, string handleHex)
        {
            if (doc == null)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "No active document." });
            if (string.IsNullOrWhiteSpace(handleHex))
                return ToJson(new ErrorSnapshot { Status = "error", Message = "handle is required." });

            var db  = doc.Database;
            var oid = ResolveObjectIdFromHandle(db, handleHex);
            if (oid.IsNull)
                return ToJson(new ErrorSnapshot { Status = "error", Message = "Entity with handle " + handleHex + " not found." });

            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var ent = tr.GetObject(oid, OpenMode.ForRead, false) as Entity;
                if (ent == null)
                {
                    tr.Commit();
                    return ToJson(new ErrorSnapshot { Status = "error", Message = "Object is not a graphical entity." });
                }

                object detail;

                if (ent is Polyline poly)
                {
                    var vertices = new List<object>();
                    int maxV = Math.Min(poly.NumberOfVertices, 200);
                    for (int i = 0; i < maxV; i++)
                    {
                        var pt = poly.GetPoint3dAt(i);
                        double bulge = poly.GetBulgeAt(i);
                        vertices.Add(new
                        {
                            index  = i,
                            x      = Math.Round(pt.X, 4),
                            y      = Math.Round(pt.Y, 4),
                            bulge  = Math.Abs(bulge) < 1e-9 ? 0.0 : Math.Round(bulge, 6)
                        });
                    }
                    double area = 0, perimeter = 0;
                    try { area = poly.Area; } catch { }
                    try { perimeter = poly.Length; } catch { }
                    double? areaM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, area, out _);
                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out var boundaryRef);

                    detail = new
                    {
                        status               = "ok",
                        handle               = handleHex,
                        type                 = "Polyline",
                        layer                = poly.Layer,
                        closed               = poly.Closed,
                        vertex_count         = poly.NumberOfVertices,
                        vertices_shown       = maxV,
                        vertices             = vertices,
                        area_du              = Math.Round(area, 4),
                        area_m2              = areaM2.HasValue ? (object)Math.Round(areaM2.Value, 4) : null,
                        perimeter_du         = Math.Round(perimeter, 4),
                        zone_boundary_ref    = boundaryRef
                    };
                }
                else if (ent is BlockReference br)
                {
                    string blockName = "?";
                    try
                    {
                        var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                        blockName = btr?.Name ?? "?";
                    }
                    catch { }

                    var attribs = new List<object>();
                    if (br.AttributeCollection != null)
                    {
                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (att != null)
                                attribs.Add(new { tag = att.Tag, value = att.TextString });
                        }
                    }

                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out var bZone);
                    detail = new
                    {
                        status            = "ok",
                        handle            = handleHex,
                        type              = "BlockReference",
                        layer             = br.Layer,
                        block_name        = blockName,
                        position          = new { x = Math.Round(br.Position.X, 4), y = Math.Round(br.Position.Y, 4) },
                        rotation_deg      = Math.Round(br.Rotation * (180.0 / Math.PI), 4),
                        scale             = new { x = Math.Round(br.ScaleFactors.X, 4), y = Math.Round(br.ScaleFactors.Y, 4) },
                        attributes        = attribs,
                        zone_boundary_ref = bZone
                    };
                }
                else if (ent is DBText dbt)
                {
                    detail = new
                    {
                        status          = "ok",
                        handle          = handleHex,
                        type            = "DBText",
                        layer           = dbt.Layer,
                        content         = dbt.TextString,
                        position        = new { x = Math.Round(dbt.Position.X, 4), y = Math.Round(dbt.Position.Y, 4) },
                        height          = Math.Round(dbt.Height, 4),
                        rotation_deg    = Math.Round(dbt.Rotation * (180.0 / Math.PI), 4)
                    };
                }
                else if (ent is MText mt)
                {
                    detail = new
                    {
                        status       = "ok",
                        handle       = handleHex,
                        type         = "MText",
                        layer        = mt.Layer,
                        content      = mt.Contents,
                        actual_text  = mt.Text,
                        position     = new { x = Math.Round(mt.Location.X, 4), y = Math.Round(mt.Location.Y, 4) },
                        text_height  = Math.Round(mt.TextHeight, 4),
                        width        = Math.Round(mt.Width, 4)
                    };
                }
                else if (ent is Line line)
                {
                    detail = new
                    {
                        status   = "ok",
                        handle   = handleHex,
                        type     = "Line",
                        layer    = line.Layer,
                        start    = new { x = Math.Round(line.StartPoint.X, 4), y = Math.Round(line.StartPoint.Y, 4) },
                        end      = new { x = Math.Round(line.EndPoint.X, 4), y = Math.Round(line.EndPoint.Y, 4) },
                        length   = Math.Round(line.Length, 4)
                    };
                }
                else if (ent is Arc arc)
                {
                    detail = new
                    {
                        status          = "ok",
                        handle          = handleHex,
                        type            = "Arc",
                        layer           = arc.Layer,
                        center          = new { x = Math.Round(arc.Center.X, 4), y = Math.Round(arc.Center.Y, 4) },
                        radius          = Math.Round(arc.Radius, 4),
                        start_angle_deg = Math.Round(arc.StartAngle * (180.0 / Math.PI), 4),
                        end_angle_deg   = Math.Round(arc.EndAngle * (180.0 / Math.PI), 4),
                        arc_length      = Math.Round(arc.Length, 4)
                    };
                }
                else if (ent is Circle circ)
                {
                    detail = new
                    {
                        status  = "ok",
                        handle  = handleHex,
                        type    = "Circle",
                        layer   = circ.Layer,
                        center  = new { x = Math.Round(circ.Center.X, 4), y = Math.Round(circ.Center.Y, 4) },
                        radius  = Math.Round(circ.Radius, 4),
                        area_du = Math.Round(circ.Area, 4)
                    };
                }
                else
                {
                    // Generic fallback: type + layer + bounding box
                    // Use Bounds (cached) instead of GeometricExtents (recalculates,
                    // deadlocks display subsystem on MText in non-rendering contexts).
                    double minX = 0, minY = 0, maxX = 0, maxY = 0;
                    try
                    {
                        var b = ent.Bounds;
                        if (b.HasValue)
                        {
                            minX = Math.Round(b.Value.MinPoint.X, 4);
                            minY = Math.Round(b.Value.MinPoint.Y, 4);
                            maxX = Math.Round(b.Value.MaxPoint.X, 4);
                            maxY = Math.Round(b.Value.MaxPoint.Y, 4);
                        }
                    }
                    catch { }

                    detail = new
                    {
                        status = "ok",
                        handle = handleHex,
                        type   = ent.GetType().Name,
                        layer  = ent.Layer,
                        bounds = new { min_x = minX, min_y = minY, max_x = maxX, max_y = maxY }
                    };
                }

                tr.Commit();
                return ToJson(detail);
            }
        }

        // ── Snapshot builder ──────────────────────────────────────────────────

        public static DrawingSnapshot BuildSnapshot(Document doc, ProjectMemory memory)
        {
            return ScanDrawing(doc, memory);
        }

        internal static DrawingSnapshot ScanDrawing(Document doc, ProjectMemory memory)
        {
            AgentLog.Write("ScanDrawing", "enter — doc=" + (doc?.Name ?? "null") +
                " tid=" + System.Threading.Thread.CurrentThread.ManagedThreadId);
            var snapshot = new DrawingSnapshot
            {
                DrawingName    = doc?.Name,
                Zones          = new List<ZoneSnapshot>(),
                Shafts         = new List<ShaftSnapshot>(),
                Nfpa           = new NfpaSnapshot
                {
                    HazardClass              = memory?.HazardClass          ?? "Light",
                    MaxCoverageM2            = memory?.MaxCoveragePerHeadM2 ?? 20.9,
                    DefaultSpacingM          = memory?.DefaultSpacingM      ?? RuntimeSettings.Load().SprinklerSpacingM,
                    MaxSpacingM              = memory?.MaxSpacingM          ?? 4.6,
                    PreferredPipeOrientation = memory?.PreferredPipeOrientation ?? "auto"
                },
                RecentDecisions = BuildRecentDecisions(memory),
                PendingIssues   = new List<string>()
            };

            if (doc == null)
            {
                snapshot.PendingIssues.Add("No active drawing.");
                return snapshot;
            }

            var db              = doc.Database;
            var zonesByBoundary = new Dictionary<string, ZoneWorkingSet>(StringComparer.OrdinalIgnoreCase);
            var shaftPoints     = new List<Point3d>();

            // ── Census accumulators ───────────────────────────────────────────
            var layerEntityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var layerEntityTypes  = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var blockTypeCounts   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var blockTypeLayers   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var btrNameCache      = new Dictionary<ObjectId, string>();
            int closedPolyCount = 0, textCount = 0, dimCount = 0, hatchCount = 0, lineCount = 0;
            int totalEntityCount = 0;

            // ── Pass 1: XData scan + full census ─────────────────────────────
            // Always called from the AutoCAD application context thread (via ExecuteInApplicationContext),
            // so no doc lock is needed — the app context thread owns implicit DB access rights.
            AgentLog.Write("ScanDrawing", "opening OpenCloseTransaction for Pass 1");
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                AgentLog.Write("ScanDrawing", "transaction opened — reading BlockTable");
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                // Log how many IDs are in model space before opening any of them.
                // If the count itself hangs the enumeration is the problem; if it works
                // but a later GetObject hangs that tells us which entity type blocks.
                var allIds = new System.Collections.Generic.List<ObjectId>();
                try
                {
                    foreach (ObjectId oid in ms) allIds.Add(oid);
                }
                catch (Exception ex)
                {
                    AgentLog.Write("ScanDrawing", "ID enumeration threw: " + ex.Message);
                }
                AgentLog.Write("ScanDrawing", "model-space ID count=" + allIds.Count + " — starting GetObject loop");

                int iterCount = 0;
                foreach (ObjectId id in allIds)
                {
                    iterCount++;
                    if (iterCount % 50 == 0)
                        AgentLog.Write("ScanDrawing", "opened " + iterCount + " of " + allIds.Count + " entities");

                    // Log the DXF class name WITHOUT opening the object — safe on any thread.
                    var dxfName = "?";
                    try { dxfName = id.ObjectClass?.DxfName ?? "null"; } catch { }

                    if (iterCount <= 3 || iterCount == allIds.Count)
                        AgentLog.Write("ScanDrawing", "entity #" + iterCount + "/" + allIds.Count + " dxf=" + dxfName);

                    // Skip types known to block or be irrelevant (OLE, raster, proxy).
                    if (dxfName == "OLE2FRAME" || dxfName == "OLEFRAME" ||
                        dxfName == "RASTERVARIABLES" || dxfName == "WIPEOUT" ||
                        dxfName == "ACAD_PROXY_ENTITY" || dxfName == "RTEXT")
                    {
                        AgentLog.Write("ScanDrawing", "skipping entity #" + iterCount + " dxf=" + dxfName + " (blocked type)");
                        continue;
                    }

                    // IMPORTANT: OpenCloseTransaction can accumulate readers if objects aren't disposed.
                    // Dispose DBObjects as soon as we're done to avoid eAtMaxReaders on large drawings.
                    DBObject dbo = null;
                    try { dbo = tr.GetObject(id, OpenMode.ForRead, false) as DBObject; }
                    catch (Exception ex)
                    {
                        AgentLog.Write("ScanDrawing", "GetObject #" + iterCount + " dxf=" + dxfName + " threw: " + ex.Message);
                        continue;
                    }

                    if (!(dbo is Entity ent))
                    {
                        try { dbo?.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    totalEntityCount++;
                    var layer    = ent.Layer ?? "0";
                    var typeName = ent.GetType().Name;

                    // Layer entity count
                    if (!layerEntityCounts.ContainsKey(layer))
                        layerEntityCounts[layer] = 0;
                    layerEntityCounts[layer]++;

                    // Type breakdown per layer
                    if (!layerEntityTypes.ContainsKey(layer))
                        layerEntityTypes[layer] = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (!layerEntityTypes[layer].ContainsKey(typeName))
                        layerEntityTypes[layer][typeName] = 0;
                    layerEntityTypes[layer][typeName]++;

                    // Category counters
                    if (ent is BlockReference brEnt)
                    {
                        string blockName = "?";
                        if (!btrNameCache.TryGetValue(brEnt.BlockTableRecord, out blockName))
                        {
                            var btrObj = tr.GetObject(brEnt.BlockTableRecord, OpenMode.ForRead) as DBObject;
                            var btr = btrObj as BlockTableRecord;
                            blockName = btr?.Name ?? "?";
                            try { btrObj?.Dispose(); } catch { /* ignore */ }
                            btrNameCache[brEnt.BlockTableRecord] = blockName;
                        }
                        if (!blockTypeCounts.ContainsKey(blockName))
                        {
                            blockTypeCounts[blockName] = 0;
                            blockTypeLayers[blockName] = layer;
                        }
                        blockTypeCounts[blockName]++;
                    }
                    else if (ent is Polyline pCheck && pCheck.Closed) { closedPolyCount++; }
                    else if (ent is DBText || ent is MText)            { textCount++; }
                    else if (ent is Dimension)                         { dimCount++; }
                    else if (ent is Hatch)                             { hatchCount++; }
                    else if (ent is Line)                              { lineCount++; }

                    // NOTE: ent.GeometricExtents is intentionally NOT called here.
                    // For MText and some other entity types, GeometricExtents triggers
                    // AutoCAD's text-layout/display subsystem which deadlocks when called
                    // from ExecuteInApplicationContext (no active display context).
                    // Extents are informational only and not needed for sprinkler design.

                    // Shaft detection — match by layer-contains-"shaft" OR block-name-contains-"shaft"
                    if (ent is BlockReference shaftBr)
                    {
                        string bName;
                        btrNameCache.TryGetValue(shaftBr.BlockTableRecord, out bName);
                        bName = bName ?? "?";
                        bool layerIsShaft = layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool nameIsShaft  = bName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (layerIsShaft || nameIsShaft)
                            shaftPoints.Add(shaftBr.Position);
                    }

                    // Zone XData grouping
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var boundaryHandleHex) ||
                        string.IsNullOrEmpty(boundaryHandleHex))
                    {
                        try { dbo.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    if (!zonesByBoundary.TryGetValue(boundaryHandleHex, out var zone))
                    {
                        zone = new ZoneWorkingSet(boundaryHandleHex);
                        zonesByBoundary[boundaryHandleHex] = zone;
                    }

                    zone.TaggedEntities.Add(id);
                    zone.TaggedEntityTypes.Add(typeName);
                    zone.TaggedLayers.Add(layer);
                    zone.CountIfSprinklerOrPipe(tr, db, ent);

                    try { dbo.Dispose(); } catch { /* ignore */ }
                }

                AgentLog.Write("ScanDrawing", "Pass 1 loop done — tr.Commit()");
                tr.Commit();
                AgentLog.Write("ScanDrawing", "Pass 1 committed");
            }

            // ── Pass 2: find zone boundary polylines with no design content yet
            AgentLog.Write("ScanDrawing", "starting Pass 2");
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                AgentLog.Write("ScanDrawing", "Pass 2 ModelSpace obtained");

                foreach (ObjectId id in ms)
                {
                    var dbo = tr.GetObject(id, OpenMode.ForRead, false) as DBObject;
                    var poly = dbo as Polyline;
                    if (poly == null) { try { dbo?.Dispose(); } catch { } continue; }
                    if (!poly.Closed) { try { dbo.Dispose(); } catch { } continue; }

                    var layer    = poly.Layer ?? string.Empty;
                    bool isZone  =
                        string.Equals(layer, SprinklerLayers.WorkLayer,     StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, SprinklerLayers.ZoneLayer,     StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, SprinklerLayers.ZoneLayerAlternateName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, SprinklerLayers.BoundaryLayer, StringComparison.OrdinalIgnoreCase);

                    if (!isZone) { try { dbo.Dispose(); } catch { } continue; }

                    // Outer floor parcels on "floor boundary" must NOT be treated as sprinkler subzones.
                    // Only polylines explicitly tagged as zone boundaries (tool-created splits) register here.
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(poly, out _))
                    {
                        try { dbo.Dispose(); } catch { /* ignore */ }
                        continue;
                    }

                    var handleHex = poly.Handle.ToString();
                    if (!zonesByBoundary.ContainsKey(handleHex))
                        zonesByBoundary[handleHex] = new ZoneWorkingSet(handleHex);

                    try { dbo.Dispose(); } catch { /* ignore */ }
                }

                AgentLog.Write("ScanDrawing", "Pass 2 loop done — committing");
                tr.Commit();
                AgentLog.Write("ScanDrawing", "Pass 2 committed");
            }

            // ── Pass 3: read layer visibility/frozen state ────────────────────
            AgentLog.Write("ScanDrawing", "starting Pass 3 (layer table)");
            var layerInfoMap = new Dictionary<string, (bool visible, bool frozen)>(StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var dbo = tr.GetObject(lid, OpenMode.ForRead) as DBObject;
                    var ltr = dbo as LayerTableRecord;
                    if (ltr != null)
                        layerInfoMap[ltr.Name] = (!ltr.IsOff, ltr.IsFrozen);
                    try { dbo?.Dispose(); } catch { /* ignore */ }
                }
                tr.Commit();
            }
            AgentLog.Write("ScanDrawing", "Pass 3 done — building census");

            // ── Build census ──────────────────────────────────────────────────
            var censusLayers = new List<LayerCensusEntry>();
            foreach (var kvp in layerEntityCounts)
            {
                layerInfoMap.TryGetValue(kvp.Key, out var info);
                censusLayers.Add(new LayerCensusEntry
                {
                    Name        = kvp.Key,
                    Visible     = info.visible,
                    Frozen      = info.frozen,
                    EntityCount = kvp.Value,
                    EntityTypes = layerEntityTypes.ContainsKey(kvp.Key)
                                    ? layerEntityTypes[kvp.Key]
                                    : new Dictionary<string, int>()
                });
            }
            censusLayers.Sort((a, b) => b.EntityCount.CompareTo(a.EntityCount));

            var censusBlockTypes = blockTypeCounts
                .Select(kvp => new BlockTypeSummary
                {
                    Name        = kvp.Key,
                    Count       = kvp.Value,
                    SampleLayer = blockTypeLayers.ContainsKey(kvp.Key) ? blockTypeLayers[kvp.Key] : null
                })
                .OrderByDescending(b => b.Count)
                .ToList();

            string units;
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters: units = "mm";   break;
                case UnitsValue.Centimeters: units = "cm";   break;
                case UnitsValue.Meters:      units = "m";    break;
                case UnitsValue.Inches:      units = "in";   break;
                case UnitsValue.Feet:        units = "ft";   break;
                default:                     units = db.Insunits.ToString(); break;
            }

            int sprinklerScopeEntityCount = 0;
            var scopeLayers = new List<LayerCensusEntry>();
            foreach (var layer in censusLayers)
            {
                if (!SprinklerLayers.IsAgentAnalysisLayerName(layer.Name))
                    continue;
                sprinklerScopeEntityCount += layer.EntityCount;
                scopeLayers.Add(layer);
            }
            scopeLayers.Sort((a, b) => b.EntityCount.CompareTo(a.EntityCount));

            snapshot.Census = new DrawingCensus
            {
                Units             = units,
                Extents           = null, // GeometricExtents removed — deadlocks display subsystem on MText
                TotalEntityCount  = totalEntityCount,
                Layers            = censusLayers,
                BlockTypes        = censusBlockTypes,
                ClosedPolylineCount = closedPolyCount,
                TextCount         = textCount,
                DimensionCount    = dimCount,
                HatchCount        = hatchCount,
                LineCount         = lineCount,
                SprinklerScopeEntityCount = sprinklerScopeEntityCount,
                LayersSprinklerScope      = scopeLayers
            };

            AgentLog.Write("ScanDrawing", "census built — building zone snapshots count=" + zonesByBoundary.Count);
            // ── Build zone snapshots ──────────────────────────────────────────
            foreach (var zone in zonesByBoundary.Values)
            {
                var zoneSnapshot = BuildZoneSnapshot(doc, memory, zone, shaftPoints, snapshot.PendingIssues);
                if (zoneSnapshot != null)
                    snapshot.Zones.Add(zoneSnapshot);
            }

            snapshot.Shafts = BuildShaftSnapshots(snapshot.Zones, shaftPoints);
            if (snapshot.Zones.Count == 0)
                snapshot.PendingIssues.Add("No zone-tagged entities were found.");

            AgentLog.Write("ScanDrawing", "done — zones=" + snapshot.Zones.Count +
                " entities=" + (snapshot.Census?.TotalEntityCount.ToString() ?? "?"));
            return snapshot;
        }

        // ── Census builder (standalone refresh) ──────────────────────────────

        internal static DrawingCensus BuildCensus(Document doc)
        {
            // Re-uses ScanDrawing since it builds the census as part of the scan.
            var snapshot = ScanDrawing(doc, ProjectMemory.LoadFor(doc));
            return snapshot.Census ?? new DrawingCensus();
        }

        // ── Zone snapshot helpers (unchanged) ─────────────────────────────────

        private static ZoneSnapshot BuildZoneSnapshot(
            Document doc,
            ProjectMemory memory,
            ZoneWorkingSet zone,
            List<Point3d> shaftPoints,
            List<string> pendingIssues)
        {
            AgentLog.Write("BuildZoneSnapshot", "enter handle=" + zone.BoundaryHandle);
            var db             = doc.Database;
            var boundaryObjectId = ResolveObjectIdFromHandle(db, zone.BoundaryHandle);
            if (boundaryObjectId.IsNull)
            {
                AgentLog.Write("BuildZoneSnapshot", "handle not resolved — returning stub");
                pendingIssues.Add("Could not resolve boundary handle " + zone.BoundaryHandle + ".");
                return new ZoneSnapshot
                {
                    Id              = zone.BoundaryHandle,
                    BoundaryHandle  = zone.BoundaryHandle,
                    Status          = "unknown",
                    HeadCount       = zone.HeadCount,
                    CoverageGaps    = 0,
                    VertexCount     = 0,
                    CentroidX       = 0,
                    CentroidY       = 0,
                    ShaftSitesInside = 0,
                    HasShaftInside   = false,
                    HasManualEdits  = IsManualEdit(memory, zone.BoundaryHandle),
                    Summary         = "Boundary entity could not be resolved."
                };
            }

            AgentLog.Write("BuildZoneSnapshot", "opening transaction for handle=" + zone.BoundaryHandle);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                AgentLog.Write("BuildZoneSnapshot", "GetObject boundary polyline");
                var boundaryEntity   = tr.GetObject(boundaryObjectId, OpenMode.ForRead, false) as Entity;
                var boundaryPolyline = boundaryEntity as Polyline;
                if (boundaryPolyline == null)
                {
                    AgentLog.Write("BuildZoneSnapshot", "boundary is not a polyline — null return");
                    pendingIssues.Add("Boundary " + zone.BoundaryHandle + " is not a polyline.");
                    tr.Commit();
                    return null;
                }

                AgentLog.Write("BuildZoneSnapshot", "GetRingForPlanarClipping");
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundaryPolyline);
                AgentLog.Write("BuildZoneSnapshot", "ring.Count=" + (ring?.Count.ToString() ?? "null"));

                double areaDu = 0, perimeterDu = 0;
                try { areaDu     = boundaryPolyline.Area;   } catch { }
                try { perimeterDu = boundaryPolyline.Length; } catch { }

                AgentLog.Write("BuildZoneSnapshot", "areaDu=" + areaDu.ToString("F2") + " TryGetAreaSquareMeters");
                double? areaM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, areaDu, out _);

                AgentLog.Write("BuildZoneSnapshot", "CollectHeadPoints");
                var headPoints = CollectHeadPoints(doc, zone.BoundaryHandle, tr);
                AgentLog.Write("BuildZoneSnapshot", "headPoints.Count=" + headPoints.Count + " — skipping coverage grid (on-demand only)");

                // Coverage grid placement (TryPlaceForZoneRing) is expensive and blocks the
                // app-context thread. Skip it during the initial scan; the AI can call
                // validate_coverage explicitly when it needs the data.
                var coverage = new CoverageSnapshot
                {
                    CoverageOk    = false,
                    ExpectedCount = 0,
                    ActualCount   = headPoints.Count,
                    GapCount      = 0,
                    Summary       = "Coverage not computed during scan — use validate_coverage tool."
                };
                AgentLog.Write("BuildZoneSnapshot", "FindNearestShaftId");

                string nearestShaft = FindNearestShaftId(ring, shaftPoints);
                var globalShaftSites2d = FindShaftsInsideBoundary.CollectGlobalShaftSitePoints2d(db);
                int shaftsInside = 0;
                foreach (var s in globalShaftSites2d)
                {
                    if (FindShaftsInsideBoundary.IsPointInPolygonRing(ring, s))
                        shaftsInside++;
                }

                string status       = DetermineStatus(zone);
                var centroid        = ComputeCentroid(ring);

                AgentLog.Write("BuildZoneSnapshot", "CollectZoneTags");
                var tags = CollectZoneTags(doc, zone.BoundaryHandle);

                // Read explicit shaft assignment from zone boundary xdata (while transaction is open).
                string assignedShaftHandle = null;
                if (boundaryEntity != null)
                    SprinklerXData.TryGetShaftAssignmentHandle(boundaryEntity, out assignedShaftHandle);

                AgentLog.Write("BuildZoneSnapshot", "tags.Count=" + tags.Count + " — committing");
                tr.Commit();

                // Detect existing trunk segments (any angle) — opens its own transaction.
                var trunkSegs = MainPipeDetector.FindMainPipes(db, zone.BoundaryHandle);
                double? trunkAngleDeg = trunkSegs.Count > 0
                    ? (double?)MainPipeDetector.DominantAngleDeg(trunkSegs)
                    : null;

                // Determine if the assigned shaft is outside the zone.
                bool shaftOutsideZone = false;
                if (!string.IsNullOrEmpty(assignedShaftHandle) && ring != null && ring.Count >= 3)
                {
                    try
                    {
                        using (var tr2 = db.TransactionManager.StartOpenCloseTransaction())
                        {
                            Handle sh;
                            try { sh = new Handle(Convert.ToInt64(assignedShaftHandle, 16)); } catch { sh = default; }
                            if (sh.Value != 0)
                            {
                                ObjectId shId = ObjectId.Null;
                                try { shId = db.GetObjectId(false, sh, 0); } catch { }
                                if (!shId.IsNull)
                                {
                                    var brEnt = tr2.GetObject(shId, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.BlockReference;
                                    if (brEnt != null)
                                        shaftOutsideZone = !FindShaftsInsideBoundary.IsPointInPolygonRing(
                                            ring, new Point2d(brEnt.Position.X, brEnt.Position.Y));
                                }
                            }
                            tr2.Commit();
                        }
                    }
                    catch { /* ignore — best-effort */ }
                }

                AgentLog.Write("BuildZoneSnapshot", "done handle=" + zone.BoundaryHandle);
                return new ZoneSnapshot
                {
                    Id                   = zone.BoundaryHandle,
                    BoundaryHandle       = zone.BoundaryHandle,
                    Layer                = boundaryPolyline.Layer,
                    AreaDrawingUnits     = areaDu,
                    AreaM2               = areaM2,
                    PerimeterDrawingUnits = perimeterDu,
                    VertexCount          = ring.Count,
                    CentroidX            = centroid.X,
                    CentroidY            = centroid.Y,
                    Status               = status,
                    HeadCount            = zone.HeadCount,
                    ExpectedHeadCount    = coverage.ExpectedCount,
                    CoverageGaps         = coverage.GapCount,
                    NearestShaftId       = nearestShaft,
                    ShaftSitesInside     = shaftsInside,
                    HasShaftInside       = shaftsInside > 0,
                    HasManualEdits       = IsManualEdit(memory, zone.BoundaryHandle),
                    MainPipeCount        = zone.MainPipeCount,
                    BranchCount          = zone.BranchCount,
                    TrunkTaggedCount     = zone.TrunkTaggedCount,
                    TotalPipeEntities    = zone.TotalPipeEntities,
                    Summary              = coverage.Summary,
                    Tags                 = tags,
                    AssignedShaftHandle  = assignedShaftHandle,
                    MainPipeAngleDeg     = trunkAngleDeg,
                    MainPipeSegmentCount = trunkSegs.Count,
                    ShaftIsOutsideZone   = shaftOutsideZone
                };
            }
        }

        private static List<ShaftSnapshot> BuildShaftSnapshots(List<ZoneSnapshot> zones, List<Point3d> shaftPoints)
        {
            var result = new List<ShaftSnapshot>();
            for (int i = 0; i < shaftPoints.Count; i++)
            {
                var p         = shaftPoints[i];
                var connected = new List<string>();
                for (int z = 0; z < zones.Count; z++)
                {
                    if (string.Equals(zones[z].NearestShaftId,
                            "shaft_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                            StringComparison.OrdinalIgnoreCase))
                        connected.Add(zones[z].BoundaryHandle);
                }

                result.Add(new ShaftSnapshot
                {
                    Id              = "shaft_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    X               = p.X,
                    Y               = p.Y,
                    ConnectedZoneIds = connected
                });
            }

            return result;
        }

        private static List<DecisionSnapshot> BuildRecentDecisions(ProjectMemory memory)
        {
            var list = new List<DecisionSnapshot>();
            if (memory?.Decisions == null || memory.Decisions.Count == 0) return list;

            int start = Math.Max(0, memory.Decisions.Count - 8);
            for (int i = start; i < memory.Decisions.Count; i++)
            {
                var d = memory.Decisions[i];
                list.Add(new DecisionSnapshot
                {
                    ZoneId       = d.ZoneId,
                    Action       = d.Action,
                    Parameters   = d.Parameters,
                    Outcome      = d.Outcome,
                    Timestamp    = d.Timestamp,
                    EngineerNote = d.EngineerNote
                });
            }

            return list;
        }

        private static bool IsManualEdit(ProjectMemory memory, string zoneId)
        {
            if (memory == null || string.IsNullOrEmpty(zoneId) || memory.Zones == null)
                return false;
            return memory.Zones.TryGetValue(zoneId, out var zm) && zm != null && zm.HasManualEdits;
        }

        private static List<ZoneTagSnapshot> CollectZoneTags(Document doc, string boundaryHandleHex)
        {
            var tags = new List<ZoneTagSnapshot>();
            if (doc == null || string.IsNullOrEmpty(boundaryHandleHex)) return tags;

            var db = doc.Database;
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)) continue;
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var handle) ||
                        !string.Equals(handle, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    tags.Add(new ZoneTagSnapshot
                    {
                        EntityHandle = ent.Handle.ToString(),
                        EntityType   = ent.GetType().Name,
                        Layer        = ent.Layer,
                        Role         = DescribeRole(tr, ent)
                    });
                }
                tr.Commit();
            }

            return tags;
        }

        private static CoverageSnapshot ComputeCoverage(Database db, List<Point2d> ring, List<Point3d> headPoints)
        {
            AgentLog.Write("ComputeCoverage", "enter ring.Count=" + (ring?.Count.ToString() ?? "null"));
            var result = new CoverageSnapshot
            {
                CoverageOk   = false,
                ExpectedCount = 0,
                ActualCount  = headPoints?.Count ?? 0,
                GapCount     = 0,
                Summary      = string.Empty
            };

            if (ring == null || ring.Count < 3)
            {
                result.Summary = "Invalid zone boundary.";
                AgentLog.Write("ComputeCoverage", "ring invalid — returning early");
                return result;
            }

            AgentLog.Write("ComputeCoverage", "calling TryPlaceForZoneRing");
            var covCfg = RuntimeSettings.Load();
            if (!SprinklerGridPlacement2d.TryPlaceForZoneRing(
                    ring, db,
                    spacingMeters: covCfg.SprinklerSpacingM,
                    coverageRadiusMeters: 1.5,
                    maxBoundarySprinklerGapMeters: covCfg.SprinklerToBoundaryDistanceM,
                    out var placement, out var placementErr))
            {
                AgentLog.Write("ComputeCoverage", "TryPlaceForZoneRing failed: " + (placementErr ?? "null"));
                result.Summary = placementErr ?? "Could not validate coverage.";
                return result;
            }
            AgentLog.Write("ComputeCoverage", "TryPlaceForZoneRing ok — sprinklers.Count=" + (placement?.Sprinklers?.Count.ToString() ?? "null"));

            result.ExpectedCount = placement.Sprinklers.Count;

            double matchTol = 0.25;
            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.25, out double matchTolDu) && matchTolDu > 0)
                matchTol = matchTolDu;

            int matched = 0;
            var remaining = new List<Point3d>(headPoints ?? new List<Point3d>());
            foreach (var expected in placement.Sprinklers)
            {
                int foundIdx = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    double dx = remaining[i].X - expected.X;
                    double dy = remaining[i].Y - expected.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) <= matchTol)
                    {
                        foundIdx = i;
                        break;
                    }
                }
                if (foundIdx >= 0) { matched++; remaining.RemoveAt(foundIdx); }
            }

            result.GapCount  = Math.Max(0, placement.Sprinklers.Count - matched);
            result.CoverageOk = result.GapCount <= 0;
            result.Summary   = "Expected heads: " + placement.Sprinklers.Count +
                                ", found: " + (headPoints?.Count ?? 0) +
                                ", gaps: " + result.GapCount +
                                ". " + (placement.Summary ?? string.Empty);
            return result;
        }

        private static List<Point3d> CollectHeadPoints(Document doc, string boundaryHandleHex, Transaction tr)
        {
            var points = new List<Point3d>();
            if (doc == null || string.IsNullOrEmpty(boundaryHandleHex)) return points;

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)) continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var handle) ||
                    !string.Equals(handle, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SprinklerLayers.IsSprinklerHeadEntity(tr, ent) && ent is BlockReference br)
                    points.Add(br.Position);
            }

            return points;
        }

        private static string FindNearestShaftId(List<Point2d> ring, List<Point3d> shaftPoints)
        {
            if (ring == null || ring.Count == 0 || shaftPoints == null || shaftPoints.Count == 0)
                return null;

            var centroid  = ComputeCentroid(ring);
            double bestDist = double.MaxValue;
            int bestIndex   = -1;
            for (int i = 0; i < shaftPoints.Count; i++)
            {
                double dx = shaftPoints[i].X - centroid.X;
                double dy = shaftPoints[i].Y - centroid.Y;
                double d  = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; bestIndex = i; }
            }

            return bestIndex >= 0
                ? "shaft_" + (bestIndex + 1).ToString(CultureInfo.InvariantCulture)
                : null;
        }

        private static Point2d ComputeCentroid(List<Point2d> ring)
        {
            if (ring == null || ring.Count == 0) return new Point2d(0, 0);
            double sumX = 0, sumY = 0;
            foreach (var pt in ring) { sumX += pt.X; sumY += pt.Y; }
            return new Point2d(sumX / ring.Count, sumY / ring.Count);
        }

        private static string DetermineStatus(ZoneWorkingSet zone)
        {
            if (zone.HeadCount <= 0)           return "empty";
            if (zone.TrunkCount > 0 && zone.BranchCount > 0) return "complete";
            if (zone.TrunkCount > 0)           return "piped";
            return "heads_placed";
        }

        private static string DescribeRole(Transaction tr, Entity ent)
        {
            if (SprinklerXData.IsTaggedTrunk(ent))    return "trunk";
            if (SprinklerXData.IsTaggedTrunkCap(ent)) return "trunk_cap";
            if (SprinklerXData.IsTaggedConnector(ent)) return "connector";
            if (SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                return "sprinkler";
            if (string.Equals(ent.Layer, SprinklerLayers.BranchPipeLayer,   StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.BranchMarkerLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.BranchReducerLayer,StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.McdReducerLayer,    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.BranchLabelLayer,   StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ent.Layer, SprinklerLayers.McdLabelLayer,    StringComparison.OrdinalIgnoreCase))
                return "branch";
            if (SprinklerLayers.IsUnifiedZoneDesignLayerName(ent.Layer) &&
                (ent is Line || (ent is Polyline pl && !pl.Closed)) &&
                !SprinklerXData.IsTaggedTrunk(ent))
                return "branch";
            return "zone-tagged";
        }

        private static ObjectId ResolveObjectIdFromHandle(Database db, string handleHex)
        {
            if (db == null || string.IsNullOrEmpty(handleHex)) return ObjectId.Null;
            try
            {
                if (!long.TryParse(handleHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
                    return ObjectId.Null;
                return db.GetObjectId(false, new Handle(value), 0);
            }
            catch { return ObjectId.Null; }
        }

        private static ZoneSnapshot FindZone(DrawingSnapshot snapshot, string boundaryHandleHex)
        {
            if (snapshot?.Zones == null || string.IsNullOrEmpty(boundaryHandleHex)) return null;
            return snapshot.Zones.FirstOrDefault(z =>
                string.Equals(z.BoundaryHandle, boundaryHandleHex, StringComparison.OrdinalIgnoreCase));
        }

        private static string Truncate(string s, int maxLen)
        {
            if (s == null) return string.Empty;
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "…";
        }

        private static string ToJson<T>(T value) => JsonSupport.Serialize(value);

        // ── ZoneWorkingSet (internal accumulator) ──────────────────────────────

        private sealed class ZoneWorkingSet
        {
            public ZoneWorkingSet(string boundaryHandle) { BoundaryHandle = boundaryHandle; }

            public string           BoundaryHandle  { get; }
            public List<ObjectId>   TaggedEntities  { get; } = new List<ObjectId>();
            public List<string>     TaggedEntityTypes { get; } = new List<string>();
            public List<string>     TaggedLayers    { get; } = new List<string>();
            public int              HeadCount       { get; private set; }
            public int              TrunkCount      { get; private set; }
            public int TrunkTaggedCount             => TrunkCount;
            public int              BranchCount     { get; private set; }
            public int              MainPipeCount   { get; private set; }
            public int              TotalPipeEntities { get; private set; }

            public void CountIfSprinklerOrPipe(Transaction tr, Database db, Entity ent)
            {
                if (ent == null) return;

                if (ent is BlockReference br && SprinklerLayers.IsSprinklerHeadSymbolLayerName(ent.Layer))
                {
                    if (SprinklerLayers.IsUnifiedZoneDesignLayerName(ent.Layer) && tr != null && db != null)
                    {
                        try
                        {
                            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (!string.Equals(btr.Name, SprinklerLayers.GetConfiguredSprinklerBlockName(), StringComparison.OrdinalIgnoreCase))
                            {
                                /* reducer / other block on unified layer */
                            }
                            else
                                HeadCount++;
                        }
                        catch { /* ignore */ }
                    }
                    else
                        HeadCount++;
                }

                bool trunk =
                    SprinklerXData.IsTaggedTrunk(ent) ||
                    (ent is Polyline &&
                     (string.Equals(ent.Layer, SprinklerLayers.MainPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(ent.Layer, SprinklerLayers.McdMainPipeLayer, StringComparison.OrdinalIgnoreCase)));
                bool branch =
                    !trunk &&
                    (string.Equals(ent.Layer, SprinklerLayers.BranchPipeLayer,    StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.BranchMarkerLayer,  StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.BranchReducerLayer, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.McdReducerLayer,    StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.BranchLabelLayer,   StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ent.Layer, SprinklerLayers.McdLabelLayer,    StringComparison.OrdinalIgnoreCase) ||
                     (SprinklerLayers.IsUnifiedZoneDesignLayerName(ent.Layer) &&
                      (ent is Line || (ent is Polyline pln && !pln.Closed)) &&
                      !SprinklerXData.IsTaggedTrunk(ent)));

                if (trunk)  { TrunkCount++; MainPipeCount++; TotalPipeEntities++; }
                else if (branch) { BranchCount++; TotalPipeEntities++; }
            }
        }
    }
}
