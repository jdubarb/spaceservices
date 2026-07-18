using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class ServiceLifecycleUtility
    {
        private const int HospitalityDepartureDetectionGraceTicks = 2500;
        private const int HospitalityDepartureHardTimeoutTicks = 7500;
        private const int HospitalityBedlessDepartureGraceTicks = 2500 * 16;
        private const int PickupBoardingHardTimeoutTicks = 2500;
        private const int HospitalityArrivalVacuumGearGuardTicks = 2500;
        private const int HospitalityVacuumTransitGuardTicks = 2500;
        private const int HospitalityVacuumProtectionTickInterval = 600;
        private const float HospitalitySafeCellSearchRadius = 12f;
        private const float HospitalityAreaSafeCellSearchRadius = 16f;
        private const int HospitalitySafeCellReachabilityChecks = 24;
        private const int HospitalityPickupClusterPadding = 10;
        private const int PickupInboundHoldTicks = 120;
        private const float VacuumEpsilon = 0.001f;
        private const float VacuumPadDistanceTolerance = 2.5f;
        private const int BlockedDepartureCacheTicks = 60;
        private const int StableActivePawnValidationTicks = 10000;
        private const int StableBedlessCheckTicks = 2500;
        private const int StableLeaveStateCheckTicks = 2500;
        private const int FailedDeparturePadReservationRetryTicks = 600;
        private const int HospitalitySafeCellCacheTicks = 120;
        private const int HospitalityRouteSafeCellScanLimit = 120;
        private const int HospitalityArrivalRouteStopCooldownTicks = 3000;
        private const int HospitalityDangerLeaveCacheTicks = 60;
        public const int DepartureHoldWanderJobTicks = 1;
        private const float DepartureHoldWanderRadius = 14f;
        private const int DepartureHoldWanderCandidateLimit = 64;
        private static readonly List<ServiceDepartureBlock> CachedDepartureBlocks = new List<ServiceDepartureBlock>();
        private static int cachedDepartureBlocksTick = -999999;
        private static readonly Dictionary<string, CachedHospitalitySafeCells> HospitalitySafeCellCache = new Dictionary<string, CachedHospitalitySafeCells>();
        private static readonly Dictionary<int, int> HospitalityArrivalRouteStopTickByPawn = new Dictionary<int, int>();
        private static readonly Dictionary<int, CachedHospitalityLeaveDelay> HospitalityDangerLeaveDelayCache = new Dictionary<int, CachedHospitalityLeaveDelay>();
        private static readonly Dictionary<string, int> DeparturePadRejectionLogTickByRecord = new Dictionary<string, int>();
        private static readonly HashSet<int> DepartureHoldJobValidationInProgress = new HashSet<int>();

        public static int NextTickInterval(List<ServiceGroupRecord> records)
        {
            if (records != null && records.Any(HospitalityVacuumProtectionActive))
            {
                return HospitalityVacuumProtectionTickInterval;
            }
            if (records != null && records.Any(record => record != null && (record.state == "pickupInbound" || record.state == "boardingPickup" || record.state == "departing")))
            {
                return 30;
            }
            if (records != null && records.Any(record => record != null && record.state == "departureHold"))
            {
                return 250;
            }
            return 250;
        }

        public static void ClearTransientCaches()
        {
            HospitalitySafeCellCache.Clear();
            HospitalityArrivalRouteStopTickByPawn.Clear();
            HospitalityDangerLeaveDelayCache.Clear();
            DeparturePadRejectionLogTickByRecord.Clear();
            CachedDepartureBlocks.Clear();
            cachedDepartureBlocksTick = -999999;
        }

        public static void RegisterPawns(Map map, string kind, IEnumerable<Pawn> pawns)
        {
            RegisterPawns(map, kind, pawns, null);
        }

        public static void RegisterPawns(Map map, string kind, IEnumerable<Pawn> pawns, Thing arrivalPad)
        {
            if (map == null || pawns == null || !SpaceServiceMapDetector.IsServiceActive(map))
            {
                return;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return;
            }

            List<Pawn> list = pawns.Where(p => !ServicePawnUtility.IsTerminalPawn(p)).Distinct().ToList();
            if (list.Count == 0)
            {
                return;
            }

            ServiceGroupRecord existing = comp.serviceGroups.FirstOrDefault(active =>
                active != null &&
                active.state != "completed" &&
                active.pawns != null &&
                active.pawns.Any(pawn => list.Contains(pawn)));
            if (existing != null)
            {
                ServiceDebugUtility.LogAudit("RegisterPawns merged kind=" + kind + " existing=" + existing.id + " incoming=" + PawnSummary(list) + " arrivalPad=" + ServiceDebugUtility.ThingAuditSummary(arrivalPad));
                bool changed = false;
                foreach (Pawn pawn in list)
                {
                    if (!existing.pawns.Contains(pawn))
                    {
                        existing.pawns.Add(pawn);
                        changed = true;
                    }
                }
                existing.timeoutTick = Math.Max(existing.timeoutTick, Find.TickManager.TicksGame + GenDate.TicksPerDay * 3);
                if (existing.arrivalPad == null && arrivalPad != null && !arrivalPad.Destroyed)
                {
                    existing.arrivalPad = arrivalPad;
                    changed = true;
                }
                if (changed || existing.serviceKind != "hospitality" || existing.lastHospitalityTransitTick != Find.TickManager.TicksGame)
                {
                    EnsureHospitalityVacuumProtection(map, existing, "arrival merge");
                    ForceHospitalityVacuumTransit(map, existing, "arrival merge");
                }
                MarkRecordDirty(map, existing, "merged arriving service pawns");
                return;
            }

            ServiceGroupRecord record = new ServiceGroupRecord
            {
                id = "SS-" + Find.UniqueIDsManager.GetNextThingID(),
                serviceKind = kind,
                state = "arrived",
                arrivalTick = Find.TickManager.TicksGame,
                timeoutTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * 3,
                arrivalPad = arrivalPad,
                pawns = list
            };

            if (kind != "hospital" && kind != "hospitality")
            {
                ServiceUse use = kind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient;
                Thing pad = ServicePadUtility.TryReserveServicePad(map, use, record.id);
                if (pad != null)
                {
                    record.reservedPad = pad;
                }
            }

            comp.serviceGroups.Add(record);
            ServiceDebugUtility.Log("Registered " + kind + " service group " + record.id + " pawns=" + list.Count + " padReserved=" + (record.reservedPad != null) + " arrivalPad=" + (arrivalPad != null));
            ServiceDebugUtility.LogAudit("RegisterPawns new " + RecordAudit(record) + " pawns=" + PawnSummary(list) + " arrivalPad=" + ServiceDebugUtility.ThingAuditSummary(arrivalPad));
            EnsureHospitalityVacuumProtection(map, record, "arrival");
            ForceHospitalityVacuumTransit(map, record, "arrival");
            MarkRecordDirty(map, record, "registered arriving service pawns");
        }

        public static bool ReleaseGroup(Map map, string groupId, string reason)
        {
            if (map == null || string.IsNullOrEmpty(groupId))
            {
                return false;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return false;
            }
            ServiceGroupRecord record = comp.serviceGroups.FirstOrDefault(group => group != null && group.id == groupId);
            if (record == null)
            {
                return false;
            }
            BeginDeparture(map, record, reason);
            MarkRecordDirty(map, record, "group release requested");
            ServiceDebugUtility.Log("Released service group " + groupId + ": " + reason);
            return true;
        }

        public static bool ClearGroupReservation(Map map, string groupId, string reason)
        {
            if (map == null || string.IsNullOrEmpty(groupId))
            {
                return false;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return false;
            }
            ServiceGroupRecord record = comp.serviceGroups.FirstOrDefault(group => group != null && group.id == groupId);
            if (record == null)
            {
                return false;
            }
            ReleaseRecord(record);
            record.reservedPad = null;
            record.state = "completed";
            ServiceDelayLodgerUtility.CleanupRecord(record, QuestEndOutcome.Unknown);
            ServiceDebugUtility.Log("Cleared service group " + groupId + " reservation: " + reason);
            return true;
        }

        public static bool RequestDepartureForPawn(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                ServiceDebugUtility.LogAudit("RequestDepartureForPawn no service record pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
                return false;
            }
            if (ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                if (ServiceDelayLodgerUtility.IsDelayLodger(record, pawn))
                {
                    BeginDeparture(map, record, reason);
                    return true;
                }
                ReleasePawn(pawn, "pawn became player-owned before service departure");
                ServiceDebugUtility.LogAudit("RequestDepartureForPawn ignored player-owned pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
                return false;
            }
            ServiceDebugUtility.LogAudit("RequestDepartureForPawn found " + RecordAudit(record) + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
            if (record.state == "completed" || record.state == "extracting")
            {
                ServiceDebugUtility.LogAudit("RequestDepartureForPawn skipped duplicate request for terminal record " + RecordAudit(record));
                return true;
            }
            BeginDeparture(map, record, reason);
            return true;
        }

        public static bool ReleasePawn(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
            }
            if (record.pawns != null)
            {
                bool pickupDeparture = record.state == "pickupInbound" || record.state == "boardingPickup";
                foreach (Pawn terminalPawn in record.pawns.Where(ServicePawnUtility.IsTerminalPawn).Where(tracked => tracked != null).Distinct().ToList())
                {
                    // External mod callbacks can release dead pawns before our lifecycle tick sees them.
                    ServicePawnUtility.CleanupTerminalPawnReferences(map, terminalPawn);
                }
                record.pawns.RemoveAll(tracked => tracked == null || tracked == pawn || ServicePawnUtility.IsTerminalPawn(tracked));
                if (record.pawns.Count == 0 && pickupDeparture)
                {
                    DepartureUtility.CompleteDeparture(map ?? (record.reservedPad == null ? null : record.reservedPad.Map), record, "last service pawn removed during pickup: " + (reason ?? "none"));
                    ServiceDebugUtility.Log("Released service group " + record.id + ": " + reason);
                    return true;
                }
            }
            if (record.pawns == null || record.pawns.Count == 0)
            {
                ServiceDelayLodgerUtility.CleanupRecord(record, QuestEndOutcome.Unknown);
                ReleaseRecord(record);
                record.state = "completed";
                ServiceDebugUtility.Log("Released service group " + record.id + ": " + reason);
            }
            else
            {
                ServiceDebugUtility.LogAudit("Released pawn from active service group " + RecordAudit(record) + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
                MarkRecordDirty(map, record, "service pawn released");
            }
            return true;
        }

        public static bool ShouldHoldPawnForServiceDeparture(Pawn pawn)
        {
            Map map;
            ServiceGroupRecord record;
            return TryFindRecordForPawn(pawn, out map, out record) &&
                record != null &&
                record.state == "departureHold";
        }

        public static bool IsIntentionalDelayLodger(Pawn pawn)
        {
            Map map;
            ServiceGroupRecord record;
            return TryFindRecordForPawn(pawn, out map, out record) &&
                ServiceDelayLodgerUtility.IsDelayLodger(record, pawn);
        }

        public static bool ShouldDelayHospitalityLeaveForService(Pawn pawn, out string reason)
        {
            reason = null;
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record) || !HospitalityLeaveManagedByService(record))
            {
                return false;
            }
            if (record.state == "pickupInbound" || record.state == "boardingPickup" || record.state == "extracting" || record.state == "completed")
            {
                return false;
            }
            map = map ?? pawn?.MapHeld ?? pawn?.Map ?? record.reservedPad?.Map ?? record.arrivalPad?.Map;
            if (map == null)
            {
                return false;
            }
            return ShouldDelayServicePickup(map, record, out reason);
        }

        public static bool ShouldDelayHospitalityLordLeaveForService(Lord lord, out string reason)
        {
            reason = null;
            if (lord == null || lord.ownedPawns == null)
            {
                return false;
            }
            foreach (Pawn pawn in lord.ownedPawns.ToList())
            {
                if (ShouldDelayHospitalityLeaveForService(pawn, out reason))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool ShouldDelayHospitalityLordLeaveForServiceDangerOnly(Lord lord, out string reason)
        {
            reason = null;
            if (lord == null || lord.ownedPawns == null)
            {
                return false;
            }
            int now = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            CachedHospitalityLeaveDelay cached;
            if (now > 0 &&
                HospitalityDangerLeaveDelayCache.TryGetValue(lord.loadID, out cached))
            {
                if (cached.createdTick > now)
                {
                    HospitalityDangerLeaveDelayCache.Clear();
                }
                else if (cached.expiresTick > now)
                {
                    reason = cached.reason;
                    return cached.delay;
                }
            }
            bool delay = false;
            foreach (Pawn pawn in lord.ownedPawns.ToList())
            {
                if (ShouldDelayHospitalityLeaveForServiceDangerOnly(pawn, out reason))
                {
                    delay = true;
                    break;
                }
            }
            if (now > 0)
            {
                HospitalityDangerLeaveDelayCache[lord.loadID] = new CachedHospitalityLeaveDelay
                {
                    delay = delay,
                    reason = reason,
                    createdTick = now,
                    expiresTick = now + HospitalityDangerLeaveCacheTicks
                };
            }
            return delay;
        }

        private static bool ShouldDelayHospitalityLeaveForServiceDangerOnly(Pawn pawn, out string reason)
        {
            reason = null;
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record) || !HospitalityLeaveManagedByService(record))
            {
                return false;
            }
            if (record.state == "pickupInbound" || record.state == "boardingPickup" || record.state == "extracting" || record.state == "completed")
            {
                return false;
            }
            map = map ?? pawn?.MapHeld ?? pawn?.Map ?? record.reservedPad?.Map ?? record.arrivalPad?.Map;
            if (map == null)
            {
                return false;
            }
            if (ServiceDangerUtility.DepartureShuttleBlocked(map, record.serviceKind, out reason))
            {
                return true;
            }
            return record.serviceKind == "hospitality" && ServiceDangerUtility.HospitalityTrafficBlocked(map, out reason);
        }

        private static bool ShouldDelayServicePickup(Map map, ServiceGroupRecord record, out string reason)
        {
            reason = null;
            if (map == null || record == null)
            {
                return false;
            }
            if (ServiceDangerUtility.DepartureShuttleBlocked(map, record.serviceKind, out reason))
            {
                return true;
            }
            if (record.serviceKind == "hospitality" && ServiceDangerUtility.HospitalityTrafficBlocked(map, out reason))
            {
                return true;
            }

            ServiceUse use = DepartureUse(record);
            if (record.reservedPad != null && !PadCanSafelyServeDeparture(record.reservedPad, use, record, ShouldBypassGuestArea(record)))
            {
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad == null)
            {
                if (!EnsureReservedDeparturePad(map, record, use))
                {
                    reason = "no usable service pad for pickup";
                    return true;
                }
            }
            return false;
        }

        private static bool HospitalityLeaveManagedByService(ServiceGroupRecord record)
        {
            return record != null &&
                (record.serviceKind == "hospitality" ||
                    (record.serviceKind == "hospital" && record.departureHoldHospitalityHandoffDone));
        }

        public static bool IsActiveDepartureState(ServiceGroupRecord record)
        {
            if (record == null)
            {
                return false;
            }
            return record.state == "departureHold" ||
                record.state == "departing" ||
                record.state == "pickupInbound" ||
                record.state == "boardingPickup";
        }

        public static void ValidateDepartureHoldCurrentJob(Pawn pawn)
        {
            ValidateCurrentTrackedPawnJob(pawn, true);
        }

        public static void ValidateTrackedPawnCurrentJob(Pawn pawn)
        {
            ValidateCurrentTrackedPawnJob(pawn, false);
        }

        private static void ValidateCurrentTrackedPawnJob(Pawn pawn, bool departureHoldOnly)
        {
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.jobs == null)
            {
                return;
            }
            int pawnId = pawn.thingIDNumber;
            if (DepartureHoldJobValidationInProgress.Contains(pawnId))
            {
                return;
            }
            DepartureHoldJobValidationInProgress.Add(pawnId);
            try
            {
                Map map;
                ServiceGroupRecord record;
                if (!TryFindRecordForPawn(pawn, out map, out record) || record == null)
                {
                    return;
                }
                if (record.state == "departureHold")
                {
                    ValidateDepartureHoldRecordCurrentJob(pawn, map, record);
                    return;
                }
                if (!departureHoldOnly)
                {
                    if (ValidateActiveServiceDepartureCurrentJob(pawn, map, record))
                    {
                        return;
                    }
                    ValidateActiveHospitalPatientCurrentJob(pawn, map, record);
                }
            }
            finally
            {
                DepartureHoldJobValidationInProgress.Remove(pawnId);
            }
        }

        private static void ValidateDepartureHoldRecordCurrentJob(Pawn pawn, Map map, ServiceGroupRecord record)
        {
            if (record == null || record.state != "departureHold")
            {
                return;
            }
            if (DepartureHoldExternallyManaged(record))
            {
                if (record.departureHoldHospitalityHandoffDone)
                {
                    RestoreHospitalityVisitLord(pawn.GetLord(), "externally managed departure hold job validation");
                }
                ClearServiceDepartureHoldJob(pawn, record, "externally managed departure hold job validation");
                return;
            }
            map = map ?? pawn.Map;
            if (map == null)
            {
                return;
            }
            Area area = DepartureHoldArea(map, record, pawn);
            if (DepartureHoldNeedsIntervention(map, pawn, area))
            {
                GuideDepartureHoldPawns(record, "current job leaves departure hold");
            }
        }

        private static bool ValidateActiveServiceDepartureCurrentJob(Pawn pawn, Map map, ServiceGroupRecord record)
        {
            if (pawn == null ||
                record == null ||
                record.state == "departureHold" ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                return false;
            }
            if (record.state != "departing" && record.state != "pickupInbound" && record.state != "boardingPickup")
            {
                return false;
            }
            Job current = pawn.CurJob;
            if (current == null || IsServiceMovementJob(current))
            {
                return false;
            }
            bool hospitalityDeparture = record.hospitalityDeparturePrepared && UsesHospitalityDepartureHandling(record);
            if (hospitalityDeparture)
            {
                MaintainPreparedHospitalityDeparture(record, "active service departure job validation");
            }
            else
            {
                map = map ?? pawn.Map;
                if (map == null ||
                    record.reservedPad == null ||
                    !record.reservedPad.Spawned ||
                    SpaceServiceMapDetector.IsGroundsideServiceActive(map) ||
                    !ActiveServiceDepartureJobTargetsMapEdge(pawn, current, map))
                {
                    return false;
                }
            }
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (record.state == "boardingPickup")
            {
                GuideBoardingPawnsToShuttle(record);
            }
            else
            {
                GuideDepartingPawnsToPad(record);
            }
            string jobName = current.def == null ? "unknown" : current.def.defName;
            ServiceDebugUtility.LogAudit("Interrupted non-service job during active service departure pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " job=" + jobName + " record=" + RecordAudit(record));
            return true;
        }

        private static bool ActiveServiceDepartureJobTargetsMapEdge(Pawn pawn, Job job, Map map)
        {
            if (pawn == null || job == null || map == null || !job.targetA.IsValid || !TargetMapEdge(job.targetA, map))
            {
                return false;
            }
            return IsTryingToLeave(pawn) || DepartureJobDef(job.def);
        }

        private static void ValidateActiveHospitalPatientCurrentJob(Pawn pawn, Map map, ServiceGroupRecord record)
        {
            if (pawn == null || record == null || record.serviceKind != "hospital" || record.state != "arrived" || pawn.Faction == Faction.OfPlayer)
            {
                return;
            }
            map = map ?? pawn.Map;
            if (map == null || !HospitalPatchHandlers.IsActiveHospitalPatient(pawn))
            {
                return;
            }
            Job current = pawn.CurJob;
            if (current == null || IsServiceMovementJob(current))
            {
                return;
            }
            bool unsafeNow = !ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, pawn.Position);
            bool unsafeDestination = JobTargetsUnsafeVacuum(current, pawn, map);
            if (!unsafeNow && !unsafeDestination)
            {
                return;
            }
            IntVec3 safeCell = FindHospitalPatientSafeCell(map, record, pawn);
            if (!safeCell.IsValid)
            {
                if (unsafeDestination && !unsafeNow)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                }
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-patient-unsafe-job-no-cell-" + pawn.thingIDNumber, "Could not find a safe cell for active Hospital patient job guard: " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
                return;
            }
            if (safeCell == pawn.Position)
            {
                if (unsafeDestination)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                }
                return;
            }
            if (PawnAlreadyGoingTo(pawn, safeCell))
            {
                return;
            }
            pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
            pawn.jobs.TryTakeOrderedJob(ServiceGotoJob(safeCell, false, LocomotionUrgency.Jog), JobTag.Misc);
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-patient-unsafe-job-guard-" + pawn.thingIDNumber, "Redirected active Hospital patient away from unsafe job target: " + ServiceDebugUtility.PawnAuditSummary(pawn) + " -> " + safeCell, GenDate.TicksPerHour);
        }

        private static IntVec3 FindHospitalPatientSafeCell(Map map, ServiceGroupRecord record, Pawn pawn)
        {
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            Building_Bed bed = RestUtility.FindBedFor(pawn, pawn, false, false, (GuestStatus?)null);
            if (HospitalPatientSafeCell(bed, map, pawn, out IntVec3 bedCell))
            {
                return bedCell;
            }
            Thing pad = record == null ? null : record.reservedPad ?? record.arrivalPad;
            return FindHospitalitySafeCell(map, pad, pawn, true, false);
        }

        private static bool HospitalPatientSafeCell(Building_Bed bed, Map map, Pawn pawn, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (bed == null || map == null || pawn == null || !bed.Spawned || bed.Map != map)
            {
                return false;
            }
            cell = bed.Position;
            return cell.IsValid &&
                cell.InBounds(map) &&
                ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, cell) &&
                pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some);
        }

        private static bool IsServiceMovementJob(Job job)
        {
            return job != null &&
                (job.def == ServiceJobDefUtility.ServiceGoto ||
                    job.def == ServiceJobDefUtility.ServiceDepartureHold ||
                    job.def == ServiceJobDefUtility.BoardServiceShuttle);
        }

        public static bool MarkPawnDirty(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
            }
            MarkRecordDirty(map, record, reason);
            return true;
        }

        public static void MarkRecordDirty(Map map, ServiceGroupRecord record, string reason)
        {
            if (map == null || record == null || record.state == "completed")
            {
                return;
            }
            // Event hooks zero the cheap validation throttles so the next lifecycle pass revalidates immediately.
            // Departure pad reservation retry is intentionally preserved; pad events and releases wake it explicitly.
            record.nextActivePawnValidationTick = 0;
            record.nextHospitalityBedlessCheckTick = 0;
            record.nextLeaveStateCheckTick = 0;
            map.GetComponent<SpaceServicesMapComponent>()?.RequestLifecycleTickSoon(reason);
        }

        private static void CooldownDeferredHospitalityDepartureChecks(ServiceGroupRecord record)
        {
            if (record == null || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            record.nextActivePawnValidationTick = Math.Max(record.nextActivePawnValidationTick, now + StableActivePawnValidationTicks);
            record.nextHospitalityBedlessCheckTick = Math.Max(record.nextHospitalityBedlessCheckTick, now + StableBedlessCheckTicks);
            record.nextLeaveStateCheckTick = Math.Max(record.nextLeaveStateCheckTick, now + StableLeaveStateCheckTicks);
            record.timeoutTick = Math.Max(record.timeoutTick, now + GenDate.TicksPerHour);
        }

        public static bool TryFindRecordForPawn(Pawn pawn, out Map map, out ServiceGroupRecord record)
        {
            map = null;
            record = null;
            if (pawn == null)
            {
                return false;
            }
            IEnumerable<Map> maps = Find.Maps ?? Enumerable.Empty<Map>();
            Map heldMap = pawn.MapHeld;
            if (heldMap != null)
            {
                maps = new[] { heldMap }.Concat(maps.Where(candidate => candidate != heldMap));
            }
            foreach (Map candidate in maps)
            {
                SpaceServicesMapComponent comp = candidate == null ? null : candidate.GetComponent<SpaceServicesMapComponent>();
                ServiceGroupRecord found = comp == null || comp.serviceGroups == null ? null : comp.serviceGroups.FirstOrDefault(group =>
                    group != null &&
                    group.state != "completed" &&
                    group.pawns != null &&
                    group.pawns.Contains(pawn));
                if (found != null)
                {
                    map = candidate;
                    record = found;
                    return true;
                }
            }
            return false;
        }

        public static bool ShouldSuppressHospitalityVacuumApparelJob(Pawn pawn, Job candidateJob)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
            }
            if (record == null ||
                !HospitalityVacuumProtectionActive(record) ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn) ||
                ServicePawnUtility.IsTerminalPawn(pawn) ||
                map == null)
            {
                return false;
            }

            bool pawnIsExposed = pawn.Spawned && ServiceEnvironmentUtility.GetVacuum(pawn.Position, map) > 0.001f;
            bool jobTargetsVacuum = JobTargetsUnsafeVacuum(candidateJob, pawn, map);
            bool transitWindow = HospitalityArrivalTransitGuardActive(record);
            bool departureWindow = IsActiveDepartureState(record);
            if (!pawnIsExposed && !jobTargetsVacuum && !transitWindow && !departureWindow)
            {
                return false;
            }

            MarkRecordDirty(map, record, "suppressed Hospitality apparel job during vacuum transit");
            return true;
        }

        public static bool ShouldProtectHospitalityVacuumApparel(Apparel apparel)
        {
            if (apparel == null || !VacSuitUtility.IsInjectedVacGear(apparel) || VacSuitUtility.InternalVacGearRemovalAllowed)
            {
                return false;
            }
            Pawn wearer = apparel.Wearer;
            if (wearer == null)
            {
                return false;
            }
            return ShouldSuppressHospitalityVacuumApparelJob(wearer, null);
        }

        public static bool DebugForceSocialFight(Pawn initiator, Pawn recipient, out string reason)
        {
            reason = null;
            if (initiator == null || recipient == null || initiator == recipient)
            {
                reason = "pick two different pawns";
                return false;
            }
            if (!CanDebugSocialFightPawn(initiator) || !CanDebugSocialFightPawn(recipient))
            {
                reason = "both pawns must be spawned, awake, non-dead humanlikes";
                return false;
            }
            if ((initiator.MapHeld ?? initiator.Map) != (recipient.MapHeld ?? recipient.Map))
            {
                reason = "pawns are not on the same map";
                return false;
            }

            object interactions = initiator.interactions;
            MethodInfo startSocialFight = interactions == null ? null : AccessTools.Method(interactions.GetType(), "StartSocialFight");
            if (startSocialFight == null)
            {
                reason = "could not find Pawn_InteractionsTracker.StartSocialFight";
                return false;
            }
            try
            {
                ParameterInfo[] parameters = startSocialFight.GetParameters();
                object[] args = parameters.Length >= 2
                    ? new object[] { recipient, "Sentence_SocialFightStarted" }
                    : new object[] { recipient };
                startSocialFight.Invoke(interactions, args);
                reason = "forced social fight: " + initiator.LabelShortCap + " vs " + recipient.LabelShortCap;
                ServiceDebugUtility.LogAudit(reason);
                return true;
            }
            catch (Exception ex)
            {
                reason = "StartSocialFight failed: " + ex.GetType().Name + " " + ex.Message;
                ServiceDebugUtility.LogVerbose(reason);
                return false;
            }
        }

        private static bool CanDebugSocialFightPawn(Pawn pawn)
        {
            return pawn != null &&
                pawn.RaceProps != null &&
                pawn.RaceProps.Humanlike &&
                pawn.Spawned &&
                !pawn.Dead &&
                !pawn.Destroyed &&
                !pawn.Downed;
        }

        public static void Tick(Map map, List<ServiceGroupRecord> records)
        {
            if (map == null || records == null || records.Count == 0)
            {
                return;
            }
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ServiceGroupRecord record = records[i];
                if (record == null || record.state == "completed")
                {
                    ServiceDelayLodgerUtility.CleanupRecord(record, QuestEndOutcome.Unknown);
                    records.RemoveAt(i);
                    continue;
                }
                List<Pawn> previouslyTrackedPawns = DistinctNonNullPawns(record.pawns);
                foreach (Pawn terminalPawn in previouslyTrackedPawns)
                {
                    if (ServicePawnUtility.IsTerminalPawn(terminalPawn))
                    {
                        // Death/destruction skips the normal shuttle extraction path, so clear lord refs here.
                        ServicePawnUtility.CleanupTerminalPawnReferences(map, terminalPawn);
                    }
                }
                if (ShouldValidateActivePawns(record))
                {
                    record.pawns = ActiveTrackedPawns(map, record);
                    record.nextActivePawnValidationTick = Find.TickManager.TicksGame + ActivePawnValidationInterval(record);
                }
                else
                {
                    record.pawns = RemoveTerminalAndDuplicatePawns(record.pawns);
                }
                if (!SamePawnSet(previouslyTrackedPawns, record.pawns))
                {
                    ServiceDebugUtility.LogAudit("ActiveTrackedPawns updated " + RecordAudit(record) + " previous=" + PawnSummary(previouslyTrackedPawns) + " active=" + PawnSummary(record.pawns));
                }
                if (record.pawns.Count == 0)
                {
                    ServiceDebugUtility.Log("Service group " + record.id + " has no active pawns; releasing reservation. Previously tracked: " + PawnSummary(previouslyTrackedPawns));
                    ServiceDelayLodgerUtility.CleanupRecord(record, QuestEndOutcome.Unknown);
                    ReleaseRecord(record);
                    records.RemoveAt(i);
                    continue;
                }
                if (HospitalityVacuumProtectionActive(record))
                {
                    EnsureHospitalityVacuumProtection(map, record, "active service");
                    if (HospitalityArrivalTransitGuardActive(record))
                    {
                        ForceHospitalityVacuumTransit(map, record, "active service");
                    }
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" && SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.hospitalityVacuumGuard)
                {
                    GuardHospitalityGuestsFromVacuum(map, record);
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" &&
                    SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.hospitalityAutoDepartBedlessGuests &&
                    ShouldCheckHospitalityBedless(record) &&
                    HospitalityBedUtility.TryFindBedlessServiceGuest(record, out Pawn bedlessGuest, out string bedlessReason))
                {
                    record.nextHospitalityBedlessCheckTick = Find.TickManager.TicksGame + StableBedlessCheckTicks;
                    if (record.hospitalityBedlessSinceTick <= 0)
                    {
                        record.hospitalityBedlessSinceTick = Find.TickManager.TicksGame;
                    }

                    int bedlessTicks = Find.TickManager.TicksGame - record.hospitalityBedlessSinceTick;
                    if (bedlessTicks > HospitalityBedlessDepartureGraceTicks)
                    {
                        BeginDeparture(map, record, "Hospitality guest without usable bed for 16 hours: " + bedlessReason);
                        ServiceDebugUtility.Log("Routing hospitality group " + record.id + " home after bedless grace expired because " + bedlessReason + " (" + bedlessGuest.LabelShortCap + ")");
                        continue;
                    }

                    // Hospitality can take a while to claim beds after arrival, so give the group time before forcing them home.
                    ServiceDebugUtility.LogThrottled("hospitality-bedless-grace-" + record.id, "Hospitality group " + record.id + " has a bedless guest during grace period: " + bedlessReason + " (" + bedlessGuest.LabelShortCap + "), waited " + (bedlessTicks / GenDate.TicksPerHour) + "/16h", GenDate.TicksPerHour);
                }
                else if (record.serviceKind == "hospitality")
                {
                    if (ShouldCheckHospitalityBedless(record))
                    {
                        record.hospitalityBedlessSinceTick = 0;
                        record.nextHospitalityBedlessCheckTick = Find.TickManager.TicksGame + StableBedlessCheckTicks;
                    }
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" &&
                    ServiceDangerUtility.HospitalityEvacuationRequired(map, out string evacuationReason))
                {
                    BeginDeparture(map, record, evacuationReason);
                    continue;
                }
                if (record.state == "pickupInbound")
                {
                    EnsureHospitalityDeparturePrepared(record);
                    if (DeparturePickupBlocked(record, out _))
                    {
                        continue;
                    }
                    if (PickupTimedOut(map, record))
                    {
                        continue;
                    }
                    if (Find.TickManager.TicksGame >= record.pickupShuttleTouchdownTick)
                    {
                        if (!ReservedPadCanServe(record, DepartureUse(record), out string blockedReason))
                        {
                            if (ShouldLogBlockedDeparture())
                            {
                                ServiceDebugUtility.Log("Pickup shuttle waiting for usable pad: " + blockedReason);
                            }
                            GuideDepartingPawnsToPad(record);
                            continue;
                        }
                        record.state = "boardingPickup";
                        GuideBoardingPawnsToShuttle(record);
                        BoardReadyPawns(map, record);
                        if (HospitalPickupReadyForDownedFallback(record))
                        {
                            DepartureUtility.CompleteDeparture(map, record, "hospital downed patient at pickup shuttle");
                        }
                    }
                    else
                    {
                        GuideDepartingPawnsToPad(record);
                    }
                    continue;
                }
                if (record.state == "boardingPickup")
                {
                    EnsureHospitalityDeparturePrepared(record);
                    if (DeparturePickupBlocked(record, out _))
                    {
                        continue;
                    }
                    BoardReadyPawns(map, record);
                    if (PickupTimedOut(map, record))
                    {
                        continue;
                    }
                    if (!ReservedPadCanServe(record, DepartureUse(record), out string blockedReason))
                    {
                        if (ShouldLogBlockedDeparture())
                        {
                            ServiceDebugUtility.Log("Service pickup boarding waiting: " + blockedReason);
                        }
                        continue;
                    }
                    if (HospitalPickupReadyForDownedFallback(record))
                    {
                        DepartureUtility.CompleteDeparture(map, record, "hospital downed patient at pickup shuttle");
                        continue;
                    }
                    if (ReadyForBoardingCompletion(record))
                    {
                        DepartureUtility.CompleteDeparture(map, record, "service pawns boarded pickup shuttle");
                    }
                    else
                    {
                        GuideBoardingPawnsToShuttle(record);
                    }
                    continue;
                }
                if (record.state == "departureHold")
                {
                    if (TryDeferNativeHospitalityDeparture(record, map, "recovering native Hospitality departure hold"))
                    {
                        continue;
                    }
                    ServiceUse use = DepartureUse(record);
                    bool questLodgerHold = record.departureHoldQuestLodgerHandoffDone;
                    bool hospitalityHandoffHold = record.departureHoldHospitalityHandoffDone;
                    if (hospitalityHandoffHold)
                    {
                        MaintainHospitalityDepartureHoldGuests(record, "externally managed departure hold");
                    }
                    if (!EnsureReservedDeparturePad(map, record, use))
                    {
                        if (questLodgerHold)
                        {
                            ServiceDelayLodgerUtility.EnforceDelayLodgers(record, map);
                        }
                        else if (!hospitalityHandoffHold)
                        {
                            GuideDepartureHoldPawns(record, "waiting for free departure pad");
                        }
                        continue;
                    }
                    if (DeparturePickupBlocked(record, out _))
                    {
                        if (questLodgerHold)
                        {
                            ServiceDelayLodgerUtility.EnforceDelayLodgers(record, map);
                        }
                        continue;
                    }
                    record.state = "departing";
                    record.departureRequestedTick = Find.TickManager.TicksGame;
                    MarkRecordDirty(map, record, "departure hold cleared");
                    continue;
                }
                if (record.state == "departing")
                {
                    if (record.serviceKind == "hospital")
                    {
                        BeginHospitalDeparture(map, record, "waiting for free departure pad");
                        continue;
                    }
                    if (record.serviceKind == "hospitality" && !EnsureReservedDeparturePad(map, record, ServiceUse.Guest))
                    {
                        continue;
                    }
                    if (record.serviceKind == "hospitality" && DeparturePickupBlocked(record, out string blockedReason))
                    {
                        TryDeferNativeHospitalityDeparture(record, map, blockedReason);
                        continue;
                    }
                    EnsureHospitalityDeparturePrepared(record);
                    if (ReadyForExtraction(record))
                    {
                        if (record.serviceKind == "hospitality" && ServiceDangerUtility.HospitalityTrafficBlocked(map, out string dangerReason))
                        {
                            ServiceDebugUtility.LogThrottled("hospitality-pickup-danger-" + record.id, "Hospitality pickup delayed for service group " + record.id + ": " + dangerReason, GenDate.TicksPerHour);
                            EnterDepartureHold(record, dangerReason);
                            continue;
                        }
                        BeginPickupShuttle(record, "service pawns waiting outside departure pad");
                    }
                    else if (record.serviceKind == "hospitality" && HospitalityDepartureTimedOut(record))
                    {
                        if (CanSafelyForceHospitalityPickup(record))
                        {
                            BeginPickupShuttle(record, "hospitality guests waiting near departure pad");
                        }
                        else
                        {
                            record.departureRequestedTick = Find.TickManager.TicksGame;
                            GuideDepartingPawnsToPad(record);
                            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-departure-timeout-wait-" + record.id, "Hospitality departure timeout extended while service pawns reach pickup staging: " + RecordAudit(record), GenDate.TicksPerHour);
                        }
                    }
                    else
                    {
                        GuideDepartingPawnsToPad(record);
                    }
                    if (record.serviceKind != "hospital" && record.serviceKind != "hospitality" && Find.TickManager.TicksGame > record.departureRequestedTick + GenDate.TicksPerHour)
                    {
                        DepartureUtility.CompleteDeparture(map, record, "departure timeout fallback");
                    }
                    continue;
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" &&
                    Find.TickManager.TicksGame > record.arrivalTick + HospitalityDepartureDetectionGraceTicks &&
                    ShouldCheckLeaveState(record))
                {
                    Pawn detachedRescuedGuest = record.pawns.FirstOrDefault(IsDetachedRescuedHospitalityServicePawn);
                    if (detachedRescuedGuest != null)
                    {
                        ServiceGroupRecord departureRecord = SplitDetachedRescuedHospitalityPawn(map, records, record, detachedRescuedGuest);
                        BeginDeparture(map, departureRecord, "tracked Hospitality service guest recovered outside visitor lord: " + detachedRescuedGuest.LabelShortCap);
                        continue;
                    }
                    if (record.pawns.Any(IsTryingToLeave))
                    {
                        BeginDeparture(map, record, "Hospitality group entered departure state");
                        continue;
                    }
                }
                if (record.serviceKind != "hospital" && record.serviceKind != "hospitality" && record.pawns.Any(IsTryingToLeave))
                {
                    BeginDeparture(map, record, "service lord entered departure state");
                    continue;
                }
                if (Find.TickManager.TicksGame > record.timeoutTick)
                {
                    if (ShouldDelayHospitalTimeout(record, out string treatmentReason))
                    {
                        record.timeoutTick = Find.TickManager.TicksGame + GenDate.TicksPerHour;
                        MarkRecordDirty(map, record, "hospital patient still needs ongoing treatment");
                        ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-timeout-treatment-delay-" + record.id, "Hospital service timeout delayed for ongoing treatment: " + treatmentReason, GenDate.TicksPerHour);
                        continue;
                    }
                    BeginDeparture(map, record, "service visit timeout");
                }
            }
        }

        private static bool ShouldDelayHospitalTimeout(ServiceGroupRecord record, out string reason)
        {
            reason = null;
            if (record == null ||
                !string.Equals(record.serviceKind, "hospital", StringComparison.OrdinalIgnoreCase) ||
                record.pawns == null)
            {
                return false;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || ServicePawnUtility.IsTerminalPawn(pawn))
                {
                    continue;
                }
                if (HospitalPatchHandlers.ShouldKeepHospitalPatientForOngoingTreatment(pawn, out string pawnReason))
                {
                    reason = pawn.LabelShortCap + ": " + pawnReason;
                    return true;
                }
            }
            return false;
        }

        private static bool ShouldLogBlockedDeparture()
        {
            return SpaceServicesMod.Settings != null &&
                SpaceServicesMod.Settings.debugLogging &&
                Find.TickManager != null &&
                Find.TickManager.TicksGame % 2500 == 0;
        }

        private static bool ShouldCheckHospitalityBedless(ServiceGroupRecord record)
        {
            return record == null ||
                record.nextHospitalityBedlessCheckTick <= 0 ||
                Find.TickManager == null ||
                Find.TickManager.TicksGame >= record.nextHospitalityBedlessCheckTick;
        }

        private static bool ShouldCheckLeaveState(ServiceGroupRecord record)
        {
            if (record == null || Find.TickManager == null)
            {
                return true;
            }
            if (record.nextLeaveStateCheckTick > 0 && Find.TickManager.TicksGame < record.nextLeaveStateCheckTick)
            {
                return false;
            }
            record.nextLeaveStateCheckTick = Find.TickManager.TicksGame + StableLeaveStateCheckTicks;
            return true;
        }

        private static bool ShouldValidateActivePawns(ServiceGroupRecord record)
        {
            if (record == null || Find.TickManager == null)
            {
                return true;
            }
            if (record.state != "arrived")
            {
                return true;
            }
            return record.nextActivePawnValidationTick <= 0 || Find.TickManager.TicksGame >= record.nextActivePawnValidationTick;
        }

        private static int ActivePawnValidationInterval(ServiceGroupRecord record)
        {
            return record != null && record.state == "arrived" ? StableActivePawnValidationTicks : NextTickInterval(null);
        }

        private static bool HospitalityArrivalVacuumProtectionActive(ServiceGroupRecord record)
        {
            return record != null &&
                record.serviceKind == "hospitality" &&
                record.state == "arrived" &&
                Find.TickManager != null &&
                Find.TickManager.TicksGame <= record.arrivalTick + HospitalityArrivalVacuumGearGuardTicks;
        }

        private static bool HospitalityArrivalTransitGuardActive(ServiceGroupRecord record)
        {
            return record != null &&
                record.serviceKind == "hospitality" &&
                record.state == "arrived" &&
                Find.TickManager != null &&
                Find.TickManager.TicksGame <= record.arrivalTick + HospitalityVacuumTransitGuardTicks;
        }

        private static bool HospitalityVacuumProtectionActive(ServiceGroupRecord record)
        {
            if (!HospitalityVacuumProtectionRecord(record))
            {
                return false;
            }
            if (!HospitalityVacuumProtectionAllowed(record))
            {
                return false;
            }
            if (record.serviceKind == "hospitality" &&
                (HospitalityArrivalVacuumProtectionActive(record) || HospitalityArrivalTransitGuardActive(record)))
            {
                return true;
            }
            return IsActiveDepartureState(record);
        }

        private static bool HospitalityVacuumProtectionRecord(ServiceGroupRecord record)
        {
            return record != null &&
                (record.serviceKind == "hospitality" ||
                    (record.serviceKind == "hospital" && record.departureHoldHospitalityHandoffDone));
        }

        private static bool HospitalityVacuumProtectionAllowed(ServiceGroupRecord record)
        {
            Map map = record?.reservedPad?.Map ??
                record?.arrivalPad?.Map ??
                record?.pawns?.FirstOrDefault(pawn => pawn != null && pawn.Spawned)?.Map;
            return map != null && SpaceServiceMapDetector.IsServiceEligible(map);
        }

        private static void EnsureHospitalityVacuumProtection(Map map, ServiceGroupRecord record, string reason)
        {
            if (!HospitalityVacuumProtectionRecord(record) || record.pawns == null)
            {
                return;
            }
            Map effectiveMap = map ?? record.reservedPad?.Map ?? record.arrivalPad?.Map ?? record.pawns.FirstOrDefault(pawn => pawn != null && pawn.Spawned)?.Map;
            if (effectiveMap == null || !SpaceServiceMapDetector.IsServiceEligible(effectiveMap))
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                if (VacSuitUtility.VacuumResistance(pawn) + 0.001f >= VacSuitUtility.PracticalVacuumSuitTarget)
                {
                    continue;
                }
                if (!HospitalityPawnNeedsVacuumGear(effectiveMap, record, pawn))
                {
                    continue;
                }
                // Hospitality can re-apply guest outfits after spawn; restore service-provided vacuum protection before exposure.
                VacSuitUtility.EnsurePracticalVacuumProtection(pawn);
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-vac-gear-" + pawn.thingIDNumber, "Restored Hospitality guest vacuum gear (" + (reason ?? "service") + "): " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            }
        }

        private static void ForceHospitalityVacuumTransit(Map map, ServiceGroupRecord record, string reason)
        {
            if (map == null || record == null || record.serviceKind != "hospitality" || record.pawns == null)
            {
                return;
            }
            if (!SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (ticksGame > 0 && record.lastHospitalityTransitTick == ticksGame)
            {
                return;
            }
            record.lastHospitalityTransitTick = ticksGame;
            Thing pad = record.arrivalPad ?? record.reservedPad;
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Downed || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                bool unsafeNow = ServiceEnvironmentUtility.GetVacuum(pawn.Position, map) > VacuumEpsilon;
                bool unsafeDestination = PawnCurrentJobTargetsUnsafeVacuum(pawn, map);
                // During the short arrival handoff, also stop long Hospitality routes once
                // they have reached the first safe guest-area tile.
                bool requireAtmosphere = HospitalityArrivalTransitGuardActive(record);
                bool stopArrivalRoute = requireAtmosphere &&
                    CurrentJobTargetCell(pawn, map).IsValid &&
                    !HospitalityArrivalRouteStopRecentlyHandled(pawn, ticksGame);
                if (!unsafeNow && !unsafeDestination && !stopArrivalRoute)
                {
                    continue;
                }
                // During arrival, "safe because they are wearing a suit" is not enough.
                // Hospitality can re-apply guest outfits if we let them idle on the pad,
                // so move them to real atmosphere before normal guest AI takes over.
                IntVec3 safeCell = FindHospitalityRouteSafeCell(map, pawn, requireAtmosphere);
                if (!safeCell.IsValid && (unsafeNow || unsafeDestination))
                {
                    safeCell = FindHospitalitySafeCell(map, pad, pawn, requireAtmosphere, true);
                }
                if (!safeCell.IsValid)
                {
                    continue;
                }
                Job current = pawn.CurJob;
                if (safeCell == pawn.Position)
                {
                    if ((unsafeDestination || stopArrivalRoute) && current != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                        if (stopArrivalRoute)
                        {
                            MarkHospitalityArrivalRouteStopHandled(pawn, ticksGame);
                        }
                    }
                    continue;
                }
                if (PawnAlreadyGoingTo(pawn, safeCell))
                {
                    continue;
                }
                // Own the brief exposed walk so Hospitality outfit jobs cannot make guests stop to change helmets in vacuum.
                Job job = ServiceGotoJob(safeCell, false, LocomotionUrgency.Sprint);
                if (pawn.CurJob != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                }
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-vac-transit-" + pawn.thingIDNumber, "Forced Hospitality guest vacuum transit (" + (reason ?? "service") + "): " + ServiceDebugUtility.PawnAuditSummary(pawn) + " -> " + safeCell, GenDate.TicksPerHour);
            }
        }

        private static bool HospitalityArrivalRouteStopRecentlyHandled(Pawn pawn, int ticksGame)
        {
            if (pawn == null)
            {
                return true;
            }
            int handledTick;
            if (!HospitalityArrivalRouteStopTickByPawn.TryGetValue(pawn.thingIDNumber, out handledTick))
            {
                return false;
            }
            return ticksGame <= handledTick + HospitalityArrivalRouteStopCooldownTicks;
        }

        private static void MarkHospitalityArrivalRouteStopHandled(Pawn pawn, int ticksGame)
        {
            if (pawn == null)
            {
                return;
            }
            HospitalityArrivalRouteStopTickByPawn[pawn.thingIDNumber] = ticksGame;
        }

        private static bool HospitalityPawnNeedsVacuumGear(Map map, ServiceGroupRecord record, Pawn pawn)
        {
            if (record == null || pawn == null)
            {
                return false;
            }
            if (IsActiveDepartureState(record))
            {
                Thing departurePad = record.reservedPad ?? record.arrivalPad;
                if ((pawn.Spawned && VacSuitUtility.ShouldAutoSuitForVacuum(pawn.Map, pawn.Position)) ||
                    VacSuitUtility.ShouldAutoSuitForVacuum(departurePad))
                {
                    return true;
                }
                if (map != null && SpaceServiceMapDetector.IsServiceEligible(map))
                {
                    ServiceUse use = DepartureUse(record);
                    return ServicePadUtility.AllDeparturePads(map, use).Any(VacSuitUtility.ShouldAutoSuitForVacuum);
                }
                return false;
            }
            if (map == null)
            {
                return false;
            }
            if (pawn.Position.IsValid && VacSuitUtility.ShouldAutoSuitForVacuum(map, pawn.Position))
            {
                return true;
            }
            Thing pad = record.arrivalPad ?? record.reservedPad;
            return pad != null &&
                pad.Spawned &&
                pad.Map == map &&
                pawn.Position.DistanceToSquared(pad.Position) <= 144 &&
                VacSuitUtility.ShouldAutoSuitForVacuum(pad);
        }

        private static void GuardHospitalityGuestsFromVacuum(Map map, ServiceGroupRecord record)
        {
            if (map == null || record == null || record.serviceKind != "hospitality" || record.pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Downed)
                {
                    continue;
                }
                bool unsafeNow = !ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, pawn.Position);
                bool unsafeDestination = PawnCurrentJobTargetsUnsafeVacuum(pawn, map);
                if (!unsafeNow && !unsafeDestination)
                {
                    continue;
                }
                IntVec3 safeCell = FindHospitalityRouteSafeCell(map, pawn, false);
                if (!safeCell.IsValid)
                {
                    safeCell = FindHospitalitySafeCell(map, record.reservedPad, pawn, false, true);
                }
                if (!safeCell.IsValid)
                {
                    continue;
                }
                if (safeCell == pawn.Position)
                {
                    if (unsafeDestination && pawn.CurJob != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                    }
                    continue;
                }
                Job job = ServiceGotoJob(safeCell, false, LocomotionUrgency.Jog);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
        }

        private static bool PawnCurrentJobTargetsUnsafeVacuum(Pawn pawn, Map map)
        {
            Job job = pawn == null ? null : pawn.CurJob;
            return JobTargetsUnsafeVacuum(job, pawn, map);
        }

        private static bool JobTargetsUnsafeVacuum(Job job, Pawn pawn, Map map)
        {
            if (job == null || map == null)
            {
                return false;
            }
            return TargetCellUnsafeForPawn(job.targetA, pawn, map) ||
                TargetCellUnsafeForPawn(job.targetB, pawn, map) ||
                TargetCellUnsafeForPawn(job.targetC, pawn, map);
        }

        private static bool TargetCellUnsafeForPawn(LocalTargetInfo target, Pawn pawn, Map map)
        {
            if (!target.IsValid || !target.Cell.IsValid || !target.Cell.InBounds(map))
            {
                return false;
            }
            return !ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, target.Cell);
        }

        private static IntVec3 FindHospitalitySafeCell(Map map, Thing pad, Pawn pawn, bool requireAtmosphere, bool preferGuestArea)
        {
            if (preferGuestArea)
            {
                IntVec3 guestAreaCell = FindHospitalityGuestAreaSafeCell(map, pad, pawn, requireAtmosphere);
                if (guestAreaCell.IsValid)
                {
                    return guestAreaCell;
                }
            }
            IntVec3 nearPawn = BestSafeReachableCell(GenRadial.RadialCellsAround(pawn.Position, HospitalitySafeCellSearchRadius, true), map, pawn, pawn.Position, requireAtmosphere);
            if (nearPawn.IsValid)
            {
                return nearPawn;
            }
            if (pad != null && pad.Spawned)
            {
                IntVec3 nearPad = BestSafeReachableCell(GenRadial.RadialCellsAround(pad.Position, HospitalitySafeCellSearchRadius, true), map, pawn, pad.Position, requireAtmosphere);
                if (nearPad.IsValid)
                {
                    return nearPad;
                }
            }
            if (!preferGuestArea)
            {
                IntVec3 guestAreaCell = FindHospitalityGuestAreaSafeCell(map, pad, pawn, requireAtmosphere);
                if (guestAreaCell.IsValid)
                {
                    return guestAreaCell;
                }
            }
            Room room = pad != null && pad.Spawned ? pad.Position.GetRoom(map) : null;
            return room == null ? IntVec3.Invalid : BestSafeReachableCell(room.Cells, map, pawn, pad.Position, requireAtmosphere);
        }

        private static IntVec3 FindHospitalityRouteSafeCell(Map map, Pawn pawn, bool requireAtmosphere)
        {
            Area area = HospitalityGuestArea(map, pawn);
            if (map == null || pawn == null || !pawn.Spawned || area == null || area.Map != map || area.TrueCount == 0)
            {
                return IntVec3.Invalid;
            }

            List<IntVec3> cells = new List<IntVec3>(HospitalityRouteSafeCellScanLimit + 2);
            AddHospitalityRouteCell(cells, pawn.Position);
            if (pawn.pather != null)
            {
                AddHospitalityRouteCell(cells, pawn.pather.nextCell);
                PawnPath currentPath = pawn.pather.curPath;
                if (currentPath != null && currentPath.Found)
                {
                    currentPath.PeekNextCells(HospitalityRouteSafeCellScanLimit, cells);
                }
            }

            IntVec3 routeCell = FirstHospitalityRouteSafeCell(cells, map, pawn, area, requireAtmosphere);
            if (routeCell.IsValid)
            {
                return routeCell;
            }

            IntVec3 target = CurrentJobTargetCell(pawn, map);
            if (!target.IsValid)
            {
                return IntVec3.Invalid;
            }

            PawnPath path = null;
            try
            {
                path = map.pathFinder.FindPathNow(pawn.Position, target, TraverseParms.For(pawn), peMode: PathEndMode.OnCell);
                if (path == null || !path.Found)
                {
                    return IntVec3.Invalid;
                }

                cells.Clear();
                AddHospitalityRouteCell(cells, pawn.Position);
                path.PeekNextCells(HospitalityRouteSafeCellScanLimit, cells);
                return FirstHospitalityRouteSafeCell(cells, map, pawn, area, requireAtmosphere);
            }
            finally
            {
                path?.ReleaseToPool();
            }
        }

        private static void AddHospitalityRouteCell(List<IntVec3> cells, IntVec3 cell)
        {
            if (cells == null || !cell.IsValid || cells.Contains(cell))
            {
                return;
            }
            cells.Add(cell);
        }

        private static IntVec3 FirstHospitalityRouteSafeCell(List<IntVec3> cells, Map map, Pawn pawn, Area area, bool requireAtmosphere)
        {
            foreach (IntVec3 cell in cells ?? Enumerable.Empty<IntVec3>())
            {
                if (IsHospitalityRouteSafeStopCell(cell, map, pawn, area, requireAtmosphere))
                {
                    return cell;
                }
            }
            return IntVec3.Invalid;
        }

        private static bool IsHospitalityRouteSafeStopCell(IntVec3 cell, Map map, Pawn pawn, Area area, bool requireAtmosphere)
        {
            if (map == null || pawn == null || area == null || area.Map != map || !cell.IsValid || !cell.InBounds(map) || !cell.Standable(map) || !area[cell])
            {
                return false;
            }
            Pawn occupyingPawn = cell.GetFirstPawn(map);
            if (occupyingPawn != null && occupyingPawn != pawn)
            {
                return false;
            }
            return IsHospitalitySafeCellCandidate(cell, map, pawn, requireAtmosphere, requireAtmosphere ? 0f : VacSuitUtility.VacuumResistance(pawn));
        }

        private static IntVec3 CurrentJobTargetCell(Pawn pawn, Map map)
        {
            Job job = pawn == null ? null : pawn.CurJob;
            if (job == null || map == null)
            {
                return IntVec3.Invalid;
            }
            if (TargetCellUnsafeForPawn(job.targetA, pawn, map))
            {
                return job.targetA.Cell;
            }
            if (TargetCellUnsafeForPawn(job.targetB, pawn, map))
            {
                return job.targetB.Cell;
            }
            if (TargetCellUnsafeForPawn(job.targetC, pawn, map))
            {
                return job.targetC.Cell;
            }
            if (job.targetA.IsValid && job.targetA.Cell.IsValid && job.targetA.Cell.InBounds(map))
            {
                return job.targetA.Cell;
            }
            if (job.targetB.IsValid && job.targetB.Cell.IsValid && job.targetB.Cell.InBounds(map))
            {
                return job.targetB.Cell;
            }
            if (job.targetC.IsValid && job.targetC.Cell.IsValid && job.targetC.Cell.InBounds(map))
            {
                return job.targetC.Cell;
            }
            return IntVec3.Invalid;
        }

        private static IntVec3 FindHospitalityGuestAreaSafeCell(Map map, Thing pad, Pawn pawn, bool requireAtmosphere)
        {
            Area area = HospitalityGuestArea(map, pawn);
            if (area == null || area.Map != map || area.TrueCount == 0)
            {
                return IntVec3.Invalid;
            }
            if (pad != null && pad.Spawned)
            {
                IntVec3 nearPad = requireAtmosphere
                    ? BestCachedSafeReachableCell(CachedHospitalityAreaSafeCells(map, pad, area, requireAtmosphere), map, pawn, pad.Position)
                    : BestSafeReachableCell(AreaCells(GenRadial.RadialCellsAround(pad.Position, HospitalityAreaSafeCellSearchRadius, true), area, map), map, pawn, pad.Position, requireAtmosphere);
                if (nearPad.IsValid)
                {
                    return nearPad;
                }
            }
            IntVec3 nearPawn = BestSafeReachableCell(AreaCells(GenRadial.RadialCellsAround(pawn.Position, HospitalityAreaSafeCellSearchRadius, true), area, map), map, pawn, pawn.Position, requireAtmosphere);
            if (nearPawn.IsValid)
            {
                return nearPawn;
            }
            return BestSafeReachableCell(AreaCells(area.ActiveCells, area, map), map, pawn, pawn.Position, requireAtmosphere);
        }

        private static IEnumerable<IntVec3> AreaCells(IEnumerable<IntVec3> cells, Area area, Map map)
        {
            foreach (IntVec3 cell in cells ?? Enumerable.Empty<IntVec3>())
            {
                if (cell.IsValid && cell.InBounds(map) && area[cell])
                {
                    yield return cell;
                }
            }
        }

        private static Area HospitalityGuestArea(Map map, Pawn pawn)
        {
            Area area = HospitalityPawnGuestArea(pawn);
            if (area != null && area.Map == map && area.TrueCount > 0)
            {
                return area;
            }
            return HospitalityDefaultGuestArea(map);
        }

        private static Area HospitalityPawnGuestArea(Pawn pawn)
        {
            if (pawn == null || pawn.AllComps == null)
            {
                return null;
            }
            foreach (ThingComp comp in pawn.AllComps)
            {
                if (comp != null && comp.GetType().FullName == "Hospitality.CompGuest")
                {
                    return Reflect.GetMember(comp, "GuestArea") as Area;
                }
            }
            return null;
        }

        private static Area HospitalityDefaultGuestArea(Map map)
        {
            IEnumerable components = map == null ? null : Reflect.GetMember(map, "components") as IEnumerable;
            foreach (object component in components ?? Enumerable.Empty<object>())
            {
                if (component != null && component.GetType().FullName == "Hospitality.Hospitality_MapComponent")
                {
                    return Reflect.GetMember(component, "defaultAreaRestriction") as Area;
                }
            }
            return null;
        }

        private static List<IntVec3> CachedHospitalityAreaSafeCells(Map map, Thing pad, Area area, bool requireAtmosphere)
        {
            string key = HospitalitySafeCellCacheKey(map, pad, area, requireAtmosphere);
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            CachedHospitalitySafeCells cached;
            if (HospitalitySafeCellCache.TryGetValue(key, out cached) && ticksGame <= cached.expiresTick)
            {
                return cached.cells;
            }

            List<IntVec3> cells = new List<IntVec3>();
            foreach (IntVec3 cell in AreaCells(GenRadial.RadialCellsAround(pad.Position, HospitalityAreaSafeCellSearchRadius, true), area, map))
            {
                if (IsHospitalitySafeCellCandidate(cell, map, null, requireAtmosphere, 0f))
                {
                    AddSafeCellCandidate(cells, cell, pad.Position);
                }
            }
            HospitalitySafeCellCache[key] = new CachedHospitalitySafeCells
            {
                expiresTick = ticksGame + HospitalitySafeCellCacheTicks,
                cells = cells
            };
            return cells;
        }

        private static string HospitalitySafeCellCacheKey(Map map, Thing pad, Area area, bool requireAtmosphere)
        {
            return (map == null ? 0 : map.uniqueID) + "|" +
                (pad == null ? 0 : pad.thingIDNumber) + "|" +
                (area == null ? -1 : area.ID) + "|" +
                (requireAtmosphere ? "atm" : "safe");
        }

        private static IntVec3 BestSafeReachableCell(IEnumerable<IntVec3> cells, Map map, Pawn pawn, IntVec3 origin, bool requireAtmosphere)
        {
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }

            // Pathfinding is the expensive part of the arrival vacuum guard.
            // Shortlist cheap safe cells first, then run reachability on only the best few.
            List<IntVec3> candidates = new List<IntVec3>(HospitalitySafeCellReachabilityChecks);
            float resistance = requireAtmosphere ? 0f : VacSuitUtility.VacuumResistance(pawn);
            foreach (IntVec3 cell in cells ?? Enumerable.Empty<IntVec3>())
            {
                if (!IsHospitalitySafeCellCandidate(cell, map, pawn, requireAtmosphere, resistance))
                {
                    continue;
                }
                AddSafeCellCandidate(candidates, cell, origin);
            }

            return BestPathReachableCell(candidates, map, pawn);
        }

        private static IntVec3 BestCachedSafeReachableCell(List<IntVec3> candidates, Map map, Pawn pawn, IntVec3 origin)
        {
            if (candidates == null || candidates.Count == 0 || map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            return BestPathReachableCell(candidates.OrderBy(cell => cell.DistanceToSquared(origin)).Take(HospitalitySafeCellReachabilityChecks), map, pawn);
        }

        private static IntVec3 BestPathReachableCell(IEnumerable<IntVec3> candidates, Map map, Pawn pawn)
        {
            if (candidates == null || map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            IntVec3 best = IntVec3.Invalid;
            float bestPathCost = float.MaxValue;
            int bestDistance = int.MaxValue;
            foreach (IntVec3 cell in candidates)
            {
                if (!cell.InBounds(map) || !cell.Standable(map) || (cell.GetFirstPawn(map) != null && cell != pawn.Position))
                {
                    continue;
                }
                if (cell == pawn.Position)
                {
                    return cell;
                }
                float pathCost = PathCostToCell(map, pawn, cell);
                if (pathCost < 0f)
                {
                    continue;
                }
                int distance = cell.DistanceToSquared(pawn.Position);
                if (pathCost < bestPathCost || (pathCost == bestPathCost && distance < bestDistance))
                {
                    best = cell;
                    bestPathCost = pathCost;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static float PathCostToCell(Map map, Pawn pawn, IntVec3 cell)
        {
            PawnPath path = null;
            try
            {
                path = map.pathFinder.FindPathNow(pawn.Position, cell, TraverseParms.For(pawn), peMode: PathEndMode.OnCell);
                if (path == null || !path.Found)
                {
                    return -1f;
                }
                return path.TotalCost;
            }
            finally
            {
                path?.ReleaseToPool();
            }
        }

        private static bool IsHospitalitySafeCellCandidate(IntVec3 cell, Map map, Pawn pawn, bool requireAtmosphere, float resistance)
        {
            Pawn occupyingPawn = cell.GetFirstPawn(map);
            if (!cell.InBounds(map) || !cell.Standable(map) || (occupyingPawn != null && (pawn == null || cell != pawn.Position)))
            {
                return false;
            }
            float vacuum = ServiceEnvironmentUtility.GetVacuum(cell, map);
            if (requireAtmosphere)
            {
                return vacuum <= VacuumEpsilon;
            }
            return resistance + VacuumEpsilon >= vacuum;
        }

        private static void AddSafeCellCandidate(List<IntVec3> candidates, IntVec3 cell, IntVec3 origin)
        {
            int distance = cell.DistanceToSquared(origin);
            int insertAt = candidates.Count;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (distance < candidates[i].DistanceToSquared(origin))
                {
                    insertAt = i;
                    break;
                }
            }
            if (insertAt >= HospitalitySafeCellReachabilityChecks && candidates.Count >= HospitalitySafeCellReachabilityChecks)
            {
                return;
            }
            candidates.Insert(insertAt, cell);
            if (candidates.Count > HospitalitySafeCellReachabilityChecks)
            {
                candidates.RemoveAt(HospitalitySafeCellReachabilityChecks);
            }
        }

        private static List<Pawn> ActiveTrackedPawns(Map map, ServiceGroupRecord record)
        {
            List<Pawn> pawns = RemoveTerminalAndDuplicatePawns(record == null ? null : record.pawns);
            if (record == null || record.serviceKind != "hospital" || pawns.Count == 0)
            {
                if (record != null && record.serviceKind == "hospitality")
                {
                    for (int i = pawns.Count - 1; i >= 0; i--)
                    {
                        if (!IsActiveHospitalityServicePawn(pawns[i]))
                        {
                            pawns.RemoveAt(i);
                        }
                    }
                    return pawns;
                }
                return pawns;
            }

            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary hospitalPatients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (hospitalPatients == null)
            {
                return pawns;
            }

            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if (!pawn.Spawned && !hospitalPatients.Contains(pawn))
                {
                    pawns.RemoveAt(i);
                }
            }
            return pawns;
        }

        private static List<Pawn> RemoveTerminalAndDuplicatePawns(List<Pawn> source)
        {
            List<Pawn> pawns = new List<Pawn>();
            if (source == null)
            {
                return pawns;
            }
            HashSet<Pawn> seen = new HashSet<Pawn>();
            foreach (Pawn pawn in source)
            {
                if (pawn != null && !ServicePawnUtility.IsTerminalPawn(pawn) && seen.Add(pawn))
                {
                    pawns.Add(pawn);
                }
            }
            return pawns;
        }

        private static bool IsActiveHospitalityServicePawn(Pawn pawn)
        {
            if (ServicePawnUtility.IsTerminalPawn(pawn))
            {
                return false;
            }
            if (ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                // Hospitality join offers and recruit-guest mods turn visitors into colonists; never route them to extraction.
                return false;
            }
            if (!pawn.Spawned)
            {
                return pawn.Downed || HospitalityBedUtility.IsRescuedGuest(pawn);
            }
            return true;
        }

        private static void BeginDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.state == "completed" || record.state == "extracting")
            {
                ServiceDebugUtility.LogAudit("BeginDeparture skipped terminal record " + RecordAudit(record) + " reason=" + (reason ?? "none"));
                return;
            }
            bool alreadyDeparting = IsActiveDepartureState(record);
            ServiceDebugUtility.LogAudit("BeginDeparture enter " + RecordAudit(record) + " reason=" + (reason ?? "none") + " pawns=" + PawnSummary(record.pawns));
            if (TryDeferNativeHospitalityDeparture(record, map, reason))
            {
                return;
            }
            if (!alreadyDeparting)
            {
                record.nextDeparturePadReservationTick = 0;
                MarkRecordDirty(map, record, "departure started");
            }
            if (record.serviceKind == "hospital")
            {
                BeginHospitalDeparture(map, record, reason);
                return;
            }
            if (record.reservedPad == null)
            {
                if (!EnsureReservedDeparturePad(map, record, ServiceUse.Guest))
                {
                    ServiceDebugUtility.LogAudit("BeginDeparture no hospitality departure pad " + RecordAudit(record));
                    WaitForDeparturePad(map, record, "waiting for free departure pad: " + (reason ?? "departure requested"));
                    return;
                }
            }
            if (DeparturePickupBlocked(record, out _))
            {
                return;
            }
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager.TicksGame;
                ServiceDebugUtility.Log("Routing " + record.serviceKind + " service group " + record.id + " to departure pad: " + reason);
            }
            EnsureHospitalityDeparturePrepared(record);
            if (ReadyForExtraction(record))
            {
                ServiceDebugUtility.LogAudit("BeginDeparture ready for immediate pickup " + RecordAudit(record));
                BeginPickupShuttle(record, reason);
                return;
            }
            GuideDepartingPawnsToPad(record);
        }

        private static bool TryDeferNativeHospitalityDeparture(ServiceGroupRecord record, Map map, string reason)
        {
            if (!CanDeferNativeHospitalityDeparture(record))
            {
                return false;
            }
            map = map ?? RecordMap(record);
            if (map == null || !ShouldDelayServicePickup(map, record, out string delayReason))
            {
                return false;
            }

            record.state = "arrived";
            record.departureRequestedTick = 0;
            record.pickupShuttleTouchdownTick = 0;
            record.pickupShuttleThingDefName = null;
            record.pickupShuttleVisualDefName = null;
            RestoreNativeHospitalityVisitState(record, reason ?? delayReason);
            ClearDeferredNativeDepartureJobs(record, map, reason ?? delayReason);
            CooldownDeferredHospitalityDepartureChecks(record);
            ServiceDebugUtility.LogThrottled(
                ServiceLogIntegration.Hospitality,
                "hospitality-departure-deferred-" + record.id,
                "Hospitality departure deferred until Space Services pickup is available for group " + record.id + ": " + (delayReason ?? reason ?? "pickup blocked"),
                GenDate.TicksPerHour);
            return true;
        }

        private static bool CanDeferNativeHospitalityDeparture(ServiceGroupRecord record)
        {
            return record != null &&
                record.serviceKind == "hospitality" &&
                !record.hospitalityDeparturePrepared &&
                record.pawns != null &&
                record.pawns.Any(pawn => pawn != null && !ServicePawnUtility.IsTerminalPawn(pawn) && !ServicePawnUtility.IsPlayerOwnedPawn(pawn));
        }

        private static bool DepartureHoldExternallyManaged(ServiceGroupRecord record)
        {
            return record != null &&
                (record.departureHoldHospitalityHandoffDone || record.departureHoldQuestLodgerHandoffDone);
        }

        private static void MaintainHospitalityDepartureHoldGuests(ServiceGroupRecord record, string reason)
        {
            if (record == null || !record.departureHoldHospitalityHandoffDone || record.pawns == null)
            {
                return;
            }
            foreach (Lord lord in record.pawns
                .Select(pawn => pawn == null ? null : pawn.GetLord())
                .Where(lord => lord != null)
                .Distinct()
                .ToList())
            {
                RestoreHospitalityVisitLord(lord, reason);
            }
            foreach (Pawn pawn in record.pawns)
            {
                ClearServiceDepartureHoldJob(pawn, record, reason);
            }
        }

        private static void ClearServiceDepartureHoldJob(Pawn pawn, ServiceGroupRecord record, string reason)
        {
            if (pawn == null || pawn.jobs == null || pawn.CurJob == null)
            {
                return;
            }
            if (pawn.CurJob.def != ServiceJobDefUtility.ServiceDepartureHold)
            {
                return;
            }
            pawn.jobs.ClearQueuedJobs();
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            ServiceDebugUtility.LogAudit("Cleared service departure hold from managed delay pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none") + " record=" + RecordAudit(record));
        }

        private static Map RecordMap(ServiceGroupRecord record)
        {
            if (record == null)
            {
                return null;
            }
            return record.reservedPad?.Map ??
                record.arrivalPad?.Map ??
                record.pawns?.FirstOrDefault(pawn => pawn != null && pawn.Spawned)?.Map;
        }

        private static void RestoreNativeHospitalityVisitState(ServiceGroupRecord record, string reason)
        {
            if (record == null || record.pawns == null)
            {
                return;
            }
            foreach (Lord lord in record.pawns
                .Select(pawn => pawn == null ? null : pawn.GetLord())
                .Where(lord => lord != null)
                .Distinct()
                .ToList())
            {
                RestoreHospitalityVisitLord(lord, reason);
            }
            foreach (Pawn pawn in record.pawns)
            {
                ClearServiceDepartureHoldJob(pawn, record, reason);
            }
        }

        private static void RestoreHospitalityVisitLord(Lord lord, string reason)
        {
            if (lord == null)
            {
                return;
            }
            object lordJob = lord.LordJob;
            bool wasLeaving = false;
            if (lordJob != null && lordJob.GetType().FullName == "Hospitality.LordJob_VisitColony")
            {
                object leaving = Reflect.GetMember(lordJob, "leaving");
                wasLeaving = leaving is bool leavingFlag && leavingFlag;
                if (wasLeaving)
                {
                    Reflect.SetMember(lordJob, "leaving", false);
                }
            }
            StateGraph graph = Reflect.GetMember(lord, "graph") as StateGraph;
            LordToil visitToil = graph?.lordToils?.FirstOrDefault(toil => toil != null && toil.GetType().FullName == "Hospitality.LordToil_VisitPoint");
            if (visitToil == null)
            {
                ServiceDebugUtility.LogAudit("Could not restore Hospitality visit toil for delayed departure lord=" + LordLabel(lord) + " reason=" + (reason ?? "none"));
                return;
            }
            if (lord.CurLordToil != visitToil)
            {
                lord.GotoToil(visitToil);
            }
            else if (!wasLeaving)
            {
                return;
            }
            else
            {
                visitToil.UpdateAllDuties();
            }
            ServiceDebugUtility.LogAudit("Restored Hospitality visit toil for delayed service departure lord=" + LordLabel(lord) + " reason=" + (reason ?? "none"));
        }

        private static void ClearDeferredNativeDepartureJobs(ServiceGroupRecord record, Map map, string reason)
        {
            if (record == null || record.pawns == null)
            {
                return;
            }
            map = map ?? RecordMap(record);
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.jobs == null || pawn.CurJob == null || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                Map pawnMap = map ?? pawn.Map;
                Job current = pawn.CurJob;
                bool nativeDepartureJob =
                    JobTargetsMapEdge(current, pawnMap) ||
                    JobTargetsUnsafeVacuum(current, pawn, pawnMap) ||
                    DepartureJobDef(current.def);
                if (!nativeDepartureJob)
                {
                    continue;
                }
                pawn.jobs.ClearQueuedJobs();
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                string jobName = current.def == null ? "unknown" : current.def.defName;
                ServiceDebugUtility.LogAudit("Cleared native departure job after delaying Hospitality pickup pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " job=" + jobName + " reason=" + (reason ?? "pickup blocked") + " record=" + RecordAudit(record));
            }
        }

        private static void EnsureHospitalityDeparturePrepared(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospitality")
            {
                return;
            }
            if (record.hospitalityDeparturePrepared)
            {
                MaintainPreparedHospitalityDeparture(record, "active Hospitality departure");
                return;
            }
            ServiceDebugUtility.LogAudit("EnsureHospitalityDeparturePrepared begin " + RecordAudit(record));
            HospitalityBedUtility.PrepareGuestsForServiceDeparture(record);
            if (record.reservedPad != null && SpaceServiceMapDetector.IsServiceEligible(record.reservedPad.Map))
            {
                EnsureHospitalityVacuumProtection(record.reservedPad.Map, record, "departure");
            }
            record.hospitalityDeparturePrepared = true;
            MaintainPreparedHospitalityDeparture(record, "prepared Hospitality departure");
            ServiceDebugUtility.LogAudit("EnsureHospitalityDeparturePrepared end " + RecordAudit(record) + " pawns=" + PawnSummary(record.pawns));
        }

        private static void EnsureDelayGuestDeparturePrepared(ServiceGroupRecord record)
        {
            if (record == null ||
                record.serviceKind != "hospital")
            {
                return;
            }
            if (record.hospitalityDeparturePrepared)
            {
                MaintainPreparedHospitalityDeparture(record, "active delay guest departure");
                return;
            }
            bool prepared = false;
            ServiceDebugUtility.LogAudit("EnsureDelayGuestDeparturePrepared begin " + RecordAudit(record));
            if (record.departureHoldHospitalityHandoffDone)
            {
                HospitalityBedUtility.PrepareDelayGuestsForServiceDeparture(record);
                prepared = true;
            }
            if (record.departureHoldQuestLodgerHandoffDone)
            {
                ServiceDelayLodgerUtility.PrepareDelayLodgersForDeparture(record);
                prepared = true;
            }
            if (prepared)
            {
                record.hospitalityDeparturePrepared = true;
                MaintainPreparedHospitalityDeparture(record, "prepared delay guest departure");
            }
            ServiceDebugUtility.LogAudit("EnsureDelayGuestDeparturePrepared end " + RecordAudit(record) + " pawns=" + PawnSummary(record.pawns));
        }

        private static void MaintainPreparedHospitalityDeparture(ServiceGroupRecord record, string reason)
        {
            if (record == null ||
                !record.hospitalityDeparturePrepared ||
                record.state == "departureHold" ||
                !UsesHospitalityDepartureHandling(record) ||
                record.pawns == null)
            {
                return;
            }
            if (record.state != "departing" && record.state != "pickupInbound" && record.state != "boardingPickup")
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed || pawn.Dead || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                HospitalityBedUtility.DetachPreparedDepartureGuest(pawn, reason);
            }
        }

        private static void BeginHospitalDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (!HospitalPatientCanWalkToDeparture(record))
            {
                if (record.reservedPad != null)
                {
                    ServiceDebugUtility.LogAudit("BeginHospitalDeparture releasing pad while patient cannot walk " + RecordAudit(record));
                    ReleaseRecord(record);
                    record.reservedPad = null;
                }
                if (record.state != "departing")
                {
                    record.state = "departing";
                    record.departureRequestedTick = Find.TickManager.TicksGame;
                    MarkRecordDirty(map, record, "hospital patient departure waiting for mobility");
                }
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture delayed until patient can walk " + RecordAudit(record));
                return;
            }
            if (record.reservedPad != null && !ReservedPadStillExists(record))
            {
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture releasing missing pad " + RecordAudit(record));
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            ServiceUse use = DepartureUse(record);
            if (record.reservedPad != null && !PadCanSafelyServeDeparture(record.reservedPad, use, record, ShouldBypassGuestArea(record)))
            {
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture releasing unsafe pad " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad == null)
            {
                if (EnsureReservedDeparturePad(map, record, use))
                {
                    ServiceDebugUtility.LogAudit("BeginHospitalDeparture reserved pad result " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
                }
            }
            if (record.reservedPad == null)
            {
                WaitForDeparturePad(map, record, "waiting for free departure pad: " + (reason ?? "hospital patient departure requested"));
                EnterDepartureHold(record, "waiting for free departure pad: " + (reason ?? "hospital patient departure requested"));
                return;
            }
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager.TicksGame;
                ServiceDebugUtility.Log("Routing hospital patient to departure pad: " + reason);
            }
            if (!ReservedPadCanServe(record, use, out string blockedReason))
            {
                if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
                {
                    ServiceDebugUtility.Log("Hospital patient departure waiting: " + blockedReason);
                }
                WaitForDeparturePad(map, record, blockedReason);
                EnterDepartureHold(record, blockedReason);
                return;
            }
            if (DeparturePickupBlocked(record, out _))
            {
                return;
            }
            EnsureDelayGuestDeparturePrepared(record);
            if (!HospitalReadyForPickupLaunch(record))
            {
                GuideDepartingPawnsToPad(record);
                return;
            }
            BeginPickupShuttle(record, reason);
            GuideDepartingPawnsToPad(record);
        }

        private static bool HospitalPatientCanWalkToDeparture(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospital" || record.pawns == null)
            {
                return true;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned)
                {
                    continue;
                }
                if (pawn.Downed ||
                    pawn.health == null ||
                    pawn.health.capacities == null ||
                    !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HospitalReadyForPickupLaunch(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospital")
            {
                return ReadyForExtraction(record);
            }
            if (ReadyForExtraction(record))
            {
                return true;
            }
            if (HospitalPickupReadyForDownedFallback(record))
            {
                return true;
            }
            ServiceDebugUtility.LogAudit("Hospital pickup launch delayed until patient reaches departure staging " + RecordAudit(record));
            return false;
        }

        private static bool EnsureReservedDeparturePad(Map map, ServiceGroupRecord record, ServiceUse use)
        {
            if (record == null)
            {
                return false;
            }
            if (record.reservedPad != null && PadCanSafelyServeDeparture(record.reservedPad, use, record, ShouldBypassGuestArea(record)))
            {
                ServiceDebugUtility.LogAudit("EnsureReservedDeparturePad keeping existing " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
                record.nextDeparturePadReservationTick = 0;
                return true;
            }
            if (record.reservedPad == null &&
                Find.TickManager != null &&
                record.nextDeparturePadReservationTick > Find.TickManager.TicksGame)
            {
                return false;
            }
            ServiceDebugUtility.LogAudit("EnsureReservedDeparturePad finding replacement " + RecordAudit(record) + " oldPad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            ReleaseRecord(record);
            record.reservedPad = TryReserveBestDeparturePad(map, use, record);
            ServiceDebugUtility.LogAudit("EnsureReservedDeparturePad result " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            if (record.reservedPad == null)
            {
                if (Find.TickManager != null)
                {
                    record.nextDeparturePadReservationTick = Find.TickManager.TicksGame + FailedDeparturePadReservationRetryTicks;
                }
                if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging && ShouldLogBlockedDeparture())
                {
                    ServiceDebugUtility.Log("Service group " + record.id + " waiting for a usable departure pad.");
                }
                return false;
            }
            record.nextDeparturePadReservationTick = 0;
            return true;
        }

        private static void BeginPickupShuttle(ServiceGroupRecord record, string reason)
        {
            if (record == null || record.reservedPad == null || !record.reservedPad.Spawned)
            {
                return;
            }
            if (record.state == "pickupInbound" || record.state == "boardingPickup")
            {
                return;
            }
            if (DeparturePickupBlocked(record, out _))
            {
                return;
            }
            EnsureDelayGuestDeparturePrepared(record);

            if (!TryClearPadFootprintForServiceShuttle(record.reservedPad, record.serviceKind, "pickup service group " + record.id, out string clearReason))
            {
                ServiceDebugUtility.LogThrottled(ServiceDebugUtility.IntegrationForServiceKind(record.serviceKind), "departure-pad-occupied-" + record.id, "Pickup shuttle delayed while clearing pad for service group " + record.id + ": " + clearReason, 250);
                EnterDepartureHold(record, clearReason);
                return;
            }

            ShuttleVisual visual = ShuttleVisual.Resolve(record.serviceKind, record.pickupShuttleVisualDefName);
            if (visual == null)
            {
                ServiceDebugUtility.LogAudit("BeginPickupShuttle no shuttle visual, completing directly " + RecordAudit(record));
                DepartureUtility.CompleteDeparture(record.reservedPad.Map, record, reason);
                return;
            }

            record.state = "pickupInbound";
            record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks;
            record.pickupShuttleThingDefName = visual.shipThingDef.defName;
            record.pickupShuttleVisualDefName = visual.id;
            int staleShuttles = ServiceShuttleUtility.CleanupServiceShuttlesForPad(record.reservedPad, visual);
            if (staleShuttles > 0)
            {
                ServiceDebugUtility.LogAudit("Cleaned stale service shuttle visuals before pickup spawn count=" + staleShuttles + " record=" + RecordAudit(record));
            }
            ServiceShuttleUtility.SpawnArrival(record.reservedPad.Map, record.reservedPad.Position, visual);
            MarkRecordDirty(record.reservedPad.Map, record, "pickup shuttle inbound");
            ServiceDebugUtility.Log("Pickup shuttle inbound for " + record.serviceKind + " service group " + record.id + ": " + reason);
            ServiceDebugUtility.LogAudit("BeginPickupShuttle " + RecordAudit(record) + " touchdownTick=" + record.pickupShuttleTouchdownTick + " ship=" + record.pickupShuttleThingDefName + " visual=" + record.pickupShuttleVisualDefName + " reason=" + (reason ?? "none"));
        }

        private static bool DeparturePickupBlocked(ServiceGroupRecord record, out string reason)
        {
            reason = null;
            if (record == null || record.reservedPad == null || !record.reservedPad.Spawned)
            {
                return false;
            }
            Map map = record.reservedPad.Map;
            if (ServiceDangerUtility.DepartureShuttleBlocked(map, record.serviceKind, out reason))
            {
                ServiceDebugUtility.LogThrottled(ServiceDebugUtility.IntegrationForServiceKind(record.serviceKind), "departure-hazard-" + record.id + "-" + reason, "Pickup shuttle delayed for service group " + record.id + ": " + reason, GenDate.TicksPerHour);
                CancelActivePickup(record, reason);
                EnterDepartureHold(record, reason);
                return true;
            }
            if (record.serviceKind == "hospitality" && ServiceDangerUtility.HospitalityTrafficBlocked(map, out reason))
            {
                ServiceDebugUtility.LogThrottled("hospitality-pickup-danger-" + record.id, "Hospitality pickup delayed for service group " + record.id + ": " + reason, GenDate.TicksPerHour);
                CancelActivePickup(record, reason);
                EnterDepartureHold(record, reason);
                return true;
            }
            return false;
        }

        private static void CancelActivePickup(ServiceGroupRecord record, string reason)
        {
            if (record == null || record.reservedPad == null || !record.reservedPad.Spawned)
            {
                return;
            }
            if (record.state != "pickupInbound" && record.state != "boardingPickup")
            {
                return;
            }
            ShuttleVisual visual = ShuttleVisual.Resolve(record.serviceKind, record.pickupShuttleVisualDefName);
            int removed = ServiceShuttleUtility.CleanupServiceShuttlesForPad(record.reservedPad, visual);
            record.pickupShuttleTouchdownTick = 0;
            record.pickupShuttleThingDefName = null;
            ServiceDebugUtility.LogAudit("Canceled active pickup due to blocked departure removedShuttles=" + removed + " reason=" + (reason ?? "none") + " record=" + RecordAudit(record));
        }

        private static void WaitForDeparturePad(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null)
            {
                return;
            }
            bool changed = false;
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                changed = true;
                ServiceDebugUtility.Log("Service group " + record.id + " waiting for a usable departure pad: " + (reason ?? "departure requested"));
            }
            if (map == null)
            {
                map = record.reservedPad?.Map ??
                    record.arrivalPad?.Map ??
                    record.pawns?.FirstOrDefault(pawn => pawn != null && pawn.Spawned)?.Map;
            }
            if (changed && map != null)
            {
                MarkRecordDirty(map, record, "waiting for departure pad");
            }
        }

        private static void EnterDepartureHold(ServiceGroupRecord record, string reason)
        {
            if (record == null)
            {
                return;
            }
            bool changed = false;
            if (record.state != "departureHold")
            {
                record.state = "departureHold";
                if (record.departureRequestedTick <= 0)
                {
                    record.departureRequestedTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                }
                changed = true;
                ServiceDebugUtility.LogAudit("EnterDepartureHold " + RecordAudit(record) + " reason=" + (reason ?? "service pickup blocked"));
            }
            TryPrepareDepartureHoldHospitality(record);
            if (record.departureHoldQuestLodgerHandoffDone)
            {
                Map lodgerMap = record.reservedPad == null ? null : record.reservedPad.Map;
                ServiceDelayLodgerUtility.EnforceDelayLodgers(record, lodgerMap);
            }
            else if (record.departureHoldHospitalityHandoffDone)
            {
                // Hospitality owns delay guest needs while Space Services waits for pickup conditions.
            }
            else
            {
                GuideDepartureHoldPawns(record, reason);
            }
            Map map = record.reservedPad == null ? null : record.reservedPad.Map;
            if (changed && map != null)
            {
                MarkRecordDirty(map, record, "departure hold entered");
            }
        }

        private static void TryPrepareDepartureHoldHospitality(ServiceGroupRecord record)
        {
            if (record == null ||
                record.serviceKind != "hospital" ||
                record.departureHoldHospitalityHandoffDone)
            {
                return;
            }
            Map map = record.reservedPad == null ? null : record.reservedPad.Map;
            if (map == null && record.pawns != null)
            {
                Pawn spawned = record.pawns.FirstOrDefault(pawn => pawn != null && pawn.Spawned);
                map = spawned == null ? null : spawned.Map;
            }
            string reason = "Hospitality handoff already attempted";
            if (!record.departureHoldHospitalityHandoffAttempted)
            {
                record.departureHoldHospitalityHandoffAttempted = true;
                if (HospitalityBedUtility.TryConvertHospitalPatientsToDelayGuests(record, map, out reason))
                {
                    ServiceDebugUtility.LogAudit("Prepared hospital departure hold with Hospitality guest handoff " + RecordAudit(record));
                    return;
                }
            }
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-delay-guest-handoff-skip-" + record.id, "Hospital departure hold using Space Services fallback: " + (reason ?? "Hospitality unavailable"), GenDate.TicksPerHour);
            if (HospitalityBedUtility.DelayGuestApiAvailable())
            {
                record.departureHoldHospitalityHandoffAttempted = false;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-delay-guest-handoff-retry-" + record.id, "Hospital departure hold waiting for Hospitality handoff eligibility: " + (reason ?? "patient not ready"), GenDate.TicksPerHour);
                return;
            }
            if (record.departureHoldQuestLodgerHandoffAttempted || record.departureHoldQuestLodgerHandoffDone)
            {
                return;
            }
            record.departureHoldQuestLodgerHandoffAttempted = true;
            if (ServiceDelayLodgerUtility.TryConvertHospitalPatientsToQuestLodgers(record, map, out string lodgerReason))
            {
                ServiceDebugUtility.LogAudit("Prepared hospital departure hold with vanilla quest lodger handoff " + RecordAudit(record));
                return;
            }
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-delay-lodger-handoff-skip-" + record.id, "Hospital departure hold could not create temporary lodgers: " + (lodgerReason ?? "unavailable"), GenDate.TicksPerHour);
        }

        private static void GuideDepartureHoldPawns(ServiceGroupRecord record, string reason)
        {
            if (record == null || record.pawns == null)
            {
                return;
            }
            Map map = record.reservedPad == null ? null : record.reservedPad.Map;
            if (map == null)
            {
                Pawn spawned = record.pawns.FirstOrDefault(pawn => pawn != null && pawn.Spawned);
                map = spawned == null ? null : spawned.Map;
            }
            if (map == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.jobs == null || !pawn.Spawned || pawn.Downed || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                Area area = DepartureHoldArea(map, record, pawn);
                if (!DepartureHoldNeedsIntervention(map, pawn, area))
                {
                    continue;
                }
                IntVec3 holdCell = DepartureHoldWanderCell(map, record, pawn);
                if (!holdCell.IsValid)
                {
                    continue;
                }
                pawn.jobs.ClearQueuedJobs();
                pawn.jobs.StartJob(ServiceDepartureHoldJob(holdCell), JobCondition.InterruptForced, tag: JobTag.Misc);
                ServiceDebugUtility.LogAudit("GuideDepartureHoldPawns ordered holdCell=" + holdCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "pickup blocked") + " record=" + RecordAudit(record));
            }
        }

        private static IntVec3 DepartureHoldWanderCell(Map map, ServiceGroupRecord record, Pawn pawn)
        {
            if (map == null || pawn == null || !pawn.Spawned)
            {
                return IntVec3.Invalid;
            }
            Area area = DepartureHoldArea(map, record, pawn);
            if (area == null || area.Map != map || area.TrueCount == 0)
            {
                return FindHospitalitySafeCell(map, record == null ? null : record.reservedPad, pawn, true, true);
            }

            List<IntVec3> candidates = new List<IntVec3>(DepartureHoldWanderCandidateLimit);
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(pawn.Position, DepartureHoldWanderRadius, true))
            {
                if (DepartureHoldWanderCandidate(cell, map, pawn, area, false))
                {
                    candidates.Add(cell);
                    if (candidates.Count >= DepartureHoldWanderCandidateLimit)
                    {
                        break;
                    }
                }
            }
            IntVec3 randomNearby = RandomReachableCell(candidates, map, pawn);
            if (randomNearby.IsValid)
            {
                return randomNearby;
            }

            candidates.Clear();
            int seen = 0;
            foreach (IntVec3 cell in AreaCells(area.ActiveCells, area, map))
            {
                if (!DepartureHoldWanderCandidate(cell, map, pawn, area, false))
                {
                    continue;
                }
                seen++;
                if (candidates.Count < DepartureHoldWanderCandidateLimit)
                {
                    candidates.Add(cell);
                }
                else if (Rand.Range(0, seen) < DepartureHoldWanderCandidateLimit)
                {
                    candidates[Rand.Range(0, candidates.Count)] = cell;
                }
            }
            IntVec3 randomArea = RandomReachableCell(candidates, map, pawn);
            if (randomArea.IsValid)
            {
                return randomArea;
            }

            if (DepartureHoldWanderCandidate(pawn.Position, map, pawn, area, true))
            {
                return pawn.Position;
            }
            return FindHospitalityGuestAreaSafeCell(map, record == null ? null : record.reservedPad, pawn, true);
        }

        private static bool DepartureHoldWanderCandidate(IntVec3 cell, Map map, Pawn pawn, Area area, bool allowCurrent)
        {
            if (area == null || map == null || pawn == null || !cell.IsValid || !cell.InBounds(map) || !area[cell])
            {
                return false;
            }
            if (!allowCurrent && cell.DistanceToSquared(pawn.Position) < 9)
            {
                return false;
            }
            return !cell.OnEdge(map) && IsHospitalitySafeCellCandidate(cell, map, pawn, true, 0f);
        }

        private static IntVec3 RandomReachableCell(List<IntVec3> candidates, Map map, Pawn pawn)
        {
            if (candidates == null || candidates.Count == 0 || map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            while (candidates.Count > 0)
            {
                int index = Rand.Range(0, candidates.Count);
                IntVec3 cell = candidates[index];
                candidates.RemoveAt(index);
                if (cell == pawn.Position || pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                {
                    return cell;
                }
            }
            return IntVec3.Invalid;
        }

        private static bool DepartureHoldNeedsIntervention(Map map, Pawn pawn, Area area)
        {
            if (map == null || pawn == null || !pawn.Spawned)
            {
                return false;
            }
            Job job = pawn.CurJob;
            if (job == null)
            {
                return !DepartureHoldSafeCell(pawn.Position, map, area);
            }
            if (job.def == ServiceJobDefUtility.ServiceDepartureHold)
            {
                return TargetMapEdge(job.targetA, map) ||
                    TargetCellVacuum(job.targetA, map);
            }
            if (job.def == ServiceJobDefUtility.ServiceGoto || job.def == ServiceJobDefUtility.BoardServiceShuttle)
            {
                return true;
            }
            if (DepartureHoldFreeRoamJob(job.def))
            {
                return true;
            }
            if (IsTryingToLeave(pawn) || DepartureJobDef(job.def))
            {
                return true;
            }
            if (JobTargetsUnsafeVacuum(job, pawn, map))
            {
                return true;
            }
            if (JobTargetsOutsideDepartureHold(job, map, area))
            {
                return true;
            }
            return !DepartureHoldSafeCell(pawn.Position, map, area);
        }

        private static bool DepartureJobDef(JobDef def)
        {
            string defName = def == null ? "" : def.defName ?? "";
            return ContainsAny(defName, "Exit", "Depart", "Leave");
        }

        private static bool DepartureHoldFreeRoamJob(JobDef def)
        {
            string defName = def == null ? "" : def.defName ?? "";
            return defName == "GoForWalk" || defName == "PlayWalking";
        }

        internal static Area DepartureHoldArea(Map map, ServiceGroupRecord record, Pawn pawn)
        {
            if (record == null)
            {
                return null;
            }
            if (record.serviceKind == "hospitality")
            {
                return HospitalityGuestArea(map, pawn) ?? HospitalityDefaultGuestArea(map);
            }
            if (record.serviceKind == "hospital")
            {
                if (record.departureHoldQuestLodgerHandoffDone)
                {
                    return record.departureHoldFallbackArea;
                }
                return record.departureHoldFallbackArea ?? HospitalityDefaultGuestArea(map);
            }
            return null;
        }

        private static bool DepartureHoldSafeVisitorCell(IntVec3 cell, Map map, Area area)
        {
            return map != null &&
                area != null &&
                area.Map == map &&
                cell.IsValid &&
                cell.InBounds(map) &&
                !cell.OnEdge(map) &&
                area[cell] &&
                ServiceEnvironmentUtility.GetVacuum(cell, map) <= VacuumEpsilon;
        }

        private static bool DepartureHoldSafeCell(IntVec3 cell, Map map, Area area)
        {
            if (area != null)
            {
                return DepartureHoldSafeVisitorCell(cell, map, area);
            }
            return map != null &&
                cell.IsValid &&
                cell.InBounds(map) &&
                !cell.OnEdge(map) &&
                ServiceEnvironmentUtility.GetVacuum(cell, map) <= VacuumEpsilon;
        }

        private static bool JobTargetsOutsideDepartureHold(Job job, Map map, Area area)
        {
            if (job == null || map == null)
            {
                return false;
            }
            return TargetOutsideDepartureHold(job.targetA, map, area) ||
                TargetOutsideDepartureHold(job.targetB, map, area) ||
                TargetOutsideDepartureHold(job.targetC, map, area);
        }

        private static bool TargetOutsideDepartureHold(LocalTargetInfo target, Map map, Area area)
        {
            if (!target.IsValid || !target.Cell.IsValid || !target.Cell.InBounds(map))
            {
                return false;
            }
            return !DepartureHoldSafeCell(target.Cell, map, area);
        }

        private static bool JobTargetsMapEdge(Job job, Map map)
        {
            if (job == null || map == null)
            {
                return false;
            }
            return TargetMapEdge(job.targetA, map) ||
                TargetMapEdge(job.targetB, map) ||
                TargetMapEdge(job.targetC, map);
        }

        private static bool TargetMapEdge(LocalTargetInfo target, Map map)
        {
            return target.IsValid &&
                target.Cell.IsValid &&
                target.Cell.InBounds(map) &&
                target.Cell.OnEdge(map);
        }

        private static bool TargetCellVacuum(LocalTargetInfo target, Map map)
        {
            return target.IsValid &&
                target.Cell.IsValid &&
                target.Cell.InBounds(map) &&
                ServiceEnvironmentUtility.GetVacuum(target.Cell, map) > VacuumEpsilon;
        }

        public static bool TryClearPadFootprintForServiceShuttle(Thing pad, string serviceKind, string context, out string reason)
        {
            reason = null;
            Map map = pad == null ? null : pad.Map;
            if (map == null || pad.Destroyed)
            {
                reason = "service pad unavailable";
                return false;
            }

            CellRect padRect = pad.OccupiedRect();
            List<Pawn> pawns = padRect.Cells
                .Select(cell => cell.GetFirstPawn(map))
                .Where(pawn => pawn != null && !pawn.Destroyed && pawn.Spawned)
                .Distinct()
                .ToList();
            foreach (Pawn pawn in pawns)
            {
                if (!TryMovePawnOffPadFootprint(pawn, pad, padRect, out IntVec3 target))
                {
                    reason = pawn.LabelShortCap + " is blocking the landing pad";
                    return false;
                }
                ServiceDebugUtility.LogAudit(ServiceDebugUtility.IntegrationForServiceKind(serviceKind), "Cleared pawn from service pad footprint pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " target=" + target + " context=" + (context ?? "service shuttle"));
            }
            return true;
        }

        private static bool TryMovePawnOffPadFootprint(Pawn pawn, Thing pad, CellRect padRect, out IntVec3 target)
        {
            target = IntVec3.Invalid;
            Map map = pad == null ? null : pad.Map;
            if (pawn == null || !pawn.Spawned || map == null)
            {
                return false;
            }
            if (!padRect.Contains(pawn.Position))
            {
                target = pawn.Position;
                return true;
            }
            target = BestPadClearanceCell(pawn, pad, padRect);
            if (!target.IsValid)
            {
                return false;
            }

            Rot4 rotation = pawn.Rotation;
            IntVec3 oldPosition = pawn.Position;
            try
            {
                pawn.DeSpawn(DestroyMode.Vanish);
                GenSpawn.Spawn(pawn, target, map, rotation, WipeMode.Vanish, false);
                pawn.Notify_Teleported();
                return true;
            }
            catch (Exception ex)
            {
                if (!pawn.Spawned && oldPosition.IsValid)
                {
                    try
                    {
                        GenSpawn.Spawn(pawn, oldPosition, map, rotation, WipeMode.Vanish, false);
                    }
                    catch
                    {
                        // The warning below is the actionable failure; avoid masking it with recovery noise.
                    }
                }
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Core, "Could not clear pawn from pickup pad before shuttle arrival: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static IntVec3 BestPadClearanceCell(Pawn pawn, Thing pad, CellRect padRect)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int radius = 1; radius <= 4; radius++)
            {
                foreach (IntVec3 cell in padRect.ExpandedBy(radius).Cells)
                {
                    if (padRect.Contains(cell) ||
                        !cell.InBounds(map) ||
                        !cell.Standable(map) ||
                        cell.GetFirstPawn(map) != null ||
                        !ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, cell))
                    {
                        continue;
                    }
                    bool sameRoom = SameRoomAsPad(pad, cell);
                    int score = cell.DistanceToSquared(pawn.Position) +
                        (sameRoom ? 0 : 1000) +
                        (radius * 100);
                    if (score < bestScore)
                    {
                        best = cell;
                        bestScore = score;
                    }
                }
                if (best.IsValid)
                {
                    return best;
                }
            }
            return best;
        }

        private static Thing TryReserveBestDeparturePad(Map map, ServiceUse use, ServiceGroupRecord record)
        {
            if (map == null || record == null || string.IsNullOrEmpty(record.id))
            {
                return null;
            }
            List<Pawn> pawns = record.pawns == null ? new List<Pawn>() : record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).ToList();
            // Pick only pads the service pawns can survive at, then prefer matching priority and reasonable vacuum routing.
            List<Thing> candidates = ServicePadUtility.AllDeparturePads(map, use)
                .Where(pad => PadCanSafelyServe(pad, use, pawns, record.id, ShouldBypassGuestArea(record)))
                .ToList();
            if (candidates.Count == 0)
            {
                if (!MatchingDepartureModePadOperationalOrReserved(map, use, record.id))
                {
                    candidates = DepartureModeFallbackPads(map, use, record)
                        .Where(pad => PadCanSafelyServeDeparture(pad, use, record, ShouldBypassGuestArea(record)))
                        .ToList();
                    if (candidates.Count > 0)
                    {
                        ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad using mode fallback record=" + record.id + " use=" + use + " pads=" + candidates.Count);
                    }
                }
            }
            foreach (Thing pad in OrderedDeparturePads(map, record, candidates, pawns, use))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.TryReserve(record.id))
                {
                    ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad reserved record=" + record.id + " use=" + use + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " score=" + DeparturePadScore(map, record, pad, pawns, use));
                    return pad;
                }
                ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad candidate could not reserve record=" + record.id + " use=" + use + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad));
            }
            ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad no candidates record=" + record.id + " use=" + use + " pawns=" + PawnSummary(pawns));
            LogDeparturePadRejections(map, use, record, pawns);
            return null;
        }

        private static void LogDeparturePadRejections(Map map, ServiceUse use, ServiceGroupRecord record, List<Pawn> pawns)
        {
            if (map == null || record == null)
            {
                return;
            }
            if (Find.TickManager != null)
            {
                string key = record.id + ":" + use;
                int now = Find.TickManager.TicksGame;
                int lastTick;
                if (DeparturePadRejectionLogTickByRecord.TryGetValue(key, out lastTick) &&
                    now < lastTick + FailedDeparturePadReservationRetryTicks)
                {
                    return;
                }
                DeparturePadRejectionLogTickByRecord[key] = now;
            }
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map).Where(pad => pad != null).Distinct())
            {
                ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad rejected record=" + record.id + " use=" + use + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " reason=" + DeparturePadRejectionReason(pad, use, record, pawns, ShouldBypassGuestArea(record)));
            }
        }

        private static string DeparturePadRejectionReason(Thing pad, ServiceUse use, ServiceGroupRecord record, List<Pawn> pawns, bool bypassGuestArea)
        {
            if (pad == null || pad.Destroyed || pad.Map == null)
            {
                return "pad unavailable";
            }
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null)
            {
                return "not a service pad";
            }
            if (!string.IsNullOrEmpty(comp.reservedForGroup) && (record == null || comp.reservedForGroup != record.id))
            {
                return "reserved for " + comp.reservedForGroup;
            }
            string padReason;
            bool modeFallback = AllowsDepartureModeFallback(record, pad, use);
            bool requirementsMet = modeFallback ? comp.MeetsOperationalRequirements(out padReason) : comp.MeetsDepartureRequirements(use, out padReason);
            if (!requirementsMet)
            {
                return padReason ?? "pad mode or operational requirements blocked";
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(pad, pawns, DepartureVacuumSuitTarget(), out string safetyReason))
            {
                return safetyReason ?? "pad is unsafe for one or more pawns";
            }
            if (!PadReachableForPawns(pad, pawns, false, bypassGuestArea, out string reachReason))
            {
                return reachReason ?? "no reachable staging cell";
            }
            return "candidate passed filters but was not selected";
        }

        private static IEnumerable<Thing> OrderedDeparturePads(Map map, ServiceGroupRecord record, List<Thing> candidates, List<Pawn> pawns, ServiceUse use)
        {
            if (candidates == null || candidates.Count == 0)
            {
                yield break;
            }

            foreach (int rank in candidates
                .Select(pad =>
                {
                    CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                    return comp == null ? 99 : comp.PriorityRank(use);
                })
                .Distinct()
                .OrderBy(value => value))
            {
                List<Thing> ranked = candidates.Where(pad =>
                {
                    CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                    return (comp == null ? 99 : comp.PriorityRank(use)) == rank;
                }).ToList();

                foreach (Thing pad in OrderedByVacuumPreference(map, record, ranked, pawns, use))
                {
                    yield return pad;
                }
            }
        }

        private static IEnumerable<Thing> OrderedByVacuumPreference(Map map, ServiceGroupRecord record, List<Thing> candidates, List<Pawn> pawns, ServiceUse use)
        {
            if (!ShouldPreferVacuumDeparturePad(pawns))
            {
                return candidates.OrderBy(pad => DeparturePadScore(map, record, pad, pawns, use));
            }

            List<Thing> vacuumPads = candidates.Where(IsVacuumPad).ToList();
            List<Thing> sealedPads = candidates.Where(pad => !IsVacuumPad(pad)).ToList();
            if (vacuumPads.Count == 0 || sealedPads.Count == 0)
            {
                return candidates.OrderBy(pad => DeparturePadScore(map, record, pad, pawns, use));
            }

            float bestVacuumDistance = vacuumPads.Min(pad => DepartureTravelDistance(record, pad, pawns));
            float bestSealedDistance = sealedPads.Min(pad => DepartureTravelDistance(record, pad, pawns));
            bool vacuumTooFar = bestVacuumDistance > bestSealedDistance * VacuumPadDistanceTolerance * VacuumPadDistanceTolerance;
            IEnumerable<Thing> preferred = vacuumTooFar ? sealedPads : vacuumPads;
            IEnumerable<Thing> fallback = vacuumTooFar ? vacuumPads : sealedPads;
            return preferred
                .OrderBy(pad => DeparturePadScore(map, record, pad, pawns, use))
                .Concat(fallback.OrderBy(pad => DeparturePadScore(map, record, pad, pawns, use)));
        }

        private static float DeparturePadScore(Map map, ServiceGroupRecord record, Thing pad, List<Pawn> pawns, ServiceUse use)
        {
            if (pad == null)
            {
                return float.MaxValue;
            }
            float score = 0f;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || !pawn.Spawned)
                {
                    continue;
                }
                IntVec3 waitCell = DepartureWaitCell(pad, pawn, ShouldBypassGuestArea(record));
                IntVec3 target = waitCell.IsValid ? waitCell : pad.Position;
                score += pawn.Position.DistanceToSquared(target);
            }
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp != null)
            {
                score += comp.PriorityRank(use) * 1000000f;
            }
            return score;
        }

        private static float DepartureTravelDistance(ServiceGroupRecord record, Thing pad, List<Pawn> pawns)
        {
            float total = 0f;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || !pawn.Spawned)
                {
                    continue;
                }
                IntVec3 waitCell = DepartureWaitCell(pad, pawn, ShouldBypassGuestArea(record));
                IntVec3 target = waitCell.IsValid ? waitCell : pad.Position;
                total += pawn.Position.DistanceToSquared(target);
            }
            return total;
        }

        private static bool ShouldPreferVacuumDeparturePad(List<Pawn> pawns)
        {
            float target = DepartureVacuumSuitTarget();
            return pawns != null &&
                pawns.Count > 0 &&
                pawns.All(pawn => pawn != null && !pawn.Destroyed && VacSuitUtility.VacuumResistance(pawn) + 0.001f >= target);
        }

        private static bool IsVacuumPad(Thing pad)
        {
            return pad != null && ServiceEnvironmentUtility.GetMaxVacuum(pad) > 0.05f;
        }

        private sealed class CachedHospitalitySafeCells
        {
            public int expiresTick;
            public List<IntVec3> cells = new List<IntVec3>();
        }

        private sealed class CachedHospitalityLeaveDelay
        {
            public bool delay;
            public string reason;
            public int createdTick;
            public int expiresTick;
        }

        private static bool PadCanSafelyServe(Thing pad, ServiceUse use, IEnumerable<Pawn> pawns, string groupId, bool bypassGuestArea)
        {
            if (pad == null || pad.Destroyed || pad.Map == null)
            {
                return false;
            }
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(comp.reservedForGroup) && comp.reservedForGroup != groupId)
            {
                return false;
            }
            if (!comp.MeetsDepartureRequirements(use))
            {
                return false;
            }
            return ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(pad, pawns, DepartureVacuumSuitTarget(), out string _) &&
                PadReachableForPawns(pad, pawns, false, bypassGuestArea, out string _);
        }

        private static bool PadCanSafelyServeDeparture(Thing pad, ServiceUse use, ServiceGroupRecord record, bool bypassGuestArea)
        {
            if (pad == null || pad.Destroyed || pad.Map == null || record == null)
            {
                return false;
            }
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(comp.reservedForGroup) && comp.reservedForGroup != record.id)
            {
                return false;
            }
            bool modeBypass = AllowsDepartureModeFallback(record, pad, use);
            bool operational = modeBypass ? comp.MeetsOperationalRequirements(out string ignoredReason) : comp.MeetsDepartureRequirements(use);
            if (!operational)
            {
                return false;
            }
            return ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(pad, record.pawns, DepartureVacuumSuitTarget(), out string _) &&
                PadReachableForPawns(pad, record.pawns, false, bypassGuestArea, out string _);
        }

        private static List<Thing> DepartureModeFallbackPads(Map map, ServiceUse use, ServiceGroupRecord record)
        {
            List<Thing> pads = ServicePadUtility.AllServicePadBuildings(map)
                .Where(pad => pad != null && !pad.Destroyed && pad.TryGetComp<CompSpaceServicePad>() != null)
                .Distinct()
                .ToList();
            if (pads.Count == 0 || record == null)
            {
                return new List<Thing>();
            }
            if (pads.Count == 1 && AllowsDepartureModeFallback(record, pads[0], use))
            {
                return pads;
            }
            return pads.Where(pad => AllowsDepartureModeFallback(record, pad, use)).ToList();
        }

        private static bool MatchingDepartureModePadOperationalOrReserved(Map map, ServiceUse use, string groupId)
        {
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                if (comp == null || !comp.AllowsDepartureUse(use))
                {
                    continue;
                }
                string reason;
                if (!string.IsNullOrEmpty(comp.reservedForGroup) && comp.reservedForGroup != groupId)
                {
                    return true;
                }
                if (comp.MeetsOperationalRequirements(out reason))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AllowsDepartureModeFallback(ServiceGroupRecord record, Thing pad, ServiceUse use)
        {
            if (record == null || pad == null || pad.Destroyed)
            {
                return false;
            }
            if (record.serviceKind != "hospital" && record.serviceKind != "hospitality")
            {
                return false;
            }
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null || comp.AllowsUse(use))
            {
                return false;
            }
            // Departures are a cleanup path, not a new service. If the right-mode pads are
            // missing, reserved, or unsafe, let existing groups escape through any safe pad.
            return true;
        }

        private static bool ReadyForExtraction(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.pawns.Count == 0)
            {
                return false;
            }
            IntVec3 cell = record.reservedPad == null ? IntVec3.Invalid : record.reservedPad.Position;
            if (!cell.IsValid)
            {
                return record.serviceKind != "hospital";
            }
            if (!ReservedPadCanServe(record, DepartureUse(record), out string blockedReason))
            {
                return false;
            }
            if (record.serviceKind == "hospitality" && HospitalityReadyForPickupCall(record))
            {
                return true;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                if (pawn.Spawned && !PawnReadyForPickupCall(pawn, record))
                {
                    ServiceDebugUtility.LogAudit("ReadyForExtraction false waiting pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                    return false;
                }
            }
            return true;
        }

        private static bool HospitalityReadyForPickupCall(ServiceGroupRecord record)
        {
            if (record == null || record.reservedPad == null || record.pawns == null)
            {
                return false;
            }
            List<Pawn> spawned = record.pawns.Where(pawn => pawn != null && pawn.Spawned && !pawn.Destroyed && !pawn.Downed).ToList();
            if (spawned.Count == 0)
            {
                return true;
            }
            int staged = spawned.Count(pawn => PawnReadyForPickupCall(pawn, record));
            if (staged == spawned.Count)
            {
                return true;
            }
            if (!SpaceServiceMapDetector.IsGroundsideServiceActive(record.reservedPad.Map))
            {
                if (staged == 0)
                {
                    return false;
                }
                if (!spawned.All(pawn => PawnNearDeparturePad(pawn, record.reservedPad)))
                {
                    return false;
                }
                ServiceDebugUtility.LogAudit("HospitalityReadyForPickupCall allowing clustered group staged=" + staged + "/" + spawned.Count + " record=" + RecordAudit(record));
                return true;
            }
            if (!spawned.All(pawn => HospitalityPawnClusteredForPickupCall(pawn, record.reservedPad)))
            {
                return false;
            }
            ServiceDebugUtility.LogAudit("HospitalityReadyForPickupCall allowing clustered group staged=" + staged + "/" + spawned.Count + " record=" + RecordAudit(record));
            return true;
        }

        private static bool PawnReadyForPickupCall(Pawn pawn, ServiceGroupRecord record)
        {
            if (record != null && record.serviceKind == "hospitality" && PickupBoardingRect(record.reservedPad).Contains(pawn.Position))
            {
                return SameRoomAsPad(record.reservedPad, pawn.Position);
            }
            if (record != null && record.serviceKind == "hospital" && PawnAtDepartureWaitCell(pawn, record))
            {
                return true;
            }
            return PawnSafelyStagedForPickup(pawn, record == null ? null : record.reservedPad);
        }

        private static bool HospitalityDepartureTimedOut(ServiceGroupRecord record)
        {
            return record != null &&
                record.serviceKind == "hospitality" &&
                record.reservedPad != null &&
                record.departureRequestedTick > 0 &&
                Find.TickManager.TicksGame > record.departureRequestedTick + HospitalityDepartureHardTimeoutTicks;
        }

        private static bool PickupTimedOut(Map map, ServiceGroupRecord record)
        {
            if (HospitalityPickupTimedOut(map, record))
            {
                return true;
            }
            return HospitalPickupTimedOut(map, record);
        }

        private static bool HospitalityPickupTimedOut(Map map, ServiceGroupRecord record)
        {
            if (record == null ||
                record.serviceKind != "hospitality" ||
                record.pickupShuttleTouchdownTick <= 0 ||
                Find.TickManager.TicksGame <= record.pickupShuttleTouchdownTick + PickupBoardingHardTimeoutTicks)
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(record.reservedPad, record.pawns, DepartureVacuumSuitTarget(), out string _))
            {
                return false;
            }
            if (record.state != "boardingPickup")
            {
                record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame;
                GuideDepartingPawnsToPad(record);
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-pickup-timeout-staging-" + record.id, "Hospitality pickup timeout extended while service pawns finish staging: " + RecordAudit(record), GenDate.TicksPerHour);
                return true;
            }
            if (ReadyForBoardingCompletion(record))
            {
                DepartureUtility.CompleteDeparture(map, record, "hospitality pickup timeout fallback after boarding");
                return true;
            }
            record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame;
            GuideBoardingPawnsToShuttle(record);
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-pickup-timeout-wait-" + record.id, "Hospitality pickup timeout extended while service pawns finish boarding: " + RecordAudit(record), GenDate.TicksPerHour);
            return true;
        }

        private static bool HospitalPickupTimedOut(Map map, ServiceGroupRecord record)
        {
            if (record == null ||
                record.serviceKind != "hospital" ||
                record.pickupShuttleTouchdownTick <= 0 ||
                Find.TickManager.TicksGame <= record.pickupShuttleTouchdownTick + PickupBoardingHardTimeoutTicks)
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(record.reservedPad, record.pawns, DepartureVacuumSuitTarget(), out string _))
            {
                return false;
            }
            if (record.state != "boardingPickup")
            {
                record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame;
                GuideDepartingPawnsToPad(record);
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-pickup-timeout-staging-" + record.id, "Hospital pickup timeout extended while service pawns finish staging: " + RecordAudit(record), GenDate.TicksPerHour);
                return true;
            }
            if (ReadyForBoardingCompletion(record))
            {
                DepartureUtility.CompleteDeparture(map, record, "hospital pickup timeout fallback after boarding");
                return true;
            }
            if (HospitalPickupReadyForDownedFallback(record))
            {
                DepartureUtility.CompleteDeparture(map, record, "hospital downed patient at pickup shuttle timeout fallback");
                return true;
            }
            record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame;
            GuideBoardingPawnsToShuttle(record);
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-pickup-timeout-wait-" + record.id, "Hospital pickup timeout extended while service pawns finish boarding: " + RecordAudit(record), GenDate.TicksPerHour);
            return true;
        }

        private static bool CanSafelyForceHospitalityPickup(ServiceGroupRecord record)
        {
            if (record == null || record.reservedPad == null || record.pawns == null)
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(record.reservedPad, record.pawns, DepartureVacuumSuitTarget(), out string _))
            {
                return false;
            }
            return record.pawns.Any(pawn =>
                pawn != null &&
                pawn.Spawned &&
                !pawn.Downed &&
                PickupBoardingRect(record.reservedPad).Contains(pawn.Position) &&
                SameRoomAsPad(record.reservedPad, pawn.Position));
        }

        private static bool ReadyForBoardingCompletion(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.pawns.Count == 0 || record.reservedPad == null)
            {
                return false;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                if (pawn.Spawned && !PawnAtPickupShuttle(pawn, record.reservedPad))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HospitalPickupReadyForDownedFallback(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospital" || record.pawns == null || record.reservedPad == null)
            {
                return false;
            }
            List<Pawn> active = record.pawns
                .Where(pawn => pawn != null &&
                    !pawn.Destroyed &&
                    (!ServicePawnUtility.IsPlayerOwnedPawn(pawn) || ServiceDelayLodgerUtility.IsDelayLodger(record, pawn)) &&
                    pawn.Spawned)
                .ToList();
            if (active.Count == 0)
            {
                return false;
            }
            bool hasDownedNearPad = false;
            foreach (Pawn pawn in active)
            {
                if (PawnAtPickupShuttle(pawn, record.reservedPad))
                {
                    continue;
                }
                if (pawn.Downed && PawnNearDeparturePad(pawn, record.reservedPad))
                {
                    hasDownedNearPad = true;
                    continue;
                }
                return false;
            }
            return hasDownedNearPad;
        }

        private static int BoardReadyPawns(Map map, ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return 0;
            }
            List<Pawn> boarding = record.pawns
                .Where(pawn => pawn != null &&
                    pawn.Spawned &&
                    !pawn.Destroyed &&
                    !pawn.Downed &&
                    PawnAtPickupShuttle(pawn, record.reservedPad))
                .Distinct()
                .ToList();
            foreach (Pawn pawn in boarding)
            {
                ServiceDebugUtility.LogAudit("BoardReadyPawns extracting pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                DepartureUtility.TryAutoExtractForRecord(map ?? pawn.MapHeld, new[] { pawn }, "service pawn boarded pickup shuttle", record);
            }
            if (boarding.Count > 0 && ReadyForBoardingCompletion(record))
            {
                DepartureUtility.CompleteDeparture(map ?? (record.reservedPad == null ? null : record.reservedPad.Map), record, "service pawns boarded pickup shuttle");
            }
            if (boarding.Count == 0)
            {
                ServiceDebugUtility.LogThrottled(
                    ServiceDebugUtility.IntegrationForServiceKind(record.serviceKind),
                    "boarding-wait-" + record.id,
                    "Pickup boarding waiting for service group " + record.id + ": " + BoardingStatusSummary(record),
                    250);
            }
            return boarding.Count;
        }

        public static bool NotifyPawnBoardedPickupShuttle(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (!TryFindRecordForPawn(pawn, out Map map, out ServiceGroupRecord record) || record == null)
            {
                ServiceDebugUtility.LogAudit("BoardServiceShuttle completed for untracked pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
                return false;
            }
            if (record.state != "boardingPickup" || record.reservedPad == null)
            {
                ServiceDebugUtility.LogAudit("BoardServiceShuttle ignored state/pad pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                return false;
            }
            if (!PawnAtPickupShuttle(pawn, record.reservedPad))
            {
                ServiceDebugUtility.LogAudit("BoardServiceShuttle reached target outside boarding footprint pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                return false;
            }
            if (!ReservedPadCanServe(record, DepartureUse(record), out string blockedReason))
            {
                ServiceDebugUtility.Log("Service pickup boarding job waiting: " + blockedReason);
                return false;
            }
            Map extractionMap = map ?? pawn.MapHeld ?? record.reservedPad.Map;
            ServiceDebugUtility.LogAudit("BoardServiceShuttle extracting pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
            bool extracted = DepartureUtility.TryAutoExtractForRecord(extractionMap, new[] { pawn }, "service pawn boarded pickup shuttle", record);
            if (extracted && ReadyForBoardingCompletion(record))
            {
                DepartureUtility.CompleteDeparture(extractionMap, record, "service pawns boarded pickup shuttle");
            }
            return extracted;
        }

        private static string BoardingStatusSummary(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return "missing record, pawns, or reserved pad";
            }
            CellRect rect = PickupBoardingRect(record.reservedPad);
            return "rect=" + rect + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad) + " pawns=" + string.Join("; ", record.pawns
                .Where(pawn => pawn != null)
                .Select(pawn =>
                    pawn.LabelShortCap +
                    " spawned=" + pawn.Spawned +
                    " downed=" + pawn.Downed +
                    " pos=" + (pawn.Spawned ? pawn.Position.ToString() : "unspawned") +
                    " inRect=" + (pawn.Spawned && rect.Contains(pawn.Position)) +
                    " sameRoom=" + (pawn.Spawned && SameRoomAsPad(record.reservedPad, pawn.Position)) +
                    " job=" + (pawn.CurJobDef == null ? "null" : pawn.CurJobDef.defName)));
        }

        private static void GuideDepartingPawnsToPad(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return;
            }
            if (!ReservedPadCanServe(record, DepartureUse(record), out string blockedReason))
            {
                return;
            }
            Map map = record.reservedPad.Map;
            if (map == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Downed)
                {
                    continue;
                }
                if (ShouldHoldDepartingPawnNearPad(record, pawn))
                {
                    HoldDepartingPawnNearPad(record, pawn);
                    continue;
                }
                if (PawnReadyForPickupCall(pawn, record))
                {
                    continue;
                }
                bool bypassGuestArea = ShouldBypassGuestArea(record);
                IntVec3 waitCell = DepartureWaitCell(record.reservedPad, pawn, bypassGuestArea);
                if (!waitCell.IsValid || pawn.Position == waitCell)
                {
                    ServiceDebugUtility.LogAudit("GuideDepartingPawnsToPad skip invalid/same waitCell=" + waitCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                    continue;
                }
                if (!bypassGuestArea && !pawn.CanReach(waitCell, PathEndMode.OnCell, Danger.Deadly))
                {
                    ServiceDebugUtility.LogAudit("GuideDepartingPawnsToPad cannot reach waitCell=" + waitCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                    continue;
                }
                if (PawnAlreadyGoingTo(pawn, waitCell))
                {
                    continue;
                }
                ServiceDebugUtility.LogAudit("GuideDepartingPawnsToPad ordered waitCell=" + waitCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                pawn.jobs.TryTakeOrderedJob(ServiceGotoJob(waitCell, bypassGuestArea, LocomotionUrgency.Jog), JobTag.Misc);
            }
        }

        private static bool ShouldHoldDepartingPawnNearPad(ServiceGroupRecord record, Pawn pawn)
        {
            return record != null &&
                record.serviceKind == "hospitality" &&
                record.state == "pickupInbound" &&
                SpaceServiceMapDetector.IsGroundsideServiceActive(record.reservedPad?.Map) &&
                HospitalityPawnClusteredForPickupCall(pawn, record.reservedPad);
        }

        private static void HoldDepartingPawnNearPad(ServiceGroupRecord record, Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.CurJobDef == JobDefOf.Wait)
            {
                return;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Wait);
            job.expiryInterval = PickupInboundHoldTicks;
            ServiceDebugUtility.LogAudit("HoldDepartingPawnNearPad ordered wait pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        private static void GuideBoardingPawnsToShuttle(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return;
            }
            Map map = record.reservedPad.Map;
            if (map == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Downed)
                {
                    continue;
                }
                if (PawnAtPickupShuttle(pawn, record.reservedPad))
                {
                    continue;
                }
                bool bypassGuestArea = ShouldBypassGuestArea(record);
                IntVec3 boardCell = PickupBoardingCell(record.reservedPad, pawn, bypassGuestArea);
                if (!boardCell.IsValid || pawn.Position == boardCell)
                {
                    ServiceDebugUtility.LogAudit("GuideBoardingPawnsToShuttle skip invalid/same boardCell=" + boardCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                    continue;
                }
                if (!bypassGuestArea && !pawn.CanReach(boardCell, PathEndMode.OnCell, Danger.Deadly))
                {
                    ServiceDebugUtility.LogAudit("GuideBoardingPawnsToShuttle cannot reach boardCell=" + boardCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                    continue;
                }
                if (PawnAlreadyGoingTo(pawn, boardCell))
                {
                    continue;
                }
                ServiceDebugUtility.LogAudit("GuideBoardingPawnsToShuttle ordered boardCell=" + boardCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                pawn.jobs.TryTakeOrderedJob(ServiceBoardingJob(boardCell, bypassGuestArea), JobTag.Misc);
            }
        }

        private static bool PawnSafelyStagedForPickup(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            CellRect padRect = pad.OccupiedRect();
            // Keep patients staged just outside the landing area, but not wandering into nearby rooms.
            if (padRect.ExpandedBy(1).Contains(pawn.Position))
            {
                return false;
            }
            if (!padRect.ExpandedBy(2).Contains(pawn.Position))
            {
                return false;
            }
            return SameRoomAsPad(pad, pawn.Position);
        }

        private static bool PawnAtDepartureWaitCell(Pawn pawn, ServiceGroupRecord record)
        {
            if (pawn == null || !pawn.Spawned || record == null || record.reservedPad == null)
            {
                return false;
            }
            IntVec3 waitCell = DepartureWaitCell(record.reservedPad, pawn, ShouldBypassGuestArea(record));
            return waitCell.IsValid && pawn.Position == waitCell && SameRoomAsPad(record.reservedPad, pawn.Position);
        }

        private static bool PawnNearDeparturePad(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return pad.OccupiedRect().ExpandedBy(5).Contains(pawn.Position) && SameRoomAsPad(pad, pawn.Position);
        }

        private static bool HospitalityPawnNearDeparturePad(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return pad.OccupiedRect().ExpandedBy(HospitalityPickupClusterPadding).Contains(pawn.Position) && SameRoomAsPad(pad, pawn.Position);
        }

        private static bool HospitalityPawnClusteredForPickupCall(Pawn pawn, Thing pad)
        {
            if (!HospitalityPawnNearDeparturePad(pawn, pad))
            {
                return false;
            }
            return !pad.OccupiedRect().ExpandedBy(1).Contains(pawn.Position);
        }

        private static bool PawnAtPickupShuttle(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return PickupBoardingRect(pad).Contains(pawn.Position) && SameRoomAsPad(pad, pawn.Position);
        }

        private static bool PadReachableForPawns(Thing pad, IEnumerable<Pawn> pawns, bool boarding, bool bypassGuestArea, out string reason)
        {
            reason = null;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Downed)
                {
                    continue;
                }
                IntVec3 cell = boarding ? PickupBoardingCell(pad, pawn, bypassGuestArea) : DepartureWaitCell(pad, pawn, bypassGuestArea);
                if (!cell.IsValid)
                {
                    reason = pawn.LabelShortCap + " cannot reach a " + (boarding ? "boarding" : "staging") + " cell near the departure pad";
                    return false;
                }
            }
            return true;
        }

        private static bool SameRoomAsPad(Thing pad, IntVec3 cell)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }
            Room padRoom = pad.Position.GetRoom(map);
            if (padRoom == null)
            {
                return true;
            }
            return cell.GetRoom(map) == padRoom;
        }

        private static IntVec3 PickupBoardingCell(Thing pad, Pawn pawn, bool bypassGuestArea)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            return BestReachableBoardingCell(PickupBoardingRect(pad), pad, pawn, bypassGuestArea);
        }

        private static CellRect PickupBoardingRect(Thing pad)
        {
            // Boarding is based on the shuttle footprint, not the full pad or pre-touchdown staging ring.
            return CellRect.CenteredOn(pad.Position, 5, 5);
        }

        private static IntVec3 DepartureWaitCell(Thing pad, Pawn pawn, bool bypassGuestArea)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            IntVec3 cell = BestReachableCell(pad.OccupiedRect().ExpandedBy(2), pad, pawn, bypassGuestArea, true);
            if (cell.IsValid)
            {
                return cell;
            }
            return BestReachableStagingFallback(pad, pawn, bypassGuestArea);
        }

        private static IntVec3 BestReachableStagingFallback(Thing pad, Pawn pawn, bool bypassGuestArea)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            CellRect padRect = pad.OccupiedRect();
            CellRect searchRect = padRect.ExpandedBy(2);
            IntVec3 bestReachable = IntVec3.Invalid;
            IntVec3 bestFallback = IntVec3.Invalid;
            int bestReachablePawnDist = int.MaxValue;
            int bestReachablePadDist = int.MaxValue;
            int bestFallbackPawnDist = int.MaxValue;
            int bestFallbackPadDist = int.MaxValue;

            foreach (IntVec3 cell in searchRect.Cells)
            {
                if (padRect.Contains(cell) ||
                    !cell.InBounds(map) ||
                    !cell.Standable(map) ||
                    !SameRoomAsPad(pad, cell) ||
                    (cell.GetFirstPawn(map) != null && cell != pawn.Position))
                {
                    continue;
                }
                int pawnDist = cell.DistanceToSquared(pawn.Position);
                int padDist = cell.DistanceToSquared(pad.Position);
                if (BetterCell(pawnDist, padDist, bestFallbackPawnDist, bestFallbackPadDist))
                {
                    bestFallback = cell;
                    bestFallbackPawnDist = pawnDist;
                    bestFallbackPadDist = padDist;
                }
                if (pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly) && BetterCell(pawnDist, padDist, bestReachablePawnDist, bestReachablePadDist))
                {
                    bestReachable = cell;
                    bestReachablePawnDist = pawnDist;
                    bestReachablePadDist = padDist;
                }
            }

            return bestReachable.IsValid || !bypassGuestArea ? bestReachable : bestFallback;
        }

        private static IntVec3 BestReachableCell(CellRect searchRect, Thing pad, Pawn pawn, bool bypassGuestArea, bool stagingRing)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            CellRect noWaitRect = stagingRing ? pad.OccupiedRect().ExpandedBy(1) : CellRect.Empty;
            IntVec3 bestReachable = IntVec3.Invalid;
            IntVec3 bestFallback = IntVec3.Invalid;
            int bestReachablePawnDist = int.MaxValue;
            int bestReachablePadDist = int.MaxValue;
            int bestFallbackPawnDist = int.MaxValue;
            int bestFallbackPadDist = int.MaxValue;

            foreach (IntVec3 cell in searchRect.Cells)
            {
                if (!cell.InBounds(map) || !cell.Standable(map) || (cell.GetFirstPawn(map) != null && cell != pawn.Position))
                {
                    continue;
                }
                if (stagingRing && (noWaitRect.Contains(cell) || !SameRoomAsPad(pad, cell)))
                {
                    continue;
                }
                int pawnDist = cell.DistanceToSquared(pawn.Position);
                int padDist = cell.DistanceToSquared(pad.Position);
                if (BetterCell(pawnDist, padDist, bestFallbackPawnDist, bestFallbackPadDist))
                {
                    bestFallback = cell;
                    bestFallbackPawnDist = pawnDist;
                    bestFallbackPadDist = padDist;
                }
                if (pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly) && BetterCell(pawnDist, padDist, bestReachablePawnDist, bestReachablePadDist))
                {
                    bestReachable = cell;
                    bestReachablePawnDist = pawnDist;
                    bestReachablePadDist = padDist;
                }
            }

            return bestReachable.IsValid || !bypassGuestArea ? bestReachable : bestFallback;
        }

        private static IntVec3 BestReachableBoardingCell(CellRect searchRect, Thing pad, Pawn pawn, bool bypassGuestArea)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            IntVec3 bestReachable = IntVec3.Invalid;
            IntVec3 bestFallback = IntVec3.Invalid;
            int bestReachablePadDist = int.MaxValue;
            int bestReachablePawnDist = int.MaxValue;
            int bestFallbackPadDist = int.MaxValue;
            int bestFallbackPawnDist = int.MaxValue;

            foreach (IntVec3 cell in searchRect.Cells)
            {
                if (!cell.InBounds(map) ||
                    !cell.Standable(map) ||
                    !SameRoomAsPad(pad, cell) ||
                    (cell.GetFirstPawn(map) != null && cell != pawn.Position))
                {
                    continue;
                }
                int padDist = cell.DistanceToSquared(pad.Position);
                int pawnDist = cell.DistanceToSquared(pawn.Position);
                if (padDist < bestFallbackPadDist || (padDist == bestFallbackPadDist && pawnDist < bestFallbackPawnDist))
                {
                    bestFallback = cell;
                    bestFallbackPadDist = padDist;
                    bestFallbackPawnDist = pawnDist;
                }
                if (pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly) &&
                    (padDist < bestReachablePadDist || (padDist == bestReachablePadDist && pawnDist < bestReachablePawnDist)))
                {
                    bestReachable = cell;
                    bestReachablePadDist = padDist;
                    bestReachablePawnDist = pawnDist;
                }
            }

            return bestReachable.IsValid || !bypassGuestArea ? bestReachable : bestFallback;
        }

        private static bool BetterCell(int pawnDist, int padDist, int bestPawnDist, int bestPadDist)
        {
            return pawnDist < bestPawnDist || (pawnDist == bestPawnDist && padDist < bestPadDist);
        }

        private static bool PawnAlreadyGoingTo(Pawn pawn, IntVec3 cell)
        {
            Job job = pawn == null ? null : pawn.CurJob;
            return job != null &&
                (job.def == JobDefOf.Goto ||
                    job.def == ServiceJobDefUtility.ServiceGoto ||
                    job.def == ServiceJobDefUtility.ServiceDepartureHold ||
                    job.def == ServiceJobDefUtility.BoardServiceShuttle) &&
                job.targetA.IsValid &&
                job.targetA.Cell == cell;
        }

        public static List<ServiceDepartureBlock> BlockedDepartures()
        {
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (tick < cachedDepartureBlocksTick + BlockedDepartureCacheTicks)
            {
                return CachedDepartureBlocks;
            }
            CachedDepartureBlocks.Clear();
            cachedDepartureBlocksTick = tick;
            BuildBlockedDepartures(CachedDepartureBlocks);
            return CachedDepartureBlocks;
        }

        private static void BuildBlockedDepartures(List<ServiceDepartureBlock> blocks)
        {
            foreach (Map map in Find.Maps ?? Enumerable.Empty<Map>())
            {
                SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
                if (comp == null || comp.serviceGroups == null)
                {
                    continue;
                }
                foreach (ServiceGroupRecord record in comp.serviceGroups)
                {
                    if (record == null || record.pawns == null || record.pawns.Count == 0 || !ShouldReportDepartureBlock(record))
                    {
                        continue;
                    }
                    ServiceUse use = DepartureUse(record);
                    if (ServiceDangerUtility.DepartureShuttleBlocked(map, record.serviceKind, out string hazardReason))
                    {
                        blocks.Add(new ServiceDepartureBlock
                        {
                            map = map,
                            record = record,
                            reason = hazardReason
                        });
                        continue;
                    }
                    if (record.serviceKind == "hospital" && record.state == "arrived")
                    {
                        string futureDepartureReason = NoReservedPadBlockReason(map, use, record);
                        if (!string.IsNullOrEmpty(futureDepartureReason))
                        {
                            blocks.Add(new ServiceDepartureBlock
                            {
                                map = map,
                                record = record,
                                reason = futureDepartureReason
                            });
                        }
                        continue;
                    }
                    if (!ReservedPadCanServe(record, use, out string reason))
                    {
                        if (!ReservedPadStillExists(record))
                        {
                            reason = NoReservedPadBlockReason(map, use, record);
                        }
                        if (string.IsNullOrEmpty(reason))
                        {
                            continue;
                        }
                        blocks.Add(new ServiceDepartureBlock
                        {
                            map = map,
                            record = record,
                            reason = reason
                        });
                    }
                }
            }
        }

        private static bool ShouldReportDepartureBlock(ServiceGroupRecord record)
        {
            if (record == null || record.state == "completed")
            {
                return false;
            }
            if (IsActiveDepartureState(record))
            {
                return true;
            }
            return false;
        }

        private static string NoReservedPadBlockReason(Map map, ServiceUse use, ServiceGroupRecord record)
        {
            List<Pawn> pawns = record == null || record.pawns == null ? new List<Pawn>() : record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).ToList();
            List<Thing> pads = ServicePadUtility.AllServicePadBuildings(map).ToList();
            if (pads.Count == 0)
            {
                return "no " + use.ToString().ToLowerInvariant() + " service pad exists";
            }
            bool anyMatchingMode = false;
            bool anyReserved = false;
            string firstSafetyReason = null;
            string firstReachReason = null;
            string firstBlockedReason = null;
            foreach (Thing pad in pads)
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                if (comp == null)
                {
                    continue;
                }
                string padReason;
                bool modeFallback = AllowsDepartureModeFallback(record, pad, use);
                bool requirementsMet = modeFallback ? comp.MeetsOperationalRequirements(out padReason) : comp.MeetsDepartureRequirements(use, out padReason);
                if (!requirementsMet)
                {
                    firstBlockedReason = firstBlockedReason ?? "service pad blocked: " + (padReason ?? "settings block this service");
                    continue;
                }
                anyMatchingMode = true;
                if (!string.IsNullOrEmpty(comp.reservedForGroup) && (record == null || comp.reservedForGroup != record.id))
                {
                    anyReserved = true;
                    continue;
                }
                if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(pad, pawns, DepartureVacuumSuitTarget(), out string safetyReason))
                {
                    firstSafetyReason = firstSafetyReason ?? safetyReason ?? "service pad is unsafe";
                    continue;
                }
                if (!PadReachableForPawns(pad, pawns, false, ShouldBypassGuestArea(record), out string reachReason))
                {
                    firstReachReason = firstReachReason ?? reachReason ?? "service pad cannot be reached";
                    continue;
                }
                return null;
            }
            if (anyReserved)
            {
                return "all matching " + use.ToString().ToLowerInvariant() + " service pads are reserved";
            }
            if (!anyMatchingMode && !string.IsNullOrEmpty(firstBlockedReason))
            {
                return firstBlockedReason;
            }
            if (!string.IsNullOrEmpty(firstSafetyReason))
            {
                return firstSafetyReason;
            }
            if (!string.IsNullOrEmpty(firstReachReason))
            {
                return firstReachReason;
            }
            return "no usable " + use.ToString().ToLowerInvariant() + " service pad exists";
        }

        private static bool ReservedPadCanServe(ServiceGroupRecord record, ServiceUse use, out string reason)
        {
            reason = null;
            if (record == null)
            {
                reason = "no service record";
                return false;
            }
            if (!ReservedPadStillExists(record))
            {
                reason = "departure pad unavailable";
                return false;
            }
            CompSpaceServicePad comp = record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (comp == null)
            {
                reason = "departure pad is not a service pad";
                return false;
            }
            bool modeFallback = AllowsDepartureModeFallback(record, record.reservedPad, use);
            bool requirementsMet = modeFallback ? comp.MeetsOperationalRequirements(out reason) : comp.MeetsDepartureRequirements(use, out reason);
            if (!requirementsMet)
            {
                reason = "departure pad blocked: " + (reason ?? "settings block this service");
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawnsAtTarget(record.reservedPad, record.pawns, DepartureVacuumSuitTarget(), out reason))
            {
                return false;
            }
            bool boarding = record.state == "boardingPickup";
            return PadReachableForPawns(record.reservedPad, record.pawns, boarding, ShouldBypassGuestArea(record), out reason);
        }

        private static bool ReservedPadStillExists(ServiceGroupRecord record)
        {
            return record != null && record.reservedPad != null && !record.reservedPad.Destroyed && record.reservedPad.Map != null;
        }

        private static float DepartureVacuumSuitTarget()
        {
            return VacSuitUtility.PracticalDepartureVacuumSuitTarget();
        }

        private static bool IsTryingToLeave(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned)
            {
                return false;
            }
            Lord lord = pawn.GetLord();
            string lordJob = lord == null || lord.LordJob == null ? "" : lord.LordJob.GetType().FullName ?? "";
            if (ContainsAny(lordJob, "TravelAndExit", "ExitOnShuttle", "Depart"))
            {
                return true;
            }
            string duty = pawn.mindState == null || pawn.mindState.duty == null || pawn.mindState.duty.def == null ? "" : pawn.mindState.duty.def.defName;
            return ContainsAny(duty, "Exit", "Depart", "Leave");
        }

        private static bool IsDetachedRescuedHospitalityServicePawn(Pawn pawn)
        {
            if (ServicePawnUtility.IsTerminalPawn(pawn) ||
                !pawn.Spawned ||
                pawn.Downed ||
                ServicePawnUtility.IsPlayerOwnedPawn(pawn) ||
                !HospitalityBedUtility.IsRescuedGuest(pawn))
            {
                return false;
            }
            return pawn.GetLord() == null;
        }

        private static ServiceGroupRecord SplitDetachedRescuedHospitalityPawn(Map map, List<ServiceGroupRecord> records, ServiceGroupRecord sourceRecord, Pawn pawn)
        {
            if (map == null ||
                records == null ||
                sourceRecord == null ||
                pawn == null ||
                sourceRecord.pawns == null ||
                sourceRecord.pawns.Count <= 1)
            {
                return sourceRecord;
            }

            sourceRecord.pawns.RemoveAll(tracked => tracked == null || tracked == pawn || ServicePawnUtility.IsTerminalPawn(tracked));
            MarkRecordDirty(map, sourceRecord, "detached rescued Hospitality guest split for solo departure");

            ServiceGroupRecord departureRecord = new ServiceGroupRecord
            {
                id = "SS-" + Find.UniqueIDsManager.GetNextThingID(),
                serviceKind = sourceRecord.serviceKind,
                state = "arrived",
                arrivalTick = sourceRecord.arrivalTick,
                timeoutTick = Math.Max(sourceRecord.timeoutTick, Find.TickManager.TicksGame + GenDate.TicksPerHour),
                arrivalPad = sourceRecord.arrivalPad,
                pawns = new List<Pawn> { pawn }
            };
            records.Add(departureRecord);
            ServiceDebugUtility.LogAudit("Split detached rescued Hospitality service pawn source=" + RecordAudit(sourceRecord) + " departure=" + RecordAudit(departureRecord) + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
            MarkRecordDirty(map, departureRecord, "detached rescued Hospitality guest solo departure");
            return departureRecord;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int i = 0; i < needles.Length; i++)
            {
                if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string PawnSummary(IEnumerable<Pawn> pawns)
        {
            List<string> labels = new List<string>();
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null)
                {
                    continue;
                }
                labels.Add(pawn.LabelShortCap + "(spawned=" + pawn.Spawned + ", destroyed=" + pawn.Destroyed + ")");
            }
            return labels.Count == 0 ? "none" : string.Join(", ", labels.ToArray());
        }

        private static bool SamePawnSet(List<Pawn> left, List<Pawn> right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }
            if (left.Count != right.Count)
            {
                return false;
            }
            HashSet<Pawn> set = new HashSet<Pawn>(left);
            foreach (Pawn pawn in right)
            {
                if (!set.Contains(pawn))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<Pawn> DistinctNonNullPawns(List<Pawn> pawns)
        {
            List<Pawn> result = new List<Pawn>();
            if (pawns == null)
            {
                return result;
            }
            HashSet<Pawn> seen = new HashSet<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                if (pawn != null && seen.Add(pawn))
                {
                    result.Add(pawn);
                }
            }
            return result;
        }

        private static string RecordAudit(ServiceGroupRecord record)
        {
            if (record == null)
            {
                return "record=null";
            }
            return "id=" + record.id +
                " kind=" + record.serviceKind +
                " state=" + record.state +
                " pawns=" + (record.pawns == null ? 0 : record.pawns.Count) +
                " prepared=" + record.hospitalityDeparturePrepared +
                " arrivalTick=" + record.arrivalTick +
                " bedlessSince=" + record.hospitalityBedlessSinceTick +
                " departureTick=" + record.departureRequestedTick +
                " pickupTick=" + record.pickupShuttleTouchdownTick +
                " reservedPad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad);
        }

        private static string LordLabel(Lord lord)
        {
            if (lord == null)
            {
                return "null";
            }
            string job = lord.LordJob == null ? "nullJob" : lord.LordJob.GetType().Name;
            string toil = lord.CurLordToil == null ? "nullToil" : lord.CurLordToil.GetType().Name;
            return lord.loadID + "/" + job + "/" + toil + "/pawns=" + (lord.ownedPawns == null ? 0 : lord.ownedPawns.Count);
        }

        private static bool ShouldBypassGuestArea(ServiceGroupRecord record)
        {
            return UsesHospitalityDepartureHandling(record);
        }

        private static bool UsesHospitalityDepartureHandling(ServiceGroupRecord record)
        {
            return record != null &&
                (record.serviceKind == "hospitality" || record.departureHoldHospitalityHandoffDone);
        }

        private static ServiceUse DepartureUse(ServiceGroupRecord record)
        {
            return UsesHospitalityDepartureHandling(record) ? ServiceUse.Guest : ServiceUse.Patient;
        }

        private static Job ServiceGotoJob(IntVec3 cell, bool bypassGuestArea, LocomotionUrgency urgency)
        {
            JobDef serviceGotoDef = ServiceJobDefUtility.ServiceGoto;
            Job job = JobMaker.MakeJob(serviceGotoDef ?? JobDefOf.Goto, cell);
            job.locomotionUrgency = urgency;
            if (bypassGuestArea)
            {
                job.playerForced = true;
                job.ignoreForbidden = true;
            }
            return job;
        }

        private static Job ServiceDepartureHoldJob(IntVec3 cell)
        {
            JobDef holdJobDef = ServiceJobDefUtility.ServiceDepartureHold;
            Job job = JobMaker.MakeJob(holdJobDef ?? JobDefOf.Goto, cell);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }

        private static Job ServiceBoardingJob(IntVec3 cell, bool bypassGuestArea)
        {
            JobDef boardJobDef = ServiceJobDefUtility.BoardServiceShuttle;
            Job job = JobMaker.MakeJob(boardJobDef ?? JobDefOf.Goto, cell);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            job.playerForced = true;
            job.ignoreForbidden = true;
            return job;
        }

        private static void ReleaseRecord(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (pad != null)
            {
                pad.Release(record.id);
            }
        }
    }
}
