using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.Localization;
using static KSTS.Statics;

namespace KSTS
{
    // Helper-class to work with vessels and proto-vessels:
    class TargetVessel
    {
        public static string TranslateDockingPortName(string dockingPortType)
        {
            switch (dockingPortType)
            {
                case "size0": return "Clamp-O-Tron Jr.";
                case "size1": return "Clamp-O-Tron";
                case "size2": return "Clamp-O-Tron Sr.";
            }
            return dockingPortType;
        }

        // Checks if the given vessel is a valid target for a mission, optionally by comparing it also to the given mission-profile parameters:
        public static bool IsValidTarget(Vessel vessel, MissionProfile profile = null)
        {
            if (vessel.situation != Vessel.Situations.ORBITING) return false;
            if (vessel.orbit == null) return false;

            var dockingPortTypes = GetVesselDockingPortTypes(vessel);
            if (dockingPortTypes.Count == 0) return false; // We have to dock for a transport-mission.
            
            if (profile == null)
            {
                if (vessel.orbit.referenceBody != FlightGlobals.GetHomeBody()) return false; // Can only record orbits around the launch body if there's no profile
            } 
            else 
            {
                if (vessel.orbit.referenceBody.bodyName != profile.bodyName) return false; // Can only record orbits around the same body as the profile

                bool hasMatchingPort = false;
                foreach (string dockingPortType in dockingPortTypes)
                {
                    if (profile.dockingPortTypes.Contains(dockingPortType))
                    {
                        hasMatchingPort = true;
                        break;
                    }
                }
                if (!hasMatchingPort) return false;

                if (vessel.orbit.ApA > profile.maxAltitude) return false; // The target must have moved ...
            }
            return true;
        }

        // Returns whih types of docking-ports the given vessel (like "node0", "node1", etc):
        public static List<string> GetVesselDockingPortTypes(Vessel vessel)
        {
            var dockingPortTypes = new List<string>();
            try
            {
                // To get this function to work with vessels even if they are not loaded, we have to check then proto-parts, which more complicated:
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    if (!KSTS.partDictionary.ContainsKey(protoPart.partName)) continue;
                    var part = KSTS.partDictionary[protoPart.partName];
                    if (part.partPrefab == null) continue;
                    foreach (var dockingNode in part.partPrefab.FindModulesImplementing<ModuleDockingNode>())
                    {
                        if (!dockingPortTypes.Contains(dockingNode.nodeType)) dockingPortTypes.Add(dockingNode.nodeType);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("GetVesselDockingPortTypes(" + Localizer.Format(vessel.vesselName)+ "): " + e.ToString());
            }
            return dockingPortTypes;
        }

        // Returns a list of resources which the given vessel has available capacity to receive in an transport-mission:
        public static List<PayloadResource> GetFreeResourcesCapacities(Vessel vessel)
        {
            var availableResources = new List<PayloadResource>();
            try
            {
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    foreach (var protoResource in protoPart.resources)
                    {
                        // We manipulate resources of unloaded vessels in "AddResources" below, which does not update "protoResource.amount", so we have
                        // to read the config-node here:
                        var free = protoResource.maxAmount - protoResource.amount;
                        if (free < 0.01) continue; // Too small amounts would get shown as 0.00, which would be confusing, so we ignore them just like 0.
                        if (!KSTS.resourceDictionary.ContainsKey(protoResource.resourceName)) continue;
                        var resource = KSTS.resourceDictionary[protoResource.resourceName];
                        if (resource.density <= 0) continue;

                        var availableResource = availableResources.Find(x => x.name == protoResource.resourceName);
                        if (availableResource != null)
                        {
                            availableResource.amount += free;
                        }
                        else
                        {
                            availableResource = new PayloadResource();
                            availableResource.amount = free;
                            availableResource.mass = resource.density;
                            availableResource.name = protoResource.resourceName;
                            availableResources.Add(availableResource);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("GetFreeResourcesCapacities(" + Localizer.Format(vessel.vesselName) + "): " + e.ToString());
            }
            return availableResources;
        }

        // Returns the number of seats of the given vessel, even if it is not loaded:
        public static int GetCrewCapacity(Vessel vessel)
        {
            var capacity = 0;
            foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
            {
                AvailablePart part;
				if (!KSTS.partDictionary.TryGetValue(protoPart.partName, out part)) continue;
				
                int partCrewCapacity = part.partPrefab.CrewCapacity;
                int moduleIdx = 0;
                foreach (ProtoPartModuleSnapshot module in protoPart.modules)
                {
                    if (module.moduleName == "USIAnimation" && module.moduleValues.GetValue("isDeployed") == "True")
                    {
                        partCrewCapacity = part.partPrefab.Modules.GetModule(moduleIdx).Fields.GetValue<int>("CrewCapacity");
                    }
                    if ((module.moduleName == "ModuleDeployableCentrifuge" || module.moduleName == "ModuleDeployableHabitat") && module.moduleValues.GetValue("Deployed") == "True")
                    {
                        partCrewCapacity = part.partPrefab.Modules.GetModule(moduleIdx).Fields.GetValue<int>("DeployedCrewCapacity");
                    }
                    moduleIdx++;
                }
                
                capacity += partCrewCapacity;
            }
            return capacity;
        }

        private static int GetCrewCapacity(ProtoPartSnapshot protoPart)
        {
            AvailablePart part;
            if (!KSTS.partDictionary.TryGetValue(protoPart.partName, out part)) return 0;
            if (part.partPrefab == null) return 0;

            int partCrewCapacity = part.partPrefab.CrewCapacity;
            int moduleIdx = 0;
            foreach (ProtoPartModuleSnapshot module in protoPart.modules)
            {
                if (module.moduleName == "USIAnimation" && module.moduleValues.GetValue("isDeployed") == "True")
                {
                    partCrewCapacity = part.partPrefab.Modules.GetModule(moduleIdx).Fields.GetValue<int>("CrewCapacity");
                }
                if ((module.moduleName == "ModuleDeployableCentrifuge" || module.moduleName == "ModuleDeployableHabitat") && module.moduleValues.GetValue("Deployed") == "True")
                {
                    partCrewCapacity = part.partPrefab.Modules.GetModule(moduleIdx).Fields.GetValue<int>("DeployedCrewCapacity");
                }
                moduleIdx++;
            }

            return partCrewCapacity;
        }

        // Returns the vessel with the given ID, if it exists:
        public static Vessel GetVesselById(Guid vesselId)
        {
            return FlightGlobals.Vessels.Find(x => x.protoVessel.vesselID == vesselId);
        }

        // Returns all crew members from the given vessel. While the vessel-object does have it's own function for this
        // this function works with the parts itself, which we modify during transport-missions. This means that this
        // function is more accurate to work with until the vessel has been active and was re-packed by KSP.
        public static List<ProtoCrewMember> GetCrew(Vessel vessel)
        {
            var crew = new List<ProtoCrewMember>();
            foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
            {
                if (protoPart.protoModuleCrew == null) continue;
                foreach (var crewMember in protoPart.protoModuleCrew)
                {
                    crew.Add(crewMember);
                }
            }
            return crew;
        }

        // Returns the number of crew-members that have a given trait (eg "Pilot"):
        public static int GetCrewCountWithTrait(Vessel vessel, string trait)
        {
            var traitCount = 0;
            foreach (var crewMember in TargetVessel.GetCrew(vessel))
            {
                if (crewMember.trait == trait) traitCount++;
            }
            return traitCount;
        }

        // Returns the maximum extension (in meters) of the given vessel in any direction:
        public static float GetVesselSize(Vessel vessel)
        {
            // I don't know of any easier way to get a vessels actual size (apparently only ShipConstruct objects have a GetVesselSize method and
            // thus far I was unable to create a ShipConstruct from a Vessel), which is why we have to aproximate the vessels size by adding all
            // the heights of its individual parts. This only a very rough aproximation, but it should always be at least as high as the actual size:
            float partHeights = 0;
            try
            {
                foreach (var pps in vessel.protoVessel.protoPartSnapshots)
                {
                    if (KSTS.partDictionary.ContainsKey(pps.partName))
                    {
                        // I guess the iconScale is the length of the 1m indicator in the editor next to the icon of the part. This means the inverse will give us the
                        // the actual height of the part, hopefully no part will be wider than heigh (in this case I hope the part will be displayed rotated in the editor):
                        var partHeight = 1 / KSTS.partDictionary[pps.partName].iconScale;
                        partHeights += partHeight;
                    }
                }
                if (partHeights == 0) throw new Exception("height seems to be zero");
                return partHeights;
            }
            catch (Exception e)
            {
                Debug.LogError("TargetVessel.GetVesselSize(" + Localizer.Format(vessel.vesselName) + "): " + e.ToString());
                return 1000; // If we can't tell, lets make it 1km to be safe
            }
        }

        // Adds the given amount of resources to the (unloaded) ship provided:
        public static void AddResources(Vessel vessel, string resourceName, double amount)
        {
            // While it is possible to manipulate the resources on loaded vessels, our crew-transport missions
            // only work on unloaded ships and we would have to implement two different routines for this use-case,
            // so we only allow adding resources on unloaded ships:
            if (vessel.loaded) throw new Exception("TargetVessel.AddResources can only be called on unloaded vessels");
            try
            {
                var amountToAdd = amount;
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    if (amountToAdd <= 0) break;
                    foreach (var protoResource in protoPart.resources)
                    {
                        if (protoResource.resourceName != resourceName) continue;
                        var partAmount = protoResource.amount; 
                        var capacity = protoResource.maxAmount - partAmount;
                        if (capacity <= 0) continue;
                        if (capacity > amountToAdd)
                        {
                            if (capacity - amountToAdd < 0.01) amountToAdd = capacity; // Just to correct some irregularities with floats
                            protoResource.amount = partAmount + amountToAdd;
                            amountToAdd = 0;
                        }
                        else
                        {
                            protoResource.amount = partAmount + capacity;
                            amountToAdd -= capacity;
                        }
                    }
                }

                // Notyfy other mods about the modification of the vessel:
                GameEvents.onVesselWasModified.Fire(vessel);

                // Log Message about the transfer:
                Log.Warning("added " + (amount - amountToAdd).ToString() + " / " + amount.ToString() + " of " + resourceName + " to " + Localizer.Format(vessel.vesselName));
                ScreenMessages.PostScreenMessage(Localizer.Format(vessel.vesselName) + " received " + Math.Round(amount + amountToAdd).ToString() + " of " + resourceName);
            }
            catch (Exception e)
            {
                Debug.LogError("TargetVessel.AddResources("+ Localizer.Format(vessel.vesselName) + ","+resourceName+","+amount.ToString()+"): " + e.ToString());
            }
        }

        // Adds to given kerbal as a crew-member to the (unloaded) vessel:
        public static void AddCrewMember(Vessel vessel, string kerbonautName)
        {
            // We can only manipulate the crew of an unloaded ship:
            // if (vessel.loaded) throw new Exception("TargetVessel.AddCrewMember can only be called on unloaded vessels");
            try
            {
                // Find the requested Kerbal on the crew-roster:
                ProtoCrewMember kerbonaut = null;
                foreach (var rosterKerbonaut in GUICrewTransferSelector.GetCrewRoster())
                {
                    if (rosterKerbonaut.name == kerbonautName)
                    {
                        kerbonaut = rosterKerbonaut;
                        break;
                    }
                }
                if (kerbonaut == null)
                {
                    // The player must have removed the kerbal from the pool of available kerbonauts:
                    Log.Warning("unable to complete crew-transfer to " + Localizer.Format(vessel.vesselName) + ", kerbonaut " + kerbonautName + " unavailable or missiong");
                    ScreenMessages.PostScreenMessage("Crew-Transfer aborted: Kerbonaut " + kerbonautName + " unavailable for transfer to " + Localizer.Format(vessel.vesselName));
                    return;
                }

                // Find an available seat on the target-vessel:
                ProtoPartSnapshot targetPart = null;
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    var crewCapacity = GetCrewCapacity(protoPart);
                    if (crewCapacity <= 0) continue;
                    if (protoPart.protoCrewNames.Count >= crewCapacity) continue;
                    targetPart = protoPart;
                    break;
                }
                if (targetPart == null)
                {
                    // Maybe there was a different transport-mission to the same target-vessel:
                    Log.Warning("unable to complete crew-transfer to " + Localizer.Format(vessel.vesselName) + ", no free seats");
                    ScreenMessages.PostScreenMessage("Crew-Transfer aborted: Vessel " + Localizer.Format(vessel.vesselName) + " had no free seat for Kerbonaut " + kerbonautName);
                    return;
                }

                // Add the kerbonaut to the selected part, using the next available seat:
                var seatIdx = 0;
                bool seatSwitched;
                do
                {
                    seatSwitched = false;
                    foreach (var crewMember in targetPart.protoModuleCrew)
                    {
                        if (seatIdx == crewMember.seatIdx) { seatIdx++; seatSwitched = true; }
                    }
                }
                while (seatSwitched);
                targetPart.protoModuleCrew.Add(kerbonaut);
                targetPart.protoCrewNames.Add(kerbonautName);

                // Remove kerbonaut from crew-roster:
                kerbonaut.seatIdx = seatIdx;

                kerbonaut.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                // Add the phases the kerbonaut would have gone through during his launch to his flight-log:
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Launch, Planetarium.fetch.Home.bodyName);
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Flight, Planetarium.fetch.Home.bodyName);
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Suborbit, Planetarium.fetch.Home.bodyName);
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Orbit, Planetarium.fetch.Home.bodyName);

                // Notyfy other mods about the modification of the vessel's crew:
                GameEvents.onVesselCrewWasModified.Fire(vessel);

                Log.Warning("added kerbonaut " + kerbonautName + " to vessel " + Localizer.Format(vessel.vesselName));
                ScreenMessages.PostScreenMessage("Kerbonaut " + kerbonautName + " transfered to " + Localizer.Format(vessel.vesselName));
            }
            catch (Exception e)
            {
                Debug.LogError("TargetVessel.AddCrewMember(" + Localizer.Format(vessel.vesselName) + "," + kerbonautName + "): " + e.ToString());
            }
        }

        // Removes the given kerbonaut from the crew of the (unloaded) vessel and returns him to the crew-roster:
        public static void RecoverCrewMember(Vessel vessel, string kerbonautName)
        {
            // We can only manipulate the crew of an unloaded ship:
            if (vessel.loaded) throw new Exception("TargetVessel.AddCrewMember can only be called on unloaded vessels");
            try
            {
                // Find the part in which the kerbonaut is currently sitting:
                ProtoPartSnapshot sourcePart = null;
                ProtoCrewMember kerbonaut = null;
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    if (protoPart.protoCrewNames.Contains(kerbonautName))
                    {
                        sourcePart = protoPart;
                        kerbonaut = protoPart.protoModuleCrew.Find(x => x.name == kerbonautName);
                        break;
                    }
                }
                if (sourcePart == null || kerbonaut == null)
                {
                    // Maybe the plaayer has removed the kerbal from the vessel (eg EVA, docking, etc):
                    Log.Warning("unable to recover kerbonaut "+kerbonautName+" from vessel "+ Localizer.Format(vessel.vesselName) + ", kerbal not found on board");
                    ScreenMessages.PostScreenMessage("Crew-Transfer aborted: Kerbonaut " + kerbonautName + " not present on " + Localizer.Format(vessel.vesselName));
                    return;
                }

                // Remove the kerbal from the part:
                sourcePart.protoCrewNames.Remove(kerbonautName);
                sourcePart.protoModuleCrew.Remove(kerbonaut);

                // Add the kerbal back to the crew-roster:
                kerbonaut.rosterStatus = ProtoCrewMember.RosterStatus.Available;

                // Add the descent-phases to his flight log and archive his flight (commits the flight-current log to his career-log):
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Land, Planetarium.fetch.Home.bodyName);
                kerbonaut.flightLog.AddEntry(FlightLog.EntryType.Recover);
                kerbonaut.ArchiveFlightLog();

                // Notyfy other mods about the modification of the vessel's crew:
                GameEvents.onVesselCrewWasModified.Fire(vessel);

                Log.Warning("recovered kerbonaut " + kerbonautName + " from vessel " + Localizer.Format(vessel.vesselName));
                ScreenMessages.PostScreenMessage("Kerbonaut " + kerbonautName + " recovered from " + Localizer.Format(vessel.vesselName));
            }
            catch (Exception e)
            {
                Debug.LogError("TargetVessel.RecoverCrewMember(" + Localizer.Format(vessel.vesselName) + "," + kerbonautName + "): " + e.ToString());
            }
        }
    }
}
