using HarmonyLib;
using RimWorld;
using System;
using Verse;

namespace SpaceServices
{
    public static class MedPodPatches
    {
        public static void Install(Harmony harmony)
        {
            Type healthUtility = AccessTools.TypeByName("MedPod.MedPodHealthAIUtility");
            if (healthUtility == null)
            {
                return;
            }

            // MedPod already injects its job giver into the relevant think trees. Service
            // visitors only need the medical-care gate loosened because Hospitality guests
            // have no visible care selector and Hospital patients often arrive below Best.
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(healthUtility, "HasAllowedMedicalCareCategory"), typeof(MedPodPatchHandlers), postfix: nameof(MedPodPatchHandlers.HasAllowedMedicalCareCategoryPostfix));
            ServiceDebugUtility.Log(ServiceLogIntegration.Core, "MedPod medical-care bridge patch installed.");
        }
    }

    public static class MedPodPatchHandlers
    {
        public static void HasAllowedMedicalCareCategoryPostfix(object[] __args, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            Pawn pawn = __args != null && __args.Length > 0 ? __args[0] as Pawn : null;
            if (ServiceMedPodUtility.ShouldBypassMedicalCareGate(pawn))
            {
                __result = true;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-care-bypass-" + pawn.thingIDNumber, "MedPod medical-care gate bypassed for service pawn " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            }
        }
    }
}
