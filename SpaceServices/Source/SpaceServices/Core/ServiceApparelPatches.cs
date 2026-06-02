using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    [HarmonyPatch]
    public static class ServiceApparelRemovePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Pawn_ApparelTracker)
                .GetMethods(AccessTools.all)
                .Where(method => method.Name == "Remove" && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Apparel)));
        }

        public static bool Prefix(object[] __args)
        {
            Apparel apparel = FirstApparelArg(__args);
            if (!ServiceLifecycleUtility.ShouldProtectHospitalityVacuumApparel(apparel))
            {
                return true;
            }

            ServiceDebugUtility.LogAudit(ServiceLogIntegration.Hospitality, "Protected service vacuum apparel from removal during Hospitality transit: " + ApparelSummary(apparel));
            return false;
        }

        private static Apparel FirstApparelArg(object[] args)
        {
            return args == null ? null : args.OfType<Apparel>().FirstOrDefault();
        }

        private static string ApparelSummary(Apparel apparel)
        {
            return apparel == null ? "null" : apparel.def.defName + "#" + apparel.thingIDNumber + " wearer=" + ServiceDebugUtility.PawnAuditSummary(apparel.Wearer);
        }
    }

    [HarmonyPatch]
    public static class ServiceApparelTryDropPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(Pawn_ApparelTracker)
                .GetMethods(AccessTools.all)
                .Where(method => method.Name == "TryDrop" && method.ReturnType == typeof(bool) && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Apparel)));
        }

        public static bool Prefix(object[] __args, ref bool __result)
        {
            Apparel apparel = __args == null ? null : __args.OfType<Apparel>().FirstOrDefault();
            if (!ServiceLifecycleUtility.ShouldProtectHospitalityVacuumApparel(apparel))
            {
                return true;
            }

            __result = false;
            ServiceDebugUtility.LogAudit(ServiceLogIntegration.Hospitality, "Protected service vacuum apparel from drop during Hospitality transit: " + (apparel == null ? "null" : apparel.def.defName + "#" + apparel.thingIDNumber));
            return false;
        }
    }
}
