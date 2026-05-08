using System;
using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Agent;
using autocad_final.Geometry;
using UnitsValue = Autodesk.AutoCAD.DatabaseServices.UnitsValue;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Layer names and helpers for sprinkler boundary workflows.
    /// </summary>
    public static class SprinklerLayers
    {
        public const string WorkLayer = "floor boundary";
        public const string BoundaryLayer = "SPRK-BOUNDARY";

        // ── MCD layer standard (new work; layers created on demand) ─────────────
        public const string McdFloorBoundaryLayer = "MCD-floor boundary";
        public const string McdZoneBoundaryLayer = "MCD-zone boundary";
        public const string McdSprinklersLayer = "MCD-sprinklers";
        public const string McdShaftsLayer = "MCD-shafts";
        public const string McdMainPipeLayer = "MCD-main pipe";
        public const string McdBranchPipeLayer = "MCD-branch pipe";

        /// <summary>Branch lines from the shaft→main connector (Route branch pipe 2) — white for contrast with main-fed branches.</summary>
        public const string McdConnectorBranchPipeLayer = "MCD - branch connector";

        /// <summary>NFPA branch-line sizing labels placed when branch pipes are routed.</summary>
        public const string McdLabelLayer = "MCD - label";

        /// <summary>Reducer blocks/wedges placed when branch pipes are routed.</summary>
        public const string McdReducerLayer = "MCD - Reducer";

        /// <summary>Sprinklers with no branch pipe after routing — yellow (current standard).</summary>
        public const string McdNoBranchPipeHighlightLayer = "MCD - no branch pipe";

        /// <summary>Legacy layer name; still recognized when clearing highlights.</summary>
        public const string McdNoBranchPipeHighlightLayerLegacy = "MCD - no branch";

        public static bool IsNoBranchPipeHighlightLayerName(string layerName) =>
            !string.IsNullOrEmpty(layerName) &&
            (string.Equals(layerName, McdNoBranchPipeHighlightLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, McdNoBranchPipeHighlightLayerLegacy, StringComparison.OrdinalIgnoreCase));

        public const short McdShaftsAciMagenta = 6;

        /// <summary>Green dashed zone outlines (formula strip zones clipped to floor).</summary>
        public const string ZoneLayer = "sprinkler - zone";

        /// <summary>Alternate spelling seen in some drawings — treated the same as <see cref="ZoneLayer"/>.</summary>
        public const string ZoneLayerAlternateName = "sprinkler - zoner";

        /// <summary>Labels for zone names and areas (contrasting text on plan).</summary>
        public const string ZoneLabelLayer = "sprinkler - zone label";

        /// <summary>
        /// Single selectable "global" boundary object generated from all zone outlines
        /// (outer boundary + internal separators). Used by Fix Zone Boundary.
        /// </summary>
        public const string ZoneGlobalBoundaryLayer = "sprinkler - zone global boundary";

        public const short ZoneLayerAciGreen = 3;
        public const short ZoneLabelAciWhite = 7;
        public const short ZoneGlobalBoundaryAciCyan = 4;

        public const string MainPipeLayer = "sprinkler - pipe main";
        public const short MainPipeAciRed = 1;
        /// <summary>ACI yellow — default color for <see cref="McdMainPipeLayer"/> when created.</summary>
        public const short MainPipeAciYellow = 2;

        public const string BranchPipeLayer = "sprinkler - pipe branch";
        public const short BranchPipeAciYellow = 1;

        public const string BranchMarkerLayer = "sprinkler - pipe branch markers";
        public const short BranchMarkerAciGray = 8;

        public const string BranchReducerLayer = "sprinkler - pipe reducer";
        public const short BranchReducerAciOrange = 30;

        public const string BranchLabelLayer = "sprinkler - pipe branch labels";
        public const short BranchLabelAciWhite = 7;

        /// <summary>ACI yellow — highlights sprinkler heads that did not receive branch piping (outside main span / not served).</summary>
        public const short NoBranchSprinklerHighlightAci = MainPipeAciYellow;

        // Apply sprinklers inserts block PendentSprinklerBlockName on this layer (legacy below layer unused for new inserts).
        public const string SprinklerMarkerAboveLayer = "sprinkler - sprinkler (above)";
        public const short SprinklerMarkerAboveAciCyan = 4;

        public const string SprinklerMarkerBelowLayer = "sprinkler - sprinkler (below)";
        public const short SprinklerMarkerBelowAciMagenta = 6;

        /// <summary>
        /// Apply sprinklers inserts this block at each computed point; the definition must exist in the current drawing.
        /// </summary>
        public const string PendentSprinklerBlockName = "PENDENT SPRINKLER (RG)";

        /// <summary>When a block with this name exists in the drawing, branch reducers use it instead of the built-in wedge.</summary>
        public const string ReducerBlockName = "reducer";

        public static string GetConfiguredSprinklerBlockName()
        {
            var configured = RuntimeSettings.Load()?.SprinklerBlockFile;
            var fromFile = SafeBlockNameFromFile(configured);
            return string.IsNullOrWhiteSpace(fromFile) ? PendentSprinklerBlockName : fromFile;
        }

        public static string GetConfiguredReducerBlockName()
        {
            var configured = RuntimeSettings.Load()?.ReducerBlockFile;
            var fromFile = SafeBlockNameFromFile(configured);
            return string.IsNullOrWhiteSpace(fromFile) ? ReducerBlockName : fromFile;
        }

        public static string GetConfiguredShaftBlockName()
        {
            var configured = RuntimeSettings.Load()?.ShaftBlockFile;
            var fromFile = SafeBlockNameFromFile(configured);
            return string.IsNullOrWhiteSpace(fromFile) ? "shaft" : fromFile;
        }

        private static string SafeBlockNameFromFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                var name = Path.GetFileNameWithoutExtension(fileName.Trim());
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True if <paramref name="layerName"/> is the unified design layer or its alternate spelling.</summary>
        public static bool IsUnifiedZoneDesignLayerName(string layerName) =>
            !string.IsNullOrEmpty(layerName) &&
            (string.Equals(layerName, ZoneLayer, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(layerName, ZoneLayerAlternateName, StringComparison.OrdinalIgnoreCase));

        public static bool IsMcdZoneOutlineLayerName(string layerName) =>
            !string.IsNullOrEmpty(layerName) &&
            string.Equals(layerName, McdZoneBoundaryLayer, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the built global separator outline (legacy name) or <see cref="McdZoneBoundaryLayer"/> (current standard).
        /// </summary>
        public static bool IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(string layerName) =>
            !string.IsNullOrEmpty(layerName) &&
            (string.Equals(layerName, ZoneGlobalBoundaryLayer, StringComparison.OrdinalIgnoreCase)
             || IsMcdZoneOutlineLayerName(layerName));

        /// <summary>
        /// Layers where automated sprinkler content may appear: unified <see cref="ZoneLayer"/> plus legacy per-role layer
        /// names from older drawings.
        /// </summary>
        public static bool IsAutomationGeometryLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            if (IsUnifiedZoneDesignLayerName(layerName)) return true;
            return string.Equals(layerName, MainPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdMainPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, BranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, BranchMarkerLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, BranchReducerLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdReducerLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, BranchLabelLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdLabelLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerMarkerAboveLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerMarkerBelowLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, McdSprinklersLayer, StringComparison.OrdinalIgnoreCase)
                || IsNoBranchPipeHighlightLayerName(layerName)
                || IsMcdZoneOutlineLayerName(layerName);
        }

        /// <summary>Legacy layers used only for sprinkler head symbols (above/below).</summary>
        public static bool IsSprinklerHeadSymbolLayerName(string layerName) =>
            string.Equals(layerName, SprinklerMarkerAboveLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, SprinklerMarkerBelowLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, McdSprinklersLayer, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the block reference is the pendent sprinkler symbol.</summary>
        public static bool IsPendentSprinklerBlock(Transaction tr, BlockReference br)
        {
            if (tr == null || br == null) return false;
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                return string.Equals(btr.Name, GetConfiguredSprinklerBlockName(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(btr.Name, PendentSprinklerBlockName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Sprinkler head placement: legacy head layers, or pendent block on the unified zone layer.</summary>
        public static bool IsSprinklerHeadEntity(Transaction tr, Entity ent)
        {
            if (ent == null) return false;
            if (IsSprinklerHeadSymbolLayerName(ent.Layer))
                return ent is BlockReference || ent is Circle;
            if (ent is BlockReference br &&
                (IsUnifiedZoneDesignLayerName(ent.Layer) ||
                 string.Equals(ent.Layer, McdSprinklersLayer, StringComparison.OrdinalIgnoreCase) ||
                 IsNoBranchPipeHighlightLayerName(ent.Layer)))
                return IsPendentSprinklerBlock(tr, br);
            if (ent is Circle && IsNoBranchPipeHighlightLayerName(ent.Layer))
                return true;
            return false;
        }

        /// <summary>Main pipe / trunk polylines (unified zone layer or legacy main-pipe layer).</summary>
        public static bool IsMainPipeLayerName(string layerName) =>
            IsUnifiedZoneDesignLayerName(layerName)
            || string.Equals(layerName, MainPipeLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, McdMainPipeLayer, StringComparison.OrdinalIgnoreCase);

        public static bool IsBranchPipeGeometryLayerName(string layerName) =>
            !string.IsNullOrEmpty(layerName) &&
            (string.Equals(layerName, BranchPipeLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, BranchMarkerLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, BranchReducerLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, McdReducerLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, BranchLabelLayer, StringComparison.OrdinalIgnoreCase)
             || string.Equals(layerName, McdLabelLayer, StringComparison.OrdinalIgnoreCase));

        /// <summary>Layers the agent should prioritize when summarizing the drawing (automation + floor boundary + labels).</summary>
        public static bool IsAgentAnalysisLayerName(string layerName) =>
            IsAutomationGeometryLayerName(layerName)
            || string.Equals(layerName, WorkLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, BoundaryLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, ZoneLabelLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, McdLabelLayer, StringComparison.OrdinalIgnoreCase);

        /// <summary>Closed polylines that may be zone or floor boundaries for discovery tools.</summary>
        public static bool IsZoneOutlineDiscoveryLayerName(string layerName) =>
            IsUnifiedZoneDesignLayerName(layerName)
            || IsMcdZoneOutlineLayerName(layerName)
            || string.Equals(layerName, WorkLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(layerName, BoundaryLayer, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Polyline global width that stays visible for typical INSUNITS (2 drawing units is invisible in mm drawings).
        /// </summary>
        public static double BoundaryPolylineConstantWidth(Database db)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters:
                case UnitsValue.Centimeters:
                    return 75.0;
                case UnitsValue.Meters:
                    return 0.05;
                case UnitsValue.Inches:
                case UnitsValue.Feet:
                    return 0.25;
                default:
                    return 2.0;
            }
        }

        /// <summary>ACI blue — default color for the work layer record when the layer is first created. Boundary polylines use ByLayer so they follow this (or any color you set on the layer).</summary>
        public const short WorkLayerAciBlue = 5;

        public static ObjectId EnsureWorkLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;
            ObjectId id;
            if (lt.Has(WorkLayer))
            {
                id = lt[WorkLayer];
                ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = WorkLayer };
                id = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, WorkLayerAciBlue);
            return id;
        }

        private static ObjectId EnsureNamedAciLayer(Transaction tr, Database db, string layerName, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;
            ObjectId id;
            if (lt.Has(layerName))
            {
                id = lt[layerName];
                ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = layerName };
                id = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, aci);
            try { ltr.IsOff = false; } catch { /* ignore */ }
            try { ltr.IsFrozen = false; } catch { /* ignore */ }
            return id;
        }

        public static ObjectId EnsureMcdFloorBoundaryLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdFloorBoundaryLayer, WorkLayerAciBlue);

        public static ObjectId EnsureMcdZoneBoundaryLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdZoneBoundaryLayer, ZoneLayerAciGreen);

        public static ObjectId EnsureMcdSprinklersLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdSprinklersLayer, SprinklerMarkerAboveAciCyan);

        public static ObjectId EnsureMcdNoBranchPipeHighlightLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdNoBranchPipeHighlightLayer, NoBranchSprinklerHighlightAci);

        /// <summary>Prefer <see cref="EnsureMcdNoBranchPipeHighlightLayer"/>; name kept for existing callers.</summary>
        public static ObjectId EnsureMcdNoBranchSprinklerHighlightLayer(Transaction tr, Database db) =>
            EnsureMcdNoBranchPipeHighlightLayer(tr, db);

        public static ObjectId EnsureMcdShaftsLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdShaftsLayer, McdShaftsAciMagenta);

        public static ObjectId EnsureMcdMainPipeLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdMainPipeLayer, MainPipeAciYellow);

        public static ObjectId EnsureMcdBranchPipeLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdBranchPipeLayer, BranchPipeAciYellow);

        /// <summary>ACI white — branch piping drawn from the tagged connector (orthogonal to connector).</summary>
        public static ObjectId EnsureMcdConnectorBranchPipeLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdConnectorBranchPipeLayer, BranchLabelAciWhite);

        public static ObjectId EnsureMcdLabelLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdLabelLayer, BranchLabelAciWhite);

        public static ObjectId EnsureMcdReducerLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, McdReducerLayer, BranchReducerAciOrange);

        public static ObjectId EnsureZoneLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;
            ObjectId id;
            if (lt.Has(ZoneLayer))
            {
                id = lt[ZoneLayer];
                ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = ZoneLayer };
                id = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, ZoneLayerAciGreen);
            ltr.IsOff = false;
            ltr.IsFrozen = false;
            return id;
        }

        public static ObjectId EnsureZoneLabelLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;
            ObjectId id;
            if (lt.Has(ZoneLabelLayer))
            {
                id = lt[ZoneLabelLayer];
                ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = ZoneLabelLayer };
                id = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, ZoneLabelAciWhite);
            ltr.IsOff = false;
            ltr.IsFrozen = false;
            return id;
        }

        public static ObjectId EnsureZoneGlobalBoundaryLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;
            ObjectId id;
            if (lt.Has(ZoneGlobalBoundaryLayer))
            {
                id = lt[ZoneGlobalBoundaryLayer];
                ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = ZoneGlobalBoundaryLayer };
                id = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, ZoneGlobalBoundaryAciCyan);
            try { ltr.IsOff = false; } catch { /* ignore */ }
            try { ltr.IsFrozen = false; } catch { /* ignore */ }
            return id;
        }

        /// <summary>Main pipe polylines use <see cref="McdMainPipeLayer"/>.</summary>
        public static ObjectId EnsureMainPipeLayer(Transaction tr, Database db) => EnsureMcdMainPipeLayer(tr, db);

        /// <summary>Branch pipe geometry uses <see cref="McdBranchPipeLayer"/>.</summary>
        public static ObjectId EnsureBranchPipeLayer(Transaction tr, Database db) => EnsureMcdBranchPipeLayer(tr, db);

        public static ObjectId EnsureBranchMarkerLayer(Transaction tr, Database db) => EnsureZoneLayer(tr, db);

        public static ObjectId EnsureBranchReducerLayer(Transaction tr, Database db) => EnsureMcdReducerLayer(tr, db);

        /// <summary>NFPA branch-line Ø labels when routing branches — separate from <see cref="McdLabelLayer"/> main schedule text.</summary>
        public static ObjectId EnsureBranchLabelLayer(Transaction tr, Database db) =>
            EnsureNamedAciLayer(tr, db, BranchLabelLayer, BranchLabelAciWhite);

        /// <summary>Pendent sprinkler inserts use <see cref="McdSprinklersLayer"/>.</summary>
        public static ObjectId EnsureSprinklerAboveLayer(Transaction tr, Database db) => EnsureMcdSprinklersLayer(tr, db);

        public static ObjectId EnsureSprinklerBelowLayer(Transaction tr, Database db) => EnsureMcdSprinklersLayer(tr, db);

        /// <summary>
        /// Ensures layers required by sprinkler automation are present and visible (floor boundary, unified zone layer, labels).
        /// </summary>
        public static void EnsureAll(Transaction tr, Database db)
        {
            EnsureWorkLayer(tr, db);
            EnsureZoneLayer(tr, db);
            EnsureZoneLabelLayer(tr, db);
            EnsureMcdZoneBoundaryLayer(tr, db);
        }

        public static double SprinklerMarkerRadius(Database db)
        {
            // Small visible marker. Tweak if needed.
            if (db != null && DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.10, out double du) && du > 0)
                return du;
            return 1.0;
        }

        /// <summary>MText height for zone labels (readable vs floor extent).</summary>
        public static double ZoneLabelTextHeight(Database db, Extents3d extents)
        {
            double baseH = BoundaryPolylineConstantWidth(db) * 4.0;
            try
            {
                double d = extents.MinPoint.DistanceTo(extents.MaxPoint);
                if (d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    return System.Math.Max(baseH, d * 0.002);
            }
            catch
            {
                /* ignore */
            }

            return baseH <= 0 ? 1.0 : baseH;
        }

        /// <summary>
        /// Line weight for zone-outline polylines (readable at typical scales).
        /// </summary>
        public static double ZoneOutlinePolylineWidth(Database db)
        {
            double w = BoundaryPolylineConstantWidth(db);
            return w <= 0 ? 1.0 : w * 0.35;
        }

        /// <summary>
        /// Ensures zone outlines stay visible when INSUNITS is unitless/wrong or at zoom extents (min ~0.05% of floor diagonal).
        /// </summary>
        public static double ZoneOutlinePolylineWidth(Database db, Extents3d extents)
        {
            double w = ZoneOutlinePolylineWidth(db);
            try
            {
                var min = extents.MinPoint;
                var max = extents.MaxPoint;
                double dx = max.X - min.X;
                double dy = max.Y - min.Y;
                double d = System.Math.Sqrt(dx * dx + dy * dy);
                if (d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    return System.Math.Max(w, d * 0.0005);
            }
            catch
            {
                /* ignore */
            }

            return w;
        }

        /// <summary>
        /// Returns ObjectId for the linetype, or Continuous if missing after load attempt.
        /// </summary>
        public static ObjectId EnsureLinetypePresent(Transaction tr, Database db, string name, Editor ed)
        {
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name))
                return ltt[name];

            string path = HostApplicationServices.Current.FindFile("acad.lin", db, FindFileHint.Default);
            if (path != null)
            {
                try
                {
                    db.LoadLineTypeFile(name, path);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\nCould not load linetype " + name + ": " + ex.Message + "\n");
                }
            }

            ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name))
                return ltt[name];

            if (name != "Continuous" && ltt.Has("Continuous"))
            {
                ed.WriteMessage("\nLinetype " + name + " not available; using Continuous for preview.\n");
                return ltt["Continuous"];
            }

            return ObjectId.Null;
        }
    }
}
