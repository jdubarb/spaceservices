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
        private const int MinimumMassCasualtyPatients = 3;

        public static bool CanAcceptHospitalIncident(string incidentDefName, Map map, bool applyPriorityThrottle = true, IncidentParms parms = null)
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
            if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospital", out _))
            {
                return false;
            }
            int freeBeds = EffectiveFreeMedicalBeds(hospital, map);
            if (freeBeds == 0 || (CallBool(hospital, "IsFull", false) && freeBeds <= 0))
            {
                return false;
            }
            if (incidentDefName == "MassCasualtyEvent" && freeBeds < MinimumMassCasualtyPatients)
            {
                return false;
            }
            int incomingPatients = IncomingPatientCount(incidentDefName, freeBeds, parms);
            if (!ServiceDebugLimits.HospitalAllows(map, incidentDefName, incomingPatients, out _))
            {
                return false;
            }
            if (!Reflect.BoolMember(hospital, "AcceptDanger", false) && HospitalDangersOnMap(map))
            {
                return false;
            }
            if (SpaceServiceMapDetector.IsServiceActive(map) && ServicePadUtility.CountServicePads(map, ServiceUse.Patient) <= 0)
            {
                return false;
            }
            if (SpaceServiceMapDetector.IsServiceActive(map) && applyPriorityThrottle && !ServicePadUtility.PriorityThrottleAllows(map, ServiceUse.Patient, out _))
            {
                return false;
            }
            return true;
        }

        public static bool TryPrepareMassCasualtyPawnCount(Map map, IncidentParms parms, out string reason)
        {
            reason = null;
            if (map == null || parms == null)
            {
                reason = "missing map or incident parms";
                return false;
            }

            int freeBeds = EffectiveFreeMedicalBeds(map);
            if (freeBeds < MinimumMassCasualtyPatients)
            {
                reason = "not enough free medical beds, need " + MinimumMassCasualtyPatients + " (freeBeds=" + freeBeds + ")";
                return false;
            }

            int requested = parms.pawnCount;
            parms.pawnCount = requested > 0 ? Mathf.Min(requested, freeBeds) : freeBeds;
            return true;
        }

        public static string ReadinessReport(string incidentDefName, Map map)
        {
            object hospital = FindHospitalComponent(map);
            if (hospital == null)
            {
                return "no HospitalMapComponent";
            }
            if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospital", out string trafficReason))
            {
                return trafficReason;
            }
            return "open=" + CallBool(hospital, "IsOpen", true) +
                ", freeBeds=" + EffectiveFreeMedicalBeds(hospital, map) +
                ", nativeFreeBeds=" + CallInt(hospital, "FreeMedicalBeds", -1) +
                ", bedCount=" + CallInt(hospital, "BedCount", -1) +
                ", full=" + CallBool(hospital, "IsFull", false) +
                ", freePatientPads=" + ServicePadUtility.CountServicePads(map, ServiceUse.Patient) +
                ", patientPadPriority=" + ServicePadUtility.PriorityReadinessReport(map, ServiceUse.Patient) +
                ", massCasualties=" + Reflect.BoolMember(hospital, "MassCasualties", true) +
                ", acceptDanger=" + Reflect.BoolMember(hospital, "AcceptDanger", false) +
                ", danger=" + HospitalDangersOnMap(map) +
                DebugLimitReport(map, incidentDefName);
        }

        private static string DebugLimitReport(Map map, string incidentDefName)
        {
            int freeBeds = EffectiveFreeMedicalBeds(map);
            int incomingPatients = IncomingPatientCount(incidentDefName, freeBeds, null);
            return ServiceDebugLimits.HospitalAllows(map, incidentDefName, incomingPatients, out string reason) ? "" : ", " + reason;
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

        public static int EffectiveFreeMedicalBeds(Map map)
        {
            object hospital = FindHospitalComponent(map);
            return hospital == null ? -1 : EffectiveFreeMedicalBeds(hospital, map);
        }

        private static int EffectiveFreeMedicalBeds(object hospital, Map map)
        {
            // Hospital's own hospital-bed flag remains authoritative, including for MedPods.
            return CallInt(hospital, "FreeMedicalBeds", -1);
        }

        private static int IncomingPatientCount(string incidentDefName, int freeBeds, IncidentParms parms)
        {
            if (incidentDefName == "MassCasualtyEvent")
            {
                if (parms != null && parms.pawnCount > 0)
                {
                    return Mathf.Min(parms.pawnCount, Mathf.Max(1, freeBeds));
                }
                return Mathf.Max(1, freeBeds);
            }
            return 1;
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
