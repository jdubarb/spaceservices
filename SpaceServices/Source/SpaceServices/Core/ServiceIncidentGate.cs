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
            if (__instance == null || parms == null)
            {
                return;
            }

            Map map = parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? null : incident.defName;

            // Some event mods can legally fire on orbit maps but are colony-ending on static service bases.
            // Block those before Space Services considers force-allowing its own traffic.
            if (__result && ServiceIncidentUtility.ShouldBlockIncident(incidentDefName, map))
            {
                __result = false;
                return;
            }

            if (__result || !ServiceIncidentUtility.ShouldForceAllow(incidentDefName, map))
            {
                return;
            }

            __result = true;
        }
    }

    public static class ServiceIncidentUtility
    {
        private const int SharedServiceArrivalCadenceTicks = 2500;
        private const string VgeAsteroidShowerIncident = "VGE_AsteroidShower";

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

        public static bool ShouldBlockIncident(string incidentDefName, Map map)
        {
            if (string.IsNullOrEmpty(incidentDefName) || map == null)
            {
                return false;
            }

            SpaceServicesSettings settings = SpaceServicesMod.Settings;
            if (settings != null &&
                settings.blockVgeAsteroidShower &&
                string.Equals(incidentDefName, VgeAsteroidShowerIncident, StringComparison.OrdinalIgnoreCase))
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "vge-asteroid-shower-block-" + map.uniqueID, "Blocked VGE asteroid shower on stationary service map.", GenDate.TicksPerHour);
                return true;
            }

            return false;
        }

        public static bool TrafficRateAllows(string incidentDefName, Map map)
        {
            if (IsDebugIncidentExecution())
            {
                return true;
            }
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp != null)
            {
                return comp.TrafficRateAllows(incidentDefName, SharedServiceArrivalCadenceTicks);
            }
            float rate = TrafficRateFor(incidentDefName);
            if (rate <= 0f)
            {
                return false;
            }
            return true;
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
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return true;
            }
            if (!comp.TryConsumeSharedTrafficSlot(SharedServiceArrivalCadenceTicks))
            {
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Service traffic slot unavailable: " + (reason ?? "unspecified"));
                return false;
            }
            return true;
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
