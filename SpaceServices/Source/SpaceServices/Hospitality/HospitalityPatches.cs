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
    public static class HospitalityPatches
    {
        public static void Install(Harmony harmony)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            Type[] types =
            {
                AccessTools.TypeByName("Hospitality.Utilities.SpawnGroupUtility"),
                AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup"),
                AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroupMax"),
                AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroupSelectFaction"),
                AccessTools.TypeByName("Hospitality.Spacer.IncidentWorker_VisitorGroupSpacer")
            };
            foreach (Type type in types.Where(t => t != null))
            {
                foreach (MethodInfo method in type.GetMethods(AccessTools.all).Where(m => m.DeclaringType == type && (m.Name == "TryDropSpawn" || m.Name == "SpawnPawns" || m.Name == "GeneratePawns")))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(OptionalPatchUtility), postfix: nameof(OptionalPatchUtility.SuitPawnsInArgsPostfix));
                }
            }
            Type visitorGroup = AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup");
            Type selectFaction = AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroupSelectFaction");
            Type spawnUtility = AccessTools.TypeByName("Hospitality.Utilities.SpawnGroupUtility");
            Type guestUtility = AccessTools.TypeByName("Hospitality.Utilities.GuestUtility");
            Type visitPoint = AccessTools.TypeByName("Hospitality.LordToil_VisitPoint");
            Type guestApparelOptimizer = AccessTools.TypeByName("Hospitality.JobGiver_OptimizeApparel_Guest");

            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(visitorGroup, "TryExecuteWorker"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.VisitorGroupTryExecutePrefix), postfix: nameof(HospitalityPatchHandlers.VisitorGroupTryExecutePostfix), finalizer: nameof(HospitalityPatchHandlers.VisitorGroupTryExecuteFinalizer));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(selectFaction, "TryExecuteWorker"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.VisitorGroupTryExecutePrefix), postfix: nameof(HospitalityPatchHandlers.VisitorGroupTryExecutePostfix), finalizer: nameof(HospitalityPatchHandlers.VisitorGroupTryExecuteFinalizer));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(visitorGroup, "AskForSafety"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.AskForSafetyPrefix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(visitorGroup, "SpawnGroup"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.SpawnGroupPrefix), postfix: nameof(HospitalityPatchHandlers.SpawnGroupPostfix), finalizer: nameof(HospitalityPatchHandlers.SpawnGroupFinalizer));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(visitorGroup, "CreateLord"), typeof(HospitalityPatchHandlers), postfix: nameof(HospitalityPatchHandlers.CreateLordPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(spawnUtility, "SpawnVisitor"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.SpawnVisitorPrefix), postfix: nameof(HospitalityPatchHandlers.SpawnVisitorPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(guestUtility, "Leave"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.GuestLeavePrefix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(visitPoint, "Leave"), typeof(HospitalityPatchHandlers), postfix: nameof(HospitalityPatchHandlers.VisitPointLeavePostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(guestApparelOptimizer, "TryGiveJob"), typeof(HospitalityPatchHandlers), prefix: nameof(HospitalityPatchHandlers.OptimizeApparelGuestPrefix));
        }
    }
}
