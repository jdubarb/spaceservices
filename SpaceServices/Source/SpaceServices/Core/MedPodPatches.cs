using HarmonyLib;
using System;
using Verse;

namespace SpaceServices
{
    public static class MedPodPatches
    {
        public static void Install(Harmony harmony)
        {
            Type jobGiver = AccessTools.TypeByName("MedPod.JobGiver_PatientGoToMedPod");
            if (jobGiver == null)
            {
                return;
            }

            // MedPod already injects itself into vanilla/Hospital/Hospitality think trees.
            // This postfix is an opt-in bridge for service pawns whose active duty never reaches that node.
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(jobGiver, "TryGiveJob"), typeof(MedPodPatchHandlers), postfix: nameof(MedPodPatchHandlers.PatientGoToMedPodPostfix));
            ServiceDebugUtility.Log(ServiceLogIntegration.Core, "MedPod service bridge patch installed.");
        }
    }

    public static class MedPodPatchHandlers
    {
        public static void PatientGoToMedPodPostfix(Pawn pawn, ref Verse.AI.Job __result)
        {
            if (__result != null)
            {
                return;
            }
            if (ServiceMedPodUtility.TryMakeMedPodJob(pawn, out Verse.AI.Job job))
            {
                __result = job;
            }
        }
    }
}
