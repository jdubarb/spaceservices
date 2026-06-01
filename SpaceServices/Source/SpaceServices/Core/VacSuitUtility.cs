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
    public static class VacSuitUtility
    {
        private static readonly string[] AdultSuitDefs = { "Apparel_Vacsuit", "Apparel_VacsuitHelmet" };
        private static readonly string[] ChildSuitDefs = { "Apparel_VacsuitChildren", "Apparel_VacsuitHelmet" };
        private static readonly HashSet<string> KnownVacSuitDefs = new HashSet<string>(AdultSuitDefs.Concat(ChildSuitDefs));
        private static readonly Dictionary<int, bool> sealedArrivalSuitRolls = new Dictionary<int, bool>();
        private static StatDef vacuumResistance;
        private static HashSet<string> cachedRemovalDefNames;

        public static void SuitPawnsForVacuum(IEnumerable<Pawn> pawns)
        {
            if (pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                SuitPawnForVacuum(pawn);
            }
        }

        public static void SuitPawnsForEnvironment(IEnumerable<Pawn> pawns, Map map, IntVec3 cell)
        {
            if (pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                SuitPawnForEnvironment(pawn, map, cell);
            }
        }

        public static void SuitPawnForEnvironment(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn == null || map == null || !cell.IsValid)
            {
                return;
            }
            float vacuum = ServiceEnvironmentUtility.GetVacuum(cell, map);
            if (VacuumResistance(pawn) + 0.001f < vacuum)
            {
                if (ShouldProvideSuitForArrival(pawn, map, cell))
                {
                    SuitPawnForVacuum(pawn);
                }
                else
                {
                    RemoveKnownVacSuit(pawn);
                }
            }
            else if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.allowSealedNoSuitArrivals && ServiceEnvironmentUtility.IsSealedNoSuitArrivalCell(cell, map) && !ShouldProvideSuitForArrival(pawn, map, cell))
            {
                RemoveKnownVacSuit(pawn);
            }
        }

        private static bool ShouldProvideSuitForArrival(Pawn pawn, Map map, IntVec3 cell)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.allowSealedNoSuitArrivals)
            {
                return true;
            }
            if (!ServiceEnvironmentUtility.IsSealedNoSuitArrivalCell(cell, map))
            {
                return true;
            }
            // Sealed arrival rooms should mostly receive ordinary patients, but keep some suited traffic mixed in.
            int key = pawn == null ? 0 : pawn.thingIDNumber;
            if (key == 0)
            {
                return Rand.Chance(0.2f);
            }
            if (!sealedArrivalSuitRolls.TryGetValue(key, out bool shouldSuit))
            {
                shouldSuit = Rand.Chance(0.2f);
                sealedArrivalSuitRolls[key] = shouldSuit;
            }
            return shouldSuit;
        }

        public static float VacuumResistance(Pawn pawn)
        {
            if (pawn == null)
            {
                return 0f;
            }
            StatDef stat = VacuumResistanceDef;
            if (stat == null)
            {
                return 0f;
            }
            try
            {
                return pawn.GetStatValue(stat);
            }
            catch
            {
                return 0f;
            }
        }

        public static void SuitPawnForVacuum(Pawn pawn)
        {
            if (pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            List<ThingDef> defs = SelectApparelSetFor(pawn);
            for (int i = 0; i < defs.Count; i++)
            {
                TryWearIfNeeded(pawn, defs[i]);
            }
        }

        private static List<ThingDef> SelectApparelSetFor(Pawn pawn)
        {
            // Modded vac gear is data-driven so patches can add apparel sets without C# changes.
            List<SpaceServiceVacuumApparelSetDef> sets = DefDatabase<SpaceServiceVacuumApparelSetDef>.AllDefsListForReading
                .Where(set => set != null && set.AppliesTo(pawn))
                .ToList();
            if (sets.Count == 0)
            {
                string[] fallback = pawn.DevelopmentalStage == DevelopmentalStage.Child ? ChildSuitDefs : AdultSuitDefs;
                return SpaceServiceDefFilters.ResolveThingDefs(fallback.ToList());
            }
            SpaceServiceVacuumApparelSetDef selected = WeightedRandomSet(sets);
            return selected == null ? new List<ThingDef>() : selected.ResolvedApparelFor(pawn);
        }

        private static SpaceServiceVacuumApparelSetDef WeightedRandomSet(List<SpaceServiceVacuumApparelSetDef> sets)
        {
            float total = sets.Sum(set => Mathf.Max(0f, set.weight));
            if (total <= 0f)
            {
                return sets.FirstOrDefault();
            }
            float roll = Rand.Value * total;
            foreach (SpaceServiceVacuumApparelSetDef set in sets)
            {
                roll -= Mathf.Max(0f, set.weight);
                if (roll <= 0f)
                {
                    return set;
                }
            }
            return sets.LastOrDefault();
        }

        private static void TryWearIfNeeded(Pawn pawn, ThingDef def)
        {
            if (def == null)
            {
                return;
            }
            if (pawn.apparel.WornApparel.Any(worn => worn.def == def))
            {
                return;
            }
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            Apparel newApparel = ThingMaker.MakeThing(def, stuff) as Apparel;
            if (newApparel == null)
            {
                return;
            }
            pawn.apparel.Wear(newApparel, false, true);
        }

        private static void RemoveKnownVacSuit(Pawn pawn)
        {
            if (pawn == null || pawn.apparel == null)
            {
                return;
            }
            foreach (Apparel apparel in pawn.apparel.WornApparel.ToList())
            {
                if (apparel != null && RemovalDefNames.Contains(apparel.def.defName))
                {
                    pawn.apparel.Remove(apparel);
                    apparel.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static HashSet<string> RemovalDefNames
        {
            get
            {
                if (cachedRemovalDefNames == null)
                {
                    cachedRemovalDefNames = new HashSet<string>(KnownVacSuitDefs);
                    foreach (SpaceServiceVacuumApparelSetDef set in DefDatabase<SpaceServiceVacuumApparelSetDef>.AllDefsListForReading)
                    {
                        if (set == null)
                        {
                            continue;
                        }
                        foreach (string defName in set.AllRemovalDefNames())
                        {
                            if (!string.IsNullOrEmpty(defName))
                            {
                                cachedRemovalDefNames.Add(defName);
                            }
                        }
                    }
                }
                return cachedRemovalDefNames;
            }
        }

        private static StatDef VacuumResistanceDef
        {
            get
            {
                if (vacuumResistance == null)
                {
                    vacuumResistance = DefDatabase<StatDef>.GetNamedSilentFail("VacuumResistance");
                }
                return vacuumResistance;
            }
        }
    }
}
