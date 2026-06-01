using System;

namespace Simvars.Util
{
    /// <summary>
    /// Small geodesic helpers used to drive AI aircraft smoothly between
    /// FlightRadar24 samples instead of teleporting them.
    /// </summary>
    public static class GeoUtil
    {
        private const double EarthRadiusMeters = 6371000.0;

        private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>Great-circle distance between two lat/long points in meters.</summary>
        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusMeters * c;
        }

        /// <summary>Initial bearing (degrees true, 0-360) from point 1 to point 2.</summary>
        public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double phi1 = ToRadians(lat1);
            double phi2 = ToRadians(lat2);
            double dLon = ToRadians(lon2 - lon1);
            double y = Math.Sin(dLon) * Math.Cos(phi2);
            double x = Math.Cos(phi1) * Math.Sin(phi2) -
                       Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
            double bearing = ToDegrees(Math.Atan2(y, x));
            return (bearing + 360.0) % 360.0;
        }

        /// <summary>
        /// Project a new lat/long a given distance (meters) along a heading (degrees true).
        /// Used to place a short rollout/lead waypoint ahead of an aircraft.
        /// </summary>
        public static void Project(double lat, double lon, double headingDegrees, double distanceMeters,
            out double newLat, out double newLon)
        {
            double phi1 = ToRadians(lat);
            double lambda1 = ToRadians(lon);
            double theta = ToRadians(headingDegrees);
            double delta = distanceMeters / EarthRadiusMeters;

            double phi2 = Math.Asin(Math.Sin(phi1) * Math.Cos(delta) +
                                    Math.Cos(phi1) * Math.Sin(delta) * Math.Cos(theta));
            double lambda2 = lambda1 + Math.Atan2(Math.Sin(theta) * Math.Sin(delta) * Math.Cos(phi1),
                                                   Math.Cos(delta) - Math.Sin(phi1) * Math.Sin(phi2));

            newLat = ToDegrees(phi2);
            newLon = (ToDegrees(lambda2) + 540.0) % 360.0 - 180.0;
        }
    }
}
