using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    [HarmonyPatch(typeof(JobDriver_Wear), nameof(JobDriver_Wear.TryMakePreToilReservations))]
    public static class ServiceWearJobReservationPatch
    {
        public static bool Prefix(JobDriver_Wear __instance, ref bool __result)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            Job job = __instance == null ? null : __instance.job;
            if (!ServiceLifecycleUtility.ShouldSuppressHospitalityVacuumApparelJob(pawn, job))
            {
                return true;
            }

            // Backstop for already-issued wear jobs. The normal Hospitality optimizer
            // prefix should prevent most cases before a job exists.
            __result = false;
            ServiceDebugUtility.LogAudit(ServiceLogIntegration.Hospitality, "Blocked Wear job during Hospitality vacuum transit: " + ServiceDebugUtility.PawnAuditSummary(pawn));
            return false;
        }
    }
}
