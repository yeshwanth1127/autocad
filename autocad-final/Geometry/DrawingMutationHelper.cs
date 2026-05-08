using Autodesk.AutoCAD.ApplicationServices;

namespace autocad_final.Geometry
{
    /// <summary>
    /// After database writes: refresh the screen and keep drawing state coherent for the next read/snapshot.
    /// Does not change <see cref="Autodesk.AutoCAD.DatabaseServices.Database.Insunits"/> (callers set units in templates).
    /// </summary>
    public static class DrawingMutationHelper
    {
        public static void AfterSuccessfulWrite(Document doc)
        {
            if (doc?.Editor == null)
                return;
            try
            {
                doc.Editor.UpdateScreen();
            }
            catch
            {
                /* ignore */
            }

            try
            {
                doc.Editor.Regen();
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
