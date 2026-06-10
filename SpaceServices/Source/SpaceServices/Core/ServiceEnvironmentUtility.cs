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
    public static class ServiceEnvironmentUtility
    {
        private const float Epsilon = 0.001f;
        private const int PadVacuumCacheTicks = 120;
        private static readonly Dictionary<int, CachedPadVacuum> PadVacuumCache = new Dictionary<int, CachedPadVacuum>();
        // These roofs seal space maps but should not block visual service shuttles or redirected drop pods.
        private static readonly HashSet<string> KnownFlyThroughRoofs = new HashSet<string>
        {
            "SMR_VacBarrierRoof",
            "SMR_AdvancedVacBarrierRoof",
            "CO_VacRoof"
        };

        public static float GetVacuum(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return 0f;
            }
            try
            {
                return Mathf.Clamp01(VacuumUtility.GetVacuum(cell, map));
            }
            catch
            {
                TerrainDef terrain = cell.GetTerrain(map);
                if (terrain != null && terrain.exposesToVacuum && !cell.Roofed(map))
                {
                    return 1f;
                }
                return 0f;
            }
        }

        public static float GetMaxVacuum(Thing pad)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pad.Destroyed)
            {
                return 0f;
            }
            int key = pad.thingIDNumber;
            int ticksGame = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            CachedPadVacuum cached;
            if (PadVacuumCache.TryGetValue(key, out cached) && cached.map == map && ticksGame <= cached.expiresTick)
            {
                return cached.maxVacuum;
            }
            float maxVacuum = 0f;
            foreach (IntVec3 cell in pad.OccupiedRect().Cells)
            {
                maxVacuum = Mathf.Max(maxVacuum, GetVacuum(cell, map));
            }
            PadVacuumCache[key] = new CachedPadVacuum
            {
                map = map,
                expiresTick = ticksGame + PadVacuumCacheTicks,
                maxVacuum = maxVacuum
            };
            return maxVacuum;
        }

        public static void ClearPadVacuumCache(Thing pad)
        {
            if (pad != null)
            {
                PadVacuumCache.Remove(pad.thingIDNumber);
            }
        }

        public static void ClearPadVacuumCache(Map map)
        {
            if (map == null)
            {
                return;
            }
            foreach (int key in PadVacuumCache.Where(pair => pair.Value != null && pair.Value.map == map).Select(pair => pair.Key).ToList())
            {
                PadVacuumCache.Remove(key);
            }
        }

        public static bool IsRoofAccessible(Thing pad, out string reason)
        {
            reason = null;
            Map map = pad == null ? null : pad.Map;
            if (map == null || pad.Destroyed)
            {
                reason = "pad unavailable";
                return false;
            }

            foreach (IntVec3 cell in pad.OccupiedRect().Cells)
            {
                if (!cell.InBounds(map))
                {
                    reason = "pad footprint reaches out of bounds at " + cell;
                    return false;
                }
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof != null && !IsFlyThroughRoof(roof))
                {
                    reason = "blocked by " + RoofLabel(roof) + " at " + cell;
                    return false;
                }
            }
            return true;
        }

        public static bool HasFullFlyThroughRoof(Thing pad, out string reason)
        {
            reason = null;
            Map map = pad == null ? null : pad.Map;
            if (map == null || pad.Destroyed)
            {
                reason = "pad unavailable";
                return false;
            }

            foreach (IntVec3 cell in pad.OccupiedRect().Cells)
            {
                if (!cell.InBounds(map))
                {
                    reason = "pad footprint reaches out of bounds at " + cell;
                    return false;
                }
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof == null)
                {
                    reason = "missing fly-through roof at " + cell;
                    return false;
                }
                if (!IsFlyThroughRoof(roof))
                {
                    reason = "blocked by " + RoofLabel(roof) + " at " + cell;
                    return false;
                }
            }
            return true;
        }

        public static string RoofAccessReport(Thing pad)
        {
            Map map = pad == null ? null : pad.Map;
            if (map == null || pad.Destroyed)
            {
                return "pad unavailable";
            }

            int total = 0;
            int clear = 0;
            int flyThrough = 0;
            int blocked = 0;
            string firstBlocked = null;
            foreach (IntVec3 cell in pad.OccupiedRect().Cells)
            {
                total++;
                if (!cell.InBounds(map))
                {
                    blocked++;
                    firstBlocked = firstBlocked ?? "out of bounds at " + cell;
                    continue;
                }
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof == null)
                {
                    clear++;
                }
                else if (IsFlyThroughRoof(roof))
                {
                    flyThrough++;
                }
                else
                {
                    blocked++;
                    firstBlocked = firstBlocked ?? RoofLabel(roof) + " at " + cell;
                }
            }

            if (blocked > 0)
            {
                return blocked + "/" + total + " blocked, first " + firstBlocked;
            }
            return total + "/" + total + " clear or fly-through (" + clear + " clear, " + flyThrough + " fly-through)";
        }

        public static bool IsSafeForPawn(Pawn pawn, Map map, IntVec3 cell)
        {
            float vacuum = GetVacuum(cell, map);
            return VacSuitUtility.VacuumResistance(pawn) + Epsilon >= vacuum;
        }

        public static bool IsSealedNoSuitArrivalCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }
            Thing pad = ServicePadUtility.ServicePadAtCell(map, cell);
            if (pad != null)
            {
                return GetMaxVacuum(pad) <= Epsilon;
            }
            return GetVacuum(cell, map) <= Epsilon;
        }

        public static bool IsPadSafeForPawns(Thing pad, IEnumerable<Pawn> pawns, out string reason)
        {
            return IsPadSafeForPawnsAtTarget(pad, pawns, VacSuitUtility.PracticalVacuumSuitTarget, out reason);
        }

        public static bool IsPadSafeForPawnsAtTarget(Thing pad, IEnumerable<Pawn> pawns, float practicalVacuumSuitTarget, out string reason)
        {
            reason = null;
            if (pad == null || pad.Destroyed || pad.Map == null)
            {
                reason = "departure pad unavailable";
                return false;
            }
            float vacuum = Mathf.Min(GetMaxVacuum(pad), Mathf.Clamp01(practicalVacuumSuitTarget));
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                float resistance = VacSuitUtility.VacuumResistance(pawn);
                if (resistance + Epsilon < vacuum)
                {
                    reason = "departure pad requires " + vacuum.ToStringPercent() + " practical vacuum resistance; " + pawn.LabelShortCap + " has " + resistance.ToStringPercent();
                    return false;
                }
            }
            return true;
        }

        private static bool IsFlyThroughRoof(RoofDef roof)
        {
            if (roof == null)
            {
                return true;
            }
            if (KnownFlyThroughRoofs.Contains(roof.defName))
            {
                return true;
            }
            if (Reflect.BoolMember(roof, "allowFlyThrough", false))
            {
                return true;
            }

            IEnumerable extensions = Reflect.GetMember(roof, "modExtensions") as IEnumerable;
            if (extensions != null)
            {
                foreach (object extension in extensions)
                {
                    if (Reflect.BoolMember(extension, "allowFlyThrough", false))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string RoofLabel(RoofDef roof)
        {
            if (roof == null)
            {
                return "no roof";
            }
            return string.IsNullOrEmpty(roof.label) ? roof.defName : roof.label;
        }

        private sealed class CachedPadVacuum
        {
            public Map map;
            public int expiresTick;
            public float maxVacuum;
        }
    }
}
