using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class ServicePawnUtility
    {
        public static bool IsTerminalPawn(Pawn pawn)
        {
            return pawn == null || pawn.Destroyed || pawn.Dead;
        }

        public static bool IsPlayerOwnedPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (pawn.Faction == Faction.OfPlayer || pawn.IsColonist)
            {
                return true;
            }
            return IsTrue(pawn, "IsColonistPlayerControlled") ||
                IsTrue(pawn, "IsPrisonerOfColony") ||
                IsTrue(pawn, "IsSlaveOfColony");
        }

        private static bool IsTrue(object instance, string memberName)
        {
            object value = Reflect.GetMember(instance, memberName);
            return value is bool flag && flag;
        }

        public static int ClearRuntimeLordReferences(Pawn pawn)
        {
            if (pawn == null)
            {
                return 0;
            }
            int cleared = 0;
            cleared += ClearJobLord(pawn.CurJob) ? 1 : 0;
            cleared += ClearQueuedJobLordReferences(pawn);
            cleared += ClearDutyLord(pawn.mindState == null ? null : pawn.mindState.duty) ? 1 : 0;
            Lord pawnLord = Reflect.GetMember(pawn, "lord") as Lord;
            if (pawnLord != null)
            {
                Reflect.SetMember(pawn, "lord", null);
                cleared++;
            }
            foreach (ThingComp comp in pawn.AllComps ?? new List<ThingComp>())
            {
                if (comp == null)
                {
                    continue;
                }
                Lord lord = Reflect.GetMember(comp, "lord") as Lord;
                if (lord == null)
                {
                    continue;
                }
                Reflect.SetMember(comp, "lord", null);
                cleared++;
            }
            return cleared;
        }

        public static int CleanupTerminalPawnReferences(Map map, Pawn pawn)
        {
            if (pawn == null)
            {
                return 0;
            }
            int cleaned = ClearRuntimeLordReferences(pawn);
            cleaned += CleanupInvalidDirectRelations(pawn);
            cleaned += CleanupLordOwnedPawnReferences(map, pawn);
            cleaned += CleanupRelationshipRecordsReferencing(pawn);
            return cleaned;
        }

        public static int CleanupInvalidDirectRelations(Pawn pawn)
        {
            if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
            {
                return 0;
            }
            return pawn.relations.DirectRelations.RemoveAll(relation =>
                relation == null ||
                relation.def == null ||
                relation.otherPawn == null ||
                relation.otherPawn.Destroyed ||
                relation.otherPawn.relations == null);
        }

        public static bool NotifyLordPawnLost(Lord lord, Pawn pawn, PawnLostCondition condition)
        {
            if (lord == null || pawn == null)
            {
                return false;
            }
            try
            {
                lord.Notify_PawnLost(pawn, condition);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not notify service pawn lord about pawn loss: " + ex.Message);
                return false;
            }
        }

        public static bool ClearJobLord(Job job)
        {
            if (job != null && job.lord != null)
            {
                job.lord = null;
                return true;
            }
            return false;
        }

        private static int ClearQueuedJobLordReferences(Pawn pawn)
        {
            object queue = pawn == null || pawn.jobs == null ? null : Reflect.GetMember(pawn.jobs, "jobQueue");
            IEnumerable enumerable = queue as IEnumerable;
            if (enumerable == null)
            {
                return 0;
            }

            int cleared = 0;
            foreach (object queued in enumerable)
            {
                if (ClearJobLord(Reflect.GetMember(queued, "job") as Job))
                {
                    cleared++;
                }
            }
            return cleared;
        }

        private static bool ClearDutyLord(PawnDuty duty)
        {
            if (duty == null)
            {
                return false;
            }
            Lord lord = Reflect.GetMember(duty, "lord") as Lord;
            if (lord == null)
            {
                return false;
            }
            Reflect.SetMember(duty, "lord", null);
            return true;
        }

        private static int CleanupLordOwnedPawnReferences(Map map, Pawn pawn)
        {
            if (map == null || pawn == null || map.lordManager == null || map.lordManager.lords == null)
            {
                return 0;
            }
            int removed = 0;
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord == null || lord.ownedPawns == null)
                {
                    continue;
                }
                removed += lord.ownedPawns.RemoveAll(owned => owned == null || owned == pawn || owned.Destroyed || owned.Dead);
            }
            return removed;
        }

        public static int CleanupRelationshipRecordsReferencing(Pawn departingPawn)
        {
            if (departingPawn == null)
            {
                return 0;
            }
            return CleanupRelationshipRecordReferences(pawn => pawn == null || pawn == departingPawn || pawn.Destroyed);
        }

        public static int CleanupBrokenRelationshipRecords()
        {
            HashSet<Pawn> knownPawns = KnownPersistentPawns();
            return CleanupRelationshipRecordReferences(pawn => pawn == null || pawn.Destroyed || !knownPawns.Contains(pawn));
        }

        public static HashSet<Pawn> KnownPersistentPawnsForCleanup()
        {
            return KnownPersistentPawns();
        }

        private static int CleanupRelationshipRecordReferences(Func<Pawn, bool> shouldRemove)
        {
            if (shouldRemove == null)
            {
                return 0;
            }
            object relationshipRecords = RelationshipRecords();
            IDictionary records = relationshipRecords == null ? null : Reflect.GetMember(relationshipRecords, "records") as IDictionary;
            if (records == null)
            {
                return 0;
            }

            int removed = 0;
            foreach (object record in records.Values)
            {
                IList references = Reflect.GetMember(record, "references") as IList;
                if (references == null)
                {
                    continue;
                }
                for (int i = references.Count - 1; i >= 0; i--)
                {
                    Pawn pawn = references[i] as Pawn;
                    if (shouldRemove(pawn))
                    {
                        references.RemoveAt(i);
                        removed++;
                    }
                }
            }
            return removed;
        }

        private static object RelationshipRecords()
        {
            object world = Find.World;
            return Reflect.GetMember(world, "relationshipRecords") ?? Reflect.GetMember(world, "RelationshipRecords");
        }

        private static HashSet<Pawn> KnownPersistentPawns()
        {
            HashSet<Pawn> pawns = new HashSet<Pawn>();
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead ?? Enumerable.Empty<Pawn>())
            {
                if (pawn != null)
                {
                    pawns.Add(pawn);
                }
            }
            foreach (Map map in Find.Maps ?? Enumerable.Empty<Map>())
            {
                if (map == null || map.mapPawns == null)
                {
                    continue;
                }
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
                foreach (Pawn pawn in map.mapPawns.AllPawnsUnspawned)
                {
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
            }
            return pawns;
        }
    }
}
