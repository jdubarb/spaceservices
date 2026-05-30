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
            if (map == null || pawns == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return;
            }

            List<Pawn> list = pawns.Where(p => p != null && !p.Destroyed).Distinct().ToList();
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
                foreach (Pawn pawn in list)
                {
                    if (!existing.pawns.Contains(pawn))
                    {
                        existing.pawns.Add(pawn);
                    }
                }
                existing.timeoutTick = Math.Max(existing.timeoutTick, Find.TickManager.TicksGame + GenDate.TicksPerDay * 3);
                return;
            }

            ServiceGroupRecord record = new ServiceGroupRecord
            {
                id = "SS-" + Find.UniqueIDsManager.GetNextThingID(),
                serviceKind = kind,
                state = "arrived",
                arrivalTick = Find.TickManager.TicksGame,
                timeoutTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * 3,
                pawns = list
            };

            if (kind != "hospital")
            {
                ServiceUse use = kind == "hospitality" ? ServiceUse.Guest : ServiceUse.Patient;
                Thing pad = ServicePadUtility.TryReserveServicePad(map, use, record.id);
                if (pad != null)
                {
                    record.reservedPad = pad;
                }
            }

            comp.serviceGroups.Add(record);
            Log.Message("[Space Services] Registered " + kind + " service group " + record.id + " pawns=" + list.Count + " padReserved=" + (record.reservedPad != null));
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

        public static bool RequestDepartureForPawn(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
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
            ReleaseRecord(record);
            record.state = "completed";
            Log.Message("[Space Services] Released service group " + record.id + ": " + reason);
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
                record.pawns = ActiveTrackedPawns(map, record);
                if (record.pawns.Count == 0)
                {
                    ReleaseRecord(record);
                    records.RemoveAt(i);
                    continue;
                }
                if (record.serviceKind == "hospitality" && record.state == "arrived")
                {
                    GuardHospitalityGuestsFromVacuum(map, record);
                }
                if (record.state == "pickupInbound")
                {
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
                    if (record.serviceKind == "hospital")
                    {
                        BeginHospitalDeparture(map, record, "waiting for free departure pad");
                        continue;
                    }
                    if (ReadyForExtraction(record))
                    {
                        BeginPickupShuttle(record, "service pawns waiting outside departure pad");
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
            List<Pawn> pawns = record == null || record.pawns == null ? new List<Pawn>() : record.pawns.Where(p => p != null && !p.Destroyed).Distinct().ToList();
            if (record == null || record.serviceKind != "hospital" || pawns.Count == 0)
            {
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

        private static void BeginDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.state == "completed")
            {
                return;
            }
            if (record.serviceKind == "hospital")
            {
                BeginHospitalDeparture(map, record, reason);
                return;
            }
            if (record.reservedPad == null)
            {
                DepartureUtility.CompleteDeparture(map, record, reason);
                return;
            }
            if (ReadyForExtraction(record))
            {
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

        private static void BeginHospitalDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record.reservedPad != null && !ReservedPadStillExists(record))
            {
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad != null && !PadCanSafelyServe(record.reservedPad, ServiceUse.Patient, record.pawns, record.id))
            {
                ReleaseRecord(record);
                record.reservedPad = null;
            }
            if (record.reservedPad == null)
            {
                record.reservedPad = TryReserveBestDeparturePad(map, ServiceUse.Patient, record);
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
                DepartureUtility.CompleteDeparture(record.reservedPad.Map, record, reason);
                return;
            }

            record.state = "pickupInbound";
            record.pickupShuttleTouchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks;
            record.pickupShuttleThingDefName = visual.shipThingDef.defName;
            ServiceShuttleUtility.SpawnArrival(record.reservedPad.Map, record.reservedPad.Position);
            Log.Message("[Space Services] Pickup shuttle inbound for " + record.serviceKind + " service group " + record.id + ": " + reason);
        }

        private static Thing TryReserveBestDeparturePad(Map map, ServiceUse use, ServiceGroupRecord record)
        {
            if (map == null || record == null || string.IsNullOrEmpty(record.id))
            {
                return null;
            }
            List<Pawn> pawns = record.pawns == null ? new List<Pawn>() : record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).ToList();
            // Pick the pad the patient can actually survive at before considering distance.
            IEnumerable<Thing> candidates = ServicePadUtility.AllServicePads(map, use)
                .Where(pad => PadCanSafelyServe(pad, use, pawns, record.id))
                .OrderBy(pad => DeparturePadScore(map, record, pad, pawns));
            foreach (Thing pad in candidates)
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.TryReserve(record.id))
                {
                    return pad;
                }
            }
            return null;
        }

        private static float DeparturePadScore(Map map, ServiceGroupRecord record, Thing pad, List<Pawn> pawns)
        {
            if (pad == null)
            {
                return float.MaxValue;
            }
            List<Pawn> spawned = pawns == null ? new List<Pawn>() : pawns.Where(pawn => pawn != null && pawn.Spawned).ToList();
            float score = spawned.Sum(pawn =>
            {
                IntVec3 waitCell = DepartureWaitCell(pad, pawn);
                IntVec3 target = waitCell.IsValid ? waitCell : pad.Position;
                return pawn.Position.DistanceToSquared(target);
            });
            if (ShouldPreserveSealedPadsForLowResistancePatients(map, record, pawns))
            {
                score += ServiceEnvironmentUtility.GetMaxVacuum(pad) > 0.05f ? -100000f : 100000f;
            }
            return score;
        }

        private static bool ShouldPreserveSealedPadsForLowResistancePatients(Map map, ServiceGroupRecord record, List<Pawn> pawns)
        {
            if (map == null || record == null || record.serviceKind != "hospital" || pawns == null || pawns.Count == 0)
            {
                return false;
            }
            if (pawns.Any(pawn => pawn != null && !pawn.Destroyed && VacSuitUtility.VacuumResistance(pawn) < 0.95f))
            {
                return false;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return false;
            }
            return comp.serviceGroups.Any(other =>
                other != null &&
                other != record &&
                other.serviceKind == "hospital" &&
                other.state != "completed" &&
                other.pawns != null &&
                other.pawns.Any(pawn => pawn != null && !pawn.Destroyed && VacSuitUtility.VacuumResistance(pawn) < 0.95f));
        }

        private static bool PadCanSafelyServe(Thing pad, ServiceUse use, IEnumerable<Pawn> pawns, string groupId)
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
                PadReachableForPawns(pad, pawns, false, out string _);
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
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                if (pawn.Spawned && !PawnSafelyStagedForPickup(pawn, record.reservedPad))
                {
                    return false;
                }
            }
            return true;
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
                IntVec3 waitCell = DepartureWaitCell(record.reservedPad, pawn);
                if (!waitCell.IsValid || pawn.Position == waitCell)
                {
                    continue;
                }
                if (!pawn.CanReach(waitCell, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Goto, waitCell);
                job.locomotionUrgency = LocomotionUrgency.Jog;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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
                IntVec3 boardCell = PickupBoardingCell(record.reservedPad, pawn);
                if (!boardCell.IsValid || pawn.Position == boardCell)
                {
                    continue;
                }
                if (!pawn.CanReach(boardCell, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Goto, boardCell);
                job.locomotionUrgency = LocomotionUrgency.Jog;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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

        private static bool PawnAtPickupShuttle(Pawn pawn, Thing pad)
        {
            if (pawn == null || !pawn.Spawned || pad == null || pad.Map == null)
            {
                return false;
            }
            return PickupBoardingRect(pad).Contains(pawn.Position);
        }

        private static bool PadReachableForPawns(Thing pad, IEnumerable<Pawn> pawns, bool boarding, out string reason)
        {
            reason = null;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Downed)
                {
                    continue;
                }
                IntVec3 cell = boarding ? PickupBoardingCell(pad, pawn) : DepartureWaitCell(pad, pawn);
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

        private static IntVec3 PickupBoardingCell(Thing pad, Pawn pawn)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            CellRect boardingRect = PickupBoardingRect(pad);
            return boardingRect.Cells
                .Where(cell => cell.InBounds(map) && cell.Standable(map) && (cell.GetFirstPawn(map) == null || cell == pawn.Position))
                .Where(cell => pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(cell => cell.DistanceToSquared(pawn.Position))
                .ThenBy(cell => cell.DistanceToSquared(pad.Position))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
        }

        private static CellRect PickupBoardingRect(Thing pad)
        {
            // Boarding is based on the shuttle's center, not the full service pad footprint.
            return CellRect.CenteredOn(pad.Position, 3, 3);
        }

        private static IntVec3 DepartureWaitCell(Thing pad, Pawn pawn)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            CellRect padRect = pad.OccupiedRect();
            CellRect noWaitRect = padRect.ExpandedBy(1);
            // The ring is intentionally tight so the shuttle is visibly picking up from this pad.
            return padRect.ExpandedBy(2).Cells
                .Where(cell => cell.InBounds(map) && !noWaitRect.Contains(cell) && SameRoomAsPad(pad, cell) && cell.Standable(map) && (cell.GetFirstPawn(map) == null || cell == pawn.Position))
                .Where(cell => pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(cell => cell.DistanceToSquared(pawn.Position))
                .ThenBy(cell => cell.DistanceToSquared(pad.Position))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
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
            List<Thing> pads = ServicePadUtility.AllServicePads(map, use).ToList();
            if (pads.Count == 0)
            {
                return "no " + use.ToString().ToLowerInvariant() + " service pad exists";
            }
            foreach (Thing pad in pads)
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                if (comp == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(comp.reservedForGroup) && (record == null || comp.reservedForGroup != record.id))
                {
                    continue;
                }
                if (!comp.MeetsUseRequirements(use, out string padReason))
                {
                    return "service pad blocked: " + (padReason ?? "settings block this service");
                }
                if (!ServiceEnvironmentUtility.IsPadSafeForPawns(pad, pawns, out string safetyReason))
                {
                    return safetyReason ?? "service pad is unsafe";
                }
                if (!PadReachableForPawns(pad, pawns, false, out string reachReason))
                {
                    return reachReason ?? "service pad cannot be reached";
                }
                return null;
            }
            return "all matching service pads are reserved";
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
            if (!comp.MeetsUseRequirements(use, out reason))
            {
                reason = "departure pad blocked: " + (reason ?? "settings block this service");
                return false;
            }
            if (!ServiceEnvironmentUtility.IsPadSafeForPawns(record.reservedPad, record.pawns, out reason))
            {
                return false;
            }
            bool boarding = record.state == "boardingPickup";
            return PadReachableForPawns(record.reservedPad, record.pawns, boarding, out reason);
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
