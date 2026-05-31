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
    public static class DepartureUtility
    {
        public static bool CompleteDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.pawns == null)
            {
                return false;
            }

            record.state = "extracting";
            record.departureRequestedTick = Find.TickManager.TicksGame;
            Log.Message("[Space Services] Departing " + record.serviceKind + " service group " + record.id + ": " + reason);
            ServiceDebugUtility.LogAudit("CompleteDeparture begin id=" + record.id + " kind=" + record.serviceKind + " state=" + record.state + " reason=" + (reason ?? "none") + " pawns=" + PawnListAudit(record.pawns) + " pad=" + ServiceDebugUtility.ThingAuditSummary(record.reservedPad));
            Map padMap = record.reservedPad == null ? null : record.reservedPad.Map;
            IntVec3 padCell = record.reservedPad == null ? IntVec3.Invalid : record.reservedPad.Position;
            ServiceShuttleUtility.CleanupTouchdownShuttle(padMap, padCell, record.pickupShuttleThingDefName);

            List<Pawn> departingPawns = record.pawns.Where(pawn => !ServicePawnUtility.IsTerminalPawn(pawn) && !ServicePawnUtility.IsPlayerOwnedPawn(pawn)).ToList();
            if (departingPawns.Count == 0)
            {
                record.state = "completed";
                ReleaseReservation(record);
                ServiceDebugUtility.LogAudit("CompleteDeparture finished without extractable pawns id=" + record.id + " pawns=" + PawnListAudit(record.pawns));
                return true;
            }
            bool completed = TryAutoExtract(map, departingPawns, reason);
            ServiceDebugUtility.LogAudit("CompleteDeparture extraction result id=" + record.id + " completed=" + completed + " pawns=" + PawnListAudit(record.pawns));
            if (completed)
            {
                if (record.reservedPad != null && record.reservedPad.Spawned)
                {
                    ServiceShuttleUtility.SpawnDeparture(record.reservedPad.Map, record.reservedPad.Position);
                }
                record.state = "completed";
                if (record.serviceKind == "hospital")
                {
                    NotifyHospitalPatientsLeft(map, departingPawns);
                }
                ReleaseReservation(record);
                ServiceDebugUtility.LogAudit("CompleteDeparture finished id=" + record.id + " state=" + record.state);
                Messages.Message("Space Services: service group departed", MessageTypeDefOf.NeutralEvent, false);
            }
            return completed;
        }

        public static bool TryAutoExtract(Map map, IEnumerable<Pawn> pawns, string reason)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.autoExtractFallback)
            {
                return false;
            }
            bool any = false;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (ServicePawnUtility.IsTerminalPawn(pawn))
                {
                    ServiceDebugUtility.LogAudit("TryAutoExtract skip destroyed/null pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
                    continue;
                }
                if (ServicePawnUtility.IsPlayerOwnedPawn(pawn))
                {
                    Log.Warning("[Space Services] Refusing to auto-extract player-owned service pawn: " + ServiceDebugUtility.PawnAuditSummary(pawn) + ", reason=" + (reason ?? "none"));
                    continue;
                }
                any = true;
                ServiceDebugUtility.LogAudit("TryAutoExtract considering " + ServiceDebugUtility.PawnAuditSummary(pawn) + " reason=" + (reason ?? "none"));
                if (pawn.Spawned)
                {
                    if (!TryExitSpawnedPawn(pawn, reason))
                    {
                        ServiceDebugUtility.LogAudit("TryAutoExtract falling back to despawn for " + ServiceDebugUtility.PawnAuditSummary(pawn));
                        LogServicePawnRemoval(pawn, "despawn fallback", reason);
                        NotifyLordPawnExited(pawn);
                        CleanupDepartingPawnReferences(map ?? pawn.MapHeld, pawn);
                        pawn.DeSpawn(DestroyMode.Vanish);
                    }
                }
                else if (pawn.MapHeld != null)
                {
                    ServiceDebugUtility.LogAudit("TryAutoExtract destroying unspawned held pawn " + ServiceDebugUtility.PawnAuditSummary(pawn));
                    LogServicePawnRemoval(pawn, "destroy unspawned", reason);
                    NotifyLordPawnExited(pawn);
                    CleanupDepartingPawnReferences(map ?? pawn.MapHeld, pawn);
                    pawn.Destroy(DestroyMode.Vanish);
                }
            }
            if (any)
            {
                Log.Message("[Space Services] Auto-extracted service pawns: " + reason);
            }
            return any;
        }

        private static bool TryExitSpawnedPawn(Pawn pawn, string reason)
        {
            if (pawn == null || !pawn.Spawned)
            {
                return false;
            }
            try
            {
                // A real map exit keeps RimWorld's play logs, tales, relations, and lords consistent.
                ServiceDebugUtility.LogAudit("TryExitSpawnedPawn before prep " + ServiceDebugUtility.PawnAuditSummary(pawn));
                PreparePawnJobsForExit(pawn);
                ServiceDebugUtility.LogAudit("TryExitSpawnedPawn after prep " + ServiceDebugUtility.PawnAuditSummary(pawn));
                LogServicePawnRemoval(pawn, "ExitMap", reason);
                pawn.ExitMap(false, Rot4.Invalid);
                int runtimeLords = ServicePawnUtility.ClearRuntimeLordReferences(pawn);
                ServiceDebugUtility.LogAudit("TryExitSpawnedPawn after ExitMap runtimeLordRefsCleared=" + runtimeLords + " " + ServiceDebugUtility.PawnAuditSummary(pawn));
                ServiceDebugUtility.LogAudit("TryExitSpawnedPawn after ExitMap " + ServiceDebugUtility.PawnAuditSummary(pawn));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not exit service pawn cleanly, falling back to vanish: " + ex.Message);
                return false;
            }
        }

        private static void LogServicePawnRemoval(Pawn pawn, string method, string reason)
        {
            string label = pawn == null ? "null pawn" : pawn.LabelShortCap + " [" + pawn.ThingID + "]";
            IntVec3 cell = pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
            Log.Message("[Space Services] Removing service pawn via " + method + ": " + label + ", cell=" + cell + ", reason=" + (reason ?? "none"));
        }

        private static void NotifyHospitalPatientsLeft(Map map, IEnumerable<Pawn> pawns)
        {
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            MethodInfo patientLeftTheMap = hospital == null ? null : AccessTools.Method(hospital.GetType(), "PatientLeftTheMap", new[] { typeof(Pawn) });
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            ServiceDebugUtility.LogAudit("NotifyHospitalPatientsLeft hospital=" + (hospital == null ? "null" : hospital.GetType().FullName) + " ownerMethod=" + (patientLeftTheMap != null) + " patientsDict=" + (patients != null));
            if (patients == null && patientLeftTheMap == null)
            {
                return;
            }
            foreach (Pawn pawn in (pawns ?? Enumerable.Empty<Pawn>()).Where(pawn => !ServicePawnUtility.IsTerminalPawn(pawn)).ToList())
            {
                if (patientLeftTheMap != null)
                {
                    try
                    {
                        ServiceDebugUtility.LogAudit("Hospital PatientLeftTheMap before pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " contains=" + (patients != null && patients.Contains(pawn)));
                        patientLeftTheMap.Invoke(hospital, new object[] { pawn });
                        ServiceDebugUtility.LogAudit("Hospital PatientLeftTheMap after pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn) + " contains=" + (patients != null && patients.Contains(pawn)));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        ServiceDebugUtility.LogVerbose("Hospital PatientLeftTheMap failed during service departure: " + ex.Message);
                    }
                }
                if (patients != null && patients.Contains(pawn))
                {
                    ServiceDebugUtility.LogAudit("Hospital fallback Patients.Remove pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
                    patients.Remove(pawn);
                }
            }
        }

        private static void PreparePawnJobsForExit(Pawn pawn)
        {
            if (pawn == null || pawn.jobs == null)
            {
                return;
            }
            try
            {
                ServiceDebugUtility.LogAudit("PreparePawnJobsForExit before " + ServiceDebugUtility.PawnAuditSummary(pawn) + " queue=" + JobQueueAudit(pawn));
                bool clearedCurrentBefore = ServicePawnUtility.ClearJobLord(pawn.CurJob);
                int clearedQueued = ClearQueuedJobLords(pawn);
                pawn.jobs.StopAll(false);
                bool clearedCurrentAfter = ServicePawnUtility.ClearJobLord(pawn.CurJob);
                ServiceDebugUtility.LogAudit("PreparePawnJobsForExit after " + ServiceDebugUtility.PawnAuditSummary(pawn) + " clearedCurrentBefore=" + clearedCurrentBefore + " clearedQueued=" + clearedQueued + " clearedCurrentAfter=" + clearedCurrentAfter + " queue=" + JobQueueAudit(pawn));
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogVerbose("Could not clear service pawn jobs before exit: " + ex.Message);
            }
        }

        private static int ClearQueuedJobLords(Pawn pawn)
        {
            object queue = pawn == null || pawn.jobs == null ? null : Reflect.GetMember(pawn.jobs, "jobQueue");
            int cleared = 0;
            IEnumerable enumerable = queue as IEnumerable;
            if (enumerable != null)
            {
                foreach (object queued in enumerable)
                {
                    if (ServicePawnUtility.ClearJobLord(Reflect.GetMember(queued, "job") as Job))
                    {
                        cleared++;
                    }
                }
            }
            MethodInfo clear = queue == null ? null : AccessTools.Method(queue.GetType(), "Clear");
            if (clear != null)
            {
                clear.Invoke(queue, null);
            }
            return cleared;
        }

        private static void CleanupDepartingPawnReferences(Map map, Pawn departingPawn)
        {
            if (departingPawn == null)
            {
                return;
            }
            int runtimeLords = ServicePawnUtility.ClearRuntimeLordReferences(departingPawn);
            int memories = CleanupSocialMemoriesReferencing(map, departingPawn);
            int relations = CleanupDirectRelationsReferencing(departingPawn);
            int relationshipRecords = ServicePawnUtility.CleanupRelationshipRecordsReferencing(departingPawn);
            int lords = CleanupLordReferences(map, departingPawn);
            ServiceDebugUtility.LogAudit("CleanupDepartingPawnReferences pawn=" + ServiceDebugUtility.PawnAuditSummary(departingPawn) + " memories=" + memories + " relations=" + relations + " relationshipRecords=" + relationshipRecords + " lordRefs=" + lords + " runtimeLordRefs=" + runtimeLords);
        }

        private static int CleanupSocialMemoriesReferencing(Map map, Pawn departingPawn)
        {
            int removed = 0;
            foreach (Pawn pawn in PawnsToClean(map))
            {
                if (pawn == null || pawn.needs == null || pawn.needs.mood == null || pawn.needs.mood.thoughts == null || pawn.needs.mood.thoughts.memories == null)
                {
                    continue;
                }
                List<Thought_Memory> memories = pawn.needs.mood.thoughts.memories.Memories;
                if (memories == null)
                {
                    continue;
                }
                removed += memories.RemoveAll(memory =>
                {
                    Thought_MemorySocial social = memory as Thought_MemorySocial;
                    return social != null && Reflect.GetMember(social, "otherPawn") == departingPawn;
                });
            }
            return removed;
        }

        private static int CleanupDirectRelationsReferencing(Pawn departingPawn)
        {
            int removed = 0;
            foreach (Pawn pawn in PawnsToClean(departingPawn.MapHeld))
            {
                if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
                {
                    continue;
                }
                removed += pawn.relations.DirectRelations.RemoveAll(relation => relation == null || relation.otherPawn == departingPawn || relation.otherPawn == null || relation.otherPawn.Destroyed);
            }
            return removed;
        }

        private static int CleanupLordReferences(Map map, Pawn departingPawn)
        {
            if (map == null || map.lordManager == null || map.lordManager.lords == null)
            {
                return 0;
            }
            int removed = 0;
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord != null && lord.ownedPawns != null)
                {
                    removed += lord.ownedPawns.RemoveAll(pawn => pawn == null || pawn == departingPawn || pawn.Destroyed);
                }
            }
            return removed;
        }

        private static IEnumerable<Pawn> PawnsToClean(Map map)
        {
            HashSet<Pawn> pawns = new HashSet<Pawn>();
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
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                if (pawn != null)
                {
                    pawns.Add(pawn);
                }
            }
            return pawns;
        }

        private static void ReleaseReservation(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (pad != null)
            {
                pad.Release(record.id);
            }
            record.reservedPad = null;
        }

        private static void NotifyLordPawnExited(Pawn pawn)
        {
            try
            {
                Lord lord = pawn.GetLord();
                if (lord == null)
                {
                    ServiceDebugUtility.LogAudit("NotifyLordPawnExited no lord for " + ServiceDebugUtility.PawnAuditSummary(pawn));
                    return;
                }
                MethodInfo method = AccessTools.Method(typeof(Lord), "Notify_PawnLost", new[] { typeof(Pawn), typeof(PawnLostCondition) });
                if (method != null)
                {
                    ServiceDebugUtility.LogAudit("NotifyLordPawnExited invoking lord=" + ServiceDebugUtility.LordAuditSummary(lord) + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn));
                    method.Invoke(lord, new object[] { pawn, PawnLostCondition.ExitedMap });
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not notify lord before service departure: " + ex.Message);
            }
        }

        private static string PawnListAudit(IEnumerable<Pawn> pawns)
        {
            List<string> labels = new List<string>();
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                labels.Add(ServiceDebugUtility.PawnAuditSummary(pawn));
            }
            return labels.Count == 0 ? "none" : string.Join(" | ", labels.ToArray());
        }

        private static string JobQueueAudit(Pawn pawn)
        {
            object queue = pawn == null || pawn.jobs == null ? null : Reflect.GetMember(pawn.jobs, "jobQueue");
            IEnumerable enumerable = queue as IEnumerable;
            if (enumerable == null)
            {
                return "null";
            }
            List<string> jobs = new List<string>();
            foreach (object queued in enumerable)
            {
                jobs.Add(ServiceDebugUtility.JobAuditSummary(Reflect.GetMember(queued, "job") as Job));
            }
            return jobs.Count == 0 ? "empty" : string.Join(" -> ", jobs.ToArray());
        }
    }
}
