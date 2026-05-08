using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Reporting
{
    /// <summary>
    /// Builds a simple two-column report table for building area and shaft count.
    /// </summary>
    public static class AreaTableService
    {
        public static void AppendAreaTable(
            Database db,
            Transaction tr,
            BlockTableRecord modelSpace,
            Point3d insertPoint,
            string itemLabel,
            string itemValue,
            string unitsLabel,
            string requiredShaftsLabel,
            string requiredShaftsValue)
        {
            var table = new Table();
            table.SetDatabaseDefaults(db);
            table.SetSize(4, 2);
            table.Position = insertPoint;
            table.SetRowHeight(1.5);
            table.SetColumnWidth(10.0);

            table.Cells[0, 0].TextString = "Item";
            table.Cells[0, 1].TextString = "Value";
            table.Cells[1, 0].TextString = itemLabel;
            table.Cells[1, 1].TextString = itemValue;
            table.Cells[2, 0].TextString = "Drawing units (INSUNITS)";
            table.Cells[2, 1].TextString = unitsLabel;
            table.Cells[3, 0].TextString = requiredShaftsLabel;
            table.Cells[3, 1].TextString = requiredShaftsValue;

            table.GenerateLayout();
            modelSpace.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);
        }
    }
}
