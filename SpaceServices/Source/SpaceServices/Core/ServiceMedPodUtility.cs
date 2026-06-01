using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public static class ServiceMedPodUtility
    {
        private const int FailedJobSearchCooldownTicks = 250;
        private static readonly Dictionary<int, int> NextMedPodCheckTickByPawnId = new Dictionary<int, int>();

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
            if (NextMedPodCheckTickByPawnId.TryGetValue(pawn.thingIDNumber, out int nextTick) && tick < nextTick)
            {
                return false;
            }

            Thing medPod = FindBestMedPod(pawn);
            if (medPod == null)
            {
                NextMedPodCheckTickByPawnId[pawn.thingIDNumber] = tick + FailedJobSearchCooldownTicks;
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

        public static int ExtraHospitalMedPodCapacity(Map map)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.medPodServiceBridge)
            {
                return 0;
            }
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return 0;
            }

            int count = 0;
            foreach (Thing medPod in AllMedPods(map))
            {
                if (!BasicUsableMedPod(medPod))
                {
                    continue;
                }
                if (HospitalCountsBed(medPod))
                {
                    continue;
                }
                count++;
            }
            return count;
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
            if (!ServiceLifecycleUtility.TryFindRecordForPawn(pawn, out _, out ServiceGroupRecord record))
            {
                return false;
            }
            if (record == null || record.state != "arrived")
            {
                return false;
            }
            return record.serviceKind == "hospital" || record.serviceKind == "hospitality";
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

        private static IEnumerable<Thing> AllMedPods(Map map)
        {
            if (map == null || map.listerThings == null)
            {
                yield break;
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (IsMedPod(thing))
                {
                    yield return thing;
                }
            }
        }

        private static bool IsMedPod(Thing thing)
        {
            return thing != null && (thing.GetType().FullName ?? "") == "MedPod.Building_BedMedPod";
        }

        private static bool BasicUsableMedPod(Thing medPod)
        {
            Building_Bed bed = medPod as Building_Bed;
            if (bed == null || !bed.Spawned || bed.Destroyed || !bed.Medical || bed.ForPrisoners || bed.def == null || bed.def.building == null || !bed.def.building.bed_humanlike)
            {
                return false;
            }
            if (bed.IsBurning() || bed.IsBrokenDown() || !Reflect.BoolMember(bed, "allowGuests", false))
            {
                return false;
            }
            CompPowerTrader power = bed.TryGetComp<CompPowerTrader>();
            return power == null || power.PowerOn;
        }

        private static bool HospitalCountsBed(Thing medPod)
        {
            ThingWithComps thingWithComps = medPod as ThingWithComps;
            ThingComp comp = thingWithComps == null || thingWithComps.AllComps == null ? null : thingWithComps.AllComps.FirstOrDefault(candidate => candidate != null && (candidate.GetType().FullName ?? "") == "Hospital.CompHospitalBed");
            return comp != null && Reflect.BoolMember(comp, "Hospital", false);
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
