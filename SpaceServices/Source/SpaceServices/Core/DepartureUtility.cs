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

            record.state = "departing";
            record.departureRequestedTick = Find.TickManager.TicksGame;
            Log.Message("[Space Services] Departing " + record.serviceKind + " service group " + record.id + ": " + reason);
            Map padMap = record.reservedPad == null ? null : record.reservedPad.Map;
            IntVec3 padCell = record.reservedPad == null ? IntVec3.Invalid : record.reservedPad.Position;
            ServiceShuttleUtility.CleanupTouchdownShuttle(padMap, padCell, record.pickupShuttleThingDefName);

            if (record.serviceKind == "hospital")
            {
                NotifyHospitalPatientsLeft(map, record.pawns);
            }
            bool completed = TryAutoExtract(map, record.pawns, reason);
            if (completed)
            {
                if (record.reservedPad != null && record.reservedPad.Spawned)
                {
                    ServiceShuttleUtility.SpawnDeparture(record.reservedPad.Map, record.reservedPad.Position);
                }
                record.state = "completed";
                ReleaseReservation(record);
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
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                any = true;
                if (pawn.Spawned)
                {
                    if (!TryExitSpawnedPawn(pawn, reason))
                    {
                        LogServicePawnRemoval(pawn, "despawn fallback", reason);
                        CleanupDepartingPawnReferences(map ?? pawn.MapHeld, pawn);
                        NotifyLordPawnExited(pawn);
                        pawn.DeSpawn(DestroyMode.Vanish);
                    }
                }
                else if (pawn.MapHeld != null)
                {
                    LogServicePawnRemoval(pawn, "destroy unspawned", reason);
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
                LogServicePawnRemoval(pawn, "ExitMap", reason);
                pawn.ExitMap(false, Rot4.Invalid);
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
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (patients == null)
            {
                return;
            }
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn != null && patients.Contains(pawn))
                {
                    patients.Remove(pawn);
                }
            }
        }

        private static void CleanupDepartingPawnReferences(Map map, Pawn departingPawn)
        {
            if (departingPawn == null)
            {
                return;
            }
            CleanupSocialMemoriesReferencing(map, departingPawn);
            CleanupDirectRelationsReferencing(departingPawn);
            CleanupLordReferences(map, departingPawn);
        }

        private static void CleanupSocialMemoriesReferencing(Map map, Pawn departingPawn)
        {
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
                memories.RemoveAll(memory =>
                {
                    Thought_MemorySocial social = memory as Thought_MemorySocial;
                    return social != null && Reflect.GetMember(social, "otherPawn") == departingPawn;
                });
            }
        }

        private static void CleanupDirectRelationsReferencing(Pawn departingPawn)
        {
            foreach (Pawn pawn in PawnsToClean(departingPawn.MapHeld))
            {
                if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
                {
                    continue;
                }
                pawn.relations.DirectRelations.RemoveAll(relation => relation == null || relation.otherPawn == departingPawn || relation.otherPawn == null || relation.otherPawn.Destroyed);
            }
        }

        private static void CleanupLordReferences(Map map, Pawn departingPawn)
        {
            if (map == null || map.lordManager == null || map.lordManager.lords == null)
            {
                return;
            }
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord != null && lord.ownedPawns != null)
                {
                    lord.ownedPawns.RemoveAll(pawn => pawn == null || pawn == departingPawn || pawn.Destroyed);
                }
            }
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
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
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
                    return;
                }
                MethodInfo method = AccessTools.Method(typeof(Lord), "Notify_PawnLost", new[] { typeof(Pawn), typeof(PawnLostCondition) });
                if (method != null)
                {
                    method.Invoke(lord, new object[] { pawn, PawnLostCondition.ExitedMap });
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not notify lord before service departure: " + ex.Message);
            }
        }
    }
}
