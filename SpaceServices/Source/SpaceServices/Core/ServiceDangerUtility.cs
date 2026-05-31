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
        public static bool HasActiveHostileThreat(Map map)
        {
            return map != null && GenHostility.AnyHostileActiveThreatToPlayer(map, true);
        }

        public static bool HospitalityTrafficBlocked(Map map, out string reason)
        {
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp != null && comp.debugForceHospitalityDanger)
            {
                reason = "debug forced danger";
                return true;
            }
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
            foreach (GameCondition condition in ActiveConditions(map))
            {
                if (ConditionBlocksService(condition, serviceKind, arrival, out reason))
                {
                    return true;
                }
            }
            reason = null;
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

            // Vanilla/DLC condition flags are authoritative; custom XML rules fill gaps after these.
            if (Reflect.BoolMember(def, "preventShuttleLaunch") && (!arrival || IsShuttleArrivalService(serviceKind)))
            {
                reason = def.LabelCap + " prevents shuttle launch";
                return true;
            }
            if (arrival && Reflect.BoolMember(def, "preventNeutralVisitors") && IsNeutralVisitorService(serviceKind))
            {
                reason = def.LabelCap + " prevents neutral visitors";
                return true;
            }
            if (Reflect.BoolMember(def, "causesTraderCaravanExit") && string.Equals(serviceKind, "trade", StringComparison.OrdinalIgnoreCase))
            {
                reason = def.LabelCap + " sends traders away";
                return true;
            }

            SpaceServiceHazardExtension extension = def.GetModExtension<SpaceServiceHazardExtension>();
            if (extension != null && extension.AppliesTo(serviceKind) && ((arrival && extension.blockArrivals) || (!arrival && extension.delayDepartures)))
            {
                reason = string.IsNullOrEmpty(extension.reason) ? def.LabelCap.ToString() : extension.reason;
                return true;
            }

            foreach (SpaceServiceHazardRuleDef rule in DefDatabase<SpaceServiceHazardRuleDef>.AllDefsListForReading)
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
    }
}
