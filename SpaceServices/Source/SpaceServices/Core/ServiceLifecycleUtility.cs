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
    public static class ServiceLifecycleUtility
    {
        private const int HospitalityDepartureDetectionGraceTicks = 2500;
        private const int HospitalityDepartureHardTimeoutTicks = 7500;
        private const int HospitalityBedlessDepartureGraceTicks = 2500 * 16;
        private const int PickupBoardingHardTimeoutTicks = 2500;
        private const float VacuumPadDistanceTolerance = 2.5f;

        public static int NextTickInterval(List<ServiceGroupRecord> records)
        {
            if (records != null && records.Any(record => record != null && (record.state == "pickupInbound" || record.state == "boardingPickup" || record.state == "departing")))
            {
                return 30;
            }
            return 250;
        }

        public static void RegisterPawns(Map map, string kind, IEnumerable<Pawn> pawns)
        {
            RegisterPawns(map, kind, pawns, null);
        }

        public static void RegisterPawns(Map map, string kind, IEnumerable<Pawn> pawns, Thing arrivalPad)
        {
            if (map == null || pawns == null || !SpaceServiceMapDetector.IsServiceEligible(map))
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
                foreach (Pawn pawn in list)
                {
                    if (!existing.pawns.Contains(pawn))
                    {
                        existing.pawns.Add(pawn);
                    }
                }
                existing.timeoutTick = Math.Max(existing.timeoutTick, Find.TickManager.TicksGame + GenDate.TicksPerDay * 3);
                if (existing.arrivalPad == null && arrivalPad != null && !arrivalPad.Destroyed)
                {
                    existing.arrivalPad = arrivalPad;
                }
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
            Log.Message("[Space Services] Released service group " + groupId + ": " + reason);
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
            Log.Message("[Space Services] Cleared service group " + groupId + " reservation: " + reason);
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
                foreach (Pawn terminalPawn in record.pawns.Where(ServicePawnUtility.IsTerminalPawn).Where(tracked => tracked != null).Distinct().ToList())
                {
                    // External mod callbacks can release dead pawns before our lifecycle tick sees them.
                    ServicePawnUtility.CleanupTerminalPawnReferences(map, terminalPawn);
                }
                record.pawns.RemoveAll(tracked => tracked == null || tracked == pawn || ServicePawnUtility.IsTerminalPawn(tracked));
            }
            if (record.pawns == null || record.pawns.Count == 0)
            {
                ReleaseRecord(record);
                record.state = "completed";
                Log.Message("[Space Services] Released service group " + record.id + ": " + reason);
            }
            else
            {
                ServiceDebugUtility.LogAudit("Released pawn from active service group " + RecordAudit(record) + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
            }
            return true;
        }

        private static bool TryFindRecordForPawn(Pawn pawn, out Map map, out ServiceGroupRecord record)
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
                    records.RemoveAt(i);
                    continue;
                }
                List<Pawn> previouslyTrackedPawns = record.pawns == null ? new List<Pawn>() : record.pawns.Where(pawn => pawn != null).Distinct().ToList();
                foreach (Pawn terminalPawn in previouslyTrackedPawns.Where(pawn => ServicePawnUtility.IsTerminalPawn(pawn)).Distinct())
                {
                    // Death/destruction skips the normal shuttle extraction path, so clear lord refs here.
                    ServicePawnUtility.CleanupTerminalPawnReferences(map, terminalPawn);
                }
                record.pawns = ActiveTrackedPawns(map, record);
                if (!SamePawnSet(previouslyTrackedPawns, record.pawns))
                {
                    ServiceDebugUtility.LogAudit("ActiveTrackedPawns updated " + RecordAudit(record) + " previous=" + PawnSummary(previouslyTrackedPawns) + " active=" + PawnSummary(record.pawns));
                }
                if (record.pawns.Count == 0)
                {
                    Log.Message("[Space Services] Service group " + record.id + " has no active pawns; releasing reservation. Previously tracked: " + PawnSummary(previouslyTrackedPawns));
                    ReleaseRecord(record);
                    records.RemoveAt(i);
                    continue;
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" && SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.hospitalityVacuumGuard)
                {
                    GuardHospitalityGuestsFromVacuum(map, record);
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived" &&
                    SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.hospitalityAutoDepartBedlessGuests &&
                    HospitalityBedUtility.TryFindBedlessServiceGuest(record, out Pawn bedlessGuest, out string bedlessReason))
                {
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
                    record.hospitalityBedlessSinceTick = 0;
                }
                if (record.state == "pickupInbound")
                {
                    EnsureHospitalityDeparturePrepared(record);
                    if (HospitalityPickupTimedOut(map, record))
                    {
                        continue;
                    }
                    if (Find.TickManager.TicksGame >= record.pickupShuttleTouchdownTick)
                    {
                        if (!ReservedPadCanServe(record, record.serviceKind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient, out string blockedReason))
                        {
                            if (ShouldLogBlockedDeparture())
                            {
                                Log.Message("[Space Services] Pickup shuttle waiting for usable pad: " + blockedReason);
                            }
                            GuideDepartingPawnsToPad(record);
                            continue;
                        }
                        record.state = "boardingPickup";
                        GuideBoardingPawnsToShuttle(record);
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
                    if (HospitalityPickupTimedOut(map, record))
                    {
                        continue;
                    }
                    if (!ReservedPadCanServe(record, record.serviceKind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient, out string blockedReason))
                    {
                        if (ShouldLogBlockedDeparture())
                        {
                            Log.Message("[Space Services] Service pickup boarding waiting: " + blockedReason);
                        }
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
                if (record.state == "departing")
                {
                    EnsureHospitalityDeparturePrepared(record);
                    if (record.serviceKind == "hospital")
                    {
                        BeginHospitalDeparture(map, record, "waiting for free departure pad");
                        continue;
                    }
                    if (record.serviceKind == "hospitality" && !EnsureReservedDeparturePad(map, record, ServiceUse.Guest))
                    {
                        continue;
                    }
                    if (ReadyForExtraction(record))
                    {
                        if (record.serviceKind == "hospitality" && ServiceDangerUtility.HospitalityTrafficBlocked(map, out string dangerReason))
                        {
                            ServiceDebugUtility.LogThrottled("hospitality-pickup-danger-" + record.id, "Hospitality pickup delayed for service group " + record.id + ": " + dangerReason, GenDate.TicksPerHour);
                            GuideDepartingPawnsToPad(record);
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
                            DepartureUtility.CompleteDeparture(map, record, "hospitality departure timeout fallback");
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
                    record.pawns.Any(IsTryingToLeave))
                {
                    BeginDeparture(map, record, "Hospitality group entered departure state");
                    continue;
                }
                if (record.serviceKind != "hospital" && record.serviceKind != "hospitality" && record.pawns.Any(IsTryingToLeave))
                {
                    BeginDeparture(map, record, "service lord entered departure state");
                    continue;
                }
                if (Find.TickManager.TicksGame > record.timeoutTick)
                {
                    BeginDeparture(map, record, "service visit timeout");
                }
            }
        }

        private static bool ShouldLogBlockedDeparture()
        {
            return SpaceServicesMod.Settings != null &&
                SpaceServicesMod.Settings.debugLogging &&
                Find.TickManager != null &&
                Find.TickManager.TicksGame % 2500 == 0;
        }

        private static void GuardHospitalityGuestsFromVacuum(Map map, ServiceGroupRecord record)
        {
            if (map == null || record == null || record.pawns == null)
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
                IntVec3 safeCell = FindHospitalitySafeCell(map, record.reservedPad, pawn);
                if (!safeCell.IsValid || safeCell == pawn.Position)
                {
                    continue;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Goto, safeCell);
                job.locomotionUrgency = LocomotionUrgency.Jog;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
        }

        private static bool PawnCurrentJobTargetsUnsafeVacuum(Pawn pawn, Map map)
        {
            Job job = pawn == null ? null : pawn.CurJob;
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

        private static IntVec3 FindHospitalitySafeCell(Map map, Thing pad, Pawn pawn)
        {
            IEnumerable<IntVec3> candidates = Enumerable.Empty<IntVec3>();
            Room room = pad != null && pad.Spawned ? pad.Position.GetRoom(map) : null;
            if (room != null)
            {
                candidates = room.Cells;
            }
            if (pad != null && pad.Spawned)
            {
                candidates = candidates.Concat(GenRadial.RadialCellsAround(pad.Position, 12f, true));
            }
            candidates = candidates.Concat(GenRadial.RadialCellsAround(pawn.Position, 12f, true));

            return candidates
                .Where(cell => cell.InBounds(map) && cell.Standable(map))
                .Where(cell => cell.GetFirstPawn(map) == null || cell == pawn.Position)
                .Where(cell => ServiceEnvironmentUtility.IsSafeForPawn(pawn, map, cell))
                .Where(cell => pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(cell => pad != null && pad.Spawned ? cell.DistanceToSquared(pad.Position) : cell.DistanceToSquared(pawn.Position))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
        }

        private static List<Pawn> ActiveTrackedPawns(Map map, ServiceGroupRecord record)
        {
            List<Pawn> pawns = record == null || record.pawns == null ? new List<Pawn>() : record.pawns.Where(p => !ServicePawnUtility.IsTerminalPawn(p)).Distinct().ToList();
            if (record == null || record.serviceKind != "hospital" || pawns.Count == 0)
            {
                if (record != null && record.serviceKind == "hospitality")
                {
                    return pawns.Where(IsActiveHospitalityServicePawn).ToList();
                }
                return pawns;
            }

            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary hospitalPatients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (hospitalPatients == null)
            {
                return pawns;
            }

            return pawns.Where(pawn => pawn.Spawned || hospitalPatients.Contains(pawn)).ToList();
        }

        private static bool IsActiveHospitalityServicePawn(Pawn pawn)
        {
            if (ServicePawnUtility.IsTerminalPawn(pawn) || !pawn.Spawned)
            {
                return false;
            }
            if (ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                // Hospitality join offers and recruit-guest mods turn visitors into colonists; never route them to extraction.
                return false;
            }
            return !HospitalityBedUtility.IsRescuedGuest(pawn);
        }

        private static void BeginDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.state == "completed" || record.state == "extracting")
            {
                ServiceDebugUtility.LogAudit("BeginDeparture skipped terminal record " + RecordAudit(record) + " reason=" + (reason ?? "none"));
                return;
            }
            ServiceDebugUtility.LogAudit("BeginDeparture enter " + RecordAudit(record) + " reason=" + (reason ?? "none") + " pawns=" + PawnSummary(record.pawns));
            if (record.serviceKind == "hospital")
            {
                BeginHospitalDeparture(map, record, reason);
                return;
            }
            if (record.reservedPad == null)
            {
                record.reservedPad = TryReserveBestDeparturePad(map, ServiceUse.Guest, record);
                if (record.reservedPad == null)
                {
                    ServiceDebugUtility.LogAudit("BeginDeparture no hospitality departure pad " + RecordAudit(record));
                    if (record.state != "departing")
                    {
                        record.state = "departing";
                        record.departureRequestedTick = Find.TickManager.TicksGame;
                        Log.Message("[Space Services] Hospitality visitors waiting for free departure pad: " + reason);
                    }
                    return;
                }
            }
            EnsureHospitalityDeparturePrepared(record);
            if (ReadyForExtraction(record))
            {
                ServiceDebugUtility.LogAudit("BeginDeparture ready for immediate pickup " + RecordAudit(record));
                BeginPickupShuttle(record, reason);
                return;
            }
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager.TicksGame;
                Log.Message("[Space Services] Routing " + record.serviceKind + " service group " + record.id + " to departure pad: " + reason);
            }
            GuideDepartingPawnsToPad(record);
        }

        private static void EnsureHospitalityDeparturePrepared(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospitality" || record.hospitalityDeparturePrepared)
            {
                return;
            }
            ServiceDebugUtility.LogAudit("EnsureHospitalityDeparturePrepared begin " + RecordAudit(record));
            HospitalityBedUtility.PrepareGuestsForServiceDeparture(record);
            record.hospitalityDeparturePrepared = true;
            ServiceDebugUtility.LogAudit("EnsureHospitalityDeparturePrepared end " + RecordAudit(record) + " pawns=" + PawnSummary(record.pawns));
        }

        private static void BeginHospitalDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record.reservedPad != null && !ReservedPadStillExists(record))
            {
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture releasing missing pad " + RecordAudit(record));
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad != null && !PadCanSafelyServeDeparture(record.reservedPad, ServiceUse.Patient, record, false))
            {
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture releasing unsafe pad " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad == null)
            {
                record.reservedPad = TryReserveBestDeparturePad(map, ServiceUse.Patient, record);
                ServiceDebugUtility.LogAudit("BeginHospitalDeparture reserved pad result " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            }
            if (record.reservedPad == null)
            {
                if (record.state != "departing")
                {
                    record.state = "departing";
                    record.departureRequestedTick = Find.TickManager.TicksGame;
                    Log.Message("[Space Services] Hospital patient waiting for free departure pad: " + reason);
                }
                return;
            }
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager.TicksGame;
                Log.Message("[Space Services] Routing hospital patient to departure pad: " + reason);
            }
            if (!ReservedPadCanServe(record, ServiceUse.Patient, out string blockedReason))
            {
                if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
                {
                    Log.Message("[Space Services] Hospital patient departure waiting: " + blockedReason);
                }
                GuideDepartingPawnsToPad(record);
                return;
            }
            BeginPickupShuttle(record, reason);
            GuideDepartingPawnsToPad(record);
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
                return true;
            }
            ServiceDebugUtility.LogAudit("EnsureReservedDeparturePad finding replacement " + RecordAudit(record) + " oldPad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            ReleaseRecord(record);
            record.reservedPad = TryReserveBestDeparturePad(map, use, record);
            ServiceDebugUtility.LogAudit("EnsureReservedDeparturePad result " + RecordAudit(record) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            if (record.reservedPad == null)
            {
                if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging && ShouldLogBlockedDeparture())
                {
                    Log.Message("[Space Services] Service group " + record.id + " waiting for a usable departure pad.");
                }
                return false;
            }
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
            ShuttleVisual visual = ShuttleVisual.Resolve();
            if (visual == null)
            {
                ServiceDebugUtility.LogAudit("BeginPickupShuttle no shuttle visual, completing directly " + RecordAudit(record));
                DepartureUtility.CompleteDeparture(record.reservedPad.Map, record, reason);
                return;
            }

            record.state = "pickupInbound";
            record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks;
            record.pickupShuttleThingDefName = visual.shipThingDef.defName;
            ServiceShuttleUtility.SpawnArrival(record.reservedPad.Map, record.reservedPad.Position);
            Log.Message("[Space Services] Pickup shuttle inbound for " + record.serviceKind + " service group " + record.id + ": " + reason);
            ServiceDebugUtility.LogAudit("BeginPickupShuttle " + RecordAudit(record) + " touchdownTick=" + record.pickupShuttleTouchdownTick + " ship=" + record.pickupShuttleThingDefName + " reason=" + (reason ?? "none"));
        }

        private static Thing TryReserveBestDeparturePad(Map map, ServiceUse use, ServiceGroupRecord record)
        {
            if (map == null || record == null || string.IsNullOrEmpty(record.id))
            {
                return null;
            }
            List<Pawn> pawns = record.pawns == null ? new List<Pawn>() : record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).ToList();
            // Pick only pads the service pawns can survive at, then prefer matching priority and reasonable vacuum routing.
            List<Thing> candidates = ServicePadUtility.AllServicePads(map, use)
                .Where(pad => PadCanSafelyServe(pad, use, pawns, record.id, ShouldBypassGuestArea(record)))
                .ToList();
            if (candidates.Count == 0)
            {
                candidates = DepartureModeFallbackPads(map, use, record)
                    .Where(pad => PadCanSafelyServeDeparture(pad, use, record, ShouldBypassGuestArea(record)))
                    .ToList();
                if (candidates.Count > 0)
                {
                    ServiceDebugUtility.LogAudit("TryReserveBestDeparturePad using mode fallback record=" + record.id + " use=" + use + " pads=" + candidates.Count);
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
            return null;
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
            List<Pawn> spawned = pawns == null ? new List<Pawn>() : pawns.Where(pawn => pawn != null && pawn.Spawned).ToList();
            float score = spawned.Sum(pawn =>
            {
                IntVec3 waitCell = DepartureWaitCell(pad, pawn, ShouldBypassGuestArea(record));
                IntVec3 target = waitCell.IsValid ? waitCell : pad.Position;
                return pawn.Position.DistanceToSquared(target);
            });
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp != null)
            {
                score += comp.PriorityRank(use) * 1000000f;
            }
            return score;
        }

        private static float DepartureTravelDistance(ServiceGroupRecord record, Thing pad, List<Pawn> pawns)
        {
            List<Pawn> spawned = pawns == null ? new List<Pawn>() : pawns.Where(pawn => pawn != null && pawn.Spawned).ToList();
            return spawned.Sum(pawn =>
            {
                IntVec3 waitCell = DepartureWaitCell(pad, pawn, ShouldBypassGuestArea(record));
                IntVec3 target = waitCell.IsValid ? waitCell : pad.Position;
                return pawn.Position.DistanceToSquared(target);
            });
        }

        private static bool ShouldPreferVacuumDeparturePad(List<Pawn> pawns)
        {
            return pawns != null &&
                pawns.Count > 0 &&
                pawns.All(pawn => pawn != null && !pawn.Destroyed && VacSuitUtility.VacuumResistance(pawn) >= 0.95f);
        }

        private static bool IsVacuumPad(Thing pad)
        {
            return pad != null && ServiceEnvironmentUtility.GetMaxVacuum(pad) > 0.05f;
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
            if (!comp.MeetsUseRequirements(use))
            {
                return false;
            }
            return ServiceEnvironmentUtility.IsPadSafeForPawns(pad, pawns, out string _) &&
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
            bool operational = modeBypass ? comp.MeetsOperationalRequirements(out string ignoredReason) : comp.MeetsUseRequirements(use);
            if (!operational)
            {
                return false;
            }
            return ServiceEnvironmentUtility.IsPadSafeForPawns(pad, record.pawns, out string _) &&
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
            if (!ReservedPadCanServe(record, record.serviceKind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient, out string blockedReason))
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

        private static bool PawnReadyForPickupCall(Pawn pawn, ServiceGroupRecord record)
        {
            if (record != null && record.serviceKind == "hospitality" && PickupBoardingRect(record.reservedPad).Contains(pawn.Position))
            {
                return SameRoomAsPad(record.reservedPad, pawn.Position);
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

        private static bool HospitalityPickupTimedOut(Map map, ServiceGroupRecord record)
        {
            if (record == null ||
                record.serviceKind != "hospitality" ||
                record.pickupShuttleTouchdownTick <= 0 ||
                Find.TickManager.TicksGame <= record.pickupShuttleTouchdownTick + PickupBoardingHardTimeoutTicks)
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawns(record.reservedPad, record.pawns, out string _))
            {
                return false;
            }
            DepartureUtility.CompleteDeparture(map, record, "hospitality pickup timeout fallback");
            return true;
        }

        private static bool CanSafelyForceHospitalityPickup(ServiceGroupRecord record)
        {
            if (record == null || record.reservedPad == null || record.pawns == null)
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawns(record.reservedPad, record.pawns, out string _))
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

        private static void GuideDepartingPawnsToPad(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return;
            }
            if (!ReservedPadCanServe(record, record.serviceKind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient, out string blockedReason))
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
                if (PawnSafelyStagedForPickup(pawn, record.reservedPad))
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
                ServiceDebugUtility.LogAudit("GuideDepartingPawnsToPad ordered waitCell=" + waitCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                pawn.jobs.TryTakeOrderedJob(ServiceGotoJob(waitCell, bypassGuestArea), JobTag.Misc);
            }
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
                ServiceDebugUtility.LogAudit("GuideBoardingPawnsToShuttle ordered boardCell=" + boardCell + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " record=" + RecordAudit(record));
                pawn.jobs.TryTakeOrderedJob(ServiceGotoJob(boardCell, bypassGuestArea), JobTag.Misc);
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

        private static bool PawnNearDeparturePad(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return pad.OccupiedRect().ExpandedBy(5).Contains(pawn.Position) && SameRoomAsPad(pad, pawn.Position);
        }

        private static bool PawnAtPickupShuttle(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return PickupBoardingRect(pad).Contains(pawn.Position);
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
            CellRect boardingRect = PickupBoardingRect(pad);
            List<IntVec3> candidates = boardingRect.Cells
                .Where(cell => cell.InBounds(map) && cell.Standable(map) && (cell.GetFirstPawn(map) == null || cell == pawn.Position))
                .OrderBy(cell => cell.DistanceToSquared(pawn.Position))
                .ThenBy(cell => cell.DistanceToSquared(pad.Position))
                .ToList();
            IntVec3 reachable = candidates
                .Where(cell => pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
            return reachable.IsValid || !bypassGuestArea ? reachable : candidates.DefaultIfEmpty(IntVec3.Invalid).First();
        }

        private static CellRect PickupBoardingRect(Thing pad)
        {
            // Boarding is based on the shuttle's center, not the full service pad footprint.
            return CellRect.CenteredOn(pad.Position, 5, 5);
        }

        private static IntVec3 DepartureWaitCell(Thing pad, Pawn pawn, bool bypassGuestArea)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            CellRect padRect = pad.OccupiedRect();
            CellRect noWaitRect = padRect.ExpandedBy(1);
            // The ring is intentionally tight so the shuttle is visibly picking up from this pad.
            List<IntVec3> candidates = padRect.ExpandedBy(2).Cells
                .Where(cell => cell.InBounds(map) && !noWaitRect.Contains(cell) && SameRoomAsPad(pad, cell) && cell.Standable(map) && (cell.GetFirstPawn(map) == null || cell == pawn.Position))
                .OrderBy(cell => cell.DistanceToSquared(pawn.Position))
                .ThenBy(cell => cell.DistanceToSquared(pad.Position))
                .ToList();
            IntVec3 reachable = candidates
                .Where(cell => pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
            return reachable.IsValid || !bypassGuestArea ? reachable : candidates.DefaultIfEmpty(IntVec3.Invalid).First();
        }

        public static List<ServiceDepartureBlock> BlockedDepartures()
        {
            List<ServiceDepartureBlock> blocks = new List<ServiceDepartureBlock>();
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
                    ServiceUse use = record.serviceKind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient;
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
            return blocks;
        }

        private static bool ShouldReportDepartureBlock(ServiceGroupRecord record)
        {
            if (record == null || record.state == "completed")
            {
                return false;
            }
            if (record.state == "departing" || record.state == "pickupInbound" || record.state == "boardingPickup")
            {
                return true;
            }
            return record.serviceKind == "hospital" && record.state == "arrived";
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
                bool requirementsMet = modeFallback ? comp.MeetsOperationalRequirements(out padReason) : comp.MeetsUseRequirements(use, out padReason);
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
                if (!ServiceEnvironmentUtility.IsPadSafeForPawns(pad, pawns, out string safetyReason))
                {
                    return safetyReason ?? "service pad is unsafe";
                }
                if (!PadReachableForPawns(pad, pawns, false, ShouldBypassGuestArea(record), out string reachReason))
                {
                    return reachReason ?? "service pad cannot be reached";
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
            bool requirementsMet = modeFallback ? comp.MeetsOperationalRequirements(out reason) : comp.MeetsUseRequirements(use, out reason);
            if (!requirementsMet)
            {
                reason = "departure pad blocked: " + (reason ?? "settings block this service");
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawns(record.reservedPad, record.pawns, out reason))
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

        private static bool ShouldBypassGuestArea(ServiceGroupRecord record)
        {
            return record != null && record.serviceKind == "hospitality";
        }

        private static Job ServiceGotoJob(IntVec3 cell, bool bypassGuestArea)
        {
            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            if (bypassGuestArea)
            {
                // Hospitality guest areas are for the stay, not the controlled shuttle transfer.
                Reflect.SetMember(job, "playerForced", true);
            }
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
