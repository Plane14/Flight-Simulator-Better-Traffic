using Microsoft.FlightSimulator.SimConnect;
using Newtonsoft.Json.Linq;
using Simvars.Emum;
using Simvars.Model;
using Simvars.Struct;
using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace Simvars.Util
{
    public class LiveTrafficHandler
    {
        public List<Aircraft> LiveTrafficAircraft;
        private readonly SimConnect _simConnect;
        private int _requestCount = 0;
        private int MaxPlanes = 60;
        private List<Addon> _addons;
        private int _teleportFixDelay = 30;
        private bool _useNativeAtc = false;

        // Waypoint refresh cadence (seconds) per phase. Tighter tracking on final approach and
        // for nimble GA traffic reduces the chance of an aircraft overflying the runway.
        private const int _approachFixDelay = 8;
        private const int _gaFixDelay = 12;

        // Ground movement tuning.
        // Use the aircraft's real reported ground speed as the waypoint speed so the AI can keep
        // pace with the live data between updates instead of being teleported. The ceiling only
        // guards against absurd values (e.g. a bad data spike); rollout/takeoff speeds are allowed.
        private const double _taxiSpeedCeilingKts = 200;
        private const double _groundSpeedMinKts = 8;        // keep the AI rolling rather than stalling
        private const double _groundStoppedKts = 3;         // below this the real aircraft is holding/parked
        // Only re-sync (teleport) on a big jump that the waypoint system cannot absorb (lost sync,
        // a data discontinuity, or a fresh placement) — not on every routine update.
        private const double _groundResyncMeters = 1500;
        private const double _touchdownRolloutMeters = 250; // forward rollout after landing
        private const double _taxiLeadMeters = 60;          // lead so the AI rolls through the point, not stops at it

        public string excludeAirportOrigin;
        public string excludeAirportDestination;
        public string excludeStatus;

        public bool excludeGround { get; set; }
        public bool ExclGaTraffic { get; set; }
        public bool ExclGlidTraffic { get; set; }
        public bool ExclAirlTraffic { get; set; }
        public bool ExclGroundTraffic { get; set; }
        public bool ExclLowAltTraffic { get; set; }
        public bool ExclMidAltTraffic { get; set; }
        public bool ExclHigAltTraffic { get; set; }
        public bool HighAltitudeTraffic { get; set; }

        // Hand eligible aircraft (any flight with a known destination) to MSFS native ATC.
        // Settable at runtime from the UI; only affects aircraft spawned after it is changed.
        public bool UseNativeAtc { get => _useNativeAtc; set => _useNativeAtc = value; }

        public LiveTrafficHandler(SimConnect simConnect)
        {
            LiveTrafficAircraft = new List<Aircraft>();
            _simConnect = simConnect;

            Settings settings = SettingsReader.FetchSettings();
            if (settings.MaximumAmountOfPlanes >= 0) MaxPlanes = settings.MaximumAmountOfPlanes;
            _useNativeAtc = settings.UseNativeAtc;
            Fr24Fetcher.Initialize(settings);
            _addons = AddonScanner.ScanAddons();
        }

        public void FetchNewData(PlayerAircraft plane)
        {
            JObject planeData = FlightRadarApi.GetAircraftNearby(plane.Longitude, plane.Latitude);
            if ((bool)planeData["success"] != true) return;
            ParsePlaneData((JObject)planeData["data"]);
        }

        public void SetObjectId(uint requestId, uint objectId)
        {

            Aircraft aircraft = LiveTrafficAircraft.FirstOrDefault(item => item.requestId == requestId);
            if (aircraft != null)
            {
                Log.Information($"Setting object ID: {objectId} for: {aircraft.callsign}");
                aircraft.objectId = objectId;

                PositionData position = new PositionData
                {
                    Latitude = aircraft.latitude,
                    Longitude = aircraft.longitude,
                    Altitude = aircraft.altimeterMeter,
                    Heading = aircraft.heading,
                    Pitch = 0,
                    Bank = 0,
                    Airspeed = (uint)aircraft.speed,
                    OnGround = (uint)(aircraft.isGrounded ? 1 : 0)
                };
                // _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneLocation,
                // aircraft.objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, position);
                // Native-ATC aircraft are flown by the sim from their flight plan, so we must NOT
                // release control back to the client for them.
                if (!aircraft.atcControlled)
                {
                    var request = DataRequests.AI_RELEASE + _requestCount;
                    _requestCount = (_requestCount + 1) % 10000;
                    _simConnect.AIReleaseControl(objectId, request);
                }
            }

        }

        private void ParsePlaneData(JObject planeData)
        {
            List<string> flightRadarIds = new List<string>();
            foreach (JProperty property in planeData.Properties())
            {
                //Determine if object is a plane, we only want planes from the api, not the other stat keys ;)
                if (!char.IsDigit(property.Name.ToCharArray()[0])) continue;
                flightRadarIds.Add(property.Name);
                Aircraft aircraft = LiveTrafficAircraft.FirstOrDefault(item => item.flightRadarId == property.Name);

                double longitude = (double)property.Value[2];
                double latitude = (double)property.Value[1];
                int heading = (int)property.Value[3];
                int altimeter = (int)property.Value[4];  //In feet (int)Math.Round((int)property.Value[4] * 0.3048); // Info for JAAP: I changed it from (int)property.Value[4]; for the right altitude Math.Round((int)property.Value[4] * 0.3048)
                int altimeterMeter = (int)Math.Round((int)property.Value[4] * 0.3048);
                int speed = (int)property.Value[5];
                string callsign = (string)property.Value[16];
                bool isGrounded = (bool)property.Value[14];
                string icaoAirline = (string)property.Value[18];
                string airportOrigin = null;
                string airportDestination = null;
                string tailNumber = callsign;
                string model = "Airbus A320 Neo";
                string modelCode = "A320";
                string airline = "";
                string infoExclude = "";
                if (aircraft == null)
                {
                    if (LiveTrafficAircraft.Count >= MaxPlanes) continue;
                    JObject extraData = FlightRadarApi.GetAircraftData(property.Name);
                    if ((bool)extraData["success"])
                    {
                        extraData = (JObject)extraData["data"];
                    }
                    else
                    {
                        Log.Error($"Failed to fetch extra data for {callsign}");
                        continue;
                    }
                    foreach (char c in callsign)
                    {
                        if (char.IsDigit(c)) break;
                        airline += c;
                    }

                    try
                    {
                        tailNumber = (string)extraData["identification"]?["number"]?["default"] ?? callsign;
                        model = (string)extraData["aircraft"]?["model"]?["text"] ?? "Airbus A320 Neo";
                        modelCode = (string)extraData["aircraft"]?["model"]?["code"] ?? "A32N";
                        airline = (string)extraData["airline"]?["name"] ?? airline;
                        airportOrigin = (string)extraData["airport"]?["origin"]?["code"]?["icao"] ?? null;
                        airportDestination = (string)extraData["airport"]?["destination"]?["code"]?["icao"] ?? null;
                    }

                    catch (Exception e)
                    {
                        Log.Error($"Failed to parse extra data for {callsign}");
                    }
                    aircraft = new Aircraft()
                    {
                        longitude = longitude,
                        latitude = latitude,
                        heading = heading,
                        altimeter = altimeter,
                        altimeterMeter = altimeterMeter,
                        speed = speed,
                        callsign = callsign,
                        flightRadarId = property.Name,
                        isGrounded = isGrounded,
                        tailNumber = tailNumber,
                        model = model,
                        airline = airline,
                        airportOrigin = airportOrigin,
                        airportDestination = airportDestination,
                        modelCode = modelCode,
                        icaoAirline = icaoAirline,
                        infoExclude = infoExclude,
                        isTeleportFixed = false,
                        spawnTime = DateTime.Now,
                        corrTime = DateTime.Now,
                        corrTime1 = DateTime.Now
                    };
                    aircraft.matchedModel = ModelMatching.MatchModel(aircraft, _addons);
                    aircraft.flightRule = FlightClassifier.Classify(aircraft);
                    aircraft.lastSimLatitude = latitude;
                    aircraft.lastSimLongitude = longitude;
                    aircraft.wasAirborne = !isGrounded;
                    // Hand any flight (IFR or VFR, departing or airborne) that has a known
                    // destination to native ATC (opt-in).
                    aircraft.atcControlled = _useNativeAtc &&
                                             !string.IsNullOrWhiteSpace(aircraft.airportDestination);

                    if (!isGrounded)
                    {
                        aircraft.countApproaching = 3;
                    }
                    else
                    {
                        aircraft.countApproaching = 0;
                    }

                    // Mauflo: This will fix the Problem with airports under the sea level - but only lower 100 feet under the level ;-)
                    if (!aircraft.onceSetGround && aircraft.altimeter <= 0 || aircraft.speed < 16)
                    {
                        aircraft.isGrounded = true; aircraft.altimeter = -100; //We should be shure, that the planes get grounded
                    }
                    if (altimeter <= 0) aircraft.isTeleportFixed = true; // Correct Altitude over 10.000 ft only for aircrafts they are not started from an airport

                    LiveTrafficAircraft.Add(aircraft);

                    if (!aircraft.infoExclude.Contains("EXCLUDED"))
                    {
                        SpawnPlane(aircraft);
                    }


                    continue;
                }

                if (aircraft.objectId == 0) continue;

                if (!aircraft.infoExclude.Contains("EXCLUDED"))
                {
                    if (aircraft.speed < 9) aircraft.isGrounded = true; //Slow GA Traffic should be grounded in this way

                    // Keep the estimated flight rule current as the live data evolves.
                    aircraft.flightRule = FlightClassifier.Classify(aircraft);

                    //Here starts the handling for the movement
                    // *-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-
                    // ATC-controlled aircraft are flown by the sim's native ATC; we do not drive them.
                    if (!aircraft.atcControlled)
                    {
                        if (!aircraft.isGrounded)
                        {
                            HandleAirborneMovement(aircraft, latitude, longitude, heading, altimeter, speed, isGrounded);
                        }
                        else
                        {
                            HandleGroundMovement(aircraft, latitude, longitude, heading, altimeter, speed);
                        }
                    }
                    // *-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-

                    // That is the function to turn on or off the teleporting of the high altitude traffic
                    if (HighAltitudeTraffic && altimeter > 9144)
                    {
                        aircraft.onceFixAltitudeCallsign = aircraft.callsign;
                    }
                    else
                    {
                        aircraft.onceFixAltitudeCallsign = "";
                    }

                    // Correct the Altitude over 30.000ft for that airplane
                    //<____________________________________________________>
                    // Here all airliners over 30.000 feet will be teleportet and fly a little then it will teleportet again.
                    // The problem is, that the AI airplane will correct the altitude to ground level after a while. so the altitude will not stay if we not teleport it.
                    if (altimeter > 29999 && aircraft.onceFixAltitudeCallsign == aircraft.callsign && aircraft.infoExclude != "HIGH ALT EXCLUDED")
                    {
                        PositionData position = new PositionData
                        {
                            Latitude = aircraft.latitude,
                            Longitude = aircraft.longitude,
                            Altitude = aircraft.altimeterMeter,
                            Heading = aircraft.heading,
                            Pitch = 0,
                            Bank = 0,
                            Airspeed = (uint)aircraft.speed,
                            OnGround = 0
                        };
                        Log.Information("Changing altitute for a highflying plane " + aircraft.tailNumber + " lat: " + aircraft.latitude + " long: " + aircraft.longitude + " request ID: " + aircraft.requestId + " speed: " + aircraft.speed + " heading: " + aircraft.heading + " objectId " + aircraft.objectId);
                        _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneLocation, aircraft.objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, position);
                        _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneWaypoints, aircraft.objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, aircraft.GetWayPointObjectArray());
                    }
                    if (altimeter > 29999 && aircraft.onceFixAltitudeCallsign != aircraft.callsign && aircraft.infoExclude != "HIGH ALT EXCLUDED") // && aircraft.objectId != 0
                    {
                        aircraft.waypoints.Add(new Waypoint()
                        {
                            Latitude = latitude,
                            Longitude = longitude,
                            Altitude = altimeter,
                            Speed = speed,
                            IsGrounded = isGrounded
                        });
                        _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneWaypoints, aircraft.objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, aircraft.GetWayPointObjectArray());
                        aircraft.onceFixAltitudeCallsign = aircraft.callsign;
                    }
                    //<____________________________________________________>
                }

                //Exclude checkbox handling
                //.-.-.-.-.-.-.-.-.-.-.-.-.
                //Exclude GA traffic 
                if (ExclGaTraffic && aircraft.icaoAirline == "" && aircraft.infoExclude != "GA EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "GA EXCLUDED";
                }
               if (!ExclGaTraffic && aircraft.icaoAirline == "" && aircraft.infoExclude == "GA EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude gliders
                if (ExclGlidTraffic && aircraft.modelCode == "GLID" && aircraft.infoExclude != "GLIDER EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "GLIDER EXCLUDED";
                }
               if (!ExclGlidTraffic && aircraft.modelCode == "GLID" && aircraft.infoExclude == "GLIDER EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude airlines
                if (ExclAirlTraffic && aircraft.icaoAirline != "" && aircraft.infoExclude != "AIRLINES EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "AIRLINES EXCLUDED";
                }
               if (!ExclAirlTraffic && aircraft.icaoAirline != "" && aircraft.infoExclude == "AIRLINES EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude ground traffic
                if (ExclGroundTraffic && altimeter <= 0 && aircraft.infoExclude != "GROUND EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "GROUND EXCLUDED";
                }
                if (!ExclGroundTraffic && altimeter <= 0 && aircraft.infoExclude == "GROUND EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude low altitude traffic
                if (ExclLowAltTraffic && aircraft.altimeter > 0 && aircraft.altimeter <= 9999 && aircraft.infoExclude != "LOW ALT EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "LOW ALT EXCLUDED";
                }
                else if (!ExclLowAltTraffic && aircraft.altimeter > 0 && aircraft.altimeter <= 9999 && aircraft.infoExclude == "LOW ALT EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude mid altitude traffic
                if (ExclMidAltTraffic && aircraft.altimeter > 9999 && aircraft.altimeter <= 19999 && aircraft.infoExclude != "MID ALT EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "MID ALT EXCLUDED";
                }
                if (!ExclMidAltTraffic && aircraft.altimeter > 9999 && aircraft.altimeter <= 19999 && aircraft.infoExclude == "MID ALT EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }

                // Exclude high altitude traffic
                if (ExclHigAltTraffic && aircraft.altimeter > 19999 && aircraft.infoExclude != "HIGH ALT EXCLUDED")
                {
                    excludeStatus = "Hide";
                    aircraft.infoExclude = "HIGH ALT EXCLUDED";
                }
                if (!ExclHigAltTraffic && aircraft.altimeter > 19000 && aircraft.infoExclude == "HIGH ALT EXCLUDED")
                {
                    excludeStatus = "Show";
                    aircraft.infoExclude = "";
                }


                if (excludeStatus == "Hide") //Despawn the aircraft
                {
                    var request = DataRequests.AI_RELEASE + _requestCount;
                    _requestCount = (_requestCount + 1) % 10000;
                    _simConnect.AIRemoveObject(aircraft.objectId, request);
                    excludeStatus = "";
                }
                
                if (excludeStatus == "Show") // Spawn the aircraft
                {
                    LiveTrafficAircraft.Remove(aircraft);
                    var request = DataRequests.AI_RELEASE + _requestCount;
                    _requestCount = (_requestCount + 1) % 10000;
                    SetObjectId(aircraft.objectId, (uint)request);
                    LiveTrafficAircraft.Add(aircraft);
                    SpawnPlane(aircraft);
                    aircraft.isTeleportFixed = false; //for the new altituide correction 
                    aircraft.onceSetGround = false; // for the new set when it was grounded
                    excludeStatus = "";
                }
                //.-.-.-.-.-.-.-.-.-.-.-.-.

                aircraft.latitude = latitude;
                aircraft.longitude = longitude;
                aircraft.altimeter = altimeter;
                aircraft.altimeterMeter = altimeterMeter;
                aircraft.heading = heading;
                aircraft.speed = speed;
                aircraft.isGrounded = isGrounded;
                infoExclude = aircraft.infoExclude;
            }
            try
            {
                DespawnOldPlanes(flightRadarIds);
            }
            catch (Exception ex)
            {
                Log.Error($"Error when trying to despawn aircraft, {ex.Message}");
            }
        }

        // Returns true when the aircraft looks like it is on final approach: low, slowing down
        // and either descending or heading for a known destination. Used to track the real
        // aircraft more tightly so the AI does not overfly the runway.
        private static bool IsOnApproach(Aircraft aircraft, int altimeter, int speed)
        {
            bool descending = aircraft.altimeter > 0 && altimeter < aircraft.altimeter;
            bool lowAndSlow = altimeter < 4000 && speed > 30 && speed < 200;
            return lowAndSlow && (descending || !string.IsNullOrWhiteSpace(aircraft.airportDestination));
        }

        // Move an airborne aircraft by feeding it a waypoint at the live position. The refresh
        // cadence tightens on final approach and for GA so the AI follows the real track down to
        // the runway instead of flying past it on a stale, far-ahead waypoint.
        private void HandleAirborneMovement(Aircraft aircraft, double latitude, double longitude, int heading,
            int altimeter, int speed, bool isGrounded)
        {
            aircraft.wasAirborne = true;
            // Leaving the ground again (a new departure) re-arms the landing logic for next time.
            aircraft.hasLandedThisFlight = false;

            int delay = IsOnApproach(aircraft, altimeter, speed)
                ? _approachFixDelay
                : (string.IsNullOrEmpty(aircraft.icaoAirline) ? _gaFixDelay : _teleportFixDelay);

            if ((DateTime.Now - aircraft.corrTime).TotalSeconds <= delay || speed <= 20 ||
                aircraft.onceFixAltitudeCallsign == aircraft.callsign)
            {
                return;
            }

            aircraft.corrTime = DateTime.Now;

            // Before the very first in-air correction of a departing aircraft, align it to the
            // runway heading once (the waypoint API otherwise rotates it on the spot).
            if (!aircraft.alignHeading && (aircraft.longitudeBefore > 0 || aircraft.latitudeBefore > 0))
            {
                PositionData alignPosition = new PositionData
                {
                    Latitude = aircraft.latitudeBefore,
                    Longitude = aircraft.longitudeBefore,
                    Altitude = aircraft.altimeterMeterBefore,
                    Heading = aircraft.StartHeading,
                    Pitch = 0,
                    Bank = 0,
                    Airspeed = (uint)speed,
                    OnGround = 0
                };
                _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneLocation, aircraft.objectId,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, alignPosition);
                aircraft.alignHeading = true;
                aircraft.DepartingHeadingCheck = true;
            }

            aircraft.waypoints.Add(new Waypoint()
            {
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altimeter,
                Speed = speed,
                IsGrounded = isGrounded
            });
            Log.Information("Updating a flying plane " + aircraft.tailNumber + " lat: " + latitude + " long: " +
                longitude + " request ID: " + aircraft.requestId + " speed: " + speed + " heading: " + heading +
                " objectId " + aircraft.objectId + " rule: " + aircraft.flightRule);
            _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneWaypoints, aircraft.objectId,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, aircraft.GetAirWaypoint(latitude, longitude, altimeter, speed));

            aircraft.lastSimLatitude = latitude;
            aircraft.lastSimLongitude = longitude;
        }

        // Drive a grounded aircraft smoothly. Normal taxi is done with an ON_GROUND waypoint
        // toward the new live point at a realistic taxi speed. We only teleport (re-sync) on the
        // first placement, right after touchdown, or when the AI has drifted too far to catch up.
        private void HandleGroundMovement(Aircraft aircraft, double latitude, double longitude, int heading,
            int altimeter, int speed)
        {
            bool justLanded = aircraft.wasAirborne && !aircraft.hasLandedThisFlight;

            double fromLat = aircraft.lastSimLatitude != 0 ? aircraft.lastSimLatitude : aircraft.latitude;
            double fromLon = aircraft.lastSimLongitude != 0 ? aircraft.lastSimLongitude : aircraft.longitude;
            double driftMeters = GeoUtil.DistanceMeters(fromLat, fromLon, latitude, longitude);

            // Drive at the real reported ground speed so the AI traverses each leg in roughly one
            // update interval (keeps pace -> no per-cycle teleport). Clamp only against bad data.
            bool stopped = speed < _groundStoppedKts;
            double taxiSpeed = stopped
                ? _groundSpeedMinKts
                : Math.Min(_taxiSpeedCeilingKts, Math.Max(_groundSpeedMinKts, speed));

            bool needResync = !aircraft.onceSetGround || justLanded || driftMeters > _groundResyncMeters;

            if (needResync)
            {
                // Plant firmly on the ground (correct OnGround flag) at the live point, then roll
                // forward along the current heading. A freshly landed aircraft gets a long rollout
                // so it decelerates down the runway instead of snapping into a taxi-in state.
                PositionData position = new PositionData
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    Altitude = 0,
                    Heading = heading,
                    Pitch = 0,
                    Bank = 0,
                    Airspeed = (uint)taxiSpeed,
                    OnGround = 1
                };
                Log.Information((justLanded ? "Landing/rollout " : "Re-syncing grounded ") + aircraft.tailNumber +
                    " lat: " + latitude + " long: " + longitude + " heading: " + heading + " objectId " +
                    aircraft.objectId + " rule: " + aircraft.flightRule);
                _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneLocation, aircraft.objectId,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, position);

                double rolloutDistance = justLanded ? _touchdownRolloutMeters : _taxiLeadMeters;
                GeoUtil.Project(latitude, longitude, heading, rolloutDistance, out double rolloutLat, out double rolloutLon);
                _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneWaypoints, aircraft.objectId,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, aircraft.GetGroundWaypoint(rolloutLat, rolloutLon, taxiSpeed));

                aircraft.onceSetGround = true;
                aircraft.hasLandedThisFlight = true;
            }
            else
            {
                // Smooth taxi: command the AI to drive along the ground via an ON_GROUND waypoint.
                // When moving, aim a short distance past the live point along the current heading so
                // the AI rolls through it (no stop-and-go); when essentially stopped, aim at the live
                // point itself so it can settle (holding short / parked).
                double targetLat = latitude, targetLon = longitude;
                if (!stopped)
                {
                    GeoUtil.Project(latitude, longitude, heading, _taxiLeadMeters,
                        out targetLat, out targetLon);
                }
                Log.Information("Taxiing " + aircraft.tailNumber + " to lat: " + targetLat + " long: " +
                    targetLon + " at " + taxiSpeed + " kts (drift " + (int)driftMeters + " m) objectId " +
                    aircraft.objectId);
                _simConnect.SetDataOnSimObject(SimConnectDataDefinition.PlaneWaypoints, aircraft.objectId,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, aircraft.GetGroundWaypoint(targetLat, targetLon, taxiSpeed));
            }

            aircraft.lastSimLatitude = latitude;
            aircraft.lastSimLongitude = longitude;
            aircraft.StartHeading = heading; // remember runway/taxi heading for a later departure
            aircraft.latitudeBefore = latitude;
            aircraft.longitudeBefore = longitude;
            aircraft.headingBefore = heading;
            aircraft.altimeterMeterBefore = 0;
            aircraft.wasAirborne = false;
            aircraft.isGrounded = true;
        }

        private void DespawnOldPlanes(List<string> flightradarIds)
        {
            List<Aircraft> removedPlanes = new List<Aircraft>();
            LiveTrafficAircraft.ForEach(plane =>
            {
                if (flightradarIds.Contains(plane.flightRadarId)) return;

                var requestId = DataRequests.AI_SPAWN + _requestCount;
                Log.Information(@"Deleting a plane " + plane.tailNumber + " request ID: " + _requestCount);
                if (plane.objectId != 0)
                {
                    _requestCount = (_requestCount + 1) % 10000;
                    _simConnect.AIRemoveObject(plane.objectId, requestId);
                }

                removedPlanes.Add(plane);
            });
            removedPlanes.ForEach(plane =>
            {
                LiveTrafficAircraft.Remove(plane);
            });
        }

        private void SpawnPlane(Aircraft aircraft)
        {
            var requestId = DataRequests.AI_SPAWN + _requestCount;
            aircraft.requestId = (10000 + _requestCount);
            Log.Information(@"Spawning a plane " + aircraft.tailNumber + " lat: " + aircraft.latitude + " long: " + aircraft.longitude + " request ID: " + aircraft.requestId);
            _requestCount = (_requestCount + 1) % 10000;

            if (aircraft.infoExclude == aircraft.callsign) return;

            // Experimental: create the aircraft under native ATC using a generated flight plan.
            if (aircraft.atcControlled && TrySpawnWithNativeAtc(aircraft, requestId)) return;

            // Default: client-driven non-ATC aircraft positioned from the live data.
            aircraft.atcControlled = false;
            var position = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = aircraft.latitude,
                Longitude = aircraft.longitude,
                Altitude = aircraft.altimeterMeter,
                Pitch = 0,
                Bank = 0,
                Heading = aircraft.heading,
                OnGround = (uint)(aircraft.isGrounded ? 1 : 0),
                Airspeed = (uint)(aircraft.speed-((aircraft.speed/100)*50))
            };
            _simConnect.AICreateNonATCAircraft(aircraft.matchedModel, aircraft.tailNumber, position, requestId);
        }

        // Creates an aircraft managed by MSFS native ATC from a generated flight plan. Returns
        // false (so the caller falls back to non-ATC) if the plan could not be written.
        private bool TrySpawnWithNativeAtc(Aircraft aircraft, DataRequests requestId)
        {
            string planPath = FlightPlanWriter.WriteDirectPlan(aircraft);
            if (string.IsNullOrEmpty(planPath)) return false;

            int flightNumber = 0;
            foreach (char c in aircraft.callsign ?? string.Empty)
            {
                if (char.IsDigit(c)) flightNumber = flightNumber * 10 + (c - '0');
            }

            Log.Information($"Spawning {aircraft.callsign} under native ATC with plan {planPath}");
            _simConnect.AICreateEnrouteATCAircraft(aircraft.matchedModel, aircraft.tailNumber, flightNumber,
                planPath, 0.0, false, requestId);
            return true;
        }
    }
}
