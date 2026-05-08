using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace autocad_final
{
    /// <summary>Thin document context for commands (editor/database accessors).</summary>
    public sealed class CadContext
    {
        public Document Document { get; }

        public Editor Editor => Document.Editor;

        public Database Database => Document.Database;

        public CadContext(Document document)
        {
            Document = document;
        }

        public static CadContext TryFromActiveDocument()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            return doc == null ? null : new CadContext(doc);
        }
    }
}
