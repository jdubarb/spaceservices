using HarmonyLib;
using System;
using System.Collections;
using Verse;

namespace SpaceServices
{
    public static class ServiceMedPodUtility
    {
        public static bool ShouldBypassMedicalCareGate(Pawn pawn)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.medPodServiceBridge)
            {
                return false;
            }
            return IsActiveServicePawn(pawn);
        }

        private static bool IsActiveServicePawn(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.MapHeld == null)
            {
                return false;
            }
            if (!SpaceServiceMapDetector.IsServiceActive(pawn.MapHeld))
            {
                return false;
            }
            if (ServicePawnUtility.IsPlayerOwnedPawn(pawn))
            {
                return false;
            }
            if (!ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out _, out ServiceGroupRecord record))
            {
                return IsActiveHospitalPatient(pawn) || IsActiveHospitalityGuest(pawn);
            }
            if (record == null || record.state != "arrived")
            {
                return false;
            }
            return record.serviceKind == "hospital" || record.serviceKind == "hospitality";
        }

        private static bool IsActiveHospitalPatient(Pawn pawn)
        {
            if (pawn == null || pawn.MapHeld == null)
            {
                return false;
            }
            object hospital = HospitalIncidentGate.FindHospitalComponent(pawn.MapHeld);
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            return patients != null && patients.Contains(pawn);
        }

        private static bool IsActiveHospitalityGuest(Pawn pawn)
        {
            object comp = CompGuest(pawn);
            if (comp == null)
            {
                return false;
            }
            return Reflect.BoolMember(comp, "arrived") && !Reflect.BoolMember(comp, "sentAway");
        }

        private static object CompGuest(Pawn pawn)
        {
            if (pawn == null || pawn.AllComps == null)
            {
                return null;
            }
            Type compType = AccessTools.TypeByName("Hospitality.CompGuest");
            foreach (ThingComp comp in pawn.AllComps)
            {
                if (comp == null)
                {
                    continue;
                }
                Type type = comp.GetType();
                if ((compType != null && compType.IsAssignableFrom(type)) || type.FullName == "Hospitality.CompGuest")
                {
                    return comp;
                }
            }
            return null;
        }
    }
}
