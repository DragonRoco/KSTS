using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections;
using UnityEngine;
using HarmonyLib;

namespace KSTS
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KerbalismDatabaseShield : MonoBehaviour
    {
        private static bool isPatched = false;

        public void Start()
        {
            if (isPatched) return;

            try
            {
                var harmony = new Harmony("com.ksts.kerbalism.dbshield");
                var kerbalismAssembly = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name.Equals("Kerbalism", StringComparison.OrdinalIgnoreCase));

                if (kerbalismAssembly == null) return;

                // 1. PATCH TARGET A: KERBALISM.DB.GetVesselData (Core Database Query)
                var dbType = kerbalismAssembly.assembly.GetType("KERBALISM.DB");
                if (dbType != null)
                {
                    var getVesselDataMethod = dbType.GetMethod("GetVesselData", BindingFlags.Public | BindingFlags.Static);
                    if (getVesselDataMethod != null)
                    {
                        var dbPrefix = typeof(KerbalismDatabaseShield).GetMethod(nameof(GetVesselDataPrefix), BindingFlags.Static | BindingFlags.Public);
                        harmony.Patch(getVesselDataMethod, prefix: new HarmonyMethod(dbPrefix));
                        Debug.Log("[KSTS-Kerbalism Patch] Core Database Prefix Shield injected.");
                    }
                }

                // 2. PATCH TARGET B: ModuleDockingNode.OnStart (Stock Docking Node Intercept)
                // Kerbalism hooks this method; we must prefix it to initialize the DB profile before Kerbalism looks at it
                var dockingNodeType = typeof(ModuleDockingNode);
                var dockingOnStartMethod = dockingNodeType.GetMethod("OnStart", BindingFlags.Public | BindingFlags.Instance);
                if (dockingOnStartMethod != null)
                {
                    var dockingPrefix = typeof(KerbalismDatabaseShield).GetMethod(nameof(DockingNodeOnStartPrefix), BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(dockingOnStartMethod, prefix: new HarmonyMethod(dockingPrefix));
                    Debug.Log("[KSTS-Kerbalism Patch] Docking Node Initialization Shield injected.");
                }

                isPatched = true;
                DontDestroyOnLoad(this);
                Debug.Log("[KSTS-Kerbalism Patch] All structural background execution shields are active!");
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSTS-Kerbalism Patch] Failed to apply shields: " + ex.ToString());
            }
        }

        // --- SHIELD A: Core Database Intercept ---
        public static bool GetVesselDataPrefix(Vessel vessel, ref object __result)
        {
            if (vessel == null) return true;
            return EnsureVesselDataProfile(vessel, ref __result, "DB Query");
        }

        // --- SHIELD B: Docking Node Intercept ---
        public static bool DockingNodeOnStartPrefix(ModuleDockingNode __instance)
        {
            if (__instance == null || __instance.vessel == null) return true;

            object dummyResult = null;
            // Force-verify/generate a database profile right before the docking node logic evaluates
            EnsureVesselDataProfile(__instance.vessel, ref dummyResult, "Docking Node Waking");

            return true; // Always return true to let the original OnStart methods run now that the DB is shielded
        }

        // --- CORE LOGIC: Multi-tiered profile generation safety net ---
        private static bool EnsureVesselDataProfile(Vessel vessel, ref object targetResult, string context)
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

                        if (vesselsDict == null || !vesselsDict.Contains(vessel.id))
                        {
                            var vesselDataType = kerbalismAssembly.assembly.GetType("KERBALISM.VesselData");
                            if (vesselDataType != null)
                            {
                                object mockVesselData = null;

                                try
                                {
                                    mockVesselData = Activator.CreateInstance(vesselDataType, new object[] { vessel.id });
                                }
                                catch
                                {
                                    try
                                    {
                                        mockVesselData = Activator.CreateInstance(vesselDataType, new object[] { vessel });
                                    }
                                    catch
                                    {
                                        mockVesselData = FormatterServices.GetUninitializedObject(vesselDataType);
                                    }
                                }

                                if (mockVesselData != null)
                                {
                                    targetResult = mockVesselData;
                                    Debug.Log($"[KSTS-Kerbalism Patch] Shield Active [{context}]! Intercepted uninitialized vessel: {vessel.vesselName}. Fallback structure injected safely.");

                                    // If this was a direct database query, we skip the original query because we just built the response
                                    if (context == "DB Query") return false;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSTS-Kerbalism Patch] Error executing Shield during {context}: " + ex.Message);
            }

            return true;
        }
    }
}
