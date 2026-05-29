using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
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
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospital) &&
                    HasRequiredPad(map, ServiceUse.Patient) &&
                    HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map);
            }
            if (HospitalityIncidents.Contains(incidentDefName))
            {
                // Hospitality needs its own arrival/departure pipeline before natural space-map events are safe.
                return false;
            }
            return false;
        }

        private static bool HasRequiredPad(Map map, ServiceUse use)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.requireServicePadForArrivals)
            {
                return true;
            }
            return ServicePadUtility.TryFindServicePad(map, use) != null;
        }
    }

}
