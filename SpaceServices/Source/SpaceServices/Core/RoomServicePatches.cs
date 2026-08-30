using HarmonyLib;
using RimWorld;
using System;
using Verse;

namespace SpaceServices
{
    public static class RoomServicePatches
    {
        public static bool IsAvailable()
        {
            return AccessTools.TypeByName("HospitalityRoomService.RoomServiceUtility") != null;
        }

        public static void Install(Harmony harmony)
        {
            if (!IsAvailable())
            {
                return;
            }
            Type optionalHospital = AccessTools.TypeByName("HospitalityRoomService.OptionalHospital");
            Type medicalGuestUtility = AccessTools.TypeByName("HospitalityRoomService.MedicalGuestUtility");
            Type roomServiceUtility = AccessTools.TypeByName("HospitalityRoomService.RoomServiceUtility");
            bool installed = false;

            if (optionalHospital != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(optionalHospital, "TryHandoff", new[] { typeof(Pawn), typeof(Hediff) }), typeof(RoomServicePatchHandlers), prefix: nameof(RoomServicePatchHandlers.TryHospitalHandoffPrefix));
                installed = true;
            }
            if (medicalGuestUtility != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(medicalGuestUtility, "TryStartMedicalGuestEvent", new[] { typeof(Pawn) }), typeof(RoomServicePatchHandlers), prefix: nameof(RoomServicePatchHandlers.TryStartMedicalGuestEventPrefix));
                installed = true;
            }
            if (roomServiceUtility != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(roomServiceUtility, "WhyCannotSolicit", new[] { typeof(Pawn), typeof(Pawn) }), typeof(RoomServicePatchHandlers), postfix: nameof(RoomServicePatchHandlers.WhyCannotSolicitPostfix));
                installed = true;
            }

            if (installed)
            {
                ServiceDebugUtility.Log(ServiceLogIntegration.Core, "Initial experimental Hospitality: Room Service guards installed.");
            }
        }
    }

    public static class RoomServicePatchHandlers
    {
        public static bool TryHospitalHandoffPrefix(Pawn pawn, ref bool __result)
        {
            if (!ShouldSuppressMedicalLifecycle(pawn, out string reason))
            {
                return true;
            }

            __result = false;
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "room-service-hospital-handoff-block-" + pawn.thingIDNumber, "Blocked Hospitality: Room Service guest-to-Hospital conversion: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
            return false;
        }

        public static bool TryStartMedicalGuestEventPrefix(Pawn guest)
        {
            if (!ShouldSuppressMedicalLifecycle(guest, out string reason))
            {
                return true;
            }

            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "room-service-surgery-offer-block-" + guest.thingIDNumber, "Blocked Hospitality: Room Service special surgery offer: " + reason + " pawn=" + ServiceDebugUtility.PawnAuditSummary(guest), GenDate.TicksPerHour);
            return false;
        }

        public static void WhyCannotSolicitPostfix(Pawn guest, ref string __result)
        {
            if (__result != null || !ExperimentalSupportEnabled() || !TryGetTrackedHospitalityRecord(guest, out ServiceGroupRecord record))
            {
                return;
            }
            if (!ServiceLifecycleUtility.IsActiveDepartureState(record))
            {
                return;
            }

            __result = "JDB_SpaceServices_RoomService_DepartingGuest".Translate();
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "room-service-solicitation-block-" + guest.thingIDNumber, "Blocked Hospitality: Room Service solicitation during service-shuttle departure. pawn=" + ServiceDebugUtility.PawnAuditSummary(guest), GenDate.TicksPerHour);
        }

        private static bool ShouldSuppressMedicalLifecycle(Pawn pawn, out string reason)
        {
            reason = null;
            if (!ExperimentalSupportEnabled() || pawn == null)
            {
                return false;
            }
            if (TryGetTrackedHospitalityRecord(pawn, out _))
            {
                reason = "pawn belongs to an active Space Services Hospitality record";
                return true;
            }

            Map map = pawn.MapHeld;
            if (map != null && HospitalityArrivalContext.TryGetArrivalPad(map, out _))
            {
                reason = "pawn is currently arriving through the Space Services Hospitality pipeline";
                return true;
            }
            return false;
        }

        private static bool TryGetTrackedHospitalityRecord(Pawn pawn, out ServiceGroupRecord record)
        {
            record = null;
            if (pawn == null ||
                !ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out _, out record) ||
                record == null ||
                !string.Equals(record.serviceKind, "hospitality", StringComparison.OrdinalIgnoreCase) ||
                record.state == "completed" ||
                record.state == "extracting")
            {
                record = null;
                return false;
            }
            return true;
        }

        private static bool ExperimentalSupportEnabled()
        {
            return SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableRoomServiceExperimentalSupport;
        }
    }
}
