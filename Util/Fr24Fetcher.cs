using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Serilog;
using Simvars.Model;

namespace Simvars.Util
{
    /// <summary>
    /// Manages the Zendriver-based FlightRadar24 fetcher sidecar (fr24_fetcher.py) and proxies
    /// FR24 requests through it. FlightRadar24 rejects plain HTTP clients, so the sidecar drives a
    /// real Chrome session. If the sidecar cannot be started, the app transparently falls back to
    /// the direct HTTP path.
    /// </summary>
    public static class Fr24Fetcher
    {
        private static Process _process;
        private static int _port;
        private static readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private static readonly object _lock = new object();

        public static bool Enabled { get; private set; }

        public static void Initialize(Settings settings)
        {
            if (Enabled) return;
            if (settings == null || !settings.UseZendriver)
            {
                Log.Information("Zendriver FR24 fetcher disabled; using direct HTTP requests.");
                return;
            }

            lock (_lock)
            {
                if (Enabled) return;
                try
                {
                    _port = settings.ZendriverPort > 0 ? settings.ZendriverPort : 8743;
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string script = Path.Combine(baseDir, "fr24_fetcher.py");
                    if (!File.Exists(script))
                    {
                        Log.Error($"Zendriver fetcher script not found at {script}; using direct HTTP requests.");
                        return;
                    }

                    string python = string.IsNullOrWhiteSpace(settings.PythonPath) ? "python" : settings.PythonPath;
                    string userDataDir = Path.Combine(baseDir, "zd_fr24_profile");
                    string args = $"\"{script}\" --port {_port} --user-data-dir \"{userDataDir}\"";
                    if (!string.IsNullOrWhiteSpace(settings.ChromePath))
                        args += $" --chrome \"{settings.ChromePath}\"";

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = python,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = baseDir
                    };
                    _process = Process.Start(startInfo);
                    if (_process == null)
                    {
                        Log.Error("Failed to start Zendriver FR24 fetcher; using direct HTTP requests.");
                        return;
                    }
                    _process.OutputDataReceived += (s, e) => { if (e.Data != null) Log.Information("[fr24] " + e.Data); };
                    _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Warning("[fr24] " + e.Data); };
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    Enabled = true;
                    Log.Information($"Started Zendriver FR24 fetcher (port {_port}); waiting for browser session.");
                    WaitForHealth(TimeSpan.FromSeconds(90));
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not start Zendriver FR24 fetcher ({ex.Message}); using direct HTTP requests.");
                    Enabled = false;
                }
            }
        }

        private static void WaitForHealth(TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (_process != null && _process.HasExited)
                {
                    Log.Error("Zendriver FR24 fetcher exited early; using direct HTTP requests.");
                    Enabled = false;
                    return;
                }
                try
                {
                    HttpResponseMessage response = _client.GetAsync($"http://127.0.0.1:{_port}/health").Result;
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Log.Information("Zendriver FR24 fetcher is ready.");
                        return;
                    }
                }
                catch
                {
                    // not up yet
                }
                System.Threading.Thread.Sleep(2000);
            }
            Log.Warning("Zendriver FR24 fetcher did not report ready within timeout; will retry per request.");
        }

        /// <summary>
        /// Fetch a FlightRadar24 URL through the sidecar. Returns the raw JSON body, or null on failure.
        /// </summary>
        public static string Fetch(string url)
        {
            if (!Enabled) return null;
            try
            {
                string proxied = $"http://127.0.0.1:{_port}/fetch?url=" + Uri.EscapeDataString(url);
                HttpResponseMessage response = _client.GetAsync(proxied).Result;
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning($"Zendriver fetch failed ({(int)response.StatusCode}) for {url}");
                    return null;
                }
                return response.Content.ReadAsStringAsync().Result;
            }
            catch (Exception ex)
            {
                Log.Warning($"Zendriver fetch error for {url}: {ex.Message}");
                return null;
            }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error shutting down Zendriver FR24 fetcher: {ex.Message}");
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                    Enabled = false;
                }
            }
        }
    }
}
