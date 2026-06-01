using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public static class ServiceMedPodUtility
    {
        private const int FailedJobSearchCooldownTicks = 1000;
        private const int AssistCooldownTicks = 1000;
        private static readonly Dictionary<int, int> NextMedPodSearchTickByPawnId = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> NextMedPodAssistTickByPawnId = new Dictionary<int, int>();

        public static void TickMapServicePawns(Map map)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.medPodServiceBridge)
            {
                return;
            }
            if (map == null || map.mapPawns == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                TryStartMedPodJob(pawn);
            }
        }

        public static void TickServiceMedPodAssist(Map map, ServiceGroupRecord record)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.medPodServiceBridge)
            {
                return;
            }
            if (map == null || record == null || record.pawns == null || record.state != "arrived")
            {
                return;
            }
            if (record.serviceKind != "hospital" && record.serviceKind != "hospitality")
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                TryStartMedPodJob(pawn);
            }
        }

        public static bool TryMakeMedPodJob(Pawn pawn, out Job job)
        {
            job = null;
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.medPodServiceBridge)
            {
                return false;
            }
            if (!CanServicePawnSeekMedPod(pawn))
            {
                return false;
            }

            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (NextMedPodSearchTickByPawnId.TryGetValue(pawn.thingIDNumber, out int nextTick) && tick < nextTick)
            {
                return false;
            }

            Thing medPod = FindBestMedPod(pawn);
            if (medPod == null)
            {
                NextMedPodSearchTickByPawnId[pawn.thingIDNumber] = tick + FailedJobSearchCooldownTicks;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-no-pod-" + pawn.thingIDNumber, "MedPod assist found no valid MedPod for " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
                return false;
            }

            JobDef jobDef = DefDatabase<JobDef>.GetNamedSilentFail("PatientGoToMedPod");
            if (jobDef == null)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "missing-medpod-jobdef", "MedPod is active but PatientGoToMedPod JobDef was not found.", GenDate.TicksPerHour);
                return false;
            }

            job = JobMaker.MakeJob(jobDef, medPod);
            job.ignoreJoyTimeAssignment = true;
            job.expiryInterval = GenDate.TicksPerHour;
            ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Routed service pawn to MedPod: " + ServiceDebugUtility.PawnAuditSummary(pawn) + " pod=" + medPod.ThingID);
            return true;
        }

        public static bool TryStartMedPodJob(Pawn pawn)
        {
            if (pawn == null || pawn.jobs == null || pawn.Downed || pawn.InMentalState || pawn.CurJobDef == DefDatabase<JobDef>.GetNamedSilentFail("PatientGoToMedPod"))
            {
                return false;
            }

            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (NextMedPodAssistTickByPawnId.TryGetValue(pawn.thingIDNumber, out int nextTick) && tick < nextTick)
            {
                return false;
            }
            NextMedPodAssistTickByPawnId[pawn.thingIDNumber] = tick + AssistCooldownTicks;

            if (!TryMakeMedPodJob(pawn, out Job job))
            {
                return false;
            }

            // Hospital and Hospitality may leave a service pawn resting in a normal bed.
            // The bridge is opt-in, so gently override that job when MedPod says a pod is valid.
            bool started = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (started)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-assist-start-" + pawn.thingIDNumber, "MedPod assist started job for " + ServiceDebugUtility.PawnAuditSummary(pawn), GenDate.TicksPerHour);
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "MedPod assist started job for " + ServiceDebugUtility.PawnAuditSummary(pawn));
            }
            return started;
        }

        private static bool CanServicePawnSeekMedPod(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.MapHeld == null || pawn.CurJobDef == DefDatabase<JobDef>.GetNamedSilentFail("PatientGoToMedPod"))
            {
                return false;
            }
            if (!SpaceServiceMapDetector.IsServiceEligible(pawn.MapHeld))
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
            System.Collections.IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as System.Collections.IDictionary;
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

        private static Thing FindBestMedPod(Pawn pawn)
        {
            Type restUtility = AccessTools.TypeByName("MedPod.MedPodRestUtility");
            MethodInfo method = restUtility == null ? null : AccessTools.Method(restUtility, "FindBestMedPod");
            if (method == null)
            {
                return null;
            }
            try
            {
                return method.Invoke(null, new object[] { pawn, pawn }) as Thing;
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-find-failed", "MedPod lookup failed for service pawn: " + ex.GetType().Name + " " + ex.Message, GenDate.TicksPerHour);
                return null;
            }
        }

    }

    public sealed class JobGiver_ServicePawnGoToMedPod : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            return ServiceMedPodUtility.TryMakeMedPodJob(pawn, out Job job) ? job : null;
        }
    }
}
