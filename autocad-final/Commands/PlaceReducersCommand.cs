using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
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
    /// Places reducer blocks at branch joints where pipe diameter steps down along a lateral
    /// (e.g. Ø50 → Ø40). Pick a shaft to identify the zone. Run after branch pipes exist.
    /// </summary>
    public class PlaceReducersCommand
    {
        private const string DbgTag = "PlaceReducers";

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

                try { ed.Regen(); } catch { /* ignore */ }
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

            try
            {
                Polyline zonePolyline = null;
                List<Point2d> zoneRing = null;
                string zoneBoundaryHex = null;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);

                    if (!TryGetShaftPoint2d(tr, shaftEntityId, out Point2d shaftPt2d, out string shaftErr))
                    {
                        resultMessage = shaftErr ?? "Could not read shaft entity.";
                        return false;
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    if (!TryFindZoneContainingPoint(tr, ms, shaftPt2d, out zonePolyline, out zoneRing, out zoneBoundaryHex))
                    {
                        resultMessage = "Shaft is not inside any zone.";
                        return false;
                    }

                    tr.Commit();
                }

                AgentLog.Write(DbgTag, "zone handle=" + (zoneBoundaryHex ?? "(none)") + " ringVerts=" + (zoneRing?.Count ?? 0));

                if (!AttachBranchesCommand.TryPlaceReducersForZone(
                        doc,
                        db,
                        zonePolyline,
                        zoneRing,
                        zoneBoundaryHex,
                        routeBranchPipesFromConnectorFirst: false,
                        explicitMainPipePolylineId: ObjectId.Null,
                        out string placeErr))
                {
                    resultMessage = placeErr ?? "Place reducers failed.";
                    AgentLog.Write(DbgTag, "FAILED: " + resultMessage);
                    return false;
                }

                success = true;
                resultMessage = placeErr ?? "Reducers placed.";
                AgentLog.Write(DbgTag, "DONE: " + resultMessage);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                resultMessage = "Place reducers failed: " + ex.ErrorStatus + " / " + ex.Message;
                AgentLog.Write(DbgTag, "EXCEPTION " + ex.ErrorStatus + " — " + ex.Message);
                return false;
            }
            catch (System.Exception ex)
            {
                resultMessage = "Place reducers failed: " + ex.Message;
                AgentLog.Write(DbgTag, "EXCEPTION (managed) — " + ex.Message);
                return false;
            }
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
    }
}
