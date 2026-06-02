using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    [HarmonyPatch(typeof(IncidentWorker), "CanFireNow")]
    public static class ServiceIncidentCanFireNowPatch
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (__result || __instance == null || parms == null)
            {
                return;
            }

            Map map = parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            if (!ServiceIncidentUtility.ShouldForceAllow(incident == null ? null : incident.defName, map))
            {
                return;
            }

            __result = true;
        }
    }

    public static class ServiceIncidentUtility
    {
        private static readonly Dictionary<string, float> TrafficRateProgress = new Dictionary<string, float>();
        private static readonly Dictionary<int, int> LastServiceArrivalTickByMap = new Dictionary<int, int>();
        private const int SharedServiceArrivalCadenceTicks = 2500;

        private static readonly HashSet<string> HospitalIncidents = new HashSet<string>
        {
            "PatientArrives",
            "MassCasualtyEvent"
        };

        private static readonly HashSet<string> HospitalityIncidents = new HashSet<string>
        {
            "VisitorGroup",
            "VisitorGroupMax",
            "VisitorGroupSelectFaction",
            "VisitorGroupSpacerCruise",
            "VisitorGroupSpacerLuxury"
        };

        public static bool ShouldForceAllow(string incidentDefName, Map map)
        {
            if (string.IsNullOrEmpty(incidentDefName))
            {
                return false;
            }
            if (HospitalIncidents.Contains(incidentDefName))
            {
                if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospital", out _))
                {
                    return false;
                }
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospital) &&
                    HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map);
            }
            if (HospitalityIncidents.Contains(incidentDefName))
            {
                if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospitality", out _))
                {
                    return false;
                }
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospitality) &&
                    HospitalityIncidentGate.CanAcceptHospitalityIncident(incidentDefName, map);
            }
            return false;
        }

        public static bool TrafficRateAllows(string incidentDefName, Map map)
        {
            if (IsDebugIncidentExecution())
            {
                return true;
            }
            float rate = TrafficRateFor(incidentDefName);
            if (rate <= 0f)
            {
                return false;
            }
            if (!SharedTrafficCadenceReady(map))
            {
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Service traffic blocked by shared one-hour cadence: " + (incidentDefName ?? "unknown"));
                return false;
            }
            if (rate >= 1f)
            {
                MarkSharedTrafficUsed(map);
                return true;
            }

            string key = TrafficRateKey(incidentDefName, map);
            float progress;
            TrafficRateProgress.TryGetValue(key, out progress);
            progress += rate;
            if (progress >= 1f)
            {
                TrafficRateProgress[key] = progress - 1f;
                MarkSharedTrafficUsed(map);
                return true;
            }
            TrafficRateProgress[key] = progress;
            return false;
        }

        public static float TrafficRateFor(string incidentDefName)
        {
            SpaceServicesSettings settings = SpaceServicesMod.Settings;
            if (settings == null || !settings.trafficRateOverride || string.IsNullOrEmpty(incidentDefName))
            {
                return 1f;
            }
            if (incidentDefName == "PatientArrives")
            {
                return SpaceServicesSettings.QuantizeRate(settings.hospitalPatientTrafficRate);
            }
            if (incidentDefName == "MassCasualtyEvent")
            {
                return SpaceServicesSettings.QuantizeRate(settings.hospitalMassCasualtyTrafficRate);
            }
            if (HospitalityIncidents.Contains(incidentDefName))
            {
                return SpaceServicesSettings.QuantizeRate(settings.hospitalityVisitorTrafficRate);
            }
            return 1f;
        }

        public static string TrafficRateReport(string incidentDefName)
        {
            return TrafficRateFor(incidentDefName).ToString("0.00") + "x";
        }

        public static bool TryConsumeSharedTrafficSlot(Map map, string reason)
        {
            if (IsDebugIncidentExecution())
            {
                return true;
            }
            if (!SharedTrafficCadenceReady(map))
            {
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Service traffic slot unavailable: " + (reason ?? "unspecified"));
                return false;
            }
            MarkSharedTrafficUsed(map);
            return true;
        }

        private static string TrafficRateKey(string incidentDefName, Map map)
        {
            return (map == null ? -1 : map.uniqueID) + ":" + (incidentDefName ?? "");
        }

        private static bool SharedTrafficCadenceReady(Map map)
        {
            if (map == null || Find.TickManager == null)
            {
                return true;
            }
            int lastTick;
            return !LastServiceArrivalTickByMap.TryGetValue(map.uniqueID, out lastTick) ||
                Find.TickManager.TicksGame - lastTick >= SharedServiceArrivalCadenceTicks;
        }

        private static void MarkSharedTrafficUsed(Map map)
        {
            if (map == null || Find.TickManager == null)
            {
                return;
            }
            // Hospital patients, mass casualties, and Hospitality groups share one service arrival slot.
            LastServiceArrivalTickByMap[map.uniqueID] = Find.TickManager.TicksGame;
        }

        public static bool IsDebugIncidentExecution()
        {
            if (!Prefs.DevMode && !DebugSettings.godMode)
            {
                return false;
            }
            try
            {
                StackTrace trace = new StackTrace(false);
                foreach (StackFrame frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
                {
                    string typeName = frame.GetMethod()?.DeclaringType?.FullName ?? "";
                    if (typeName.IndexOf("DebugActionsIncidents", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("Dialog_Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("DebugActionNode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("DebugTabMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

    }
}
