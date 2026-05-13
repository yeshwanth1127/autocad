using System;
using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Branch pipe schedule: minimum nominal diameter (mm) for a given number of sprinklers on the branch,
    /// matching the project pipe schedule (max sprinklers per Ø): 25→2, 32→3, 40→5, 50→10, 65→20, 80→40, 100→100, 150→275.
    /// Counts beyond 275 still map to 150 mm nominal for plan labels and widths (manual heads may exceed the table).
    /// </summary>
    public static class NfpaBranchPipeSizing
    {
        /// <summary>
        /// Returns the smallest nominal pipe (mm) whose schedule row covers <paramref name="sprinklerCount"/> plan sprinklers
        /// (symbols on the branch — same counts as the PIPE SCHEDULE table).
        /// Counts above the 150Ø row (275) still resolve to 150 mm so labels and routing can proceed when heads are added manually.
        /// </summary>
        public static bool TryGetMinNominalMmForSprinklerCount(int sprinklerCount, out int nominalMm)
        {
            nominalMm = 0;
            if (sprinklerCount <= 0)
            {
                nominalMm = 25;
                return true;
            }

            if (sprinklerCount <= 2) nominalMm = 25;
            else if (sprinklerCount <= 3) nominalMm = 32;
            else if (sprinklerCount <= 5) nominalMm = 40;
            else if (sprinklerCount <= 10) nominalMm = 50;
            else if (sprinklerCount <= 20) nominalMm = 65;
            else if (sprinklerCount <= 40) nominalMm = 80;
            else if (sprinklerCount <= 100) nominalMm = 100;
            else nominalMm = 150;

            return true;
        }

        /// <summary>
        /// Uses nominal outside diameter in mm as polyline global width in drawing units (plan view convention).
        /// </summary>
        public static bool TryNominalPipeWidthDrawingUnits(Database db, int nominalMm, out double widthDu)
        {
            widthDu = 0;
            if (db == null || nominalMm <= 0)
                return false;

            double meters = nominalMm / 1000.0;
            return DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, meters, out widthDu) && widthDu > 0;
        }

        /// <summary>
        /// Polyline <see cref="Polyline.ConstantWidth"/> for branch pipes on the plan so NFPA schedule steps read clearly.
        /// Raw mm→DU widths are often only a few drawing units apart (25 vs 32 mm), so this applies a floor tied to the
        /// main pipe and exaggerates relative steps while staying monotonic with nominal diameter.
        /// </summary>
        public static double GetBranchPolylineDisplayWidthDu(Database db, int nominalMm, double mainTrunkWidthDu)
        {
            nominalMm = Math.Max(1, nominalMm);
            double trueW = 0;
            if (!TryNominalPipeWidthDrawingUnits(db, nominalMm, out trueW) || trueW <= 0)
                trueW = Math.Max(1.0, nominalMm * 0.35);

            double bw = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double floorW = Math.Max(mainTrunkWidthDu * 0.14, bw * 0.07);

            // 25 mm = baseline; larger nominals scale up so 25→50→100 are obviously different on screen.
            double rel = nominalMm / 25.0;
            double scaled = trueW * Math.Pow(rel, 1.08);

            double result = Math.Max(floorW, scaled);

            // Keep main visually dominant on plan: branch width must not exceed one quarter of main display width.
            if (mainTrunkWidthDu > 1e-9)
            {
                double maxBranchW = mainTrunkWidthDu * 0.25;
                if (result > maxBranchW)
                    result = maxBranchW;
            }

            return result;
        }

        /// <summary>
        /// Main trunk polyline width when routing: four times the reference branch display width (25 mm nominal baseline).
        /// Kept consistent with <see cref="GetBranchPolylineDisplayWidthDu"/> so mains read clearly thicker than branch segments on plan.
        /// </summary>
        public static double GetMainTrunkPolylineDisplayWidthDu(Database db)
        {
            double bw = SprinklerLayers.BoundaryPolylineConstantWidth(db);
            double provisionalTrunk = bw * 0.35;
            if (provisionalTrunk <= 0) provisionalTrunk = 1.0;
            double branchRef = GetBranchPolylineDisplayWidthDu(db, 25, provisionalTrunk);
            return Math.Max(bw * 0.01, branchRef * 4.0);
        }
    }
}
