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
    public static class SpaceportsPatchHandlers
    {
        public static void SpaceportsIsMapInSpacePostfix(Map map, ref bool __result)
        {
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.enableSpaceportsBridge && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                __result = true;
            }
        }

        public static bool SpaceportsSuitUpPawnsPrefix(List<Pawn> pawns)
        {
            VacSuitUtility.SuitPawnsForVacuum(pawns);
            return true;
        }

        public static void SpaceportsHospitalityShuttleCheckPostfix(Map map, Faction faction, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (faction != null && faction.def != null && faction.def.techLevel == TechLevel.Neolithic)
            {
                return;
            }
            __result = true;
        }

        public static void SpaceportsCheckIfClearForLandingPostfix(Map map, int typeVal, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (typeVal != 1 && typeVal != 3)
            {
                return;
            }
            if (!SpaceServicesMod.Settings.enableHospitality || ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return;
            }
            if (HasBlockingLandingCondition(map))
            {
                return;
            }
            __result = true;
        }

        private static bool HasBlockingLandingCondition(Map map)
        {
            if (map == null)
            {
                return true;
            }
            GameConditionDef kessler = DefDatabase<GameConditionDef>.GetNamedSilentFail("Spaceports_KesslerSyndrome");
            if (kessler != null && map.gameConditionManager.ConditionIsActive(kessler))
            {
                return true;
            }
            if (GenHostility.AnyHostileActiveThreatToPlayer(map, true))
            {
                return true;
            }
            return false;
        }
    }
}
