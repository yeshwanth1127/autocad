using Autodesk.AutoCAD.EditorInput;

namespace autocad_final.Licensing
{
    internal static class TrialGuard
    {
        internal static bool EnsureActive(Editor ed)
        {
            if (!TrialExpiry.IsExpired())
                return true;
            try { ed?.WriteMessage("\n" + TrialExpiry.ExpiredUserMessage + "\n"); } catch { /* ignore */ }
            return false;
        }
    }
}
