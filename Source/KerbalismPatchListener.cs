using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace KSTS
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KerbalismPatchListener : MonoBehaviour
    {
        private static bool isPatched = false;
        private static HashSet<Guid> injectedVessels = new HashSet<Guid>();

        public void Start()
        {
            if (isPatched) return;

            try
            {
                var harmony = new Harmony("com.ksts.kerbalism.patchlistener");

                var kerbalismAssembly = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name.Equals("Kerbalism", StringComparison.OrdinalIgnoreCase));

                if (kerbalismAssembly == null) return;

                // 1. SHIELD A: KERBALISM.DB.GetVesselData (Spawning NRE Protection)
                var dbType = kerbalismAssembly.assembly.GetType("KERBALISM.DB");
                if (dbType != null)
                {
                    var getVesselDataMethod = dbType.GetMethod("GetVesselData", BindingFlags.Public | BindingFlags.Static);
                    if (getVesselDataMethod != null)
                    {
                        var dbPrefix = typeof(KerbalismPatchListener).GetMethod(nameof(GetVesselDataPrefix), BindingFlags.Static | BindingFlags.Public);
                        harmony.Patch(getVesselDataMethod, prefix: new HarmonyMethod(dbPrefix));
                    }
                }

                // 2. SHIELD B: ModuleDockingNode.OnStart (Spawning Docking Hook Protection)
                var dockingNodeType = typeof(ModuleDockingNode);
                var dockingOnStartMethod = dockingNodeType.GetMethod("OnStart", BindingFlags.Public | BindingFlags.Instance);
                if (dockingOnStartMethod != null)
                {
                    var dockingPrefix = typeof(KerbalismPatchListener).GetMethod(nameof(DockingNodeOnStartPrefix), BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(dockingOnStartMethod, prefix: new HarmonyMethod(dockingPrefix));
                }

                // Hook game engine structural cleanup events to track spawning lifecycles
                GameEvents.onVesselCreate.Add(OnVesselCreatedWipeCleanup);
                GameEvents.onVesselLoaded.Add(OnVesselLoadedWipeCleanup);

                isPatched = true;
                DontDestroyOnLoad(this);
                Debug.Log("[KSTS-Kerbalism Patch] Core Launch & Spawning Protections Enabled Successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSTS-Kerbalism Patch] Failed to initialize hooks: " + ex.ToString());
            }
        }

        // --- PUBLIC INVOCATION EXTENSION (Cleaned up and made completely safe) ---
        // You can leave the call inside MissionController.cs if you wish, or remove it.
        // It acts as a safe, explicit event fire that ensures standard engine registration loops parse normally.
        public static void SyncBackgroundVessel(Vessel targetVessel)
        {
            if (targetVessel == null || targetVessel.loaded) return;

            // Explicitly broadcast standard KSP layout update notifications to background caches.
            // KSP will use this to update its internal structural logs right before scene shifts.
            GameEvents.onVesselWasModified.Fire(targetVessel);
        }

        // --- LIFECYCLE HOOKS (Prevents Save File Corruption & Freezes) ---
        private void OnVesselCreatedWipeCleanup(Vessel vessel) { ClearInjectedMockProfile(vessel, "onVesselCreate"); }
        private void OnVesselLoadedWipeCleanup(Vessel vessel) { ClearInjectedMockProfile(vessel, "onVesselLoaded"); }

        private static void ClearInjectedMockProfile(Vessel vessel, string trigger)
        {
            if (vessel == null || !injectedVessels.Contains(vessel.id)) return;

            try
            {
                var kerbalismAssembly = AssemblyLoader.loadedAssemblies.FirstOrDefault(a => a.name == "Kerbalism");
                var dbType = kerbalismAssembly?.assembly.GetType("KERBALISM.DB");

                if (dbType != null)
                {
                    var vesselsField = dbType.GetField("vessels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (vesselsField != null)
                    {
                        var vesselsDict = vesselsField.GetValue(null) as System.Collections.IDictionary;
                        if (vesselsDict != null && vesselsDict.Contains(vessel.id))
                        {
                            vesselsDict.Remove(vessel.id);
                            injectedVessels.Remove(vessel.id);
                            Debug.Log($"[KSTS-Kerbalism Patch] Eviction Complete ({trigger})! Safely removed temporary shell for {vessel.vesselName}.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSTS-Kerbalism Patch] Failed cleaning up fake profile wrapper: " + ex.Message);
            }
        }

        // --- SHIELD PREFIX INTERCEPTORS ---
        public static bool GetVesselDataPrefix(Vessel vessel, ref object __result)
        {
            if (vessel == null) return true;
            return EnsureAndInjectVesselProfile(vessel, ref __result, "DB Query");
        }

        public static bool DockingNodeOnStartPrefix(ModuleDockingNode __instance)
        {
            // Bypasses Kerbalism's unshielded background docking node scanning patches during the initial background instantiation pass.
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                try
                {
                    if (__instance != null)
                    {
                        string vName = __instance.vessel != null ? __instance.vessel.vesselName : "Unknown Background Vessel";
                        Debug.Log($"[KSTS-Kerbalism Patch] Bypassing DockingNode OnStart execution for {vName} during SpaceCenter background generation to prevent NRE.");
                    }
                }
                catch { }
                return false; // Skip original method execution during instantiation
            }
            return true;
        }

        private static bool EnsureAndInjectVesselProfile(Vessel vessel, ref object targetResult, string context)
        {
            try
            {
                var kerbalismAssembly = AssemblyLoader.loadedAssemblies.FirstOrDefault(a => a.name == "Kerbalism");
                var dbType = kerbalismAssembly?.assembly.GetType("KERBALISM.DB");

                if (dbType != null)
                {
                    var vesselsField = dbType.GetField("vessels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (vesselsField != null)
                    {
                        var vesselsDict = vesselsField.GetValue(null) as System.Collections.IDictionary;
                        if (vesselsDict != null)
                        {
                            if (vesselsDict.Contains(vessel.id))
                            {
                                if (context == "DB Query")
                                {
                                    targetResult = vesselsDict[vessel.id];
                                    return false; // Return valid cached record, shield call
                                }
                                return true;
                            }

                            var vesselDataType = kerbalismAssembly.assembly.GetType("KERBALISM.VesselData");
                            if (vesselDataType != null)
                            {
                                object mockVesselData = null;

                                try { mockVesselData = Activator.CreateInstance(vesselDataType, new object[] { vessel.id }); }
                                catch
                                {
                                    try { mockVesselData = Activator.CreateInstance(vesselDataType, new object[] { vessel }); }
                                    catch { mockVesselData = FormatterServices.GetUninitializedObject(vesselDataType); }
                                }

                                if (mockVesselData != null)
                                {
                                    vesselsDict[vessel.id] = mockVesselData;
                                    targetResult = mockVesselData;
                                    injectedVessels.Add(vessel.id);
                                    Debug.Log($"[KSTS-Kerbalism Patch] Injection Success [{context}]! Force-seeded {vessel.vesselName} ({vessel.id}) directly into Kerbalism's tracking matrix.");
                                    if (context == "DB Query") return false;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSTS-Kerbalism Patch] Injection Failure during {context}: " + ex.Message);
            }
            return true;
        }

        public void OnDestroy()
        {
            GameEvents.onVesselCreate.Remove(OnVesselCreatedWipeCleanup);
            GameEvents.onVesselLoaded.Remove(OnVesselLoadedWipeCleanup);
        }
    }
}
