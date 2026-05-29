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
    public static class HospitalIncidentGate
    {
        public static bool CanAcceptHospitalIncident(string incidentDefName, Map map)
        {
            object hospital = FindHospitalComponent(map);
            if (hospital == null)
            {
                return false;
            }
            if (!CallBool(hospital, "IsOpen", true))
            {
                return false;
            }
            if (incidentDefName == "MassCasualtyEvent" && !Reflect.BoolMember(hospital, "MassCasualties", true))
            {
                return false;
            }
            int freeBeds = CallInt(hospital, "FreeMedicalBeds", -1);
            if (freeBeds == 0 || CallBool(hospital, "IsFull", false))
            {
                return false;
            }
            // Hospital mass casualties overfill hard; require room for the whole minimum batch.
            if (incidentDefName == "MassCasualtyEvent" && freeBeds < 3)
            {
                return false;
            }
            if (!Reflect.BoolMember(hospital, "AcceptDanger", false) && HospitalDangersOnMap(map))
            {
                return false;
            }
            if (SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.CountServicePads(map, ServiceUse.Patient) <= 0)
            {
                return false;
            }
            return true;
        }

        public static string ReadinessReport(string incidentDefName, Map map)
        {
            object hospital = FindHospitalComponent(map);
            if (hospital == null)
            {
                return "no HospitalMapComponent";
            }
            return "open=" + CallBool(hospital, "IsOpen", true) +
                ", freeBeds=" + CallInt(hospital, "FreeMedicalBeds", -1) +
                ", bedCount=" + CallInt(hospital, "BedCount", -1) +
                ", full=" + CallBool(hospital, "IsFull", false) +
                ", freePatientPads=" + ServicePadUtility.CountServicePads(map, ServiceUse.Patient) +
                ", massCasualties=" + Reflect.BoolMember(hospital, "MassCasualties", true) +
                ", acceptDanger=" + Reflect.BoolMember(hospital, "AcceptDanger", false) +
                ", danger=" + HospitalDangersOnMap(map);
        }

        public static object FindHospitalComponent(Map map)
        {
            if (map == null || map.components == null)
            {
                return null;
            }
            return map.components.FirstOrDefault(comp => comp != null && (comp.GetType().FullName ?? "") == "Hospital.HospitalMapComponent");
        }

        private static bool HospitalDangersOnMap(Map map)
        {
            Type patientUtility = AccessTools.TypeByName("Hospital.Utilities.PatientUtility");
            MethodInfo method = patientUtility == null ? null : AccessTools.Method(patientUtility, "DangersOnMap");
            if (method == null)
            {
                return GenHostility.AnyHostileActiveThreatToPlayer(map, true);
            }

            object[] args = { map, null };
            try
            {
                object result = method.Invoke(null, args);
                return result is bool && (bool)result;
            }
            catch
            {
                return GenHostility.AnyHostileActiveThreatToPlayer(map, true);
            }
        }

        private static bool CallBool(object target, string methodName, bool fallback)
        {
            MethodInfo method = AccessTools.Method(target.GetType(), methodName);
            if (method == null)
            {
                return fallback;
            }
            try
            {
                object result = method.Invoke(target, null);
                return result is bool ? (bool)result : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static int CallInt(object target, string methodName, int fallback)
        {
            MethodInfo method = AccessTools.Method(target.GetType(), methodName);
            if (method == null)
            {
                return fallback;
            }
            try
            {
                object result = method.Invoke(target, null);
                return result is int ? (int)result : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

}
