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
        private const float AutoSetSelectionChance = 0.35f;
        // Odyssey still applies vacuum harm below full resistance, so service transit treats 100% as the practical safety floor.
        public const float PracticalVacuumSuitTarget = 1f;
        private static StatDef vacuumResistance;
        private static List<VacuumApparelCandidate> cachedAutoCandidates;
        private static int internalVacGearRemovalDepth;

        public static bool InternalVacGearRemovalAllowed
        {
            get
            {
                return internalVacGearRemovalDepth > 0;
            }
        }

        public static bool IsInjectedVacGear(Apparel apparel)
        {
            return HasInjectedVacGearTag(apparel);
        }

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
            float targetVacuum = PracticalVacuumTargetFor(map, vacuum);
            if (VacuumResistance(pawn) + 0.001f < targetVacuum)
            {
                if (ShouldProvideSuitForArrival(pawn, map, cell))
                {
                    SuitPawnForVacuum(pawn, targetVacuum);
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

        private static float PracticalVacuumTargetFor(Map map, float measuredVacuum)
        {
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && measuredVacuum > 0.001f)
            {
                return PracticalVacuumSuitTarget;
            }
            return Mathf.Min(measuredVacuum, PracticalVacuumSuitTarget);
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

        public static void EnsurePracticalVacuumProtection(Pawn pawn)
        {
            SuitPawnForVacuum(pawn, PracticalVacuumSuitTarget);
        }

        private static void SuitPawnForVacuum(Pawn pawn, float targetVacuum)
        {
            if (pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            // Hospitality can move our safety helmet into inventory after arrival; re-wear that exact item before creating replacements.
            if (TryRewearInjectedVacuumGear(pawn, targetVacuum))
            {
                return;
            }

            List<VacuumApparelCandidate> autoCandidates = SelectAutomaticApparel(pawn, targetVacuum);
            if (autoCandidates.Count > 0)
            {
                // Prefer active modded apparel that already advertises vacuum resistance, then fall back
                // to explicit XML sets and Odyssey vacsuits if the generated outfit still is not enough.
                for (int i = 0; i < autoCandidates.Count; i++)
                {
                    TryWearIfNeeded(pawn, autoCandidates[i].def, true);
                }
                if (VacuumResistance(pawn) + 0.001f >= targetVacuum)
                {
                    return;
                }
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Automatic vacuum apparel did not reach target for " + pawn.LabelShortCap + "; falling back to explicit vacsuit set.");
            }

            List<ThingDef> defs = SelectXmlApparelSetFor(pawn);
            for (int i = 0; i < defs.Count; i++)
            {
                TryWearIfNeeded(pawn, defs[i], true);
            }
        }

        private static bool TryRewearInjectedVacuumGear(Pawn pawn, float targetVacuum)
        {
            if (pawn == null || pawn.apparel == null || pawn.inventory == null || pawn.inventory.innerContainer == null)
            {
                return false;
            }
            List<Apparel> injectedVacGear = pawn.inventory.innerContainer
                .OfType<Apparel>()
                .Where(apparel => apparel != null && HasInjectedVacGearTag(apparel) && VacuumResistanceFromDef(apparel.def) > 0.001f)
                .OrderByDescending(apparel => VacuumResistanceFromDef(apparel.def))
                .ToList();
            if (injectedVacGear.Count == 0)
            {
                return false;
            }
            foreach (Apparel apparel in injectedVacGear)
            {
                if (apparel == null || apparel.Destroyed || apparel.def == null || pawn.apparel.WornApparel.Any(worn => worn != null && worn == apparel))
                {
                    continue;
                }
                if (!pawn.inventory.innerContainer.Remove(apparel))
                {
                    continue;
                }
                RemoveApparelConflictingWith(pawn, apparel);
                pawn.apparel.Wear(apparel, false, true);
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Re-wore injected vacuum apparel from inventory for " + pawn.LabelShortCap + ": " + apparel.def.defName);
                if (VacuumResistance(pawn) + 0.001f >= targetVacuum)
                {
                    return true;
                }
            }
            return VacuumResistance(pawn) + 0.001f >= targetVacuum;
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

            if (Rand.Chance(AutoSetSelectionChance))
            {
                List<VacuumApparelCandidate> taggedSet = SelectTaggedAutomaticSet(pool, needed);
                if (taggedSet.Count > 0)
                {
                    return taggedSet;
                }
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

        private static List<VacuumApparelCandidate> SelectTaggedAutomaticSet(List<VacuumApparelCandidate> pool, float needed)
        {
            List<VacuumApparelSetCandidate> sets = new List<VacuumApparelSetCandidate>();
            foreach (IGrouping<string, VacuumApparelCandidate> group in pool
                .SelectMany(candidate => candidate.setTags.Select(tag => new { tag, candidate }))
                .GroupBy(item => item.tag, item => item.candidate))
            {
                List<VacuumApparelCandidate> set = BuildCompatibleSet(group.Distinct().ToList(), needed);
                if (set.Count > 1)
                {
                    sets.Add(new VacuumApparelSetCandidate(group.Key, set));
                }
            }
            if (sets.Count == 0)
            {
                return new List<VacuumApparelCandidate>();
            }

            VacuumApparelSetCandidate selected = WeightedRandomSetCandidate(sets);
            return selected == null ? new List<VacuumApparelCandidate>() : selected.candidates;
        }

        private static List<VacuumApparelCandidate> BuildCompatibleSet(List<VacuumApparelCandidate> candidates, float needed)
        {
            List<VacuumApparelCandidate> selected = new List<VacuumApparelCandidate>();
            float provided = 0f;
            foreach (VacuumApparelCandidate candidate in candidates
                .OrderByDescending(candidate => candidate.resistance)
                .ThenBy(candidate => candidate.marketValue))
            {
                if (selected.Any(existing => candidate.ConflictsWith(existing)))
                {
                    continue;
                }
                selected.Add(candidate);
                provided += candidate.resistance;
                if (provided + 0.001f >= needed)
                {
                    return selected;
                }
            }
            return new List<VacuumApparelCandidate>();
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

        private static VacuumApparelSetCandidate WeightedRandomSetCandidate(List<VacuumApparelSetCandidate> sets)
        {
            float total = sets.Sum(set => set.selectionWeight);
            if (total <= 0f)
            {
                return sets.FirstOrDefault();
            }
            float roll = Rand.Value * total;
            foreach (VacuumApparelSetCandidate set in sets)
            {
                roll -= set.selectionWeight;
                if (roll <= 0f)
                {
                    return set;
                }
            }
            return sets.LastOrDefault();
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
            Apparel inventoryApparel = TakeInventoryApparel(pawn, def);
            if (inventoryApparel != null)
            {
                if (markInjected)
                {
                    MarkInjectedVacGear(inventoryApparel);
                }
                RemoveApparelConflictingWith(pawn, inventoryApparel);
                pawn.apparel.Wear(inventoryApparel, false, true);
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

        private static Apparel TakeInventoryApparel(Pawn pawn, ThingDef def)
        {
            if (pawn == null || pawn.inventory == null || pawn.inventory.innerContainer == null || def == null)
            {
                return null;
            }
            Apparel apparel = pawn.inventory.innerContainer
                .OfType<Apparel>()
                .FirstOrDefault(item => item != null && item.def == def);
            if (apparel == null)
            {
                return null;
            }
            pawn.inventory.innerContainer.Remove(apparel);
            return apparel;
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
                WithInternalVacGearRemoval(() =>
                {
                    pawn.apparel.Remove(worn);
                    worn.Destroy(DestroyMode.Vanish);
                });
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
                    WithInternalVacGearRemoval(() =>
                    {
                        pawn.apparel.Remove(apparel);
                        apparel.Destroy(DestroyMode.Vanish);
                    });
                }
            }
        }

        private static void WithInternalVacGearRemoval(Action action)
        {
            if (action == null)
            {
                return;
            }
            internalVacGearRemovalDepth++;
            try
            {
                action();
            }
            finally
            {
                internalVacGearRemovalDepth--;
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

        private static bool HasInjectedVacGearTag(Apparel apparel)
        {
            return apparel != null && apparel.questTags != null && apparel.questTags.Contains(InjectedVacGearTag);
        }

        private static float VacuumResistanceFromDef(ThingDef def)
        {
            StatDef stat = VacuumResistanceDef;
            if (def == null || stat == null || def.equippedStatOffsets.NullOrEmpty())
            {
                return 0f;
            }
            float resistance = 0f;
            foreach (StatModifier modifier in def.equippedStatOffsets)
            {
                if (modifier != null && modifier.stat == stat)
                {
                    resistance += modifier.value;
                }
            }
            return resistance;
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
            if (AdultSuitDefs.Contains(apparel.def.defName) || ChildSuitDefs.Contains(apparel.def.defName))
            {
                return true;
            }
            // Do not strip naturally-generated modded apparel just because it has vacuum stats.
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
            public float marketValue;
            public float selectionWeight;
            public List<string> setTags = new List<string>();
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
                    marketValue = marketValue,
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
                candidate.setTags = SetTagsFor(def);
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

            private static List<string> SetTagsFor(ThingDef def)
            {
                if (def == null || def.apparel == null || def.apparel.tags.NullOrEmpty())
                {
                    return new List<string>();
                }
                return def.apparel.tags
                    .Where(tag => !string.IsNullOrEmpty(tag) && LooksLikeSetTag(tag))
                    .Distinct()
                    .ToList();
            }

            private static bool LooksLikeSetTag(string tag)
            {
                string lowered = tag.ToLowerInvariant();
                return lowered.Contains("vac") || tag.Contains("_");
            }
        }

        private sealed class VacuumApparelSetCandidate
        {
            public string tag;
            public List<VacuumApparelCandidate> candidates;
            public float selectionWeight;

            public VacuumApparelSetCandidate(string tag, List<VacuumApparelCandidate> candidates)
            {
                this.tag = tag;
                this.candidates = candidates;
                float totalValue = Mathf.Max(1f, candidates.Sum(candidate => candidate.marketValue));
                float totalResistance = candidates.Sum(candidate => candidate.resistance);
                // Keep set rarity aligned with piece rarity: expensive matching armor sets stay uncommon.
                selectionWeight = Mathf.Clamp(totalResistance / totalValue, 0.0001f, 1f);
            }
        }
    }
}
