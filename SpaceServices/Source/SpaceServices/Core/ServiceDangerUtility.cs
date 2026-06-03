using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SpaceServices
{
    public static class ServiceDangerUtility
    {
        private static readonly Dictionary<string, TrafficBlockResult> TrafficCache = new Dictionary<string, TrafficBlockResult>();
        private static readonly Dictionary<GameConditionDef, HazardConditionInfo> ConditionInfoCache = new Dictionary<GameConditionDef, HazardConditionInfo>();
        private static readonly Dictionary<string, List<SpaceServiceHazardRuleDef>> RulesByConditionDefName = new Dictionary<string, List<SpaceServiceHazardRuleDef>>(StringComparer.OrdinalIgnoreCase);
        private static bool rulesIndexed;
        private static int trafficCacheTick = -1;

        public static bool HasActiveHostileThreat(Map map)
        {
            return map != null && GenHostility.AnyHostileActiveThreatToPlayer(map, true);
        }

        public static bool HospitalityTrafficBlocked(Map map, out string reason)
        {
            if (HasActiveHostileThreat(map))
            {
                reason = "active hostile threat";
                return true;
            }
            reason = null;
            return false;
        }

        public static bool ArrivalTrafficBlocked(Map map, string serviceKind, out string reason)
        {
            return ServiceTrafficBlocked(map, serviceKind, arrival: true, out reason);
        }

        public static bool DepartureShuttleBlocked(Map map, string serviceKind, out string reason)
        {
            return ServiceTrafficBlocked(map, serviceKind, arrival: false, out reason);
        }

        private static bool ServiceTrafficBlocked(Map map, string serviceKind, bool arrival, out string reason)
        {
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (tick != trafficCacheTick)
            {
                TrafficCache.Clear();
                trafficCacheTick = tick;
            }
            string cacheKey = (map == null ? "null" : map.uniqueID.ToString()) + "|" + (serviceKind ?? "") + "|" + arrival;
            TrafficBlockResult cached;
            if (TrafficCache.TryGetValue(cacheKey, out cached))
            {
                reason = cached.reason;
                return cached.blocked;
            }

            foreach (GameCondition condition in ActiveConditions(map))
            {
                if (ConditionBlocksService(condition, serviceKind, arrival, out reason))
                {
                    TrafficCache[cacheKey] = new TrafficBlockResult { blocked = true, reason = reason };
                    return true;
                }
            }
            reason = null;
            TrafficCache[cacheKey] = new TrafficBlockResult { blocked = false, reason = null };
            return false;
        }

        private static bool ConditionBlocksService(GameCondition condition, string serviceKind, bool arrival, out string reason)
        {
            reason = null;
            GameConditionDef def = condition == null ? null : condition.def;
            if (def == null)
            {
                return false;
            }

            HazardConditionInfo info = InfoFor(def);

            // Vanilla/DLC condition flags are authoritative; custom XML rules fill gaps after these.
            if (info.preventShuttleLaunch && (!arrival || IsShuttleArrivalService(serviceKind)))
            {
                reason = def.LabelCap + " prevents shuttle launch";
                return true;
            }
            if (arrival && info.preventNeutralVisitors && IsNeutralVisitorService(serviceKind))
            {
                reason = def.LabelCap + " prevents neutral visitors";
                return true;
            }
            if (info.causesTraderCaravanExit && string.Equals(serviceKind, "trade", StringComparison.OrdinalIgnoreCase))
            {
                reason = def.LabelCap + " sends traders away";
                return true;
            }

            SpaceServiceHazardExtension extension = info.extension;
            if (extension != null && extension.AppliesTo(serviceKind) && ((arrival && extension.blockArrivals) || (!arrival && extension.delayDepartures)))
            {
                reason = string.IsNullOrEmpty(extension.reason) ? def.LabelCap.ToString() : extension.reason;
                return true;
            }

            foreach (SpaceServiceHazardRuleDef rule in RulesFor(def.defName))
            {
                if (rule != null && rule.AppliesToCondition(condition, serviceKind) && ((arrival && rule.blockArrivals) || (!arrival && rule.delayDepartures)))
                {
                    reason = string.IsNullOrEmpty(rule.reason) ? def.LabelCap.ToString() : rule.reason;
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<GameCondition> ActiveConditions(Map map)
        {
            if (map == null || map.gameConditionManager == null)
            {
                yield break;
            }
            IEnumerable conditions = Reflect.GetMember(map.gameConditionManager, "ActiveConditions") as IEnumerable;
            if (conditions == null)
            {
                yield break;
            }
            foreach (object condition in conditions)
            {
                GameCondition gameCondition = condition as GameCondition;
                if (gameCondition != null)
                {
                    yield return gameCondition;
                }
            }
        }

        private static HazardConditionInfo InfoFor(GameConditionDef def)
        {
            HazardConditionInfo info;
            if (ConditionInfoCache.TryGetValue(def, out info))
            {
                return info;
            }
            info = new HazardConditionInfo
            {
                preventShuttleLaunch = Reflect.BoolMember(def, "preventShuttleLaunch"),
                preventNeutralVisitors = Reflect.BoolMember(def, "preventNeutralVisitors"),
                causesTraderCaravanExit = Reflect.BoolMember(def, "causesTraderCaravanExit"),
                extension = def.GetModExtension<SpaceServiceHazardExtension>()
            };
            ConditionInfoCache[def] = info;
            return info;
        }

        private static List<SpaceServiceHazardRuleDef> RulesFor(string conditionDefName)
        {
            EnsureRulesIndexed();
            List<SpaceServiceHazardRuleDef> rules;
            return RulesByConditionDefName.TryGetValue(conditionDefName ?? "", out rules) ? rules : EmptyRules;
        }

        private static readonly List<SpaceServiceHazardRuleDef> EmptyRules = new List<SpaceServiceHazardRuleDef>();

        private static void EnsureRulesIndexed()
        {
            if (rulesIndexed)
            {
                return;
            }
            rulesIndexed = true;
            RulesByConditionDefName.Clear();
            foreach (SpaceServiceHazardRuleDef rule in DefDatabase<SpaceServiceHazardRuleDef>.AllDefsListForReading)
            {
                if (rule == null || !rule.enabled || rule.gameConditionDefNames.NullOrEmpty())
                {
                    continue;
                }
                foreach (string conditionDefName in rule.gameConditionDefNames)
                {
                    if (string.IsNullOrEmpty(conditionDefName))
                    {
                        continue;
                    }
                    List<SpaceServiceHazardRuleDef> list;
                    if (!RulesByConditionDefName.TryGetValue(conditionDefName, out list))
                    {
                        list = new List<SpaceServiceHazardRuleDef>();
                        RulesByConditionDefName[conditionDefName] = list;
                    }
                    list.Add(rule);
                }
            }
        }

        private static bool IsShuttleArrivalService(string serviceKind)
        {
            return string.Equals(serviceKind, "hospital", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceKind, "hospitality", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceKind, "trade", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNeutralVisitorService(string serviceKind)
        {
            return string.Equals(serviceKind, "hospitality", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceKind, "trade", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class TrafficBlockResult
        {
            public bool blocked;
            public string reason;
        }

        private sealed class HazardConditionInfo
        {
            public bool preventShuttleLaunch;
            public bool preventNeutralVisitors;
            public bool causesTraderCaravanExit;
            public SpaceServiceHazardExtension extension;
        }
    }
}
