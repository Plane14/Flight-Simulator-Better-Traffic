using System;
using System.Collections.Generic;
using System.Linq;
using Simvars.Emum;
using Simvars.Model;

namespace Simvars.Util
{
    /// <summary>
    /// Estimates whether a live aircraft is operating IFR or VFR. FlightRadar24 does not
    /// expose the filed flight rule, so this uses robust heuristics based on the operator,
    /// the presence of a scheduled flight plan, the aircraft category and the altitude.
    /// </summary>
    public static class FlightClassifier
    {
        // ICAO type designators that are (almost) always flown VFR by GA pilots.
        private static readonly HashSet<string> LightPistonTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "C150", "C152", "C162", "C170", "C172", "C175", "C177", "C180", "C182", "C185",
            "C188", "C195", "C205", "C206", "C207", "C210", "C82R", "C82S", "C82T",
            "P28A", "P28B", "P28R", "P28T", "P32R", "PA18", "PA22", "PA24", "PA28", "PA32", "PA38", "PA44",
            "BE33", "BE35", "BE36", "BE58", "BE76", "DA40", "DA42", "DA20", "DR40", "DV20",
            "M20P", "M20T", "SR20", "SR22", "RV4", "RV6", "RV7", "RV8", "RV9", "RV10",
            "AA5", "AC11", "GA8", "TB10", "TB20", "TB21", "F152", "F172", "ECHO", "ULAC", "GLID",
            "C42", "P208", "P92", "MTOS"
        };

        // Keywords in the FR24 model text that indicate a light GA / VFR-typical aircraft.
        private static readonly string[] LightModelKeywords =
        {
            "cessna 1", "cessna f1", "cessna 2", "cessna f2", "piper", "cirrus", "diamond",
            "robin", "tecnam", "ikarus", "autogyro", "glider", "ultralight", "gyro", "mooney",
            "beech 3", "beech bonanza", "beech baron", "grumman", "socata", "vans rv", "skyhawk",
            "skylane", "cherokee", "warrior", "archer"
        };

        public static FlightRule Classify(Aircraft aircraft)
        {
            if (aircraft == null) return FlightRule.Unknown;

            string modelCode = (aircraft.modelCode ?? string.Empty).Trim();
            string model = (aircraft.model ?? string.Empty).Trim().ToLowerInvariant();
            bool hasAirline = !string.IsNullOrWhiteSpace(aircraft.icaoAirline);
            bool hasFlightPlan = !string.IsNullOrWhiteSpace(aircraft.airportOrigin) &&
                                 !string.IsNullOrWhiteSpace(aircraft.airportDestination);
            bool isLight = IsLightAircraft(modelCode, model);

            // Gliders are VFR by definition here.
            if (string.Equals(modelCode, "GLID", StringComparison.OrdinalIgnoreCase)) return FlightRule.VFR;

            // A scheduled airline operation is effectively always IFR.
            if (hasAirline) return FlightRule.IFR;

            // Turbine / heavier aircraft on a known route flies IFR.
            if (hasFlightPlan && !isLight) return FlightRule.IFR;

            // High cruising aircraft (above the typical VFR band) is IFR.
            if (aircraft.altimeter >= 11000) return FlightRule.IFR;

            // Light GA without an operator, low and slow, is treated as VFR.
            if (isLight) return FlightRule.VFR;

            // Fall back on a best guess: low/slow -> VFR, otherwise IFR.
            if (aircraft.altimeter > 0 && aircraft.altimeter < 9000 && aircraft.speed < 180)
                return FlightRule.VFR;

            return FlightRule.IFR;
        }

        private static bool IsLightAircraft(string modelCode, string lowerModel)
        {
            if (!string.IsNullOrEmpty(modelCode) && LightPistonTypes.Contains(modelCode)) return true;
            if (string.IsNullOrEmpty(lowerModel)) return false;
            return LightModelKeywords.Any(keyword => lowerModel.Contains(keyword));
        }
    }
}
