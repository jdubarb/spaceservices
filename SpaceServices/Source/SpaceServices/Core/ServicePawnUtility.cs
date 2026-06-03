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
            cleaned += CleanupDirectRelationsReferencing(pawn, map);
            cleaned += CleanupLordOwnedPawnReferences(map, pawn);
            cleaned += CleanupRelationshipRecordsReferencing(pawn);
            return cleaned;
        }

        public static int CleanupInvalidDirectRelations(Pawn pawn)
        {
            return CleanupInvalidDirectRelations(pawn, KnownPersistentPawns());
        }

        public static int CleanupDirectRelationsReferencing(Pawn targetPawn, Map map = null)
        {
            if (targetPawn == null)
            {
                return 0;
            }
            HashSet<Pawn> knownPawns = KnownPersistentPawns();
            int removed = 0;
            foreach (Pawn pawn in PawnsForRelationCleanup(map, knownPawns, targetPawn))
            {
                if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
                {
                    continue;
                }
                removed += pawn.relations.DirectRelations.RemoveAll(relation =>
                    relation == null ||
                    relation.otherPawn == targetPawn ||
                    IsInvalidDirectRelation(pawn, relation, knownPawns));
            }
            return removed;
        }

        public static int DetachDirectRelationsForDeparture(Pawn departingPawn, Map map = null)
        {
            if (departingPawn == null)
            {
                return 0;
            }

            int removed = CleanupDirectRelationsReferencing(departingPawn, map);
            List<DirectPawnRelation> directRelations = departingPawn.relations == null ? null : departingPawn.relations.DirectRelations;
            if (directRelations == null || directRelations.Count == 0)
            {
                return removed;
            }

            // Service pawns are transient visitors/patients. Clearing their direct relations before they
            // become world-GC candidates avoids vanilla ClearAllRelations touching stale reciprocal refs later.
            foreach (DirectPawnRelation relation in directRelations.ToList())
            {
                Pawn otherPawn = relation == null ? null : relation.otherPawn;
                if (otherPawn == null || otherPawn.relations == null || otherPawn.relations.DirectRelations == null)
                {
                    continue;
                }
                removed += otherPawn.relations.DirectRelations.RemoveAll(otherRelation =>
                    otherRelation == null ||
                    otherRelation.otherPawn == departingPawn ||
                    IsInvalidDirectRelation(otherPawn, otherRelation, null));
            }
            removed += directRelations.Count;
            directRelations.Clear();
            return removed;
        }

        public static int CleanupBrokenDirectRelations(Map map = null)
        {
            HashSet<Pawn> knownPawns = KnownPersistentPawns();
            int removed = 0;
            foreach (Pawn pawn in PawnsForRelationCleanup(map, knownPawns))
            {
                removed += CleanupInvalidDirectRelations(pawn, knownPawns);
            }
            return removed;
        }

        private static int CleanupInvalidDirectRelations(Pawn pawn, HashSet<Pawn> knownPawns)
        {
            if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
            {
                return 0;
            }
            return pawn.relations.DirectRelations.RemoveAll(relation => IsInvalidDirectRelation(pawn, relation, knownPawns));
        }

        private static bool IsInvalidDirectRelation(Pawn owner, DirectPawnRelation relation, HashSet<Pawn> knownPawns)
        {
            if (relation == null || relation.def == null || relation.otherPawn == null)
            {
                return true;
            }
            Pawn otherPawn = relation.otherPawn;
            return otherPawn == owner ||
                otherPawn.Destroyed ||
                otherPawn.relations == null ||
                otherPawn.relations.DirectRelations == null ||
                (knownPawns != null && !knownPawns.Contains(otherPawn)) ||
                IsMissingReflexiveReciprocal(owner, relation);
        }

        private static bool IsMissingReflexiveReciprocal(Pawn owner, DirectPawnRelation relation)
        {
            if (owner == null || relation == null || relation.def == null || !relation.def.reflexive)
            {
                return false;
            }
            Pawn otherPawn = relation.otherPawn;
            if (otherPawn == null || otherPawn.relations == null || otherPawn.relations.DirectRelations == null)
            {
                return true;
            }
            return !otherPawn.relations.DirectRelations.Any(otherRelation =>
                otherRelation != null &&
                otherRelation.def == relation.def &&
                otherRelation.otherPawn == owner);
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

        private static IEnumerable<Pawn> PawnsForRelationCleanup(Map map, HashSet<Pawn> knownPawns, Pawn inboundTarget = null)
        {
            HashSet<Pawn> pawns = new HashSet<Pawn>();
            if (knownPawns != null)
            {
                foreach (Pawn pawn in knownPawns)
                {
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
            }
            if (map != null && map.mapPawns != null)
            {
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
            if (inboundTarget != null && inboundTarget.relations != null)
            {
                // Vanilla stores incoming direct-relation owners separately and may touch them
                // during Pawn.Discard even when they are not in the usual pawn finder lists.
                IEnumerable inboundPawns = Reflect.GetMember(inboundTarget.relations, "pawnsWithDirectRelationsWithMe") as IEnumerable;
                if (inboundPawns != null)
                {
                    foreach (object inbound in inboundPawns)
                    {
                        Pawn pawn = inbound as Pawn;
                        if (pawn != null)
                        {
                            pawns.Add(pawn);
                        }
                    }
                }
            }
            return pawns;
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
