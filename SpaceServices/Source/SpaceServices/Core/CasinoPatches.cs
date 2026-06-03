using HarmonyLib;
using RimWorld;
using System;
using System.Reflection;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public static class CasinoPatches
    {
        public static void Install(Harmony harmony)
        {
            Type slotJobGiver = AccessTools.TypeByName("HospitalityCasino.JobGiver_PlaySlotMachines");
            if (slotJobGiver != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(slotJobGiver, "TryGiveJob"), typeof(CasinoPatchHandlers), prefix: nameof(CasinoPatchHandlers.TryGiveSlotJobPrefix));
                ServiceDebugUtility.Log(ServiceLogIntegration.Core, "Hospitality: Casino patient-gambling guard patch installed.");
            }

            Type slotJobDriver = AccessTools.TypeByName("HospitalityCasino.JobDriver_PlaySlotMachine");
            if (slotJobDriver != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(slotJobDriver, "TryMakePreToilReservations"), typeof(CasinoPatchHandlers), prefix: nameof(CasinoPatchHandlers.TryReserveSlotJobPrefix));
            }
        }
    }

    public static class CasinoPatchHandlers
    {
        public static bool TryGiveSlotJobPrefix(Pawn pawn, ref Job __result)
        {
            if (!ShouldSuppressGambling(pawn, out string reason))
            {
                return true;
            }

            __result = null;
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "casino-patient-job-block-" + pawn.thingIDNumber, "Blocked Hospitality: Casino slot job for Hospital patient under ongoing treatment: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        public static bool TryReserveSlotJobPrefix(JobDriver __instance, ref bool __result)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (!ShouldSuppressGambling(pawn, out string reason))
            {
                return true;
            }

            __result = false;
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "casino-patient-reserve-block-" + pawn.thingIDNumber, "Blocked existing Hospitality: Casino slot reservation for Hospital patient under ongoing treatment: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        private static bool ShouldSuppressGambling(Pawn pawn, out string reason)
        {
            reason = null;
            if (SpaceServicesMod.Settings == null ||
                !SpaceServicesMod.Settings.enableHospital ||
                !SpaceServicesMod.Settings.disablePatientGamblingAddictions)
            {
                return false;
            }
            if (pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceEligible(pawn.Map))
            {
                return false;
            }
            if (!ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out _, out ServiceGroupRecord record) ||
                record == null ||
                !string.Equals(record.serviceKind, "hospital", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (HospitalPatchHandlers.ShouldKeepHospitalPatientForOngoingTreatment(pawn, out reason))
            {
                return true;
            }
            if (HospitalPatchHandlers.IsActiveHospitalPatient(pawn))
            {
                reason = "active Hospital patient";
                return true;
            }
            return false;
        }
    }
}
