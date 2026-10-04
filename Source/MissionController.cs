using System;
using System.Collections.Generic;
using UnityEngine;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using KSP.Localization;
using KSP.UI.Screens;
using static KSTS.Statics;
using KSTS_KACWrapper;


namespace KSTS
{
    // Actual running mission:
    public enum MissionType { DEPLOY = 1, TRANSPORT = 2, CONSTRUCT = 3 };
    public class Mission : Saveable
    {
        public MissionType missionType;
        public Orbit orbit = null;      // The orbit in which the new vesselo should get launched
        public string shipName = "";    // Name of the new vessel
        public double eta;              // Timestamp when this mission should end (checked in the timer-function).

        // We store the names of the profile and the template-file instead of their objects (as they are passed by the factory-method),
        // to make saving and loading these objects simpler. If we really need these objects, we can always look them up again.
        public string profileName = "";             // Name of the mission-profile (also its key)
        public string shipTemplateFilename = "";    // File of the saved ship's template

        public Guid? targetVesselId = null;        // The vessel referenced by a transport- or construction-mission
        public List<string> crewToDeliver = null;  // Names of kerbals to transport to the target-vessel
        public List<string> crewToCollect = null;  // Names of kerbals to bring back from the target-vessel
        public Dictionary<string, double> resourcesToDeliver = null; // ResourceName => ResourceAmount
        public string flagURL = null;              // The flag to be used for newly created vessels

        static bool inittedKAC = false;
        static bool KACavail = false;
        internal static void InitKAC()
        {
            if (!inittedKAC)
            {
                KACavail = KACWrapper.InitKACWrapper();
                inittedKAC = true;
            }
        }
        public MissionProfile GetProfile()
        {
            if (MissionController.missionProfiles.ContainsKey(profileName))
            {
                return MissionController.missionProfiles[profileName];
            }

            return null;
        }

        public ShipTemplate GetShipTemplate()
        {
            return GUI.shipTemplates.Find(x => SanitizePath(x.template.filename) == shipTemplateFilename)?.template;
        }

        public static string GetMissionTypeName(MissionType type)
        {
            if (type == MissionType.DEPLOY)
            {
                return "deployment";
            }

            if (type == MissionType.TRANSPORT)
            {
                return "transport";
            }

            if (type == MissionType.CONSTRUCT)
            {
                return "construction";
            }

            return "N/A";
        }

        // Helper function for re-formating paths (like from vessel-templates) for save storage in config-nodes:
        public static string SanitizePath(string path)
        {
            path = Regex.Replace(path, @"\\", "/"); // Only fools use backslashes in paths
            path = Regex.Replace(path, @"(/+|/\\./)", "/"); // Remove redundant elements
            return path;
        }

        public static Mission CreateDeployment(string shipName, ShipTemplate template, Orbit orbit, MissionProfile profile, List<string> crew, string flagURL)
        {
            var mission = new Mission
            {
                missionType = MissionType.DEPLOY,
                shipTemplateFilename = SanitizePath(template.filename),
                orbit = orbit,
                shipName = shipName,
                profileName = profile.profileName,
                eta = Planetarium.GetUniversalTime() + profile.missionDuration,
                crewToDeliver = crew,
                flagURL = flagURL,

            };
            Log.Info("CreateDeployment, current time: " + Planetarium.GetUniversalTime().ToString("F0") + ", eta: " + mission.eta.ToString("F0"));
           if (KACWrapper.APIReady && MissionController.useKACifAvailable)
            {
                Log.Info("Setting KAC Alarm");
                var KACalarmID = KACWrapper.KAC.CreateAlarm(
                                KACWrapper.KACAPI.AlarmTypeEnum.Raw,
                                "Deployment: " + shipName,
                                mission.eta
                            );
                var a = KACWrapper.KAC.Alarms.FirstOrDefault(z => z.ID == KACalarmID);
                if (a != null)
                {
                    a.AlarmAction = KACWrapper.KACAPI.AlarmActionEnum.KillWarp;
                    a.AlarmMargin = 0;
                    //a.VesselID = FlightGlobals.ActiveVessel.id.ToString();
                    a.Notes = "Vessel deployment of " + shipName + " by Kerbal Space Transport System";
                }
            }
            if (MissionController.useStockAlarmClock)
            {
                Log.Info("Setting stock alarm");
                AlarmTypeRaw alarmToSet = new AlarmTypeRaw
                {
                    title = "KSTS Vessel Deployment",
                    description = "Vessel deployment of " + shipName + " by Kerbal Space Transport System",
                    actions =
                            {
                                warp = AlarmActions.WarpEnum.KillWarp,
                                message = AlarmActions.MessageEnum.Yes
                            },
                    ut = mission.eta
                };
                AlarmClockScenario.AddAlarm(alarmToSet);
            }
            // The filename contains silly portions like "KSP_x64_Data/..//saves", which break savegames because "//" starts a comment in the savegame ...
            // The crew we want the new vessel to start with.

            return mission;
        }

        public static Mission CreateTransport(Vessel target, MissionProfile profile, List<PayloadResource> resources, List<CrewTransferOrder> crewTransfers)
        {
            var mission = new Mission
            {
                missionType = MissionType.TRANSPORT,
                profileName = profile.profileName,
                eta = Planetarium.GetUniversalTime() + profile.missionDuration,
                targetVesselId = target.protoVessel.vesselID
            };

            string transport = "";
            if (resources != null)
            {
                mission.resourcesToDeliver = new Dictionary<string, double>();
                foreach (var resource in resources)
                {
                    if (resource.amount > 0)
                    {
                        mission.resourcesToDeliver.Add(resource.name, resource.amount);
                        if (transport == "")
                            transport = resource.name;
                        else
                            transport += ", " + resource.name;
                    }
                }
            }
            if (crewTransfers != null)
            {
                foreach (var crewTransfer in crewTransfers)
                {
                    switch (crewTransfer.direction)
                    {
                        case CrewTransferOrder.CrewTransferDirection.DELIVER:
                            if (mission.crewToDeliver == null)
                            {
                                mission.crewToDeliver = new List<string>();
                            }

                            mission.crewToDeliver.Add(crewTransfer.kerbalName);

                            if (transport == "")
                                transport = crewTransfer.kerbalName;
                            else
                                transport += ", " + crewTransfer.kerbalName;

                            break;
                        case CrewTransferOrder.CrewTransferDirection.COLLECT:

                            if (mission.crewToCollect == null)
                            {
                                mission.crewToCollect = new List<string>();
                            }

                            mission.crewToCollect.Add(crewTransfer.kerbalName);

                            //mission.crewToDeliver.Add(crewTransfer.kerbalName);

                            if (transport == "")
                                transport = crewTransfer.kerbalName;
                            else
                                transport += ", " + crewTransfer.kerbalName;

                            break;
                        default:
                            throw new Exception("unknown transfer-direction: '" + crewTransfer.direction.ToString() + "'");
                    }
                }
            }

            if (KACWrapper.APIReady && MissionController.useKACifAvailable)
            {
                var KACalarmID = KACWrapper.KAC.CreateAlarm(
                                KACWrapper.KACAPI.AlarmTypeEnum.Raw,
                                "Transport",
                                mission.eta
                            );
                var a = KACWrapper.KAC.Alarms.FirstOrDefault(z => z.ID == KACalarmID);
                if (a != null)
                {
                    a.AlarmAction = KACWrapper.KACAPI.AlarmActionEnum.KillWarp;
                    a.AlarmMargin = 0;
                    //a.VesselID = FlightGlobals.ActiveVessel.id.ToString();
                    a.Notes = "Transport " + transport +" to " + target.protoVessel.GetDisplayName() + " by Kerbal Space Transport System";
                }
            }
            if (MissionController.useStockAlarmClock)
            {
                AlarmTypeRaw alarmToSet = new AlarmTypeRaw
                {
                    title = "KSTS Transport",
                    description = "Transport " + transport + " to " + target.protoVessel.GetDisplayName() + " by Kerbal Space Transport System",
                    actions =
                            {
                                warp = AlarmActions.WarpEnum.KillWarp,
                                message = AlarmActions.MessageEnum.Yes
                            },
                    ut = mission.eta
                };
                AlarmClockScenario.AddAlarm(alarmToSet);
            }

            return mission;
        }

        public static Mission CreateConstruction(string shipName, ShipTemplate template, Vessel spaceDock, MissionProfile profile, List<string> crew, string flagURL, double constructionTime)
        {
            var mission = new Mission
            {
                missionType = MissionType.CONSTRUCT,
                shipTemplateFilename = SanitizePath(template.filename),
                targetVesselId = spaceDock.protoVessel.vesselID,
                shipName = shipName,
                profileName = profile.profileName,
                eta = Planetarium.GetUniversalTime() + constructionTime,
                crewToDeliver = crew,
                flagURL = flagURL
            };
            // The crew we want the new vessel to start with.

            if (KACWrapper.APIReady && MissionController.useKACifAvailable)
            {
                var KACalarmID = KACWrapper.KAC.CreateAlarm(
                                KACWrapper.KACAPI.AlarmTypeEnum.Raw,
                                "Construction: " + shipName,
                                mission.eta
                            );
                var a = KACWrapper.KAC.Alarms.FirstOrDefault(z => z.ID == KACalarmID);
                if (a != null)
                {
                    a.AlarmAction = KACWrapper.KACAPI.AlarmActionEnum.KillWarp;
                    a.AlarmMargin = 0;
                    //a.VesselID = FlightGlobals.ActiveVessel.id.ToString();
                    a.Notes = "Construction of " + shipName + " by Kerbal Space Transport System";
                }
            }
            if (MissionController.useStockAlarmClock)
            {
                AlarmTypeRaw alarmToSet = new AlarmTypeRaw
                {
                    title = "KSTS Construction",
                    description = "Construction of " + shipName + " by Kerbal Space Transport System",
                    actions =
                            {
                                warp = AlarmActions.WarpEnum.KillWarp,
                                message = AlarmActions.MessageEnum.Yes
                            },
                    ut = mission.eta
                };
                AlarmClockScenario.AddAlarm(alarmToSet);
            }

            return mission;
        }

        public static Mission CreateFromConfigNode(ConfigNode node)
        {
            var mission = new Mission();
            return (Mission)CreateFromConfigNode(node, mission);
        }

        // Tries to execute this mission and returns true if it was successfull:
        public bool TryExecute()
        {
            switch (missionType)
            {
                case MissionType.DEPLOY:
                    // Ship-Creation is only possible while not in flight with the current implementation:
                    if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                    {
                        CreateShip();
                        return true;
                    }
                    return false;

                case MissionType.CONSTRUCT:
                    if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                    {
                        Vessel targetVessel = null;
                        if (targetVesselId == null || (targetVessel = TargetVessel.GetVesselById((Guid)targetVesselId)) == null || !TargetVessel.IsValidTarget(targetVessel, MissionController.missionProfiles[profileName]))
                        {
                            // Abort mission (maybe the vessel was removed or got moved out of range):
                            Log.Warning("aborting transport-construction: target-vessel missing or out of range");
                            ScreenMessages.PostScreenMessage("Aborting construction-mission: Target-vessel not found at expected rendezvous-coordinates!");
                        }
                        else
                        {
                            CreateShip();
                            return true;
                        }
                    }
                    return false;

                case MissionType.TRANSPORT:
                    // Our functions for manipulating ships don't work on active vessels, beeing in flight however should be fine:
                    if (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.id != targetVesselId)
                    {
                        Vessel targetVessel = null;
                        if (!MissionController.missionProfiles.ContainsKey(profileName))
                        {
                            throw new Exception("unable to execute transport-mission, profile '" + profileName + "' missing");
                        }

                        if (targetVesselId == null || (targetVessel = TargetVessel.GetVesselById((Guid)targetVesselId)) == null || !TargetVessel.IsValidTarget(targetVessel, MissionController.missionProfiles[profileName]))
                        {
                            // Abort mission (maybe the vessel was removed or got moved out of range):
                            Log.Warning("aborting transport-mission: target-vessel missing or out of range");
                            ScreenMessages.PostScreenMessage("Aborting transport-mission: Target-vessel not found at expected rendezvous-coordinates!");
                        }
                        else
                        {
                            // Do the actual transport-mission:
                            if (resourcesToDeliver != null)
                            {
                                foreach (var item in resourcesToDeliver)
                                {
                                    TargetVessel.AddResources(targetVessel, item.Key, item.Value);
                                }
                            }
                            if (crewToCollect != null)
                            {
                                foreach (var kerbonautName in crewToCollect)
                                {
                                    TargetVessel.RecoverCrewMember(targetVessel, kerbonautName);
                                }
                            }
                            if (crewToDeliver != null)
                            {
                                foreach (var kerbonautName in crewToDeliver)
                                {
                                    TargetVessel.AddCrewMember(targetVessel, kerbonautName);
                                }
                            }
                            if (targetVessel != null)
                            {
                                // Force Kerbalism to recalculate background life support using new resource & crew levels
                                GameEvents.onVesselWasModified.Fire(targetVessel);

                                // Refresh the tracking engine to register any newly boarded/departed Kerbals
                                GameEvents.onVesselCreate.Fire(targetVessel);
                            }
                        }
                        return true;
                    }
                    return false;

                default:
                    throw new Exception("unexpected mission-type '" + missionType.ToString() + "'");
            }
        }

        // Helper function for building an ordered list of parts which are attached to the given root-part. The resulting
        // List is passed by reference and also returend.
        private List<Part> FindAndAddAttachedParts(Part p, ref List<Part> list)
        {
            if (list == null)
            {
                list = new List<Part>();
            }

            if (list.Contains(p))
            {
                return list;
            }

            list.Add(p);
            foreach (var an in p.attachNodes)
            {
                if (an.attachedPart == null || list.Contains(an.attachedPart))
                {
                    continue;
                }

                FindAndAddAttachedParts(an.attachedPart, ref list);
            }
            return list;
        }

        public static IEnumerable<ProtoCrewMember> CrewRoster()
        {
            var crew = HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available);
            var tourists = HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Tourist, ProtoCrewMember.RosterStatus.Available);
            var roster = crew.Concat(tourists);
            return roster;
        }

        public static Vessel AssembleForLaunchUnlanded(ShipConstruct ship, IEnumerable<string> crewToDeliver, double duration, Orbit orbit,
                                                       string flagUrl, Game sceneState)
        {
            var localRoot = ship.parts[0].localRoot;
            var vessel = localRoot.gameObject.GetComponent<Vessel>();
            if (vessel == null)
            {
                vessel = localRoot.gameObject.AddComponent<Vessel>();
            }

            vessel.id = Guid.NewGuid();
            vessel.vesselName = Localizer.Format(ship.shipName);
            vessel.persistentId = ship.persistentId;
            vessel.Initialize();
            if (orbit != null)
            {
                var orbitDriver = vessel.gameObject.GetComponent<OrbitDriver>();
                if (orbitDriver == null)
                {
                    orbitDriver = vessel.gameObject.AddComponent<OrbitDriver>();
                    vessel.orbitDriver = orbitDriver;
                }
            }
            vessel.Landed = false;
            vessel.Splashed = false;
            vessel.skipGroundPositioning = true;
            vessel.vesselSpawning = false;
            // vessel.loaded = false;

            var pCrewMembers = CrewRoster().Where(k => crewToDeliver.Contains(k?.name)).ToList();
            // Maybe add the initial crew to the vessel:
            if (pCrewMembers.Any())
            {
                CrewTransferBatch.moveCrew(vessel, pCrewMembers, false);
                LifeSupportWrapper.Instance.PrepForLaunch(vessel, pCrewMembers, duration);
                pCrewMembers.ForEach(pcm =>
                {
                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                // Add the phases the kerbonaut would have gone through during his launch to his flight-log:
                var homeBody = Planetarium.fetch.Home;
                    pcm.flightLog.AddEntry(FlightLog.EntryType.Launch, homeBody.bodyName);
                    pcm.flightLog.AddEntry(FlightLog.EntryType.Flight, homeBody.bodyName);
                    pcm.flightLog.AddEntry(FlightLog.EntryType.Suborbit, homeBody.bodyName);
                    pcm.flightLog.AddEntry(FlightLog.EntryType.Orbit, homeBody.bodyName);
                    if (orbit.referenceBody != homeBody)
                    {
                        pcm.flightLog.AddEntry(FlightLog.EntryType.Escape, homeBody.bodyName);
                        pcm.flightLog.AddEntry(FlightLog.EntryType.Orbit, orbit.referenceBody.bodyName);
                    }
                });
            }

            // TODO this seems like overkill so commenting out, we'll see...
            vessel.orbitDriver.UpdateOrbit();
            vessel.SetOrbit(orbit);
            vessel.orbitDriver.UpdateOrbit();
            var hashCode = (uint)Guid.NewGuid().GetHashCode();
            var launchId = HighLogic.CurrentGame.launchID++;
            foreach (var part in vessel.parts)
            {
                part.flightID = ShipConstruction.GetUniqueFlightID(sceneState.flightState);
                part.missionID = hashCode;
                part.launchID = launchId;
                part.flagURL = flagUrl;
            }
            if (localRoot.isControlSource == Vessel.ControlLevel.NONE)
            {
                var firstCrewablePart = ShipConstruction.findFirstCrewablePart(ship.parts[0]);
                if (firstCrewablePart == null)
                {
                    var firstControlSource = ShipConstruction.findFirstControlSource(vessel);
                    firstCrewablePart = firstControlSource ?? localRoot;
                }
                vessel.SetReferenceTransform(firstCrewablePart, true);
            }
            else
            {
                vessel.SetReferenceTransform(localRoot, true);
            }

            Log.Warning("Vessel assembled for launch: " + Localizer.Format(vessel.vesselName));
            return vessel;
        }

        // Creates a new ship with the given parameters for this mission. The code however seems unnecessarily convoluted and
        // error-prone, but there are no better examples available on the internet.
        private void CreateShip()
        {
            try
            {
                // The ShipConstruct-object can only savely exist while not in flight, otherwise it will spam Null-Pointer Exceptions every tick:
                if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                {
                    throw new Exception("unable to run CreateShip while in flight");
                }

                // Load the parts form the saved vessel:
                if (!File.Exists(shipTemplateFilename))
                {
                    throw new Exception("file '" + shipTemplateFilename + "' not found");
                }

                var shipConstruct = ShipConstruction.LoadShip(shipTemplateFilename);

                // Maybe adjust the orbit:
                var vesselHeight = Math.Max(Math.Max(shipConstruct.shipSize.x, shipConstruct.shipSize.y), shipConstruct.shipSize.z);
                if (missionType == MissionType.DEPLOY)
                {
                    // Make sure that there won't be any collisions, when the vessel is created at the given orbit:
                    orbit = GUIOrbitEditor.ApplySafetyDistance(orbit, vesselHeight);
                }
                else if (missionType == MissionType.CONSTRUCT)
                {
                    // Deploy the new ship next to the space-dock:
                    var spaceDock = TargetVessel.GetVesselById((Guid)targetVesselId);
                    orbit = GUIOrbitEditor.CreateFollowingOrbit(spaceDock.orbit, TargetVessel.GetVesselSize(spaceDock) + vesselHeight);
                    orbit = GUIOrbitEditor.ApplySafetyDistance(orbit, vesselHeight);
                }
                else
                {
                    throw new Exception("invalid mission-type '" + missionType + "'");
                }

                var game = FlightDriver.FlightStateCache ?? HighLogic.CurrentGame;
                var profile = GetProfile();
                var duration = profile.missionDuration;
                AssembleForLaunchUnlanded(shipConstruct, crewToDeliver ?? Enumerable.Empty<string>(), duration, orbit, flagURL, game);
                var newVessel = FlightGlobals.Vessels[FlightGlobals.Vessels.Count - 1];
                newVessel.vesselName = shipName;
                TrackedVessels.Add(newVessel.id);
                if (!_addedEvent)
                {
                    GameEvents.onVesselLoaded.Add(CheckStaging);
                    _addedEvent = true;
                }
                Log.Warning("deployed new ship '" + shipName + "' as '" + newVessel.protoVessel.vesselRef.id + "'");
                ScreenMessages.PostScreenMessage("Vessel '" + shipName + "' deployed"); // Popup message to notify the player

                // Notify other mods about the new vessel:
                GameEvents.onVesselCreate.Fire(newVessel);
            }
            catch (Exception e)
            {
                Debug.LogError("Mission.CreateShip(): " + e);
            }
        }

        private static readonly HashSet<Guid> TrackedVessels = new HashSet<Guid>();

        private static bool _addedEvent;

        public void CheckStaging(Vessel vessel)
        {
            if (!TrackedVessels.Contains(vessel.id)) return;
            StageManager.BeginFlight();
            TrackedVessels.Remove(vessel.id);
        }

        // Generates a description for displaying on the GUI:
        public string GetDescription()
        {
            var description = "<color=#F9FA86><b>" + profileName + "</b></color> <color=#FFFFFF>(" + GetMissionTypeName(missionType) + ")\n";

            var shipTemplate = GetShipTemplate();
            if (shipTemplate != null)
            {
                description += "<b>Ship:</b> " + shipName + " (" + shipTemplate.shipName.ToString() + ")\n";
            }

            if (orbit != null)
            {
                description += "<b>Orbit:</b> " + orbit.referenceBody.bodyName.ToString() + " @ " + GUI.FormatAltitude(orbit.semiMajorAxis - orbit.referenceBody.Radius) + "\n";
            }

            // Display the targeted vessel (transport- and construction-missions):
            Vessel targetVessel = null;
            if (targetVesselId != null && (targetVessel = TargetVessel.GetVesselById((Guid)targetVesselId)) != null)
            {
                description += "<b>Target:</b> " + Localizer.Format(targetVessel.vesselName) + " @ " + GUI.FormatAltitude(targetVessel.altitude) + "\n";
            }

            // Display the total weight of the payload we are hauling (transport-missions):
            if (resourcesToDeliver != null)
            {
                double totalMass = 0;
                foreach (var item in resourcesToDeliver)
                {
                    if (!KSTS.resourceDictionary.ContainsKey(item.Key))
                    {
                        continue;
                    }

                    totalMass += KSTS.resourceDictionary[item.Key].density * item.Value;
                }
                description += "<b>Cargo:</b> " + totalMass.ToString("#,##0.00t") + "\n";
            }

            // Display the crew-members we are transporting and collection:
            if (crewToDeliver != null && crewToDeliver.Count > 0)
            {
                description += "<b>Crew-Transfer (Outbound):</b> " + String.Join(", ", crewToDeliver.ToArray()).Replace(" Kerman", "") + "\n";
            }
            if (crewToCollect != null && crewToCollect.Count > 0)
            {
                description += "<b>Crew-Transfer (Inbound):</b> " + String.Join(", ", crewToCollect.ToArray()).Replace(" Kerman", "") + "\n";
            }

            // Display the remaining time:
            var remainingTime = eta - Planetarium.GetUniversalTime();
            if (remainingTime < 0)
            {
                remainingTime = 0;
            }

            var etaColorComponent = 0xFF;
            if (remainingTime <= 300)
            {
                etaColorComponent = (int)Math.Round((0xFF / 300.0) * remainingTime); // Starting at 5 minutes, start turning the ETA green.
            }

            var etaColor = "#" + etaColorComponent.ToString("X2") + "FF" + etaColorComponent.ToString("X2");
            description += "<color=" + etaColor + "><b>ETA:</b> " + GUI.FormatDuration(remainingTime) + "</color>";

            description += "</color>";
            return description;
        }
    }

    // Recorded mission-profile for a flight:
    public enum MissionProfileType { DEPLOY = 1, TRANSPORT = 2 };
    public class MissionProfile : Saveable
    {
        public string profileName = "";
        public string vesselName = "";
        public MissionProfileType missionType;
        public double launchCost = 0;
        public double launchMass = 0;
        public double payloadMass = 0;
        public double minAltitude = 0;
        public double maxAltitude = 0;
        public string bodyName = "";
        public double missionDuration = 0;
        public bool oneWayMission = true;
        public int crewCapacity = 0;
        public List<string> dockingPortTypes = null;

        public static string GetMissionProfileTypeName(MissionProfileType type)
        {
            if (type == MissionProfileType.DEPLOY)
            {
                return "deployment";
            }

            if (type == MissionProfileType.TRANSPORT)
            {
                return "transport";
            }

            return "N/A";
        }

        public static MissionProfile CreateFromConfigNode(ConfigNode node)
        {
            var missionProfile = new MissionProfile();
            return (MissionProfile)CreateFromConfigNode(node, missionProfile);
        }

        public static MissionProfile CreateFromRecording(Vessel vessel, FlightRecording recording)
        {
            var profile = new MissionProfile();

            profile.profileName = recording.profileName;
            profile.vesselName = Localizer.Format(vessel.vesselName);
            profile.missionType = recording.missionType;
            profile.launchCost = recording.launchCost;
            profile.launchMass = recording.launchMass - recording.payloadMass;
            profile.payloadMass = recording.payloadMass;
            profile.minAltitude = recording.minAltitude;
            profile.maxAltitude = recording.maxAltitude;
            profile.bodyName = recording.launchBodyName;
            profile.missionDuration = recording.deploymentTime - recording.startTime;
            profile.crewCapacity = vessel.GetCrewCapacity() - vessel.GetCrewCount(); // Capacity at the end of the mission, so we can use it for oneway- as well als return-trips.
            profile.dockingPortTypes = recording.dockingPortTypes;

            if (vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED)
            {
                profile.oneWayMission = false;
                profile.launchCost -= recording.GetCurrentVesselValue();
                if (profile.launchCost < 0)
                {
                    profile.launchCost = 0; // Shouldn't happen
                }
            }
            else
            {
                profile.oneWayMission = true;
            }

            return profile;
        }
    }

    class MissionController
    {
        public static SortedList<string, MissionProfile> missionProfiles = null;
        public static List<Mission> missions = null;

        public static bool useKACifAvailable = true;
        public static bool useStockAlarmClock = true;


        public static void Initialize()
        {
            if (MissionController.missionProfiles == null)
            {
                Log.Warning("MissionController.Initialize");
                MissionController.missionProfiles = new SortedList<string, MissionProfile>();
            }

            if (MissionController.missions == null)
            {
                MissionController.missions = new List<Mission>();
            }
        }

        static string[] postfixes = { "Alpha", "Beta", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda", "Omega" };

        private static string GetUniqueProfileName(string name)
        {
            name = name.Trim();
            if (name == "")
            {
                name = "KSTS";
            }

            var postfixNumber = 0;
            var uniqueName = name;
            var lowercase = name.ToLower() == name; // If the name is in all lowercase, we don't want to break it by adding uppercase letters
            while (MissionController.missionProfiles.ContainsKey(uniqueName))
            {
                uniqueName = name + " ";
                if (postfixNumber >= postfixes.Length)
                {
                    uniqueName += postfixNumber.ToString();
                }
                else
                {
                    uniqueName += postfixes[postfixNumber];
                }

                if (lowercase)
                {
                    uniqueName = uniqueName.ToLower();
                }

                postfixNumber++;
            }
            return uniqueName;
        }

        public static void CreateMissionProfile(Vessel vessel, FlightRecording recording)
        {
            var profile = MissionProfile.CreateFromRecording(vessel, recording);

            // Make the profile-name unique to use it as a key:
            profile.profileName = MissionController.GetUniqueProfileName(profile.profileName);

            MissionController.missionProfiles.Add(profile.profileName, profile);
            Log.Warning("saved new mission profile '" + profile.profileName + "'" + "   Total of " + MissionController.missionProfiles.Count + " missions saved");
        }

        public static void DeleteMissionProfile(string name)
        {
            // Abort all running missions of this profile:
            var cancelledMission = missions.RemoveAll(x => x.profileName == name);
            if (cancelledMission > 0)
            {
                Log.Warning("cancelled " + cancelledMission.ToString() + " missions due to profile-deletion");
                ScreenMessages.PostScreenMessage("Cancelled " + cancelledMission.ToString() + " missions!");
            }

            // Remove the profile:
            if (MissionController.missionProfiles.ContainsKey(name))
            {
                Log.Warning("MissionController.DeleteMissionProfile");
                MissionController.missionProfiles.Remove(name);
            }
        }

        public static void ChangeMissionProfileName(string name, string newName)
        {
            MissionProfile profile = null;
            if (!MissionController.missionProfiles.TryGetValue(name, out profile))
            {
                return;
            }

            Log.Warning("MissionController.ChangeMissionProfileName");
            MissionController.missionProfiles.Remove(name);
            profile.profileName = MissionController.GetUniqueProfileName(newName);
            MissionController.missionProfiles.Add(profile.profileName, profile);
            Log.Warning("MissionController.ChangeMissionProfileName");
        }

        public static void LoadMissions(ConfigNode node)
        {
            MissionController.missionProfiles.Clear();
            var missionProfilesNode = node.GetNode("MissionProfiles");
            if (missionProfilesNode != null)
            {
                foreach (var missionProfileNode in missionProfilesNode.GetNodes())
                {
                    var missionProfile = MissionProfile.CreateFromConfigNode(missionProfileNode);
                    MissionController.missionProfiles.Add(missionProfile.profileName, missionProfile);
                    Log.Warning("MissionController.LoadMissions");
                }
            }

            MissionController.missions.Clear();
            var missionsNode = node.GetNode("Missions");
            if (missionsNode != null)
            {
                foreach (var missionNode in missionsNode.GetNodes())
                {
                    MissionController.missions.Add(Mission.CreateFromConfigNode(missionNode));
                }
            }
        }

        public static void SaveMissions(ConfigNode node)
        {
            var missionProfilesNode = node.AddNode("MissionProfiles");
            foreach (var item in MissionController.missionProfiles)
            {
                missionProfilesNode.AddNode(item.Value.CreateConfigNode("MissionProfile"));
            }

            var missionsNode = node.AddNode("Missions");
            foreach (var mission in MissionController.missions)
            {
                missionsNode.AddNode(mission.CreateConfigNode("Mission"));
            }
        }

        public static void StartMission(Mission mission)
        {
            MissionController.missions.Add(mission);
        }

        // Returns the mission (if any), the given kerbal is assigned to:
        public static Mission GetKerbonautsMission(string kerbonautName)
        {
            foreach (var mission in missions)
            {
                if (mission.crewToDeliver != null && mission.crewToDeliver.Contains(kerbonautName))
                {
                    return mission;
                }

                if (mission.crewToCollect != null && mission.crewToCollect.Contains(kerbonautName))
                {
                    return mission;
                }
            }
            return null;
        }

        // Is called every second and handles the running missions:
        public static void Timer()
        {
            try
            {
                var now = Planetarium.GetUniversalTime();
                var toExecute = new List<Mission>();
                foreach (var mission in missions)
                {
                    if (mission.eta <= now)
                    {
                        toExecute.Add(mission);
                    }
                }
                foreach (var mission in toExecute)
                {
                    try
                    {
                        if (mission.TryExecute())
                        {
                            missions.Remove(mission);
                        }
                    }
                    catch (Exception e)
                    {
                        // This is serious, but to avoid calling "execute" on every timer-tick, we better remove this mission:
                        Debug.LogError("FlightRecoorder.Timer().TryExecute(): " + e.ToString());
                        Debug.LogError("cancelling broken mission");
                        missions.Remove(mission);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("FlightRecoorder.Timer(): " + e.ToString());
            }
        }
    }
}