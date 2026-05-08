using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace autocad_final.Geometry
{
    internal static class DbObjectSafeAccess
    {
        internal static bool TryGetObject<T>(Transaction tr, ObjectId id, OpenMode mode, out T obj)
            where T : DBObject
        {
            obj = null;
            if (tr == null || id.IsNull || id.IsErased || !id.IsValid)
                return false;

            try
            {
                obj = tr.GetObject(id, mode, false) as T;
                if (obj == null || obj.IsErased)
                {
                    obj = null;
                    return false;
                }
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (
                ex.ErrorStatus == ErrorStatus.WasErased ||
                ex.ErrorStatus == ErrorStatus.NullObjectId ||
                ex.ErrorStatus == ErrorStatus.InvalidInput)
            {
                obj = null;
                return false;
            }
        }
    }
}
