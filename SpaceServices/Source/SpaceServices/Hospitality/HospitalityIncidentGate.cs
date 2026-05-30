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
    public static class HospitalityIncidentGate
    {
        public static bool CanAcceptHospitalityIncident(string incidentDefName, Map map)
        {
            return CanAcceptHospitalityIncident(incidentDefName, map, null);
        }

        public static bool CanAcceptHospitalityIncident(string incidentDefName, Map map, object worker)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return false;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return false;
            }
            if (ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return false;
            }
            return !RequiresGuestBedCapacity() || HospitalityBedUtility.Report(map).freeBeds >= EstimatedBedDemand(incidentDefName, worker);
        }

        public static string ReadinessReport(string incidentDefName, Map map)
        {
            return ReadinessReport(incidentDefName, map, null);
        }

        public static string ReadinessReport(string incidentDefName, Map map, object worker)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return "Hospitality support disabled in Space Services settings";
            }
            if (map == null)
            {
                return "no target map";
            }
            SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(map);
            if (!eligibility.allowed)
            {
                return string.Join(", ", eligibility.blockReasons.ToArray());
            }
            if (ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return "no usable guest service pad";
            }
            if (RequiresGuestBedCapacity())
            {
                HospitalityBedReport beds = HospitalityBedUtility.Report(map);
                int demand = EstimatedBedDemand(incidentDefName, worker);
                if (beds.freeBeds < demand)
                {
                    return "not enough free guest beds, need " + demand + " (" + beds.ToSummary() + ")";
                }
            }
            return "ready";
        }

        public static bool RequiresGuestBedCapacity()
        {
            return SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.hospitalityRequireGuestBeds;
        }

        public static int EstimatedBedDemand(string incidentDefName, object worker)
        {
            int max = IntMember(worker, "maxGuestGroupSize");
            if (max > 0)
            {
                return max;
            }
            return 1;
        }

        private static int IntMember(object obj, string name)
        {
            object value = Reflect.GetMember(obj, name);
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }
    }
}
