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
    public static class OptionalModPatches
    {
        public static void Install(Harmony harmony)
        {
            TryPatchSpaceports(harmony);
            TryPatchHospital(harmony);
            TryPatchHospitality(harmony);
        }

        private static void TryPatchSpaceports(Harmony harmony)
        {
            Type utils = AccessTools.TypeByName("Spaceports.Utils");
            if (utils == null)
            {
                return;
            }
            PatchIfExists(harmony, AccessTools.Method(utils, "IsMapInSpace"), postfix: nameof(OptionalPatchHandlers.SpaceportsIsMapInSpacePostfix));
            PatchIfExists(harmony, AccessTools.Method(utils, "SuitUpPawns"), prefix: nameof(OptionalPatchHandlers.SpaceportsSuitUpPawnsPrefix));
            PatchIfExists(harmony, AccessTools.Method(utils, "HospitalityShuttleCheck"), postfix: nameof(OptionalPatchHandlers.SpaceportsHospitalityShuttleCheckPostfix));
            PatchIfExists(harmony, AccessTools.Method(utils, "CheckIfClearForLanding"), postfix: nameof(OptionalPatchHandlers.SpaceportsCheckIfClearForLandingPostfix));
            Log.Message("[Space Services] Spaceports bridge patches installed.");
        }

        private static void TryPatchHospital(Harmony harmony)
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
                    PatchIfExists(harmony, method, postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
                }
                MethodInfo landing = patientUtility.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "TryFindSafeLandingSpotCloseToColony" && m.ReturnType == typeof(IntVec3));
                PatchIfExists(harmony, landing, postfix: nameof(OptionalPatchHandlers.HospitalLandingSpotPostfix));
            }
            Type incidentHelper = AccessTools.TypeByName("Hospital.IncidentHelper");
            if (incidentHelper != null)
            {
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "CanSpawnPatient"), postfix: nameof(OptionalPatchHandlers.HospitalCanSpawnPatientPostfix));
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "TryFindEntryCell"), postfix: nameof(OptionalPatchHandlers.HospitalTryFindEntryCellPostfix));
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "SetUpNewPatient"), postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
            }
            Type patientArrival = AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives");
            if (patientArrival != null)
            {
                PatchIfExists(harmony, AccessTools.Method(patientArrival, "TryExecuteWorker"), prefix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePostfix));
                PatchIfExists(harmony, AccessTools.Method(patientArrival, "SpawnPatient"), prefix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPostfix));
            }
            Type massCasualty = AccessTools.TypeByName("Hospital.IncidentWorker_MassCasualtyEvent");
            if (massCasualty != null)
            {
                PatchIfExists(harmony, AccessTools.Method(massCasualty, "TryExecuteWorker"), prefix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(OptionalPatchHandlers.HospitalMassCasualtyTryExecutePostfix));
                foreach (MethodInfo method in massCasualty.GetMethods(AccessTools.all).Where(m => m.Name == "SpawnPatient"))
                {
                    PatchIfExists(harmony, method, prefix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPostfix));
                }
            }
            Type hospitalComponent = AccessTools.TypeByName("Hospital.HospitalMapComponent");
            if (hospitalComponent != null)
            {
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeaves"), postfix: nameof(OptionalPatchHandlers.HospitalPatientDeparturePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "DismissPatient"), postfix: nameof(OptionalPatchHandlers.HospitalPatientDeparturePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeftTheMap"), postfix: nameof(OptionalPatchHandlers.HospitalPatientGonePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientDied"), postfix: nameof(OptionalPatchHandlers.HospitalPatientGonePostfix));
            }
            PatchIfExists(harmony, AccessTools.Method(typeof(DropPodUtility), "MakeDropPodAt", new[] { typeof(IntVec3), typeof(Map), typeof(ActiveTransporterInfo), typeof(Faction) }), prefix: nameof(OptionalPatchHandlers.HospitalDropPodAtPrefix));
            if (incidentHelper != null || patientArrival != null || massCasualty != null)
            {
                Log.Message("[Space Services] Hospital bridge patches installed.");
            }
        }

        private static void TryPatchHospitality(Harmony harmony)
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
                AccessTools.TypeByName("Hospitality.Spacer.IncidentWorker_VisitorGroupSpacer")
            };
            foreach (Type type in types.Where(t => t != null))
            {
                foreach (MethodInfo method in type.GetMethods(AccessTools.all).Where(m => m.DeclaringType == type && (m.Name == "TryDropSpawn" || m.Name == "SpawnGroup" || m.Name == "SpawnPawns" || m.Name == "SpawnVisitor" || m.Name == "GeneratePawns")))
                {
                    PatchIfExists(harmony, method, postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
                }
            }
        }

        private static void PatchIfExists(Harmony harmony, MethodInfo method, string prefix = null, string postfix = null)
        {
            if (method == null)
            {
                return;
            }
            try
            {
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(OptionalPatchHandlers), prefix);
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(OptionalPatchHandlers), postfix);
                harmony.Patch(method, pre, post, null);
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not patch " + method.FullDescription() + ": " + ex.Message);
            }
        }
    }

}
