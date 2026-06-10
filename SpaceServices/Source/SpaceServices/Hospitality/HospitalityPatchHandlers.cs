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
        private static MethodInfo setNextOptimizeTickMethod;

        public static bool VisitorGroupTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return true;
            }
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            ServiceDebugUtility.LogAudit("Hospitality VisitorGroup prefix map=" + map.uniqueID + " parmsSpawn=" + (parms == null ? IntVec3.Invalid : parms.spawnCenter) + " faction=" + (parms == null || parms.faction == null ? "null" : parms.faction.Name));

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? "VisitorGroup" : incident.defName;
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident(incidentDefName, map, __instance, false))
            {
                string report = HospitalityIncidentGate.ReadinessReport(incidentDefName, map, __instance);
                ServiceDebugUtility.LogAudit("Hospitality VisitorGroup blocked incident=" + incidentDefName + " report=" + report);
                ServiceDebugUtility.LogThrottled("hospitality-block-" + incidentDefName + "-" + report, "Hospitality visitor incident blocked: " + report, GenDate.TicksPerHour);
                __result = false;
                return false;
            }

            if (HospitalityDelayedIncidentContext.TryGetPad(map, out Thing delayedPad))
            {
                parms.spawnCenter = delayedPad.Position;
                HospitalityArrivalContext.Push(map, delayedPad, true);
                ServiceDebugUtility.LogAudit("Hospitality VisitorGroup executing delayed touchdown pad=" + ServiceDebugUtility.ThingAuditSummary(delayedPad));
                return true;
            }

            if (!ServiceIncidentUtility.TrafficRateAllows(incidentDefName, map))
            {
                ServiceDebugUtility.LogThrottled("hospitality-rate-block-" + incidentDefName, "Hospitality visitor incident blocked by traffic rate " + ServiceIncidentUtility.TrafficRateReport(incidentDefName), GenDate.TicksPerHour);
                __result = false;
                return false;
            }

            ShuttleVisual visual = ShuttleVisual.Resolve("hospitality", null);
            Thing pad = ServicePadUtility.TryFindNearestServicePad(map, ServiceUse.Guest, parms == null ? IntVec3.Invalid : parms.spawnCenter);
            if (visual != null && pad != null)
            {
                if (!ServiceLifecycleUtility.TryClearPadFootprintForServiceShuttle(pad, "hospitality", "hospitality arrival scheduling", out string clearReason))
                {
                    ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-arrival-pad-occupied-" + pad.thingIDNumber, "Hospitality visitor shuttle delayed because the service pad could not be cleared: " + clearReason, 250);
                    __result = false;
                    return false;
                }
                parms.spawnCenter = pad.Position;
                SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
                if (comp != null && comp.ScheduleHospitalityIncident(__instance, parms, pad, visual.shipThingDef == null ? null : visual.shipThingDef.defName, visual.id))
                {
                    ServiceDebugUtility.LogAudit("Hospitality VisitorGroup scheduled shuttle arrival pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " visual=" + (visual.shipThingDef == null ? "none" : visual.shipThingDef.defName));
                    ServiceShuttleUtility.SpawnArrival(map, pad.Position, visual);
                    Messages.Message("Space Services: visitors inbound", pad, MessageTypeDefOf.SilentInput, false);
                    __result = true;
                    return false;
                }
            }

            if (ServicePadUtility.TryFindNearestServicePadCell(map, ServiceUse.Guest, parms == null ? IntVec3.Invalid : parms.spawnCenter, out IntVec3 cell))
            {
                parms.spawnCenter = cell;
            }
            HospitalityArrivalContext.Push(map);
            ServiceDebugUtility.LogAudit("Hospitality VisitorGroup falling through to immediate spawn cell=" + parms.spawnCenter);
            return true;
        }

        public static void VisitorGroupTryExecutePostfix(IncidentParms parms)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            Map map = parms == null ? null : parms.target as Map;
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.FinalizeArrival(map);
                HospitalityArrivalContext.Pop(map);
            }
        }

        public static void VisitorGroupTryExecuteFinalizer(IncidentParms parms, Exception __exception)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (__exception == null)
            {
                return;
            }
            Map map = parms == null ? null : parms.target as Map;
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.Pop(map);
            }
        }

        public static bool AskForSafetyPrefix(IncidentParms parms, Action allow)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return true;
            }
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident("VisitorGroup", map, null, false))
            {
                return true;
            }

            // Hospitality's normal safety check does not understand sealed space-service pads.
            allow?.Invoke();
            return false;
        }

        public static void SpawnGroupPrefix(IncidentParms parms, Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.TryFindNearestServicePadCell(map, ServiceUse.Guest, parms == null ? IntVec3.Invalid : parms.spawnCenter, out IntVec3 cell))
            {
                parms.spawnCenter = cell;
            }
        }

        public static void SpawnGroupPostfix(IncidentParms parms, Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.FinalizeArrival(map);
                HospitalityArrivalContext.Pop(map);
            }
        }

        public static void SpawnGroupFinalizer(Map map, Exception __exception)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (__exception != null && map != null && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalityArrivalContext.Pop(map);
            }
        }

        public static void SpawnVisitorPrefix(Pawn pawn, Map map, ref IntVec3 location)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (HospitalityArrivalContext.TryGetArrivalCell(map, out IntVec3 cell))
            {
                location = cell;
            }
            VacSuitUtility.SuitPawnForEnvironment(pawn, map, location);
            ServiceDebugUtility.LogAudit("Hospitality SpawnVisitor prefix pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " location=" + location);
        }

        public static void SpawnVisitorPostfix(List<Pawn> spawned, Pawn pawn, Map map, Pawn __result)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
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
            ServiceDebugUtility.LogAudit("Hospitality SpawnVisitor postfix registeredCount=" + pawns.Distinct().Count() + " pad=" + ServiceDebugUtility.ThingAuditSummary(arrivalPad));
            ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns.Distinct(), arrivalPad);
        }

        public static void CreateLordPostfix(List<Pawn> pawns, Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (!HospitalityArrivalContext.TryGetArrivalPad(map, out Thing arrivalPad))
            {
                ServiceDebugUtility.LogVerbose("Ignored Hospitality CreateLordPostfix outside Space Services arrival context.");
                return;
            }
            ServiceDebugUtility.LogAudit("Hospitality CreateLord postfix pawns=" + (pawns == null ? 0 : pawns.Count) + " pad=" + ServiceDebugUtility.ThingAuditSummary(arrivalPad));
            ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns, arrivalPad);
        }

        public static bool GuestLeavePrefix(Pawn pawn)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return true;
            }
            if (pawn != null && NativeGuestLeaveAllowed.Remove(pawn))
            {
                ServiceDebugUtility.LogAudit("Hospitality GuestUtility.Leave allowed through Space Services bypass: " + HospitalityBedUtility.GuestDebugSummary(pawn));
                return true;
            }
            // Native Hospitality removes guests by walking/despawning them. In space-service maps we
            // intercept that handoff so pickup shuttles can own the departure instead.
            if (ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospitality marked guest leaving"))
            {
                ServiceDebugUtility.LogAudit("Hospitality GuestUtility.Leave blocked; Space Services owns departure: " + HospitalityBedUtility.GuestDebugSummary(pawn));
                return false;
            }
            ServiceDebugUtility.LogAudit("Hospitality GuestUtility.Leave passed through unmanaged: " + HospitalityBedUtility.GuestDebugSummary(pawn));
            return true;
        }

        public static bool PocketHeadgearPrefix(Pawn pawn)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return true;
            }
            if (!ServiceLifecycleUtility.ShouldSuppressHospitalityVacuumApparelJob(pawn, null))
            {
                return true;
            }

            // Hospitality pockets normal headgear when guests "arrive"; in space that can remove
            // temperature or vacuum-relevant helmets before the guest reaches atmosphere.
            ServiceDebugUtility.LogAudit(ServiceLogIntegration.Hospitality, "Suppressed Hospitality headgear pocketing during vacuum transit: " + ServiceDebugUtility.PawnAuditSummary(pawn));
            return false;
        }

        public static bool OptimizeApparelGuestPrefix(object __instance, Pawn pawn, ref Job __result)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return true;
            }
            if (!ServiceLifecycleUtility.ShouldSuppressHospitalityVacuumApparelJob(pawn, null))
            {
                return true;
            }

            __result = null;
            SetNextOptimizeTick(__instance, pawn);
            // Without bumping Hospitality's optimizer cooldown, blocked outfit jobs can retry every think tick.
            ServiceDebugUtility.LogAudit(ServiceLogIntegration.Hospitality, "Suppressed Hospitality guest apparel optimization during vacuum transit: " + ServiceDebugUtility.PawnAuditSummary(pawn));
            return false;
        }

        private static void SetNextOptimizeTick(object jobGiver, Pawn pawn)
        {
            if (jobGiver == null || pawn == null)
            {
                return;
            }
            try
            {
                if (setNextOptimizeTickMethod == null)
                {
                    setNextOptimizeTickMethod = AccessTools.Method(typeof(JobGiver_OptimizeApparel), "SetNextOptimizeTick", new[] { typeof(Pawn) });
                }
                setNextOptimizeTickMethod?.Invoke(jobGiver, new object[] { pawn });
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-set-next-optimize-" + pawn.thingIDNumber, "Could not throttle suppressed guest apparel optimization: " + ex.GetType().Name + " " + ex.Message, GenDate.TicksPerHour);
            }
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
                ServiceDebugUtility.LogAudit("Before native Hospitality GuestUtility.Leave: " + HospitalityBedUtility.GuestDebugSummary(pawn));
                NativeGuestLeaveAllowed.Add(pawn);
                leave.Invoke(null, new object[] { pawn });
                ServiceDebugUtility.LogAudit("After native Hospitality GuestUtility.Leave: " + HospitalityBedUtility.GuestDebugSummary(pawn));
                return true;
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogAudit("Hospitality GuestUtility.Leave failed during service departure: " + ex);
                return false;
            }
            finally
            {
                NativeGuestLeaveAllowed.Remove(pawn);
            }
        }

        public static void VisitPointLeavePostfix(object __instance)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            Lord lord = Reflect.GetMember(__instance, "lord") as Lord;
            if (lord == null || lord.ownedPawns == null)
            {
                return;
            }
            foreach (Pawn pawn in lord.ownedPawns.ToList())
            {
                // LordToil_VisitPoint.Leave is Hospitality's clearest "visit is over" signal.
                ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospitality visit ended");
            }
        }
    }
}
