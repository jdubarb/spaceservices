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
    public static class StaleReferenceCleanupUtility
    {
        public static void CleanupAfterLoad(Map map)
        {
            if (map == null)
            {
                return;
            }

            int removedHospitalPatients = CleanupHospitalPatients(map);
            int removedLordPawns = CleanupLordPawnLists(map);
            int removedServicePawns = CleanupServiceGroups(map);
            int removedLegacyShuttles = CleanupLegacyPassengerShuttleSkyfallers(map);
            int removedSocialMemories = CleanupBrokenSocialMemories(map);
            int removedDirectRelations = CleanupBrokenDirectRelations(map);

            if (removedHospitalPatients > 0 || removedLordPawns > 0 || removedServicePawns > 0 || removedLegacyShuttles > 0 || removedSocialMemories > 0 || removedDirectRelations > 0)
            {
                Log.Message("[Space Services] cleaned stale service references: hospitalPatients=" + removedHospitalPatients + ", lordPawns=" + removedLordPawns + ", servicePawns=" + removedServicePawns + ", legacyPassengerShuttles=" + removedLegacyShuttles + ", socialMemories=" + removedSocialMemories + ", directRelations=" + removedDirectRelations);
            }
        }

        private static int CleanupLegacyPassengerShuttleSkyfallers(Map map)
        {
            if (!SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return 0;
            }

            HashSet<IntVec3> serviceCells = new HashSet<IntVec3>();
            foreach (Thing pad in ServicePadUtility.AllServicePads(map, ServiceUse.Patient).Concat(ServicePadUtility.AllServicePads(map, ServiceUse.Guest)))
            {
                if (pad != null && pad.Spawned)
                {
                    serviceCells.Add(pad.Position);
                }
            }

            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp != null && comp.serviceGroups != null)
            {
                foreach (ServiceGroupRecord record in comp.serviceGroups)
                {
                    if (record != null && record.reservedPad != null && record.reservedPad.Spawned)
                    {
                        serviceCells.Add(record.reservedPad.Position);
                    }
                }
            }

            if (serviceCells.Count == 0)
            {
                return 0;
            }

            int removed = 0;
            removed += CleanupLegacyPassengerShuttleSkyfallerDef(map, "PassengerShuttleIncoming", serviceCells);
            removed += CleanupLegacyPassengerShuttleSkyfallerDef(map, "PassengerShuttleLeaving", serviceCells);
            return removed;
        }

        private static int CleanupLegacyPassengerShuttleSkyfallerDef(Map map, string defName, HashSet<IntVec3> serviceCells)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return 0;
            }

            List<Thing> things = map.listerThings.ThingsOfDef(def)
                .Where(thing => thing != null && !thing.Destroyed && serviceCells.Any(cell => thing.Position.InHorDistOf(cell, 10f)))
                .ToList();
            foreach (Thing thing in things)
            {
                thing.Destroy(DestroyMode.Vanish);
            }
            return things.Count;
        }

        private static int CleanupHospitalPatients(Map map)
        {
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (patients == null)
            {
                return 0;
            }

            List<object> removeKeys = new List<object>();
            foreach (object key in patients.Keys)
            {
                Pawn pawn = key as Pawn;
                object patientData = key == null ? null : patients[key];
                if (pawn == null || pawn.Destroyed || IsSyntheticFallbackPatientData(patientData))
                {
                    removeKeys.Add(key);
                }
            }

            foreach (object key in removeKeys)
            {
                patients.Remove(key);
            }
            return removeKeys.Count;
        }

        private static bool IsSyntheticFallbackPatientData(object patientData)
        {
            if (patientData == null)
            {
                return false;
            }
            return string.Equals(Reflect.GetMember(patientData, "Diagnosis") as string, "wounds", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Reflect.GetMember(patientData, "Cure") as string, "tend to wounds", StringComparison.OrdinalIgnoreCase);
        }

        private static int CleanupLordPawnLists(Map map)
        {
            if (map.lordManager == null || map.lordManager.lords == null)
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
                removed += lord.ownedPawns.RemoveAll(pawn => pawn == null);
            }
            return removed;
        }

        private static int CleanupBrokenSocialMemories(Map map)
        {
            if (map == null)
            {
                return 0;
            }

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
                    if (social == null)
                    {
                        return false;
                    }
                    Pawn otherPawn = Reflect.GetMember(social, "otherPawn") as Pawn;
                    return otherPawn == null || otherPawn.Destroyed;
                });
            }
            return removed;
        }

        private static int CleanupBrokenDirectRelations(Map map)
        {
            int removed = 0;
            foreach (Pawn pawn in PawnsToClean(map))
            {
                if (pawn == null || pawn.relations == null || pawn.relations.DirectRelations == null)
                {
                    continue;
                }
                removed += pawn.relations.DirectRelations.RemoveAll(relation => relation == null || relation.otherPawn == null || relation.otherPawn.Destroyed);
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
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            {
                if (pawn != null)
                {
                    pawns.Add(pawn);
                }
            }
            return pawns;
        }

        private static int CleanupServiceGroups(Map map)
        {
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return 0;
            }

            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary hospitalPatients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            int removed = 0;
            foreach (ServiceGroupRecord record in comp.serviceGroups)
            {
                if (record == null || record.pawns == null)
                {
                    continue;
                }
                if (record.serviceKind == "hospital" && record.state != "departing" && record.reservedPad != null)
                {
                    ReleaseServiceRecord(record);
                    record.reservedPad = null;
                }
                int before = record.pawns.Count;
                record.pawns = record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).Distinct().ToList();
                removed += before - record.pawns.Count;
                if (record.serviceKind == "hospital" && hospitalPatients != null)
                {
                    before = record.pawns.Count;
                    record.pawns = record.pawns.Where(pawn => hospitalPatients.Contains(pawn)).ToList();
                    removed += before - record.pawns.Count;
                    if (before > 0 && record.pawns.Count == 0)
                    {
                        ReleaseServiceRecord(record);
                        record.state = "completed";
                    }
                }
            }
            return removed;
        }

        private static void ReleaseServiceRecord(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record == null || record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (pad != null)
            {
                pad.Release(record.id);
            }
        }
    }

}
