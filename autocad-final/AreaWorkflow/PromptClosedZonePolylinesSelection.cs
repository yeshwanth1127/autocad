using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Prompts for one or more closed lightweight polylines (typical zone outlines).
    /// </summary>
    public static class PromptClosedZonePolylinesSelection
    {
        /// <returns>True when at least one closed <see cref="Polyline"/> was selected.</returns>
        public static bool TrySelect(Editor ed, Database db, out List<ObjectId> closedPolylineIds, out string errorMessage)
        {
            closedPolylineIds = new List<ObjectId>();
            errorMessage = null;

            if (ed == null || db == null)
            {
                errorMessage = "No editor or database.";
                return false;
            }

            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect closed polyline zone boundaries (one or more): ",
                AllowDuplicates = false
            };

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });

            var psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK || psr.Value == null)
            {
                errorMessage = "Selection cancelled.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    Entity ent;
                    try { ent = tr.GetObject(so.ObjectId, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (ent == null || ent.IsErased) continue;
                    if (!(ent is Polyline pl)) continue;
                    if (!pl.Closed) continue;
                    closedPolylineIds.Add(so.ObjectId);
                }

                tr.Commit();
            }

            if (closedPolylineIds.Count == 0)
            {
                errorMessage = "No closed lightweight polylines in the selection.";
                return false;
            }

            return true;
        }
    }
}
