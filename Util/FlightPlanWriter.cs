using System;
using System.Globalization;
using System.IO;
using Serilog;
using Simvars.Emum;
using Simvars.Model;

namespace Simvars.Util
{
    /// <summary>
    /// Generates a minimal MSFS/FSX-compatible .pln flight plan so an aircraft can be created
    /// with <c>AICreateEnrouteATCAircraft</c> and handed to the simulator's native ATC.
    /// EXPERIMENTAL: only used when <c>UseNativeAtc</c> is enabled. The plan is a simple direct
    /// route from the aircraft's current position to its destination (projected along the current
    /// heading because FlightRadar24 does not give us destination coordinates).
    /// </summary>
    public static class FlightPlanWriter
    {
        private static readonly string PlanFolder = Path.Combine(".", "Config", "FlightPlans");

        public static string WriteDirectPlan(Aircraft aircraft)
        {
            try
            {
                Directory.CreateDirectory(PlanFolder);

                string departureId = string.IsNullOrWhiteSpace(aircraft.airportOrigin) ? "DEP" : aircraft.airportOrigin;
                string destinationId = string.IsNullOrWhiteSpace(aircraft.airportDestination) ? "DEST" : aircraft.airportDestination;
                string fpType = aircraft.flightRule == FlightRule.VFR ? "VFR" : "IFR";
                int cruiseAlt = (int)Math.Max(aircraft.altimeter, aircraft.flightRule == FlightRule.VFR ? 4500 : 10000);

                // Project a destination point ahead along the current track so the plan has a direction.
                GeoUtil.Project(aircraft.latitude, aircraft.longitude, aircraft.heading, 200000,
                    out double destLat, out double destLon);

                string depLla = FormatLla(aircraft.latitude, aircraft.longitude, aircraft.altimeter);
                string destLla = FormatLla(destLat, destLon, cruiseAlt);

                string content =
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<SimBase.Document Type=""AceXML"" version=""1,0"">
  <Descr>AceXML Document</Descr>
  <FlightPlan.FlightPlan>
    <Title>{departureId} to {destinationId}</Title>
    <FPType>{fpType}</FPType>
    <CruisingAlt>{cruiseAlt.ToString(CultureInfo.InvariantCulture)}</CruisingAlt>
    <DepartureID>{departureId}</DepartureID>
    <DepartureLLA>{depLla}</DepartureLLA>
    <DestinationID>{destinationId}</DestinationID>
    <DestinationLLA>{destLla}</DestinationLLA>
    <Descr>{departureId} to {destinationId}</Descr>
    <DepartureName>{departureId}</DepartureName>
    <DestinationName>{destinationId}</DestinationName>
    <AppVersion>
      <AppVersionMajor>11</AppVersionMajor>
      <AppVersionBuild>282174</AppVersionBuild>
    </AppVersion>
    <ATCWaypoint id=""{departureId}"">
      <ATCWaypointType>User</ATCWaypointType>
      <WorldPosition>{depLla}</WorldPosition>
      <ICAO>
        <ICAOIdent>{departureId}</ICAOIdent>
      </ICAO>
    </ATCWaypoint>
    <ATCWaypoint id=""{destinationId}"">
      <ATCWaypointType>User</ATCWaypointType>
      <WorldPosition>{destLla}</WorldPosition>
      <ICAO>
        <ICAOIdent>{destinationId}</ICAOIdent>
      </ICAO>
    </ATCWaypoint>
  </FlightPlan.FlightPlan>
</SimBase.Document>";

                string fileName = "AI_" + SanitizeFileName(aircraft.callsign) + ".pln";
                string fullPath = Path.GetFullPath(Path.Combine(PlanFolder, fileName));
                File.WriteAllText(fullPath, content);
                return fullPath;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to write flight plan for {aircraft.callsign}: {ex.Message}");
                return null;
            }
        }

        // Format a decimal lat/long + altitude (feet) into the FSX "N47° 26' 50.40\",E8°...,+035000.00" form.
        private static string FormatLla(double latitude, double longitude, double altitudeFeet)
        {
            string lat = FormatDms(latitude, true);
            string lon = FormatDms(longitude, false);
            string alt = (altitudeFeet >= 0 ? "+" : "-") + Math.Abs(altitudeFeet).ToString("000000.00", CultureInfo.InvariantCulture);
            return $"{lat},{lon},{alt}";
        }

        private static string FormatDms(double value, bool isLatitude)
        {
            char hemisphere = isLatitude ? (value >= 0 ? 'N' : 'S') : (value >= 0 ? 'E' : 'W');
            double abs = Math.Abs(value);
            int degrees = (int)abs;
            double minutesFull = (abs - degrees) * 60.0;
            int minutes = (int)minutesFull;
            double seconds = (minutesFull - minutes) * 60.0;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}° {2}' {3:0.00}\"", hemisphere, degrees, minutes, seconds);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "UNKNOWN";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
