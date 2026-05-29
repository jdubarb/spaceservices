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
    public static class HospitalityIncidentGate
    {
        public static bool CanAcceptHospitalityIncident(string incidentDefName, Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return false;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return false;
            }
            return ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) != null;
        }

        public static string ReadinessReport(string incidentDefName, Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return "Hospitality support disabled in Space Services settings";
            }
            if (map == null)
            {
                return "no target map";
            }
            SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(map);
            if (!eligibility.allowed)
            {
                return string.Join(", ", eligibility.blockReasons.ToArray());
            }
            if (ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return "no usable guest service pad";
            }
            return "ready";
        }
    }
}
