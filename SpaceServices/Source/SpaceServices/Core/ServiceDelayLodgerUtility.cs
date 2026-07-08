using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public static class ServiceDelayLodgerUtility
    {
        private const float VacuumEpsilon = 0.001f;
        private const string FallbackAreaLabel = "Space Services hold";
        private const string DelayQuestName = "Space Services pickup delay";

        public static bool TryConvertHospitalPatientsToQuestLodgers(ServiceGroupRecord record, Map map, out string reason)
        {
            reason = null;
            if (record == null || record.serviceKind != "hospital")
            {
                reason = "not a hospital service record";
                return false;
            }
            if (record.departureHoldQuestLodgerHandoffDone)
            {
                return true;
            }
            if (map == null)
            {
                map = RecordMap(record);
            }
            if (map == null || map.Parent == null)
            {
                reason = "no active map for temporary lodger quest";
                return false;
            }

            List<Pawn> pawns = EligibleDelayPawns(record).ToList();
            if (pawns.Count == 0)
            {
                reason = "no eligible hospital patients";
                return false;
            }

            Area area = record.departureHoldFallbackArea;
            if (area == null || area.Map != map || area.TrueCount == 0)
            {
                area = TryCreateFallbackArea(map, pawns);
                if (area == null)
                {
                    reason = "could not create a safe temporary allowed area";
                    return false;
                }
                record.departureHoldFallbackArea = area;
                record.departureHoldFallbackAreaCreated = true;
            }

            Faction originalFaction = pawns.FirstOrDefault(pawn => pawn.Faction != null)?.Faction;
            if (originalFaction == null)
            {
                reason = "hospital patients had no source faction";
                return false;
            }

            Quest quest = Quest.MakeRaw();
            EnsureDelayQuestRoot(quest);
            string delayReason = DelayReason(record, map);
            quest.name = DelayQuestName;
            quest.description = DelayDescription(pawns, delayReason);
            quest.appearanceTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            quest.hidden = true;
            quest.SetInitiallyAccepted();
            RememberOriginalState(record, pawns);
            QuestGen_Factions.ExtraFaction(quest, originalFaction, pawns, ExtraFactionType.HomeFaction, false, null);
            QuestGen_Misc.JoinPlayer(quest, map.Parent, pawns, true, false, quest.InitiateSignal);

            record.departureHoldQuest = quest;
            record.departureHoldQuestLodgerHandoffDone = true;

            Find.QuestManager.Add(quest);
            foreach (Pawn pawn in pawns)
            {
                PrepareDelayLodgerPawn(pawn, area);
            }

            Messages.Message("Space Services: Delayed pickup converted " + pawns.Count + " patient" + (pawns.Count == 1 ? "" : "s") + " into temporary lodger" + (pawns.Count == 1 ? "" : "s") + " because shuttle departure is blocked and Hospitality guest handling is unavailable.", MessageTypeDefOf.NeutralEvent, false);
            ServiceDebugUtility.LogAudit("Prepared hospital departure hold with vanilla quest lodgers record=" + record.id + " pawns=" + PawnSummary(pawns) + " area=" + (area == null ? "null" : area.Label));
            return true;
        }

        public static void EnforceDelayLodgers(ServiceGroupRecord record, Map map)
        {
            if (record == null || !record.departureHoldQuestLodgerHandoffDone)
            {
                return;
            }
            Area area = record.departureHoldFallbackArea;
            foreach (Pawn pawn in record.departureHoldQuestLodgerPawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || pawn.Destroyed || !pawn.Spawned)
                {
                    continue;
                }
                if (map == null)
                {
                    map = pawn.MapHeld ?? pawn.Map;
                }
                Area holdArea = area != null && area.Map == map && area.TrueCount > 0
                    ? area
                    : ServiceLifecycleUtility.DepartureHoldArea(map, record, pawn);
                PrepareDelayLodgerPawn(pawn, holdArea);
                if (holdArea != null && JobTargetsOutsideArea(pawn.CurJob, map, holdArea))
                {
                    pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
        }

        public static bool IsDelayLodger(ServiceGroupRecord record, Pawn pawn)
        {
            return record != null &&
                pawn != null &&
                record.departureHoldQuestLodgerHandoffDone &&
                record.departureHoldQuestLodgerPawns != null &&
                record.departureHoldQuestLodgerPawns.Contains(pawn);
        }

        public static void PrepareDelayLodgersForDeparture(ServiceGroupRecord record)
        {
            if (record == null || !record.departureHoldQuestLodgerHandoffDone)
            {
                return;
            }
            ServiceDebugUtility.LogAudit("PrepareDelayLodgersForDeparture begin record=" + record.id + " pawns=" + PawnSummary(record.departureHoldQuestLodgerPawns));
            EndDelayQuest(record, QuestEndOutcome.Success);
            RestoreOriginalState(record);
            DeleteFallbackArea(record);
            ServiceDebugUtility.LogAudit("PrepareDelayLodgersForDeparture end record=" + record.id);
        }

        public static void CleanupRecord(ServiceGroupRecord record, QuestEndOutcome outcome)
        {
            if (record == null)
            {
                return;
            }
            if (record.departureHoldQuestLodgerHandoffDone)
            {
                EndDelayQuest(record, outcome);
                RestoreOriginalState(record);
            }
            DeleteFallbackArea(record);
        }

        private static IEnumerable<Pawn> EligibleDelayPawns(ServiceGroupRecord record)
        {
            foreach (Pawn pawn in record.pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null ||
                    pawn.Destroyed ||
                    pawn.Dead ||
                    !pawn.Spawned ||
                    pawn.RaceProps == null ||
                    !pawn.RaceProps.Humanlike ||
                    ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    continue;
                }
                yield return pawn;
            }
        }

        private static void RememberOriginalState(ServiceGroupRecord record, List<Pawn> pawns)
        {
            record.departureHoldQuestLodgerPawns = new List<Pawn>();
            record.departureHoldQuestLodgerOriginalFactions = new List<Faction>();
            record.departureHoldQuestLodgerOriginalAreas = new List<Area>();
            foreach (Pawn pawn in pawns)
            {
                record.departureHoldQuestLodgerPawns.Add(pawn);
                record.departureHoldQuestLodgerOriginalFactions.Add(pawn.Faction);
                record.departureHoldQuestLodgerOriginalAreas.Add(pawn.playerSettings == null ? null : pawn.playerSettings.AreaRestrictionInPawnCurrentMap);
            }
        }

        private static void PrepareDelayLodgerPawn(Pawn pawn, Area area)
        {
            if (pawn == null || pawn.Destroyed)
            {
                return;
            }
            if (pawn.drafter != null && pawn.drafter.Drafted)
            {
                pawn.drafter.Drafted = false;
            }
            if (pawn.workSettings != null)
            {
                pawn.workSettings.DisableAll();
            }
            if (pawn.playerSettings != null && area != null)
            {
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            }
        }

        private static void RestoreOriginalState(ServiceGroupRecord record)
        {
            List<Pawn> pawns = record.departureHoldQuestLodgerPawns ?? new List<Pawn>();
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                Area originalArea = i < (record.departureHoldQuestLodgerOriginalAreas?.Count ?? 0)
                    ? record.departureHoldQuestLodgerOriginalAreas[i]
                    : null;
                Faction originalFaction = i < (record.departureHoldQuestLodgerOriginalFactions?.Count ?? 0)
                    ? record.departureHoldQuestLodgerOriginalFactions[i]
                    : null;
                if (pawn.playerSettings != null)
                {
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = originalArea;
                }
                if (originalFaction != null && pawn.Faction == Faction.OfPlayer)
                {
                    pawn.SetFaction(originalFaction);
                }
            }
            record.departureHoldQuestLodgerHandoffDone = false;
            record.departureHoldQuestLodgerPawns = new List<Pawn>();
            record.departureHoldQuestLodgerOriginalFactions = new List<Faction>();
            record.departureHoldQuestLodgerOriginalAreas = new List<Area>();
        }

        private static void EndDelayQuest(ServiceGroupRecord record, QuestEndOutcome outcome)
        {
            Quest quest = record.departureHoldQuest;
            record.departureHoldQuest = null;
            if (quest == null || quest.Historical)
            {
                return;
            }
            try
            {
                EnsureDelayQuestRoot(quest);
                quest.End(outcome, false, false);
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospital, "Could not end Space Services delay lodger quest: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void EnsureDelayQuestRoot(Quest quest)
        {
            if (quest == null || quest.root != null)
            {
                return;
            }
            QuestScriptDef root = SpaceServicesDefOf.JDB_SpaceServices_DelayLodgerQuest;
            if (root == null)
            {
                root = DefDatabase<QuestScriptDef>.GetNamedSilentFail("JDB_SpaceServices_DelayLodgerQuest");
            }
            if (root != null)
            {
                quest.root = root;
            }
        }

        private static Area TryCreateFallbackArea(Map map, List<Pawn> pawns)
        {
            if (map == null || map.areaManager == null || !map.areaManager.TryMakeNewAllowed(out Area_Allowed area))
            {
                return null;
            }
            Reflect.SetMember(area, "labelInt", FallbackAreaLabel);
            area.Clear();
            foreach (IntVec3 cell in map.AllCells)
            {
                if (CanUseFallbackAreaCell(cell, map, pawns))
                {
                    area[cell] = true;
                }
            }
            if (area.TrueCount == 0)
            {
                area.Delete();
                return null;
            }
            return area;
        }

        private static bool CanUseFallbackAreaCell(IntVec3 cell, Map map, List<Pawn> pawns)
        {
            if (!cell.InBounds(map) || cell.OnEdge(map) || ServiceEnvironmentUtility.GetVacuum(cell, map) > VacuumEpsilon)
            {
                return false;
            }
            if (!cell.Walkable(map) && !cell.GetThingList(map).Any(thing => thing is Building_Bed))
            {
                return false;
            }
            return pawns == null || pawns.Count == 0 || pawns.Any(pawn => pawn != null && pawn.Spawned && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly));
        }

        private static void DeleteFallbackArea(ServiceGroupRecord record)
        {
            if (record == null || !record.departureHoldFallbackAreaCreated)
            {
                return;
            }
            Area area = record.departureHoldFallbackArea;
            record.departureHoldFallbackArea = null;
            record.departureHoldFallbackAreaCreated = false;
            if (area != null)
            {
                area.Delete();
            }
        }

        private static bool JobTargetsOutsideArea(Job job, Map map, Area area)
        {
            if (job == null || map == null || area == null)
            {
                return false;
            }
            return TargetOutsideArea(job.targetA, map, area) ||
                TargetOutsideArea(job.targetB, map, area) ||
                TargetOutsideArea(job.targetC, map, area);
        }

        private static bool TargetOutsideArea(LocalTargetInfo target, Map map, Area area)
        {
            return target.IsValid &&
                target.Cell.IsValid &&
                target.Cell.InBounds(map) &&
                !area[target.Cell];
        }

        private static Map RecordMap(ServiceGroupRecord record)
        {
            if (record == null)
            {
                return null;
            }
            if (record.reservedPad != null)
            {
                return record.reservedPad.Map;
            }
            return (record.pawns ?? new List<Pawn>())
                .Where(pawn => pawn != null && pawn.Spawned)
                .Select(pawn => pawn.Map)
                .FirstOrDefault(map => map != null);
        }

        private static string PawnSummary(IEnumerable<Pawn> pawns)
        {
            return string.Join(", ", (pawns ?? Enumerable.Empty<Pawn>()).Where(pawn => pawn != null).Select(pawn => pawn.LabelShortCap).ToArray());
        }

        private static string DelayDescription(List<Pawn> pawns, string reason)
        {
            string names = PawnSummary(pawns);
            if (string.IsNullOrEmpty(names))
            {
                names = "Service patients";
            }
            string verb = pawns != null && pawns.Count == 1 ? "is" : "are";
            return names + " " + verb + " temporary lodgers because Space Services cannot safely launch a pickup shuttle" +
                (string.IsNullOrEmpty(reason) ? "." : ": " + reason + ".") +
                " They will be released back to their faction when pickup conditions are safe.";
        }

        private static string DelayReason(ServiceGroupRecord record, Map map)
        {
            if (record != null && map != null && ServiceDangerUtility.DepartureShuttleBlocked(map, record.serviceKind, out string reason))
            {
                return reason;
            }
            return "pickup conditions are not safe";
        }
    }
}
