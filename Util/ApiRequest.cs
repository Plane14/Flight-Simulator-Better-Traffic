using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;

namespace Simvars.Util
{
    public static class ApiRequest
    {
        public static JObject MakeGetRequest(string url)
        {
            JObject returnValue = new JObject { ["success"] = false };

            // Prefer the Zendriver sidecar (real browser) because FlightRadar24 blocks plain
            // HTTP clients. Fall back to a direct request if the sidecar is unavailable.
            if (Fr24Fetcher.Enabled)
            {
                string body = Fr24Fetcher.Fetch(url);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        returnValue["success"] = true;
                        returnValue["data"] = JObject.Parse(body);
                        return returnValue;
                    }
                    catch (JsonReaderException)
                    {
                        // Not JSON (e.g. a block/redirect page); fall through to the direct path.
                    }
                }
            }

            using (var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }))
            {
                HttpResponseMessage response = client.GetAsync(url).Result;

                if (response.IsSuccessStatusCode)
                {
                    string result = response.Content.ReadAsStringAsync().Result;
                    returnValue["success"] = true;
                    returnValue["data"] = JObject.Parse(result);
                }
                return returnValue;
            }
        }
    }
}
