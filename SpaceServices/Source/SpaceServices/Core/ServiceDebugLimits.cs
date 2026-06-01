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
    public static class ServiceDebugLimits
    {
        public static bool HospitalAllows(Map map, string incidentDefName, int incomingCount, out string reason)
        {
            reason = null;
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.debugHospitalPatientLimit < 0)
            {
                return true;
            }
            int current = CountActiveServicePawns(map, "hospital");
            if (current + Math.Max(1, incomingCount) <= comp.debugHospitalPatientLimit)
            {
                return true;
            }
            reason = "debug Hospital patient limit " + comp.debugHospitalPatientLimit + " reached (" + current + " active)";
            return false;
        }

        public static bool HospitalityAllows(Map map, int incomingGroups, int incomingPawns, out string reason)
        {
            reason = null;
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return true;
            }

            int groups = CountActiveGroups(map, "hospitality");
            int pawns = CountActiveServicePawns(map, "hospitality");
            if (comp.debugHospitalityGroupLimit >= 0 && groups + Math.Max(1, incomingGroups) > comp.debugHospitalityGroupLimit)
            {
                reason = "debug Hospitality group limit " + comp.debugHospitalityGroupLimit + " reached (" + groups + " active)";
                return false;
            }
            if (comp.debugHospitalityPawnLimit >= 0 && pawns + Math.Max(1, incomingPawns) > comp.debugHospitalityPawnLimit)
            {
                reason = "debug Hospitality pawn limit " + comp.debugHospitalityPawnLimit + " reached (" + pawns + " active)";
                return false;
            }
            return true;
        }

        public static int CountActiveGroups(Map map, string serviceKind)
        {
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return 0;
            }
            return comp.serviceGroups.Count(record => record != null && record.serviceKind == serviceKind && record.state != "completed" && ActivePawnCount(record) > 0);
        }

        public static int CountActiveServicePawns(Map map, string serviceKind)
        {
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return 0;
            }
            return comp.serviceGroups
                .Where(record => record != null && record.serviceKind == serviceKind && record.state != "completed")
                .Sum(ActivePawnCount);
        }

        public static int ActivePawnCount(ServiceGroupRecord record)
        {
            return record == null || record.pawns == null
                ? 0
                : record.pawns.Count(pawn => pawn != null && !ServicePawnUtility.IsTerminalPawn(pawn));
        }
    }
}
