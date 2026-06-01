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
        private static readonly Dictionary<int, bool> sealedArrivalSuitRolls = new Dictionary<int, bool>();
        private const string InjectedVacGearTag = "JDB_SpaceServices_AutoVacGear";
        private static StatDef vacuumResistance;
        private static List<VacuumApparelCandidate> cachedAutoCandidates;

        public static void SuitPawnsForVacuum(IEnumerable<Pawn> pawns)
        {
            if (pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                SuitPawnForVacuum(pawn, 1f);
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
                    SuitPawnForVacuum(pawn, vacuum);
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
            SuitPawnForVacuum(pawn, 1f);
        }

        private static void SuitPawnForVacuum(Pawn pawn, float targetVacuum)
        {
            if (pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            List<VacuumApparelCandidate> autoCandidates = SelectAutomaticApparel(pawn, targetVacuum);
            if (autoCandidates.Count > 0)
            {
                for (int i = 0; i < autoCandidates.Count; i++)
                {
                    TryWearIfNeeded(pawn, autoCandidates[i].def, true);
                }
                return;
            }

            List<ThingDef> defs = SelectXmlApparelSetFor(pawn);
            for (int i = 0; i < defs.Count; i++)
            {
                TryWearIfNeeded(pawn, defs[i], true);
            }
        }

        private static List<ThingDef> SelectXmlApparelSetFor(Pawn pawn)
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

        private static List<VacuumApparelCandidate> SelectAutomaticApparel(Pawn pawn, float targetVacuum)
        {
            float needed = Mathf.Clamp01(targetVacuum) - VacuumResistance(pawn);
            if (needed <= 0.001f)
            {
                return new List<VacuumApparelCandidate>();
            }

            List<VacuumApparelCandidate> pool = AutoCandidates
                .Where(candidate => candidate.CanWear(pawn))
                .ToList();
            if (pool.Count == 0)
            {
                return new List<VacuumApparelCandidate>();
            }

            List<VacuumApparelCandidate> selected = new List<VacuumApparelCandidate>();
            float provided = 0f;
            for (int i = 0; i < 8 && provided + 0.001f < needed; i++)
            {
                List<VacuumApparelCandidate> compatible = pool
                    .Where(candidate => selected.All(existing => !candidate.ConflictsWith(existing)))
                    .ToList();
                if (compatible.Count == 0)
                {
                    break;
                }
                VacuumApparelCandidate next = WeightedRandomCandidate(compatible);
                if (next == null)
                {
                    break;
                }
                selected.Add(next);
                pool.Remove(next);
                provided += next.resistance;
            }

            return provided + 0.001f >= needed
                ? selected
                : new List<VacuumApparelCandidate>();
        }

        private static VacuumApparelCandidate WeightedRandomCandidate(List<VacuumApparelCandidate> candidates)
        {
            float total = candidates.Sum(candidate => candidate.selectionWeight);
            if (total <= 0f)
            {
                return candidates.FirstOrDefault();
            }
            float roll = Rand.Value * total;
            foreach (VacuumApparelCandidate candidate in candidates)
            {
                roll -= candidate.selectionWeight;
                if (roll <= 0f)
                {
                    return candidate;
                }
            }
            return candidates.LastOrDefault();
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

        private static void TryWearIfNeeded(Pawn pawn, ThingDef def, bool markInjected)
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
            if (markInjected)
            {
                MarkInjectedVacGear(newApparel);
            }
            RemoveApparelConflictingWith(pawn, newApparel);
            pawn.apparel.Wear(newApparel, false, true);
        }

        private static void RemoveApparelConflictingWith(Pawn pawn, Apparel newApparel)
        {
            if (pawn == null || pawn.apparel == null || pawn.RaceProps == null || newApparel == null)
            {
                return;
            }
            foreach (Apparel worn in pawn.apparel.WornApparel.ToList())
            {
                if (worn == null || ApparelUtility.CanWearTogether(newApparel.def, worn.def, pawn.RaceProps.body))
                {
                    continue;
                }
                // Limit outfit disruption to the exact layer/body conflict caused by the safety gear.
                pawn.apparel.Remove(worn);
                worn.Destroy(DestroyMode.Vanish);
            }
        }

        private static void RemoveKnownVacSuit(Pawn pawn)
        {
            if (pawn == null || pawn.apparel == null)
            {
                return;
            }
            foreach (Apparel apparel in pawn.apparel.WornApparel.ToList())
            {
                if (apparel != null && ShouldRemoveOnSealedArrival(apparel))
                {
                    pawn.apparel.Remove(apparel);
                    apparel.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static void MarkInjectedVacGear(Apparel apparel)
        {
            if (apparel == null)
            {
                return;
            }
            if (apparel.questTags == null)
            {
                apparel.questTags = new List<string>();
            }
            if (!apparel.questTags.Contains(InjectedVacGearTag))
            {
                apparel.questTags.Add(InjectedVacGearTag);
            }
        }

        private static bool ShouldRemoveOnSealedArrival(Apparel apparel)
        {
            if (apparel == null || apparel.def == null)
            {
                return false;
            }
            if (apparel.questTags != null && apparel.questTags.Contains(InjectedVacGearTag))
            {
                return true;
            }
            // Do not strip naturally-generated apparel just because it has vacuum stats.
            // That can delete a pawn's only shell/middle layer and create outfit conflicts.
            return false;
        }

        private static List<VacuumApparelCandidate> AutoCandidates
        {
            get
            {
                if (cachedAutoCandidates == null)
                {
                    cachedAutoCandidates = BuildAutoCandidates();
                    ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Cached " + cachedAutoCandidates.Count + " automatic vacuum apparel candidates using inverse market-value weighting.");
                }
                return cachedAutoCandidates;
            }
        }

        private static List<VacuumApparelCandidate> BuildAutoCandidates()
        {
            StatDef stat = VacuumResistanceDef;
            if (stat == null)
            {
                return new List<VacuumApparelCandidate>();
            }

            return DefDatabase<ThingDef>.AllDefsListForReading
                .Select(def => VacuumApparelCandidate.TryCreate(def, stat))
                .Where(candidate => candidate != null)
                .OrderBy(candidate => candidate.def.defName)
                .ToList();
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

        private sealed class VacuumApparelCandidate
        {
            public ThingDef def;
            public float resistance;
            public float selectionWeight;
            private DevelopmentalStage developmentalStageFilter;
            private HashSet<string> layers = new HashSet<string>();
            private HashSet<string> bodyPartGroups = new HashSet<string>();

            public static VacuumApparelCandidate TryCreate(ThingDef def, StatDef vacuumStat)
            {
                if (def == null ||
                    def.apparel == null ||
                    def.equippedStatOffsets.NullOrEmpty() ||
                    !def.apparel.canBeGeneratedToSatisfyVacuumResistance ||
                    IsBlacklisted(def))
                {
                    return null;
                }

                float resistance = 0f;
                foreach (StatModifier modifier in def.equippedStatOffsets)
                {
                    if (modifier != null && modifier.stat == vacuumStat)
                    {
                        resistance += modifier.value;
                    }
                }
                if (resistance <= 0.001f)
                {
                    return null;
                }

                float marketValue = Mathf.Max(1f, def.GetStatValueAbstract(StatDefOf.MarketValue));
                VacuumApparelCandidate candidate = new VacuumApparelCandidate
                {
                    def = def,
                    resistance = Mathf.Clamp01(resistance),
                    developmentalStageFilter = def.apparel.developmentalStageFilter,
                    // Cheap, practical safety gear should be much more common than expensive powered armor.
                    selectionWeight = Mathf.Clamp(resistance / marketValue, 0.0001f, 1f)
                };
                if (!def.apparel.layers.NullOrEmpty())
                {
                    candidate.layers = new HashSet<string>(def.apparel.layers.Select(layer => layer.defName));
                }
                if (!def.apparel.bodyPartGroups.NullOrEmpty())
                {
                    candidate.bodyPartGroups = new HashSet<string>(def.apparel.bodyPartGroups.Select(group => group.defName));
                }
                return candidate;
            }

            public bool CanWear(Pawn pawn)
            {
                if (pawn == null)
                {
                    return false;
                }
                if (developmentalStageFilter != DevelopmentalStage.None &&
                    (developmentalStageFilter & pawn.DevelopmentalStage) == 0)
                {
                    return false;
                }
                return true;
            }

            public bool ConflictsWith(VacuumApparelCandidate other)
            {
                if (other == null)
                {
                    return false;
                }
                return layers.Overlaps(other.layers) && bodyPartGroups.Overlaps(other.bodyPartGroups);
            }

            private static bool IsBlacklisted(ThingDef def)
            {
                string defName = (def.defName ?? "").ToLowerInvariant();
                string label = (def.label ?? "").ToLowerInvariant();
                string packageId = def.modContentPack == null ? "" : (def.modContentPack.PackageId ?? "").ToLowerInvariant();
                return defName.Contains("warcasket") ||
                    label.Contains("warcasket") ||
                    packageId.Contains("vfe.pirates");
            }
        }
    }
}
