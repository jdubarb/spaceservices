using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class HospitalPatchHandlers
    {
        private const int MinimumSurgeryMedicine = 5;
        private static bool HospitalSupportEnabled => SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospital;
        private static bool HospitalPatientCareModeEnabled => SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.hospitalPatientCareMode;
        private static bool BlockHospitalSurgeryScariaEnabled => SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.blockHospitalSurgeryScaria;
        private static bool HoldFailedSurgeryAftercareEnabled => SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.holdFailedSurgeryAftercare;
        private static readonly Stack<SurgeryAcceptanceOverride> SurgeryAcceptanceOverrides = new Stack<SurgeryAcceptanceOverride>();

        private struct SurgeryAcceptanceOverride
        {
            public Map map;
            public object hospital;
            public bool acceptSurgery;
        }
        private const int OngoingTreatmentBedJobRetryTicks = GenDate.TicksPerHour;
        private const int OngoingTreatmentDecisionCacheTicks = 250;
        private static readonly Dictionary<int, int> LastOngoingTreatmentBedJobTickByPawn = new Dictionary<int, int>();
        // Medical-rest think nodes call this path heavily; cache the reflected Hospital lookup briefly.
        private static readonly Dictionary<int, OngoingTreatmentDecisionCache> OngoingTreatmentDecisionCacheByPawn = new Dictionary<int, OngoingTreatmentDecisionCache>();
        private static Dictionary<string, List<SpaceServiceHospitalTreatmentHediffDef>> OngoingTreatmentRulesByHediffDefName;

        public static void HospitalLandingSpotPostfix(object[] __args, ref IntVec3 __result)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            Map map = OptionalPatchUtility.FindMap(__args);
            if (map != null && SpaceServiceMapDetector.IsServiceActive(map) && ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 cell))
            {
                __result = cell;
            }
        }

        public static void HospitalCanSpawnPatientPostfix(Map map, ref bool __result)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return;
            }
            string incidentDefName = HospitalArrivalIncidentContext.IsMassCasualty(map) ? "MassCasualtyEvent" : "PatientArrives";
            __result = ServiceIncidentUtility.ShouldForceAllow(incidentDefName, map);
        }

        public static bool HospitalPatientArrivesTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            if (!HospitalSupportEnabled)
            {
                return true;
            }
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return true;
            }
            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? (IsMassCasualtyIncident(__instance, null) ? "MassCasualtyEvent" : "PatientArrives") : incident.defName;
            if (HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map, false, parms))
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
                if (incidentDefName == "MassCasualtyEvent" && !HospitalIncidentGate.TryPrepareMassCasualtyPawnCount(map, parms, out string massCasualtyReason))
                {
                    ServiceDebugUtility.LogThrottled("hospital-mass-casualty-size-block-" + massCasualtyReason, "Hospital mass casualty blocked: " + massCasualtyReason, GenDate.TicksPerHour);
                    __result = false;
                    return false;
                }
                MaybeSuppressSurgeryForLowMedicine(map, incidentDefName);
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
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
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
            if (!HospitalSupportEnabled)
            {
                return;
            }
            Map map = OptionalPatchUtility.FindMap(__args);
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
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
            if (!HospitalSupportEnabled)
            {
                return;
            }
            try
            {
                Map map = OptionalPatchUtility.FindMap(__args);
                List<Pawn> pawns = OptionalPatchUtility.PawnsFromArgs(__args).Distinct().ToList();
                IntVec3 arrivalCell = IntVec3.Invalid;
                bool hasArrivalCell = map != null && HospitalLandingRedirectContext.TryGetActiveCell(map, out arrivalCell);
                foreach (Pawn pawn in pawns)
                {
                    ClearOngoingTreatmentCache(pawn);
                    IntVec3 cell = hasArrivalCell ? arrivalCell : pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
                    VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
                    EnsureSurgeryPatientMedicineCare(pawn, map);
                    RemoveBlockedScariaFromSurgeryPatient(pawn, map, true, "after Hospital patient spawn");
                }
                if (map != null && pawns.Count > 0 && SpaceServiceMapDetector.IsServiceActive(map))
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
            if (!HospitalSupportEnabled)
            {
                return;
            }
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
            if (!HospitalSupportEnabled)
            {
                return true;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
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
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-shuttle-late-hazard-" + trafficReason, "Hospital arrival delayed by traffic hazard: " + trafficReason, GenDate.TicksPerHour);
                return !ServiceShuttleUtility.TryReplaceDropPodWithArrivalShuttle(c, map, info, faction, false, true, "hospital");
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
            if (!HospitalSupportEnabled)
            {
                return;
            }
            ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospital requested patient departure");
        }

        public static void HospitalSentAwayPostfix(Pawn pawn, ref bool __result)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (!__result || pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceActive(pawn.Map))
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
            if (!HospitalSupportEnabled || !HospitalPatientCareModeEnabled)
            {
                return;
            }
            if (__result != null || pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceActive(pawn.Map))
            {
                return;
            }
            if (!ShouldKeepHospitalPatientForOngoingTreatment(pawn, out string reason))
            {
                return;
            }
            if (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.LayDown)
            {
                return;
            }
            if (!CanIssueOngoingTreatmentBedJob(pawn))
            {
                return;
            }

            Building_Bed bed = FindOngoingTreatmentBed(pawn);
            if (bed == null)
            {
                if (ServiceDebugUtility.ShouldLog(ServiceLogIntegration.Hospital))
                {
                    ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-ongoing-bed-missing-" + pawn.thingIDNumber, "Could not find ongoing-treatment hospital bed for " + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + reason, GenDate.TicksPerHour);
                }
                return;
            }

            Job job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            job.restUntilHealed = true;
            job.expiryInterval = OngoingTreatmentBedJobRetryTicks;
            __result = job;
            if (ServiceDebugUtility.ShouldLog(ServiceLogIntegration.Hospital))
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-ongoing-bed-job-" + pawn.thingIDNumber, "Keeping Hospital patient in bed for ongoing treatment: " + reason + " bed=" + ServiceDebugUtility.ThingAuditSummary(bed), GenDate.TicksPerHour);
            }
        }

        public static void ShouldSeekMedicalRestPostfix(Pawn pawn, ref bool __result)
        {
            if (__result || !HospitalSupportEnabled || !HospitalPatientCareModeEnabled)
            {
                return;
            }
            if (pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceActive(pawn.Map))
            {
                return;
            }
            if (ShouldKeepHospitalPatientForOngoingTreatment(pawn, out _))
            {
                // Let Hospital's normal patient think tree choose bed rest instead of forcing a job globally.
                __result = true;
            }
        }

        private static bool CanIssueOngoingTreatmentBedJob(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (LastOngoingTreatmentBedJobTickByPawn.TryGetValue(pawn.thingIDNumber, out int lastTick) && ticksGame < lastTick + OngoingTreatmentBedJobRetryTicks)
            {
                return false;
            }
            // Some long hediffs make vanilla immediately discard LayDown, so retry slowly instead of
            // reissuing the same bed job during every job scan.
            LastOngoingTreatmentBedJobTickByPawn[pawn.thingIDNumber] = ticksGame;
            return true;
        }

        public static bool ShouldKeepHospitalPatientForOngoingTreatment(Pawn pawn, out string reason)
        {
            reason = null;
            if (TryGetCachedOngoingTreatmentDecision(pawn, out bool cachedResult, out string cachedReason))
            {
                reason = cachedReason;
                return cachedResult;
            }

            bool result = ComputeShouldKeepHospitalPatientForOngoingTreatment(pawn, out reason);
            StoreOngoingTreatmentDecision(pawn, result, reason);
            return result;
        }

        private static bool ComputeShouldKeepHospitalPatientForOngoingTreatment(Pawn pawn, out string reason)
        {
            reason = null;
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null || !TryGetHospitalPatientData(pawn, out _, out _))
            {
                return false;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (hediffs == null)
            {
                return false;
            }
            if (HoldFailedSurgeryAftercareEnabled && IsSurgeryPatient(pawn, pawn.MapHeld ?? pawn.Map) && PawnNeedsFailedSurgeryAftercare(pawn))
            {
                reason = "failed surgery aftercare";
                return true;
            }
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (IsOngoingHospitalTreatmentHediff(hediff))
                {
                    reason = (hediff.LabelCap ?? hediff.def?.LabelCap ?? "ongoing condition").ToString();
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetCachedOngoingTreatmentDecision(Pawn pawn, out bool result, out string reason)
        {
            result = false;
            reason = null;
            if (pawn == null)
            {
                return false;
            }
            if (!OngoingTreatmentDecisionCacheByPawn.TryGetValue(pawn.thingIDNumber, out OngoingTreatmentDecisionCache cache))
            {
                return false;
            }
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            int mapId = pawn.Map == null ? -1 : pawn.Map.uniqueID;
            int hediffCount = CurrentHediffCount(pawn);
            if (ticksGame > cache.tick + OngoingTreatmentDecisionCacheTicks || mapId != cache.mapId || hediffCount != cache.hediffCount)
            {
                return false;
            }
            result = cache.result;
            reason = cache.reason;
            return true;
        }

        private static void StoreOngoingTreatmentDecision(Pawn pawn, bool result, string reason)
        {
            if (pawn == null)
            {
                return;
            }
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            OngoingTreatmentDecisionCacheByPawn[pawn.thingIDNumber] = new OngoingTreatmentDecisionCache
            {
                tick = ticksGame,
                mapId = pawn.Map == null ? -1 : pawn.Map.uniqueID,
                hediffCount = CurrentHediffCount(pawn),
                result = result,
                reason = reason
            };
        }

        private static void ClearOngoingTreatmentCache(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            OngoingTreatmentDecisionCacheByPawn.Remove(pawn.thingIDNumber);
            LastOngoingTreatmentBedJobTickByPawn.Remove(pawn.thingIDNumber);
        }

        private static int CurrentHediffCount(Pawn pawn)
        {
            List<Hediff> hediffs = pawn == null || pawn.health == null || pawn.health.hediffSet == null ? null : pawn.health.hediffSet.hediffs;
            return hediffs == null ? -1 : hediffs.Count;
        }

        public static bool IsActiveHospitalPatient(Pawn pawn)
        {
            return TryGetHospitalPatientData(pawn, out _, out _);
        }

        private static bool TryGetHospitalPatientData(Pawn pawn, out string diagnosis, out bool dataLooksLikeDisease)
        {
            diagnosis = null;
            dataLooksLikeDisease = false;
            // Hospital's public behavior is driven by an internal Patients dictionary. Reflection here is
            // intentionally narrow: only enough to decide whether long-running hediffs should keep bed rest.
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

        private static bool IsOngoingHospitalTreatmentHediff(Hediff hediff)
        {
            if (hediff == null || hediff.def == null || hediff is Hediff_Injury)
            {
                return false;
            }
            if (hediff.Severity <= 0f)
            {
                return false;
            }
            EnsureOngoingTreatmentRulesIndexed();
            if (!OngoingTreatmentRulesByHediffDefName.TryGetValue(hediff.def.defName, out List<SpaceServiceHospitalTreatmentHediffDef> rules))
            {
                return false;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].AppliesTo(hediff))
                {
                    return true;
                }
            }
            return false;
        }

        private static void EnsureOngoingTreatmentRulesIndexed()
        {
            if (OngoingTreatmentRulesByHediffDefName != null)
            {
                return;
            }
            OngoingTreatmentRulesByHediffDefName = new Dictionary<string, List<SpaceServiceHospitalTreatmentHediffDef>>(StringComparer.OrdinalIgnoreCase);
            foreach (SpaceServiceHospitalTreatmentHediffDef rule in DefDatabase<SpaceServiceHospitalTreatmentHediffDef>.AllDefsListForReading)
            {
                if (rule == null || !rule.enabled || rule.hediffDefNames.NullOrEmpty() || !SpaceServiceDefFilters.RequiredPackagesLoaded(rule.requiredPackageIds))
                {
                    continue;
                }
                for (int i = 0; i < rule.hediffDefNames.Count; i++)
                {
                    string defName = rule.hediffDefNames[i];
                    if (string.IsNullOrEmpty(defName))
                    {
                        continue;
                    }
                    if (!OngoingTreatmentRulesByHediffDefName.TryGetValue(defName, out List<SpaceServiceHospitalTreatmentHediffDef> rules))
                    {
                        rules = new List<SpaceServiceHospitalTreatmentHediffDef>();
                        OngoingTreatmentRulesByHediffDefName[defName] = rules;
                    }
                    rules.Add(rule);
                }
            }
        }

        private static Building_Bed FindOngoingTreatmentBed(Pawn pawn)
        {
            Building_Bed bed = RestUtility.FindBedFor(pawn, pawn, false, false, (GuestStatus?)null);
            if (bed != null)
            {
                return bed;
            }
            Map map = pawn == null ? null : pawn.Map;
            if (map == null || map.listerBuildings == null)
            {
                return null;
            }

            // Hospital patients can be non-colony pawns, so vanilla's owner/social checks sometimes miss beds
            // that Hospital itself is already counting. This fallback stays medical-only and reservation-aware.
            Building_Bed best = null;
            float bestDistance = float.MaxValue;
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                Building_Bed candidate = building as Building_Bed;
                if (candidate == null ||
                    !candidate.Spawned ||
                    candidate.Destroyed ||
                    candidate.ForPrisoners ||
                    !candidate.Medical ||
                    !candidate.AnyUnoccupiedSleepingSlot)
                {
                    continue;
                }
                if (!pawn.CanReserve(candidate) || !pawn.CanReach(candidate, PathEndMode.OnCell, Danger.Some))
                {
                    continue;
                }
                float distance = pawn.Position.DistanceToSquared(candidate.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private static bool ContainsInsensitive(string text, string value)
        {
            return !string.IsNullOrEmpty(text) &&
                !string.IsNullOrEmpty(value) &&
                text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool HospitalPatientGonePrefix(Pawn pawn, ref bool __state)
        {
            __state = false;
            if (!HospitalSupportEnabled)
            {
                return true;
            }
            if (!ShouldHoldFailedSurgeryAftercare(pawn))
            {
                return true;
            }
            __state = true;
            ClearOngoingTreatmentCache(pawn);
            ServiceLifecycleUtility.MarkPawnDirty(pawn, "Hospital failed surgery aftercare held");
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-failed-surgery-aftercare-" + pawn.thingIDNumber, "Holding failed surgery patient for aftercare instead of service departure: " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        public static void HospitalPatientGonePostfix(Pawn pawn, bool __state)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (__state)
            {
                return;
            }
            ClearOngoingTreatmentCache(pawn);
            if (ShouldRouteSpawnedHospitalPatientRemovalToServiceDeparture(pawn))
            {
                return;
            }
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital removed patient from map");
        }

        public static bool CanDebugForceTrackedSurgeryFailure(Pawn pawn)
        {
            if (!HospitalSupportEnabled ||
                pawn == null ||
                !pawn.Spawned ||
                ServicePawnUtility.IsTerminalPawn(pawn) ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn) ||
                !ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out Map map, out ServiceGroupRecord record) ||
                record == null ||
                record.serviceKind != "hospital" ||
                record.state == "completed" ||
                record.state == "extracting" ||
                !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return false;
            }
            return IsSurgeryPatient(pawn, map);
        }

        public static bool DebugForceTrackedSurgeryFailure(Pawn pawn, out string reason)
        {
            reason = null;
            if (!CanDebugForceTrackedSurgeryFailure(pawn))
            {
                reason = "selected pawn is not a tracked active surgery patient";
                return false;
            }
            Map map = pawn.MapHeld ?? pawn.Map;
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            MethodInfo patientLeftTheMap = hospital == null ? null : AccessTools.Method(hospital.GetType(), "PatientLeftTheMap", new[] { typeof(Pawn) });
            if (hospital == null || patientLeftTheMap == null)
            {
                reason = "Hospital patient cleanup method was not found";
                return false;
            }
            try
            {
                ServiceDebugUtility.LogAudit("DEV forcing tracked surgery failure for pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
                patientLeftTheMap.Invoke(hospital, new object[] { pawn });
                ServiceLifecycleUtility.MarkPawnDirty(pawn, "DEV forced surgery failure");
                reason = "forced surgery failure";
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                Log.Warning("[Space Services] DEV force surgery failure failed: " + ex);
                return false;
            }
        }

        private static bool ShouldRouteSpawnedHospitalPatientRemovalToServiceDeparture(Pawn pawn)
        {
            if (pawn == null ||
                !pawn.Spawned ||
                ServicePawnUtility.IsTerminalPawn(pawn) ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn) ||
                !ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out Map map, out ServiceGroupRecord record) ||
                record == null ||
                record.serviceKind != "hospital" ||
                record.state == "extracting" ||
                !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return false;
            }
            const string reason = "Hospital removed spawned patient from map";
            if (ServiceLifecycleUtility.IsActiveDepartureState(record))
            {
                ServiceLifecycleUtility.MarkPawnDirty(pawn, reason);
                return true;
            }
            return ServiceLifecycleUtility.RequestDepartureForPawn(pawn, reason);
        }

        public static void HospitalPatientDiedPrefix(Pawn pawn, ref HospitalPatientDeathState __state)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            __state = HospitalPatientDeathState.Capture(pawn);
        }

        public static void HospitalPatientDiedPostfix(Pawn pawn, HospitalPatientDeathState __state)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            Map map = __state == null ? pawn?.MapHeld : __state.map;
            // Hospital clears patient data on death, but the corpse can still save its old lord ref.
            bool notified = ServicePawnUtility.NotifyLordPawnLost(__state == null ? null : __state.lord, pawn, PawnLostCondition.Killed);
            int cleaned = ServicePawnUtility.CleanupTerminalPawnReferences(map, pawn);
            ServiceDebugUtility.LogAudit("HospitalPatientDied cleanup pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " lordNotified=" + notified + " refsCleaned=" + cleaned);
            ClearOngoingTreatmentCache(pawn);
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital patient died");
        }

        public static void HospitalPatientArrivesTryExecutePostfix(object __instance, IncidentParms parms, ref bool __result)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            try
            {
                if (__result || __instance == null || parms == null)
                {
                    return;
                }
                Map map = parms.target as Map;
                if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
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
                HospitalArrivalIncidentContext.Pop(parms == null ? null : parms.target as Map);
                RestoreSuppressedSurgeryAcceptance(parms == null ? null : parms.target as Map);
            }
        }

        public static void HospitalPatientArrivesTryExecuteFinalizer(IncidentParms parms, Exception __exception)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (__exception != null)
            {
                HospitalArrivalIncidentContext.Pop(parms == null ? null : parms.target as Map);
                RestoreSuppressedSurgeryAcceptance(parms == null ? null : parms.target as Map);
            }
        }

        public static void HospitalMassCasualtyTryExecutePostfix(IncidentParms parms)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            HospitalArrivalIncidentContext.Pop(parms == null ? null : parms.target as Map);
            RestoreSuppressedSurgeryAcceptance(parms == null ? null : parms.target as Map);
        }

        public static void HospitalMassCasualtyTryExecuteFinalizer(IncidentParms parms, Exception __exception)
        {
            if (!HospitalSupportEnabled)
            {
                return;
            }
            if (__exception != null)
            {
                HospitalArrivalIncidentContext.Pop(parms == null ? null : parms.target as Map);
                RestoreSuppressedSurgeryAcceptance(parms == null ? null : parms.target as Map);
            }
        }

        public static IEnumerable<CodeInstruction> SurgeryMapHeldFallbackTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mapHeldGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MapHeld));
            MethodInfo fallback = AccessTools.Method(typeof(HospitalPatchHandlers), nameof(MapHeldForHospitalSurgery));
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, mapHeldGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, fallback);
                    continue;
                }
                yield return instruction;
            }
        }

        public static Map MapHeldForHospitalSurgery(Thing thing)
        {
            Map map = thing == null ? null : thing.MapHeld;
            if (map != null)
            {
                return map;
            }
            if (!HospitalLandingRedirectContext.TryGetActiveMap(out map) || !SurgeryPatientsEnabled(map))
            {
                return null;
            }
            return map;
        }

        public static void SurgeryAddRandomBillPrefix(object[] __args)
        {
            if (!HospitalSupportEnabled || !BlockHospitalSurgeryScariaEnabled)
            {
                return;
            }
            Map map = OptionalPatchUtility.FindMap(__args);
            if (map == null)
            {
                HospitalLandingRedirectContext.TryGetActiveMap(out map);
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return;
            }
            foreach (Pawn pawn in OptionalPatchUtility.PawnsFromArgs(__args).Distinct())
            {
                RemoveBlockedScariaFromSurgeryPatient(pawn, map, false, "before Hospital surgery bill");
            }
        }

        private static void MaybeSuppressSurgeryForLowMedicine(Map map, string incidentDefName)
        {
            if (map == null || incidentDefName != "PatientArrives")
            {
                return;
            }
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            if (hospital == null || !SurgeryPatientsEnabled(hospital))
            {
                return;
            }
            int medicineCount = CountSurgeryMedicine(map);
            if (medicineCount >= MinimumSurgeryMedicine)
            {
                return;
            }
            SurgeryAcceptanceOverrides.Push(new SurgeryAcceptanceOverride
            {
                map = map,
                hospital = hospital,
                acceptSurgery = true
            });
            Reflect.SetMember(hospital, "AcceptSurgery", false);
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-surgery-low-medicine-" + map.uniqueID, "Hospital surgery patient arrivals temporarily disabled: " + medicineCount + "/" + MinimumSurgeryMedicine + " industrial-or-better medicine available.", GenDate.TicksPerHour);
        }

        private static void RestoreSuppressedSurgeryAcceptance(Map map)
        {
            if (map == null || SurgeryAcceptanceOverrides.Count == 0)
            {
                return;
            }
            SurgeryAcceptanceOverride state = SurgeryAcceptanceOverrides.Peek();
            if (state.map != map)
            {
                return;
            }
            SurgeryAcceptanceOverrides.Pop();
            if (state.hospital != null)
            {
                Reflect.SetMember(state.hospital, "AcceptSurgery", state.acceptSurgery);
            }
        }

        private static int CountSurgeryMedicine(Map map)
        {
            return CountAvailableMedicine(map, DefDatabase<ThingDef>.GetNamedSilentFail("MedicineIndustrial")) +
                CountAvailableMedicine(map, DefDatabase<ThingDef>.GetNamedSilentFail("MedicineUltratech"));
        }

        private static int CountAvailableMedicine(Map map, ThingDef def)
        {
            if (map == null || map.listerThings == null || def == null)
            {
                return 0;
            }
            int count = 0;
            foreach (Thing thing in map.listerThings.ThingsOfDef(def))
            {
                if (thing != null && !thing.Destroyed && thing.Spawned && !thing.IsForbidden(Faction.OfPlayer))
                {
                    count += thing.stackCount;
                }
            }
            return count;
        }

        private static void EnsureSurgeryPatientMedicineCare(Pawn pawn, Map map)
        {
            if (pawn == null || pawn.playerSettings == null || !IsSurgeryPatient(pawn, map))
            {
                return;
            }
            if ((int)pawn.playerSettings.medCare < (int)MedicalCareCategory.NormalOrWorse)
            {
                pawn.playerSettings.medCare = MedicalCareCategory.NormalOrWorse;
            }
        }

        private static bool RemoveBlockedScariaFromSurgeryPatient(Pawn pawn, Map map, bool requireSurgeryPatient, string reason)
        {
            if (!BlockHospitalSurgeryScariaEnabled ||
                pawn == null ||
                pawn.health == null ||
                pawn.health.hediffSet == null ||
                (requireSurgeryPatient && !IsSurgeryPatient(pawn, map)))
            {
                return false;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (hediffs == null)
            {
                return false;
            }
            bool removed = false;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                Hediff hediff = hediffs[i];
                if (hediff == null || hediff.def == null || !string.Equals(hediff.def.defName, "Scaria", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pawn.health.RemoveHediff(hediff);
                removed = true;
            }
            if (removed)
            {
                ClearOngoingTreatmentCache(pawn);
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-surgery-scaria-removed-" + pawn.thingIDNumber, "Removed scaria from Space Services Hospital surgery patient " + ServiceDebugUtility.PawnAuditSummary(pawn) + " (" + (reason ?? "surgery patient") + ")", GenDate.TicksPerHour);
            }
            return removed;
        }

        private static bool ShouldHoldFailedSurgeryAftercare(Pawn pawn)
        {
            if (!HoldFailedSurgeryAftercareEnabled ||
                pawn == null ||
                !pawn.Spawned ||
                ServicePawnUtility.IsTerminalPawn(pawn) ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn) ||
                !ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out Map map, out ServiceGroupRecord record) ||
                record == null ||
                record.serviceKind != "hospital" ||
                record.state == "completed" ||
                record.state == "extracting" ||
                ServiceLifecycleUtility.IsActiveDepartureState(record) ||
                map == null ||
                !SpaceServiceMapDetector.IsServiceActive(map) ||
                !IsSurgeryPatient(pawn, map))
            {
                return false;
            }
            return PawnNeedsFailedSurgeryAftercare(pawn);
        }

        private static bool PawnNeedsFailedSurgeryAftercare(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }
            if (pawn.Downed || pawn.health.hediffSet.BleedRateTotal > 0.001f)
            {
                return true;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (hediffs == null)
            {
                return false;
            }
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (hediff is Hediff_Injury && hediff.TendableNow(false))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsSurgeryPatient(Pawn pawn, Map map)
        {
            object hospital = HospitalIncidentGate.FindHospitalComponent(map ?? pawn?.MapHeld ?? pawn?.Map);
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            object patientData = patients == null || pawn == null || !patients.Contains(pawn) ? null : patients[pawn];
            object type = patientData == null ? null : Reflect.GetMember(patientData, "Type");
            return type != null && string.Equals(type.ToString(), "Surgery", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SurgeryPatientsEnabled(Map map)
        {
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            return SurgeryPatientsEnabled(hospital);
        }

        private static bool SurgeryPatientsEnabled(object hospital)
        {
            return hospital != null && Reflect.BoolMember(hospital, "AcceptSurgery");
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

        private sealed class OngoingTreatmentDecisionCache
        {
            public int tick;
            public int mapId;
            public int hediffCount;
            public bool result;
            public string reason;
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
