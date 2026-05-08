using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Blocks
{
    public static class WblockExportService
    {
        public static bool TryExportBlockDefinition(Database db, ObjectId blockDefId, string targetDwgPath, out string error)
        {
            error = null;
            if (db == null || blockDefId.IsNull)
            {
                error = "Invalid block definition.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetDwgPath))
            {
                error = "Invalid output path.";
                return false;
            }

            try
            {
                var folder = Path.GetDirectoryName(targetDwgPath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            try
            {
                var ids = new ObjectIdCollection(new[] { blockDefId });
                using (var wdb = db.Wblock(ids, Point3d.Origin))
                {
                    if (wdb == null)
                        throw new InvalidOperationException("WBLOCK failed.");

                    // Match units so INSERT-from-file doesn't auto-scale unexpectedly.
                    try { wdb.Insunits = db.Insunits; } catch { /* ignore */ }
                    try { wdb.Measurement = db.Measurement; } catch { /* ignore */ }

                    wdb.SaveAs(targetDwgPath, DwgVersion.Current);
                }
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string SafeBlockFileName(string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return "block";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = blockName.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                foreach (var c in invalid)
                {
                    if (chars[i] == c)
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            var name = new string(chars);
            while (name.Contains("  "))
                name = name.Replace("  ", " ");
            // Keep spaces so INSERT uses the correct block name (file name -> block name).
            return name.Length == 0 ? "block" : name;
        }

        public static string DefaultWblockFolder()
        {
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(docs))
                    docs = Path.GetTempPath();
                return Path.Combine(docs, "autocad-final", "wblocks");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "autocad-final", "wblocks");
            }
        }
    }
}

