using System;
using Newtonsoft.Json;
using Sentry;
using Serilog;

namespace Simvars.Model
{
    public class Settings
    {
        public string CommunityFolderPath;
        public string AdditionalFolderPath;
        public int MaximumAmountOfPlanes;
        // Experimental: hand flights with a known route to MSFS native ATC
        // (AICreateEnrouteATCAircraft) instead of driving them from live data.
        public bool UseNativeAtc;
        // Fetch FlightRadar24 data through the Zendriver browser sidecar (fr24_fetcher.py)
        // because FlightRadar24 blocks plain HTTP clients.
        public bool UseZendriver;
        public int ZendriverPort;
        // Optional overrides; leave empty to use "python" on PATH and Zendriver's auto-detected Chrome.
        public string PythonPath;
        public string ChromePath;

        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(this);

                //write string to file
                System.IO.File.WriteAllText(@".\Config\Settings.json", json);
            }
            catch (Exception ex)
            {
                _ = SentrySdk.CaptureException(ex);
                Log.Error(ex.Message);
            }
        }
    }
}
