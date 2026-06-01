using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI.Group;

namespace SpaceServices
{
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class ServicePawnKillPatch
    {
        public static void Postfix(Pawn __instance)
        {
            // Death bypasses Hospitality leave callbacks, so clear service tracking as soon as RimWorld kills the pawn.
            ServiceLifecycleUtility.ReleasePawn(__instance, "tracked service pawn died");
        }
    }

    [HarmonyPatch]
    public static class ServicePawnDespawnPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Pawn)
                .GetMethods(AccessTools.all)
                .Where(method => method.Name == "DeSpawn");
        }

        public static void Postfix(Pawn __instance)
        {
            // Despawn can be temporary, so only wake validation instead of assuming final departure.
            ServiceLifecycleUtility.MarkPawnDirty(__instance, "tracked service pawn despawned");
        }
    }

    [HarmonyPatch]
    public static class ServicePawnSetFactionPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Pawn)
                .GetMethods(AccessTools.all)
                .Where(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "SetFaction" &&
                        parameters.Length > 0 &&
                        parameters[0].ParameterType == typeof(Faction);
                });
        }

        public static void Postfix(Pawn __instance)
        {
            if (ServicePawnUtility.IsPlayerOwnedPawn(__instance))
            {
                // Recruit/join offers are not departures. Once the pawn belongs to the player, stop managing them.
                ServiceLifecycleUtility.ReleasePawn(__instance, "tracked service pawn joined the colony");
                return;
            }
            ServiceLifecycleUtility.MarkPawnDirty(__instance, "tracked service pawn faction changed");
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    public static class ServiceLordPawnLostPatch
    {
        public static void Postfix(Pawn p, PawnLostCondition condition)
        {
            if (ServicePawnUtility.IsTerminalPawn(p) || ServicePawnUtility.IsPlayerOwnedPawn(p))
            {
                ServiceLifecycleUtility.ReleasePawn(p, "tracked service lord lost terminal/player pawn: " + condition);
                return;
            }
            ServiceLifecycleUtility.MarkPawnDirty(p, "tracked service lord lost pawn: " + condition);
        }
    }
}
