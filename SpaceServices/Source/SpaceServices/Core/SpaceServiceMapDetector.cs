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
    public sealed class SpaceServiceEligibility
    {
        public bool allowed;
        public readonly List<string> allowReasons = new List<string>();
        public readonly List<string> blockReasons = new List<string>();

        public string ToLogString(Map map)
        {
            string mapId = map == null ? "null map" : "map " + map.uniqueID;
            return mapId + " allowed=" + allowed + " allow=[" + string.Join(", ", allowReasons.ToArray()) + "] block=[" + string.Join(", ", blockReasons.ToArray()) + "]";
        }
    }

    public static class SpaceServiceMapDetector
    {
        private static readonly Dictionary<int, CachedEligibility> EligibilityByMapId = new Dictionary<int, CachedEligibility>();
        private const int EligibilityCacheTicks = 2500;

        public static SpaceServiceEligibility Evaluate(Map map)
        {
            return map == null ? EvaluateUncached(map) : Clone(CachedOrEvaluate(map));
        }

        private static SpaceServiceEligibility EvaluateUncached(Map map)
        {
            SpaceServiceEligibility result = new SpaceServiceEligibility();
            if (map == null)
            {
                result.blockReasons.Add("no map");
                return result;
            }

            object parent = Reflect.GetMember(map, "Parent");
            string parentDef = Reflect.DefName(parent);
            string parentType = parent == null ? "" : parent.GetType().FullName ?? "";

            if (IsActualGravshipParent(parentDef, parentType))
            {
                result.blockReasons.Add("actual gravship parent");
            }
            if (parentDef == "QE_SpaceCustomSite" || parentDef == "QE_CustomMap_SpaceSubMap" || parentType.StartsWith("QuestEditor_Library.", StringComparison.Ordinal))
            {
                result.blockReasons.Add("temporary quest space map");
            }

            string layerDef = DefNameFromNested(map, "Tile", "LayerDef");
            if (Reflect.BoolFromNested(map, "Tile", "LayerDef", "isSpace"))
            {
                result.allowReasons.Add("tile layer is space:" + layerDef);
            }
            if (IsExplicitServiceLayer(layerDef))
            {
                result.allowReasons.Add("explicit service layer:" + layerDef);
            }

            string biomeDef = Reflect.DefName(map.Biome);
            if (Reflect.BoolMember(map.Biome, "inVacuum"))
            {
                result.allowReasons.Add("biome is vacuum:" + biomeDef);
            }

            object orbitalDebris = Reflect.GetMember(map, "OrbitalDebris") ?? Reflect.GetMember(map, "orbitalDebris");
            if (Reflect.DefName(orbitalDebris) == "Asteroid" || Convert.ToString(orbitalDebris) == "Asteroid")
            {
                result.allowReasons.Add("orbital debris asteroid");
            }

            object generatorDef = Reflect.GetMember(map, "generatorDef") ?? Reflect.GetMember(map, "GeneratorDef");
            string generator = Reflect.DefName(generatorDef);
            if (ContainsAny(generator, "Asteroid", "Orbit", "Moon", "Station", "Space"))
            {
                result.allowReasons.Add("space-like generator:" + generator);
            }

            if (parentDef == "SpaceSettlement")
            {
                result.allowReasons.Add("space settlement parent");
            }
            if (TypeOrBaseNameContains(parent, "SpaceMapParent") || ContainsAny(parentType, "SpaceSettlement", "AsteroidMapParent", "Station", "OrbitalBase"))
            {
                result.allowReasons.Add("stationary space parent:" + parentType);
            }
            if (ContainsAny(parentType, "LayeredAtmosphereOrbit.AtmosphereMapParent", "LayeredAtmosphereOrbit.FloatingIslandMapParent", "DeepOrbit.BigAsteroidMapParent"))
            {
                result.allowReasons.Add("explicit service parent:" + parentType);
            }

            if (Reflect.BoolFromNested(map, "Tile", "LayerDef", "isSpace") == false && !IsExplicitServiceLayer(layerDef) && ContainsAny(layerDef, "SkyIsland", "Troposphere", "Stratosphere", "Mesosphere"))
            {
                result.blockReasons.Add("non-vacuum atmospheric or sky layer");
            }

            result.allowed = result.blockReasons.Count == 0 && result.allowReasons.Count > 0;
            return result;
        }

        public static bool IsServiceEligible(Map map)
        {
            return map != null && CachedOrEvaluate(map).allowed;
        }

        public static bool IsServiceActive(Map map)
        {
            if (map == null)
            {
                return false;
            }
            if (IsServiceEligible(map))
            {
                return true;
            }
            return IsGroundsideServiceActive(map);
        }

        public static SpaceServiceEligibility EvaluateServiceAccess(Map map)
        {
            SpaceServiceEligibility result = Evaluate(map);
            if (!result.allowed && IsGroundsideServiceActive(map))
            {
                result.allowed = true;
                result.allowReasons.Add("experimental groundside service pads");
            }
            return result;
        }

        public static bool IsGroundsideServiceActive(Map map)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableGroundsideServicePads)
            {
                return false;
            }
            SpaceServiceEligibility eligibility = CachedOrEvaluate(map);
            if (eligibility.allowed || eligibility.blockReasons.Count > 0 || eligibility.allowReasons.Count > 0)
            {
                return false;
            }
            return ServicePadUtility.HasAnyServicePadBuilding(map);
        }

        private static SpaceServiceEligibility CachedOrEvaluate(Map map)
        {
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            CachedEligibility cached;
            if (EligibilityByMapId.TryGetValue(map.uniqueID, out cached) && tick < cached.tick + EligibilityCacheTicks)
            {
                return cached.eligibility;
            }
            SpaceServiceEligibility evaluated = EvaluateUncached(map);
            EligibilityByMapId[map.uniqueID] = new CachedEligibility { tick = tick, eligibility = evaluated };
            return evaluated;
        }

        private static SpaceServiceEligibility Clone(SpaceServiceEligibility source)
        {
            SpaceServiceEligibility clone = new SpaceServiceEligibility();
            if (source == null)
            {
                return clone;
            }
            clone.allowed = source.allowed;
            clone.allowReasons.AddRange(source.allowReasons);
            clone.blockReasons.AddRange(source.blockReasons);
            return clone;
        }

        private static string DefNameFromNested(object root, params string[] path)
        {
            object current = root;
            for (int i = 0; i < path.Length; i++)
            {
                current = Reflect.GetMember(current, path[i]);
            }
            return Reflect.DefName(current);
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

        private static bool IsActualGravshipParent(string parentDef, string parentType)
        {
            // Only block the real Odyssey gravship parent. Some stationary space mods
            // use "Gravship" in type names even though their maps are not vehicles.
            return parentDef == "Gravship" ||
                parentType == "RimWorld.Planet.Gravship" ||
                parentType == "RimWorld.Gravship";
        }

        private static bool IsExplicitServiceLayer(string layerDef)
        {
            return layerDef == "LAO_Troposphere" ||
                layerDef == "LAO_Stratosphere" ||
                layerDef == "LAO_Mesosphere" ||
                layerDef == "LAO_HighOrbit" ||
                layerDef == "LAO_Surface_Luna" ||
                layerDef == "Orbit2" ||
                layerDef == "Moon" ||
                layerDef == "MoonReal";
        }

        private static bool TypeOrBaseNameContains(object obj, string name)
        {
            for (Type type = obj == null ? null : obj.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || (type.FullName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class CachedEligibility
        {
            public int tick;
            public SpaceServiceEligibility eligibility;
        }
    }
}
