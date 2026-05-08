using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class FindRoomNameInsideBoundary
    {
        public static string Run(Database db, Polyline boundary)
        {
            var vertices = new List<Point2d>();
            for (int i = 0; i < boundary.NumberOfVertices; i++)
                vertices.Add(boundary.GetPoint2dAt(i));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent is DBText dbText)
                    {
                        var p = new Point2d(dbText.Position.X, dbText.Position.Y);
                        if (IsPointInPolygon(vertices, p))
                        {
                            tr.Commit();
                            return (dbText.TextString ?? string.Empty).Trim();
                        }
                    }
                    else if (ent is MText mText)
                    {
                        var p = new Point2d(mText.Location.X, mText.Location.Y);
                        if (IsPointInPolygon(vertices, p))
                        {
                            tr.Commit();
                            return (mText.Text ?? mText.Contents ?? string.Empty).Trim();
                        }
                    }
                }

                tr.Commit();
            }

            return string.Empty;
        }

        private static bool IsPointInPolygon(IList<Point2d> poly, Point2d pt)
        {
            bool inside = false;
            int n = poly.Count;
            if (n < 3) return false;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                bool intersect =
                    ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y)) &&
                    (pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / ((poly[j].Y - poly[i].Y) + 1e-12) + poly[i].X);
                if (intersect) inside = !inside;
            }

            return inside;
        }
    }
}

