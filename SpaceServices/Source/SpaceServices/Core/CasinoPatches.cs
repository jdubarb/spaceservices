using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public static class CasinoPatches
    {
        private static readonly string[] CasinoGamblingDriverTypes =
        {
            "HospitalityCasino.JobDriver_PlaySlotMachine",
            "HospitalityCasino.JobDriver_PlayPokerForMoney",
            "HospitalityCasino.JobDriver_PlayRouletteForMoney"
        };

        public static bool IsAvailable()
        {
            return AccessTools.TypeByName("HospitalityCasino.IncidentWorker_CasinoVisitorGroup") != null;
        }

        public static void Install(Harmony harmony)
        {
            bool installed = false;
            Type slotJobGiver = AccessTools.TypeByName("HospitalityCasino.JobGiver_PlaySlotMachines");
            if (slotJobGiver != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(slotJobGiver, "TryGiveJob"), typeof(CasinoPatchHandlers), prefix: nameof(CasinoPatchHandlers.TryGiveSlotJobPrefix));
                installed = true;
            }

            Type slotJoyGiver = AccessTools.TypeByName("HospitalityCasino.JoyGiver_PlaySlotMachine");
            if (slotJoyGiver != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(slotJoyGiver, "TryGivePlayJob"), typeof(CasinoPatchHandlers), prefix: nameof(CasinoPatchHandlers.TryGiveSlotJoyJobPrefix));
                installed = true;
            }

            HashSet<MethodInfo> patchedReservationMethods = new HashSet<MethodInfo>();
            for (int i = 0; i < CasinoGamblingDriverTypes.Length; i++)
            {
                Type driverType = AccessTools.TypeByName(CasinoGamblingDriverTypes[i]);
                MethodInfo reservationMethod = driverType == null ? null : AccessTools.Method(driverType, "TryMakePreToilReservations");
#if RIMWORLD_1_7
                // Poker and roulette inherit this implementation. Harmony needs the
                // declaring type's MethodInfo, also allowing the shared guard to deduplicate.
                if (reservationMethod != null)
                {
                    reservationMethod = AccessTools.DeclaredMethod(reservationMethod.DeclaringType,
                        reservationMethod.Name, new[] { typeof(bool) });
                }
#endif
                if (reservationMethod != null && patchedReservationMethods.Add(reservationMethod))
                {
                    OptionalModPatches.PatchIfExists(harmony, reservationMethod, typeof(CasinoPatchHandlers), prefix: nameof(CasinoPatchHandlers.TryReserveCasinoJobPrefix));
                    installed = true;
                }
            }

            if (installed)
            {
                ServiceDebugUtility.Log(ServiceLogIntegration.Core, "Hospitality: Casino slot, poker, and roulette service-pawn gambling guards installed.");
            }
        }
    }

    public static class CasinoPatchHandlers
    {
        public static bool TryGiveSlotJobPrefix(Pawn pawn, ref Job __result)
        {
            if (!ShouldSuppressGambling(pawn, out string reason, out ServiceLogIntegration integration))
            {
                return true;
            }

            __result = null;
            ServiceDebugUtility.LogThrottled(integration, "casino-service-job-block-" + pawn.thingIDNumber, "Blocked Hospitality: Casino slot job for tracked service pawn: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        public static bool TryGiveSlotJoyJobPrefix(Pawn pawn, ref Job __result)
        {
            if (!ShouldSuppressGambling(pawn, out string reason, out ServiceLogIntegration integration))
            {
                return true;
            }

            __result = null;
            ServiceDebugUtility.LogThrottled(integration, "casino-service-joy-job-block-" + pawn.thingIDNumber, "Blocked Hospitality: Casino slot joy job for tracked service pawn: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        public static bool TryReserveCasinoJobPrefix(JobDriver __instance, ref bool __result)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (!IsCasinoGamblingDriver(__instance) ||
                !ShouldSuppressGambling(pawn, out string reason, out ServiceLogIntegration integration))
            {
                return true;
            }

            __result = false;
            ServiceDebugUtility.LogThrottled(integration, "casino-service-reserve-block-" + pawn.thingIDNumber, "Blocked Hospitality: Casino gambling reservation for tracked service pawn: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        private static bool IsCasinoGamblingDriver(JobDriver driver)
        {
            string typeName = driver == null ? null : driver.GetType().FullName;
            return string.Equals(typeName, "HospitalityCasino.JobDriver_PlaySlotMachine", StringComparison.Ordinal) ||
                string.Equals(typeName, "HospitalityCasino.JobDriver_PlayPokerForMoney", StringComparison.Ordinal) ||
                string.Equals(typeName, "HospitalityCasino.JobDriver_PlayRouletteForMoney", StringComparison.Ordinal);
        }

        private static bool ShouldSuppressGambling(Pawn pawn, out string reason, out ServiceLogIntegration integration)
        {
            reason = null;
            integration = ServiceLogIntegration.Core;
            if (pawn == null || pawn.Map == null || !SpaceServiceMapDetector.IsServiceActive(pawn.Map))
            {
                return false;
            }
            if (!ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out _, out ServiceGroupRecord record) ||
                record == null ||
                record.state == "completed" ||
                record.state == "extracting")
            {
                return false;
            }

            if (string.Equals(record.serviceKind, "hospitality", StringComparison.OrdinalIgnoreCase) &&
                ServiceLifecycleUtility.IsActiveDepartureState(record))
            {
                integration = ServiceLogIntegration.Hospitality;
                reason = "Hospitality guest is staging for service-shuttle departure";
                return true;
            }

            if (!string.Equals(record.serviceKind, "hospital", StringComparison.OrdinalIgnoreCase) ||
                SpaceServicesMod.Settings == null ||
                !SpaceServicesMod.Settings.enableHospital ||
                !SpaceServicesMod.Settings.disablePatientGamblingAddictions)
            {
                return false;
            }
            integration = ServiceLogIntegration.Hospital;
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
