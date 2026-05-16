using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    /// <summary>Optional command to verify slanted-main grid attach regression (no drawing changes).</summary>
    public class RoutingRegressionCommand
    {
        [CommandMethod("SPRINKLERRoutingRegression")]
        public void RunRoutingRegression()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            if (ed == null) return;
            bool ok = PolylineTrunkBranchRoutingRegression.RunAll();
            ed.WriteMessage(ok ? "\nSPRINKLERRoutingRegression: OK.\n" : "\nSPRINKLERRoutingRegression: FAILED.\n");
        }
    }
}
