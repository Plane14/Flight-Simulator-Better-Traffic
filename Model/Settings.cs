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
        // Experimental: hand IFR airliners that have a known route to MSFS native ATC
        // (AICreateEnrouteATCAircraft) instead of driving them from live data.
        public bool UseNativeAtc;

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
