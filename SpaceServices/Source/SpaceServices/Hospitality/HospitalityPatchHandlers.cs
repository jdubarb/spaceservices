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
    public static class HospitalityPatchHandlers
    {
        public static bool VisitorGroupTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? "VisitorGroup" : incident.defName;
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident(incidentDefName, map))
            {
                Log.Message("[Space Services] Hospitality visitor incident blocked: " + HospitalityIncidentGate.ReadinessReport(incidentDefName, map));
                __result = false;
                return false;
            }

            if (ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Guest, out IntVec3 cell))
            {
                parms.spawnCenter = cell;
            }
            HospitalityArrivalContext.Push(map);
            return true;
        }

        public static void VisitorGroupTryExecutePostfix(IncidentParms parms)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.Pop();
            }
        }

        public static bool AskForSafetyPrefix(IncidentParms parms, Action allow)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident("VisitorGroup", map))
            {
                return true;
            }

            // Hospitality's normal safety check does not understand sealed space-service pads.
            allow?.Invoke();
            return false;
        }

        public static void SpawnGroupPrefix(IncidentParms parms, Map map)
        {
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Guest, out IntVec3 cell))
            {
                parms.spawnCenter = cell;
            }
        }

        public static void SpawnGroupPostfix(IncidentParms parms, Map map)
        {
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.Pop();
            }
        }

        public static void SpawnVisitorPrefix(Pawn pawn, Map map, ref IntVec3 location)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (HospitalityArrivalContext.TryGetArrivalCell(map, out IntVec3 cell))
            {
                location = cell;
            }
            VacSuitUtility.SuitPawnForEnvironment(pawn, map, location);
        }

        public static void SpawnVisitorPostfix(List<Pawn> spawned, Pawn pawn, Map map, Pawn __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            List<Pawn> pawns = new List<Pawn>();
            if (spawned != null)
            {
                pawns.AddRange(spawned.Where(p => p != null));
            }
            if (pawn != null)
            {
                pawns.Add(pawn);
            }
            if (__result != null)
            {
                pawns.Add(__result);
            }
            ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns.Distinct());
        }

        public static void GuestLeavePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospitality marked guest leaving");
        }

        public static void VisitPointLeavePostfix(object __instance)
        {
            Lord lord = Reflect.GetMember(__instance, "lord") as Lord;
            if (lord == null || lord.ownedPawns == null)
            {
                return;
            }
            foreach (Pawn pawn in lord.ownedPawns.ToList())
            {
                ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospitality visit ended");
            }
        }
    }
}
