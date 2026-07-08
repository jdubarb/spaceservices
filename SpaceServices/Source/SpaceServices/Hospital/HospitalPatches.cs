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
    public static class HospitalPatches
    {
        private static bool checkedHospitalApi;
        private static bool hospitalApiAvailable;

        public static bool HospitalApiAvailable()
        {
            if (checkedHospitalApi)
            {
                return hospitalApiAvailable;
            }
            checkedHospitalApi = true;
            hospitalApiAvailable =
                AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives") != null ||
                AccessTools.TypeByName("Hospital.IncidentHelper") != null ||
                AccessTools.TypeByName("Hospital.HospitalMapComponent") != null;
            return hospitalApiAvailable;
        }

        public static void Install(Harmony harmony)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospital)
            {
                return;
            }
            Type patientUtility = AccessTools.TypeByName("Hospital.Utilities.PatientUtility") ?? AccessTools.TypeByName("Hospital.PatientUtility");
            if (patientUtility != null)
            {
                // Hospital has moved helper classes between releases, so use reflection and patch only
                // methods that exist in the active build instead of binding to one exact version.
                foreach (MethodInfo method in patientUtility.GetMethods(AccessTools.all).Where(m => m.Name == "SetUpNewPatient" || m.Name == "Arrive"))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(OptionalPatchUtility), postfix: nameof(OptionalPatchUtility.SuitPawnsInArgsPostfix));
                }
                MethodInfo landing = patientUtility.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "TryFindSafeLandingSpotCloseToColony" && m.ReturnType == typeof(IntVec3));
                OptionalModPatches.PatchIfExists(harmony, landing, typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalLandingSpotPostfix));
            }
            Type surgeryUtility = AccessTools.TypeByName("Hospital.Utilities.SurgeryUtility") ?? AccessTools.TypeByName("Hospital.SurgeryUtility");
            if (surgeryUtility != null)
            {
                MethodInfo addRandomSurgeryBill = AccessTools.Method(surgeryUtility, "AddRandomSurgeryBill");
                if (addRandomSurgeryBill != null)
                {
                    try
                    {
                        harmony.Patch(addRandomSurgeryBill,
                            prefix: new HarmonyMethod(typeof(HospitalPatchHandlers), nameof(HospitalPatchHandlers.SurgeryAddRandomBillPrefix)),
                            transpiler: new HarmonyMethod(typeof(HospitalPatchHandlers), nameof(HospitalPatchHandlers.SurgeryMapHeldFallbackTranspiler)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[Space Services] Could not patch Hospital surgery map fallback: " + ex.Message);
                    }
                }
            }
            Type incidentHelper = AccessTools.TypeByName("Hospital.IncidentHelper");
            if (incidentHelper != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(incidentHelper, "CanSpawnPatient"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalCanSpawnPatientPostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(incidentHelper, "TryFindEntryCell"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalTryFindEntryCellPostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(incidentHelper, "SetUpNewPatient"), typeof(OptionalPatchUtility), postfix: nameof(OptionalPatchUtility.SuitPawnsInArgsPostfix));
            }
            Type patientArrival = AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives");
            if (patientArrival != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(patientArrival, "TryExecuteWorker"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePostfix), finalizer: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecuteFinalizer));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(patientArrival, "SpawnPatient"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPostfix), finalizer: nameof(HospitalPatchHandlers.HospitalSpawnPatientFinalizer));
            }
            Type massCasualty = AccessTools.TypeByName("Hospital.IncidentWorker_MassCasualtyEvent");
            if (massCasualty != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(massCasualty, "TryExecuteWorker"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(HospitalPatchHandlers.HospitalMassCasualtyTryExecutePostfix), finalizer: nameof(HospitalPatchHandlers.HospitalMassCasualtyTryExecuteFinalizer));
                // Mass casualty SpawnPatient has changed signatures; patch every overload and inspect args at runtime.
                foreach (MethodInfo method in massCasualty.GetMethods(AccessTools.all).Where(m => m.Name == "SpawnPatient"))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPostfix), finalizer: nameof(HospitalPatchHandlers.HospitalSpawnPatientFinalizer));
                }
            }
            Type hospitalComponent = AccessTools.TypeByName("Hospital.HospitalMapComponent");
            if (hospitalComponent != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeaves"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientDeparturePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "DismissPatient"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientDeparturePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeftTheMap"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientGonePrefix), postfix: nameof(HospitalPatchHandlers.HospitalPatientGonePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientDied"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientDiedPrefix), postfix: nameof(HospitalPatchHandlers.HospitalPatientDiedPostfix));
            }
            Type sentAwayTrigger = AccessTools.TypeByName("Hospital.Trigger_SentAway");
            if (sentAwayTrigger != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(sentAwayTrigger, "SentAway"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalSentAwayPostfix));
            }
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(HealthAIUtility), "ShouldSeekMedicalRest", new[] { typeof(Pawn) }), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.ShouldSeekMedicalRestPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(HealthAIUtility), "ShouldSeekMedicalRestUrgent", new[] { typeof(Pawn) }), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.ShouldSeekMedicalRestPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(JobGiver_PatientGoToBed), "TryGiveJob"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.PatientGoToBedPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(DropPodUtility), "MakeDropPodAt", new[] { typeof(IntVec3), typeof(Map), typeof(ActiveTransporterInfo), typeof(Faction) }), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalDropPodAtPrefix));
            if (incidentHelper != null || patientArrival != null || massCasualty != null)
            {
                ServiceDebugUtility.Log(ServiceLogIntegration.Hospital, "Hospital bridge patches installed.");
            }
        }
    }
}
