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
        public static void Install(Harmony harmony)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospital)
            {
                return;
            }
            Type patientUtility = AccessTools.TypeByName("Hospital.Utilities.PatientUtility") ?? AccessTools.TypeByName("Hospital.PatientUtility");
            if (patientUtility != null)
            {
                foreach (MethodInfo method in patientUtility.GetMethods(AccessTools.all).Where(m => m.Name == "SetUpNewPatient" || m.Name == "Arrive"))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(OptionalPatchUtility), postfix: nameof(OptionalPatchUtility.SuitPawnsInArgsPostfix));
                }
                MethodInfo landing = patientUtility.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "TryFindSafeLandingSpotCloseToColony" && m.ReturnType == typeof(IntVec3));
                OptionalModPatches.PatchIfExists(harmony, landing, typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalLandingSpotPostfix));
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
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(patientArrival, "TryExecuteWorker"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(patientArrival, "SpawnPatient"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPostfix));
            }
            Type massCasualty = AccessTools.TypeByName("Hospital.IncidentWorker_MassCasualtyEvent");
            if (massCasualty != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(massCasualty, "TryExecuteWorker"), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(HospitalPatchHandlers.HospitalMassCasualtyTryExecutePostfix));
                foreach (MethodInfo method in massCasualty.GetMethods(AccessTools.all).Where(m => m.Name == "SpawnPatient"))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(HospitalPatchHandlers.HospitalSpawnPatientPostfix));
                }
            }
            Type hospitalComponent = AccessTools.TypeByName("Hospital.HospitalMapComponent");
            if (hospitalComponent != null)
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeaves"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientDeparturePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "DismissPatient"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientDeparturePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeftTheMap"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientGonePostfix));
                OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientDied"), typeof(HospitalPatchHandlers), postfix: nameof(HospitalPatchHandlers.HospitalPatientGonePostfix));
            }
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(DropPodUtility), "MakeDropPodAt", new[] { typeof(IntVec3), typeof(Map), typeof(ActiveTransporterInfo), typeof(Faction) }), typeof(HospitalPatchHandlers), prefix: nameof(HospitalPatchHandlers.HospitalDropPodAtPrefix));
            if (incidentHelper != null || patientArrival != null || massCasualty != null)
            {
                Log.Message("[Space Services] Hospital bridge patches installed.");
            }
        }
    }
}
