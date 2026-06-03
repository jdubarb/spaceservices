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
                HospitalMassCasualtyVisualContext.Push(false);
                HospitalLandingRedirectContext.Push(null, IntVec3.Invalid, null);
                return;
            }
            HospitalMassCasualtyVisualContext.Push(HospitalArrivalIncidentContext.IsMassCasualty(map));

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
                HospitalMassCasualtyVisualContext.Pop();
                HospitalLandingRedirectContext.Pop();
            }
        }

        public static void HospitalSpawnPatientFinalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return;
            }
            // If Hospital throws while spawning, postfix cleanup will not run.
            HospitalMassCasualtyVisualContext.Pop();
            HospitalLandingRedirectContext.Pop();
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
            if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospital", out string trafficReason))
            {
                // Late safety check for hazards that start after the incident was accepted but before the visual shuttle is swapped in.
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-shuttle-late-hazard-" + trafficReason, "Hospital arrival shuttle suppressed by traffic hazard: " + trafficReason, GenDate.TicksPerHour);
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

        public static void HospitalSentAwayPostfix(Pawn pawn, ref bool __result)
        {
            if (!__result || pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceEligible(pawn.Map))
            {
                return;
            }
            if (ShouldKeepHospitalPatientForOngoingTreatment(pawn, out string reason))
            {
                // Hospital normally discharges diseases once the immediate tend/rest job is done.
                // In space, keep those patients in Hospital's lord until the long hediff clears.
                __result = false;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-delay-discharge-" + pawn.thingIDNumber, "Delayed Hospital patient departure for ongoing treatment: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            }
        }

        public static void PatientGoToBedPostfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceEligible(pawn.Map))
            {
                return;
            }
            if (!ShouldKeepHospitalPatientForOngoingTreatment(pawn, out string reason))
            {
                return;
            }

            Building_Bed bed = RestUtility.FindBedFor(pawn, pawn, false, false, (GuestStatus?)null);
            if (bed == null)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-ongoing-bed-missing-" + pawn.thingIDNumber, "Could not find ongoing-treatment hospital bed for " + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + reason, GenDate.TicksPerHour);
                return;
            }

            Job job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            job.restUntilHealed = true;
            __result = job;
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-ongoing-bed-job-" + pawn.thingIDNumber, "Keeping Hospital patient in bed for ongoing treatment: " + reason + " bed=" + ServiceDebugUtility.ThingAuditSummary(bed), GenDate.TicksPerHour);
        }

        public static bool ShouldKeepHospitalPatientForOngoingTreatment(Pawn pawn, out string reason)
        {
            reason = null;
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null || !TryGetHospitalPatientData(pawn, out string diagnosis, out bool dataLooksLikeDisease))
            {
                return false;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (hediffs == null)
            {
                return false;
            }
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (IsOngoingHospitalDiseaseHediff(hediff, diagnosis, dataLooksLikeDisease))
                {
                    reason = (hediff.LabelCap ?? hediff.def?.LabelCap ?? "ongoing condition").ToString();
                    return true;
                }
            }
            return false;
        }

        public static bool IsActiveHospitalPatient(Pawn pawn)
        {
            return TryGetHospitalPatientData(pawn, out _, out _);
        }

        private static bool TryGetHospitalPatientData(Pawn pawn, out string diagnosis, out bool dataLooksLikeDisease)
        {
            diagnosis = null;
            dataLooksLikeDisease = false;
            object hospital = HospitalIncidentGate.FindHospitalComponent(pawn == null ? null : pawn.Map);
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (patients == null || pawn == null || !patients.Contains(pawn))
            {
                return false;
            }
            object data = patients[pawn];
            diagnosis = Reflect.GetMember(data, "Diagnosis") as string;
            object type = Reflect.GetMember(data, "Type");
            if (type != null && string.Equals(type.ToString(), "Disease", StringComparison.OrdinalIgnoreCase))
            {
                dataLooksLikeDisease = true;
                return true;
            }
            string cure = Reflect.GetMember(data, "Cure") as string;
            dataLooksLikeDisease = ContainsInsensitive(cure, "disease");
            return true;
        }

        private static bool IsOngoingHospitalDiseaseHediff(Hediff hediff, string diagnosis, bool dataLooksLikeDisease)
        {
            if (hediff == null || hediff.def == null || hediff is Hediff_Injury)
            {
                return false;
            }
            if (hediff.Severity <= 0f || hediff.def.defName == "Anesthetic")
            {
                return false;
            }
            if (DiagnosisMatchesHediff(diagnosis, hediff))
            {
                return true;
            }
            return dataLooksLikeDisease && (hediff.def.tendable || hediff.def.makesSickThought || hediff.def.lethalSeverity > 0f);
        }

        private static bool DiagnosisMatchesHediff(string diagnosis, Hediff hediff)
        {
            if (string.IsNullOrEmpty(diagnosis) || hediff == null || hediff.def == null)
            {
                return false;
            }
            return ContainsInsensitive(diagnosis, hediff.def.label) ||
                ContainsInsensitive(diagnosis, hediff.Label) ||
                ContainsInsensitive(hediff.Label, diagnosis);
        }

        private static bool ContainsInsensitive(string text, string value)
        {
            return !string.IsNullOrEmpty(text) &&
                !string.IsNullOrEmpty(value) &&
                text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
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

        public static void HospitalPatientArrivesTryExecuteFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                HospitalArrivalIncidentContext.Pop();
            }
        }

        public static void HospitalMassCasualtyTryExecutePostfix()
        {
            HospitalArrivalIncidentContext.Pop();
        }

        public static void HospitalMassCasualtyTryExecuteFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                HospitalArrivalIncidentContext.Pop();
            }
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
