using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;
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
                if (ServiceLifecycleUtility.IsIntentionalDelayLodger(__instance))
                {
                    ServiceLifecycleUtility.MarkPawnDirty(__instance, "tracked service pawn became temporary delay lodger");
                    return;
                }
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
        public static void Postfix(Pawn pawn, PawnLostCondition cond)
        {
            if (ServicePawnUtility.IsTerminalPawn(pawn) || ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                ServiceLifecycleUtility.ReleasePawn(pawn, "tracked service lord lost terminal/player pawn: " + cond);
                return;
            }
            ServiceLifecycleUtility.MarkPawnDirty(pawn, "tracked service lord lost pawn: " + cond);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class ServicePawnStartJobPatch
    {
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

        public static void Postfix(Pawn_JobTracker __instance)
        {
            Pawn pawn = __instance == null || PawnField == null ? null : PawnField.GetValue(__instance) as Pawn;
            ServiceLifecycleUtility.ValidateTrackedPawnCurrentJob(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class ServicePawnDebugGizmosPatch
    {
        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!Prefs.DevMode || !HospitalPatchHandlers.CanDebugForceTrackedSurgeryFailure(__instance))
            {
                return;
            }
            IEnumerable<Gizmo> original = __result ?? Enumerable.Empty<Gizmo>();
            __result = original.Concat(DebugGizmos(__instance));
        }

        private static IEnumerable<Gizmo> DebugGizmos(Pawn pawn)
        {
            yield return new Command_Action
            {
                defaultLabel = "JDB_SpaceServices_Gizmo_DebugForceSurgeryFailure".Translate(),
                defaultDesc = "JDB_SpaceServices_Gizmo_DebugForceSurgeryFailureDesc".Translate(),
                action = delegate
                {
                    if (HospitalPatchHandlers.DebugForceTrackedSurgeryFailure(pawn, out string reason))
                    {
                        Messages.Message("Space Services: " + reason, pawn, MessageTypeDefOf.NeutralEvent, false);
                    }
                    else
                    {
                        Messages.Message("Space Services: cannot force surgery failure: " + reason, pawn, MessageTypeDefOf.RejectInput, false);
                    }
                }
            };
        }
    }
}
