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
        private static readonly HashSet<Pawn> NativeGuestLeaveAllowed = new HashSet<Pawn>();

        public static bool VisitorGroupTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? "VisitorGroup" : incident.defName;
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident(incidentDefName, map, __instance))
            {
                string report = HospitalityIncidentGate.ReadinessReport(incidentDefName, map, __instance);
                ServiceDebugUtility.LogThrottled("hospitality-block-" + incidentDefName + "-" + report, "Hospitality visitor incident blocked: " + report, GenDate.TicksPerHour);
                __result = false;
                return false;
            }

            if (HospitalityDelayedIncidentContext.TryGetPad(map, out Thing delayedPad))
            {
                parms.spawnCenter = delayedPad.Position;
                HospitalityArrivalContext.Push(map, delayedPad, true);
                return true;
            }

            ShuttleVisual visual = ShuttleVisual.Resolve();
            Thing pad = ServicePadUtility.TryFindRandomServicePad(map, ServiceUse.Guest);
            if (visual != null && pad != null)
            {
                parms.spawnCenter = pad.Position;
                SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
                if (comp != null && comp.ScheduleHospitalityIncident(__instance, parms, pad, visual.shipThingDef == null ? null : visual.shipThingDef.defName))
                {
                    ServiceShuttleUtility.SpawnArrival(map, pad.Position);
                    Messages.Message("Space Services: visitors inbound", pad, MessageTypeDefOf.NeutralEvent, false);
                    __result = true;
                    return false;
                }
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
                HospitalityArrivalContext.FinalizeArrival(map);
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
                HospitalityArrivalContext.FinalizeArrival(map);
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
            if (!HospitalityArrivalContext.TryGetArrivalPad(map, out Thing arrivalPad))
            {
                ServiceDebugUtility.LogVerbose("Ignored Hospitality SpawnVisitorPostfix outside Space Services arrival context.");
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
            ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns.Distinct(), arrivalPad);
        }

        public static void CreateLordPostfix(List<Pawn> pawns, Map map)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (!HospitalityArrivalContext.TryGetArrivalPad(map, out Thing arrivalPad))
            {
                ServiceDebugUtility.LogVerbose("Ignored Hospitality CreateLordPostfix outside Space Services arrival context.");
                return;
            }
            ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns, arrivalPad);
        }

        public static bool GuestLeavePrefix(Pawn pawn)
        {
            if (pawn != null && NativeGuestLeaveAllowed.Remove(pawn))
            {
                return true;
            }
            if (ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospitality marked guest leaving"))
            {
                return false;
            }
            return true;
        }

        public static bool TryRunNativeGuestLeave(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            Type guestUtility = AccessTools.TypeByName("Hospitality.Utilities.GuestUtility");
            MethodInfo leave = guestUtility == null ? null : AccessTools.Method(guestUtility, "Leave", new[] { typeof(Pawn) });
            if (leave == null)
            {
                return false;
            }
            try
            {
                NativeGuestLeaveAllowed.Add(pawn);
                leave.Invoke(null, new object[] { pawn });
                return true;
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogVerbose("Hospitality GuestUtility.Leave failed during service departure: " + ex.Message);
                return false;
            }
            finally
            {
                NativeGuestLeaveAllowed.Remove(pawn);
            }
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
