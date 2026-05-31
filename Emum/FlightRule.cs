namespace Simvars.Emum
{
    /// <summary>
    /// Estimated flight rules of a live-traffic aircraft. Derived heuristically from the
    /// FlightRadar24 data (airline / flight plan / aircraft type / altitude) since FR24 does
    /// not expose the filed flight rule directly.
    /// </summary>
    public enum FlightRule
    {
        Unknown,
        VFR,
        IFR
    }
}
