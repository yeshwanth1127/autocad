using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.Agent;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Converts polyline area (square drawing units) to square meters using <see cref="Database.Insunits"/>.
    /// </summary>
    public static class DrawingUnitsHelper
    {
        /// <summary>Maximum floor area per shaft (m²), from <c>ShaftMaxServiceAreaM2</c> in Properties.config.</summary>
        public static double ShaftAreaLimitM2 => RuntimeSettings.Load().ShaftMaxServiceAreaM2;

        /// <summary>
        /// Returns area in m² when possible; otherwise null and sets <paramref name="rawArea"/> to native area.
        /// </summary>
        public static double? TryGetAreaSquareMeters(Database db, double areaInSquareDrawingUnits, out double rawArea)
        {
            rawArea = areaInSquareDrawingUnits;
            return TryGetAreaSquareMetersFromUnit(db.Insunits, areaInSquareDrawingUnits);
        }

        public static double? TryGetAreaSquareMetersFromUnit(UnitsValue unit, double areaInSquareDrawingUnits)
        {
            switch (unit)
            {
                case UnitsValue.Meters:
                    return areaInSquareDrawingUnits;
                case UnitsValue.Millimeters:
                    return areaInSquareDrawingUnits / 1_000_000.0;
                case UnitsValue.Centimeters:
                    return areaInSquareDrawingUnits / 10_000.0;
                case UnitsValue.Inches:
                    return areaInSquareDrawingUnits * 0.00064516;
                case UnitsValue.Feet:
                    return areaInSquareDrawingUnits * 0.09290304;
                default:
                    return null;
            }
        }

        public static string InsunitsLabel(Database db)
        {
            return db.Insunits.ToString();
        }

        public static int RequiredShaftsCeil(double areaM2)
        {
            if (areaM2 <= 0) return 1;
            return (int)System.Math.Ceiling(areaM2 / ShaftAreaLimitM2);
        }

        /// <summary>
        /// Converts a length in meters to drawing units when <paramref name="unit"/> is supported for metric/imperial conversion.
        /// </summary>
        public static bool TryMetersToDrawingLength(UnitsValue unit, double meters, out double lengthDrawingUnits)
        {
            lengthDrawingUnits = 0;
            switch (unit)
            {
                case UnitsValue.Meters:
                    lengthDrawingUnits = meters;
                    return true;
                case UnitsValue.Millimeters:
                    lengthDrawingUnits = meters * 1000.0;
                    return true;
                case UnitsValue.Centimeters:
                    lengthDrawingUnits = meters * 100.0;
                    return true;
                case UnitsValue.Inches:
                    lengthDrawingUnits = meters / 0.0254;
                    return true;
                case UnitsValue.Feet:
                    lengthDrawingUnits = meters / 0.3048;
                    return true;
                default:
                    return false;
            }
        }

        // Candidate du-per-meter scale factors tried when INSUNITS is unset or mismatched.
        // Order: mm first (most common in architectural drawings), then cm, m, inches, feet.
        private static readonly double[] KnownScales = { 1000.0, 100.0, 1.0, 1.0 / 0.0254, 1.0 / 0.3048 };

        // Plausible architectural zone size in meters — anything from a small closet (~1m)
        // up to a very large warehouse floor (~500m). Beyond that, the scale is almost
        // certainly wrong regardless of what INSUNITS says.
        private const double MinPlausibleZoneMeters = 0.8;
        private const double MaxPlausibleZoneMeters = 500.0;
        // Ideal log-median zone size for scoring (~20 m — typical room / sub-zone).
        private const double IdealZoneMeters = 20.0;
        // Hard cap on cells per axis — above this, the downstream grid placer will struggle
        // even if the zone size itself looks plausible.
        private const double MaxCellsPerAxis = 3000.0;

        /// <summary>
        /// Returns drawing-units-per-meter, auto-detecting the actual scale from the zone extent when
        /// INSUNITS is unset or doesn't match the coordinate values in the drawing.
        /// <para>
        /// Picks the candidate scale whose resulting zone size in meters is most plausible
        /// (closest to <see cref="IdealZoneMeters"/> in log-space, within the
        /// [<see cref="MinPlausibleZoneMeters"/>, <see cref="MaxPlausibleZoneMeters"/>] range).
        /// Declared INSUNITS gets a small preference so it wins ties against guessed scales.
        /// Works for any irregular boundary because it reasons over the boundary's bbox extent,
        /// not its shape.
        /// </para>
        /// </summary>
        public static bool TryAutoGetDrawingScale(UnitsValue unit, double spacingMeters, double extentHint, out double duPerMeter)
        {
            duPerMeter = 0;
            if (spacingMeters <= 0) return false;

            double declared = TryMetersToDrawingLength(unit, 1.0, out double d) && d > 0 ? d : 0;

            // No extent — can't score. Trust declared or fail.
            if (extentHint <= 0)
            {
                if (declared > 0) { duPerMeter = declared; return true; }
                return false;
            }

            double bestScale = 0;
            double bestScore = double.PositiveInfinity;

            // When INSUNITS maps cleanly to du/m and the bbox is plausible, use it — do not let
            // auto-guess override (e.g. a 150 m-wide floor in meters loses to "inches" because
            // 150/39.37 ≈ 3.8 m is closer to the ideal ~20 m in log-space than 150 m is,
            // which yields spacing ≈118 DU instead of 3 DU and far too few sprinklers).
            if (declared > 0)
            {
                double declaredScore = ScoreScale(declared, spacingMeters, extentHint);
                if (!double.IsPositiveInfinity(declaredScore))
                {
                    duPerMeter = declared;
                    return true;
                }
            }

            foreach (var candidate in KnownScales)
            {
                double s = ScoreScale(candidate, spacingMeters, extentHint);
                if (s < bestScore) { bestScore = s; bestScale = candidate; }
            }

            if (bestScale > 0 && !double.IsPositiveInfinity(bestScore))
            {
                duPerMeter = bestScale;
                return true;
            }

            // No plausible scale found — fall back to declared so existing callers don't crash,
            // but the downstream grid cap will reject it with a readable error.
            if (declared > 0) { duPerMeter = declared; return true; }
            return false;
        }

        /// <summary>
        /// Scores how plausible it is that <paramref name="duPerMeter"/> is the real scale
        /// given the zone's extent in drawing units. Lower score = better.
        /// Returns +∞ to reject scales that imply an impossible zone size or an impossibly dense grid.
        /// </summary>
        private static double ScoreScale(double duPerMeter, double spacingMeters, double extentDu)
        {
            if (duPerMeter <= 0) return double.PositiveInfinity;

            double zoneSizeM = extentDu / duPerMeter;
            if (zoneSizeM < MinPlausibleZoneMeters || zoneSizeM > MaxPlausibleZoneMeters)
                return double.PositiveInfinity;

            double cellsPerAxis = extentDu / (duPerMeter * spacingMeters);
            if (cellsPerAxis > MaxCellsPerAxis || cellsPerAxis < 1.0)
                return double.PositiveInfinity;

            // Distance from the ideal in log-space — 2 m and 200 m are roughly equidistant
            // from 20 m, so medium and large zones are treated fairly.
            return System.Math.Abs(System.Math.Log(zoneSizeM) - System.Math.Log(IdealZoneMeters));
        }

        /// <summary>
        /// Converts a length in drawing units to meters when <paramref name="unit"/> is supported (inverse of <see cref="TryMetersToDrawingLength"/>).
        /// </summary>
        public static bool TryDrawingLengthToMeters(UnitsValue unit, double lengthDrawingUnits, out double meters)
        {
            meters = 0;
            switch (unit)
            {
                case UnitsValue.Meters:
                    meters = lengthDrawingUnits;
                    return true;
                case UnitsValue.Millimeters:
                    meters = lengthDrawingUnits / 1000.0;
                    return true;
                case UnitsValue.Centimeters:
                    meters = lengthDrawingUnits / 100.0;
                    return true;
                case UnitsValue.Inches:
                    meters = lengthDrawingUnits * 0.0254;
                    return true;
                case UnitsValue.Feet:
                    meters = lengthDrawingUnits * 0.3048;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Converts an area in m² to square drawing units (inverse of <see cref="TryGetAreaSquareMetersFromUnit"/>).
        /// </summary>
        public static double SquareMetersToDrawingArea(UnitsValue unit, double areaM2)
        {
            switch (unit)
            {
                case UnitsValue.Meters:
                    return areaM2;
                case UnitsValue.Millimeters:
                    return areaM2 * 1_000_000.0;
                case UnitsValue.Centimeters:
                    return areaM2 * 10_000.0;
                case UnitsValue.Inches:
                    return areaM2 / 0.00064516;
                case UnitsValue.Feet:
                    return areaM2 / 0.09290304;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(unit), unit, "INSUNITS not supported for m² area conversion.");
            }
        }

        /// <summary>
        /// Per-zone target: floor area ÷ N (equal split of the entire boundary). <paramref name="remainderM2"/> is 0 when m² is known.
        /// <paramref name="aTargetDrawingArea"/> is each zone's area in square drawing units for geometry.
        /// </summary>
        public static void ComputeFormulaZoneTargets(
            Database db,
            double floorAreaDrawingUnits,
            int shaftCount,
            out double aTargetDrawingArea,
            out double? floorM2,
            out double? aTargetM2,
            out double? remainderM2)
        {
            floorM2 = TryGetAreaSquareMeters(db, floorAreaDrawingUnits, out _);
            remainderM2 = null;
            aTargetM2 = null;
            aTargetDrawingArea = 0;

            if (shaftCount < 2)
                return;

            if (floorM2.HasValue)
            {
                double fm = floorM2.Value;
                aTargetM2 = fm / shaftCount;
                remainderM2 = 0;
                aTargetDrawingArea = SquareMetersToDrawingArea(db.Insunits, aTargetM2.Value);
                return;
            }

            // Floor m² unknown: equal split in native drawing units (full floor).
            double perNative = floorAreaDrawingUnits / shaftCount;
            aTargetDrawingArea = perNative;
        }
    }
}
