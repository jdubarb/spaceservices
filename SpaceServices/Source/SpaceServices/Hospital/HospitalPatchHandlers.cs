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
    public static class HospitalPatchHandlers
    {
        public static void HospitalLandingSpotPostfix(object[] __args, ref IntVec3 __result)
        {
            Map map = OptionalPatchUtility.FindMap(__args);
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 cell))
            {
                __result = cell;
            }
        }

        public static void HospitalCanSpawnPatientPostfix(Map map, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            __result = ServiceIncidentUtility.ShouldForceAllow("PatientArrives", map);
        }

        public static bool HospitalPatientArrivesTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? (IsMassCasualtyIncident(__instance, null) ? "MassCasualtyEvent" : "PatientArrives") : incident.defName;
            if (HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map, false))
            {
                if (!ServiceIncidentUtility.TrafficRateAllows(incidentDefName, map))
                {
                    if (incidentDefName == "PatientArrives")
                    {
                        HospitalArrivalIncidentContext.MarkPatientFallbackSuppressed(map);
                    }
                    ServiceDebugUtility.LogThrottled("hospital-rate-block-" + incidentDefName, "Hospital patient incident blocked by traffic rate " + ServiceIncidentUtility.TrafficRateReport(incidentDefName), GenDate.TicksPerHour);
                    __result = false;
                    return false;
                }
                HospitalArrivalIncidentContext.Push(map, IsMassCasualtyIncident(__instance, incidentDefName));
                return true;
            }
            string report = HospitalIncidentGate.ReadinessReport(incidentDefName, map);
            ServiceDebugUtility.LogThrottled("hospital-block-" + incidentDefName + "-" + report, "Hospital patient incident blocked: " + report, GenDate.TicksPerHour);
            __result = false;
            return false;
        }

        public static void HospitalTryFindEntryCellPostfix(Map map, ref IntVec3 cell, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 serviceCell))
            {
                cell = serviceCell;
                __result = true;
            }
        }

        public static void HospitalSpawnPatientPrefix(MethodBase __originalMethod, object[] __args)
        {
            Map map = OptionalPatchUtility.FindMap(__args);
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalLandingRedirectContext.Push(null, IntVec3.Invalid, null);
                return;
            }

            IntVec3 cell;
            if (!HospitalLandingRedirectContext.TryGetForcedCell(map, out cell) &&
                !HospitalArrivalIncidentContext.TryGetNextArrivalCell(map, out cell) &&
                !ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out cell))
            {
                cell = IntVec3.Invalid;
            }
            foreach (Pawn pawn in OptionalPatchUtility.PawnsFromArgs(__args))
            {
                VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
            }
            Thing tempLandingSpot = HospitalLandingRedirectContext.CreateTemporaryPatientLandingSpot(map, cell);
            HospitalLandingRedirectContext.Push(map, cell, tempLandingSpot);
        }

        public static void HospitalSpawnPatientPostfix(object[] __args)
        {
            try
            {
                Map map = OptionalPatchUtility.FindMap(__args);
                List<Pawn> pawns = OptionalPatchUtility.PawnsFromArgs(__args).Distinct().ToList();
                IntVec3 arrivalCell = IntVec3.Invalid;
                bool hasArrivalCell = map != null && HospitalLandingRedirectContext.TryGetActiveCell(map, out arrivalCell);
                foreach (Pawn pawn in pawns)
                {
                    IntVec3 cell = hasArrivalCell ? arrivalCell : pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
                    VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
                }
                if (map != null && pawns.Count > 0 && SpaceServiceMapDetector.IsServiceEligible(map))
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
                }
            }
            finally
            {
                HospitalLandingRedirectContext.Pop();
            }
        }

        public static bool HospitalDropPodAtPrefix(ref IntVec3 c, Map map, ActiveTransporterInfo info, Faction faction)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            if (HospitalLandingRedirectContext.TryGetActiveCell(map, out IntVec3 cell) && cell.IsValid)
            {
                c = cell;
            }
            if (info != null && info.innerContainer != null)
            {
                List<Pawn> pawns = new List<Pawn>();
                foreach (Thing thing in info.innerContainer)
                {
                    Pawn pawn = thing as Pawn;
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
                VacSuitUtility.SuitPawnsForEnvironment(pawns, map, c);
            }
            if (HospitalArrivalIncidentContext.IsMassCasualty(map))
            {
                ServiceDebugUtility.LogAudit("Hospital mass casualty using normal drop pod landing at " + c);
                return true;
            }
            HospitalArrivalIncidentContext.ArrivalVisualFlags(map, out bool showArrival, out bool showDeparture);
            if (HospitalLandingRedirectContext.TryGetActiveCell(map, out IntVec3 activeCell) && activeCell.IsValid && ServiceShuttleUtility.TryReplaceDropPodWithArrivalShuttle(c, map, info, faction, showArrival, showDeparture))
            {
                return false;
            }
            return true;
        }

        public static void HospitalPatientDeparturePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospital requested patient departure");
        }

        public static void HospitalPatientGonePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital removed patient from map");
        }

        public static void HospitalPatientDiedPrefix(Pawn pawn, ref HospitalPatientDeathState __state)
        {
            __state = HospitalPatientDeathState.Capture(pawn);
        }

        public static void HospitalPatientDiedPostfix(Pawn pawn, HospitalPatientDeathState __state)
        {
            Map map = __state == null ? pawn?.MapHeld : __state.map;
            // Hospital clears patient data on death, but the corpse can still save its old lord ref.
            bool notified = ServicePawnUtility.NotifyLordPawnLost(__state == null ? null : __state.lord, pawn, PawnLostCondition.Killed);
            int cleaned = ServicePawnUtility.CleanupTerminalPawnReferences(map, pawn);
            ServiceDebugUtility.LogAudit("HospitalPatientDied cleanup pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " lordNotified=" + notified + " refsCleaned=" + cleaned);
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital patient died");
        }

        public static void HospitalPatientArrivesTryExecutePostfix(object __instance, IncidentParms parms, ref bool __result)
        {
            try
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
                if (HospitalArrivalIncidentContext.ConsumePatientFallbackSuppressed(map))
                {
                    ServiceDebugUtility.LogThrottled("hospital-fallback-rate-suppressed", "Hospital PatientArrives fallback suppressed by traffic rate.", GenDate.TicksPerHour);
                    return;
                }
                if (!HospitalIncidentGate.CanAcceptHospitalIncident("PatientArrives", map, false))
                {
                    string report = HospitalIncidentGate.ReadinessReport("PatientArrives", map);
                    ServiceDebugUtility.LogThrottled("hospital-fallback-block-" + report, "Hospital PatientArrives returned false; fallback blocked: " + report, GenDate.TicksPerHour);
                    return;
                }
                try
                {
                    __result = HospitalPatientFallback.TryExecutePatientArrival(__instance, parms, map, IntVec3.Invalid);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Space Services] Hospital patient fallback failed: " + ex);
                }
            }
            finally
            {
                HospitalArrivalIncidentContext.Pop();
            }
        }

        public static void HospitalMassCasualtyTryExecutePostfix()
        {
            HospitalArrivalIncidentContext.Pop();
        }

        private static bool IsMassCasualtyIncident(object worker, string incidentDefName)
        {
            if (incidentDefName == "MassCasualtyEvent")
            {
                return true;
            }
            string typeName = worker == null ? "" : worker.GetType().FullName ?? "";
            return typeName.IndexOf("MassCasualty", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class HospitalPatientDeathState
    {
        public Map map;
        public Lord lord;

        public static HospitalPatientDeathState Capture(Pawn pawn)
        {
            return new HospitalPatientDeathState
            {
                map = pawn == null ? null : pawn.MapHeld ?? pawn.Map,
                lord = SafeLord(pawn)
            };
        }

        private static Lord SafeLord(Pawn pawn)
        {
            try
            {
                return pawn == null ? null : pawn.GetLord();
            }
            catch
            {
                return null;
            }
        }
    }
}
