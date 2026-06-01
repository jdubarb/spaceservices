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

            Thing medPod = FindBestMedPod(pawn) ?? FindBestServiceMedPod(pawn);
            if (medPod == null)
            {
                NextMedPodSearchTickByPawnId[pawn.thingIDNumber] = tick + FailedJobSearchCooldownTicks;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-no-pod-" + pawn.thingIDNumber, "MedPod assist found no valid MedPod for " + ServiceDebugUtility.PawnAuditSummary(pawn) + " " + MedPodMapSummary(pawn), GenDate.TicksPerHour);
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

        private static Thing FindBestServiceMedPod(Pawn pawn)
        {
            Map map = pawn == null ? null : pawn.MapHeld;
            Type restUtility = AccessTools.TypeByName("MedPod.MedPodRestUtility");
            MethodInfo validator = restUtility == null ? null : AccessTools.Method(restUtility, "IsValidMedPodFor");
            if (map == null || validator == null)
            {
                return null;
            }

            Thing best = null;
            float bestDistance = float.MaxValue;
            foreach (Thing medPod in AllMedPods(map))
            {
                if (!ValidServiceMedPod(validator, medPod, pawn, out _))
                {
                    continue;
                }
                float distance = pawn.Position.DistanceToSquared(medPod.Position);
                if (distance < bestDistance)
                {
                    best = medPod;
                    bestDistance = distance;
                }
            }
            if (best != null)
            {
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-service-lookup-" + pawn.thingIDNumber, "Space Services selected MedPod by service lookup for " + ServiceDebugUtility.PawnAuditSummary(pawn) + " pod=" + best.ThingID, GenDate.TicksPerHour);
            }
            return best;
        }

        private static bool ValidServiceMedPod(MethodInfo validator, Thing medPod, Pawn pawn)
        {
            foreach (object guestStatus in GuestStatusCandidates(pawn))
            {
                try
                {
                    object result = validator.Invoke(null, new[] { medPod, pawn, pawn, guestStatus });
                    if (result is bool && (bool)result)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "medpod-validator-failed", "MedPod validator failed during service lookup: " + ex.GetType().Name + " " + ex.Message, GenDate.TicksPerHour);
                    return false;
                }
            }
            return false;
        }

        private static string MedPodRejectReason(Thing medPod, Pawn pawn)
        {
            Type restUtility = AccessTools.TypeByName("MedPod.MedPodRestUtility");
            Type healthUtility = AccessTools.TypeByName("MedPod.MedPodHealthAIUtility");
            MethodInfo validator = restUtility == null ? null : AccessTools.Method(restUtility, "IsValidMedPodFor");
            if (validator != null && ValidServiceMedPod(validator, medPod, pawn, out object passingGuestStatus))
            {
                return "valid guestStatus=" + (passingGuestStatus ?? "null");
            }

            Building_Bed bed = medPod as Building_Bed;
            CompPowerTrader power = medPod.TryGetComp<CompPowerTrader>();
            if (medPod == null)
            {
                return "null";
            }
            if (power != null && !power.PowerOn)
            {
                return "power off";
            }
            if (medPod.IsForbidden(pawn))
            {
                return "forbidden";
            }
            if (!pawn.CanReserve(medPod))
            {
                return "cannot reserve";
            }
            if (!pawn.CanReach(medPod, PathEndMode.OnCell, Danger.Deadly))
            {
                return "cannot reach";
            }
            if (bed != null && !RestUtility.CanUseBedEver(pawn, bed.def))
            {
                return "CanUseBedEver false";
            }
            MethodInfo userType = restUtility == null ? null : AccessTools.Method(restUtility, "IsValidBedForUserType");
            if (userType != null && !CallBool(userType, medPod, pawn))
            {
                return "invalid bed user type";
            }
            MethodInfo shouldSeek = healthUtility == null ? null : AccessTools.Method(healthUtility, "ShouldSeekMedPodRest");
            if (shouldSeek != null && !CallBool(shouldSeek, pawn, medPod))
            {
                return "ShouldSeekMedPodRest false";
            }
            MethodInfo medicalCare = healthUtility == null ? null : AccessTools.Method(healthUtility, "HasAllowedMedicalCareCategory");
            if (medicalCare != null && !CallBool(medicalCare, pawn))
            {
                return "medical care category disallows MedPod";
            }
            MethodInfo race = healthUtility == null ? null : AccessTools.Method(healthUtility, "IsValidRaceForMedPod");
            if (race != null && !CallBool(race, pawn, Reflect.GetMember(medPod, "DisallowedRaces")))
            {
                return "race blocked";
            }
            MethodInfo xenotype = healthUtility == null ? null : AccessTools.Method(healthUtility, "IsValidXenotypeForMedPod");
            if (xenotype != null && !CallBool(xenotype, pawn, Reflect.GetMember(medPod, "DisallowedXenotypes")))
            {
                return "xenotype blocked";
            }
            MethodInfo hediffs = healthUtility == null ? null : AccessTools.Method(healthUtility, "HasUsageBlockingHediffs");
            if (hediffs != null && CallBool(hediffs, pawn, Reflect.GetMember(medPod, "UsageBlockingHediffs")))
            {
                return "usage-blocking hediff";
            }
            MethodInfo traits = healthUtility == null ? null : AccessTools.Method(healthUtility, "HasUsageBlockingTraits");
            if (traits != null && CallBool(traits, pawn, Reflect.GetMember(medPod, "UsageBlockingTraits")))
            {
                return "usage-blocking trait";
            }
            if (Reflect.BoolMember(medPod, "Aborted", false))
            {
                return "aborted";
            }
            if (medPod.IsBurning())
            {
                return "burning";
            }
            if (medPod.IsBrokenDown())
            {
                return "broken down";
            }
            return "unknown MedPod validator rejection";
        }

        private static bool ValidServiceMedPod(MethodInfo validator, Thing medPod, Pawn pawn, out object passingGuestStatus)
        {
            passingGuestStatus = null;
            foreach (object guestStatus in GuestStatusCandidates(pawn))
            {
                try
                {
                    object result = validator.Invoke(null, new[] { medPod, pawn, pawn, guestStatus });
                    if (result is bool && (bool)result)
                    {
                        passingGuestStatus = guestStatus;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static bool CallBool(MethodInfo method, params object[] args)
        {
            try
            {
                object result = method.Invoke(null, args);
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<object> GuestStatusCandidates(Pawn pawn)
        {
            object status = pawn == null || pawn.guest == null ? null : Reflect.GetMember(pawn.guest, "GuestStatus");
            if (status != null)
            {
                yield return status;
            }
            yield return GuestStatus.Guest;
            yield return null;
        }

        private static IEnumerable<Thing> AllMedPods(Map map)
        {
            if (map == null || map.listerThings == null)
            {
                yield break;
            }
            Type medPodType = AccessTools.TypeByName("MedPod.Building_BedMedPod");
            foreach (Thing thing in map.listerThings.AllThings)
            {
                Type type = thing == null ? null : thing.GetType();
                if (type != null && ((medPodType != null && medPodType.IsAssignableFrom(type)) || type.FullName == "MedPod.Building_BedMedPod"))
                {
                    yield return thing;
                }
            }
        }

        private static string MedPodMapSummary(Pawn pawn)
        {
            Map map = pawn == null ? null : pawn.MapHeld;
            if (map == null)
            {
                return "medPods=no-map";
            }
            List<string> parts = new List<string>();
            foreach (Thing medPod in AllMedPods(map))
            {
                Building_Bed bed = medPod as Building_Bed;
                CompPowerTrader power = medPod.TryGetComp<CompPowerTrader>();
                parts.Add(medPod.ThingID +
                    ": spawned=" + medPod.Spawned +
                    ", forbidden=" + medPod.IsForbidden(pawn) +
                    ", reach=" + pawn.CanReach(medPod, PathEndMode.OnCell, Danger.Deadly) +
                    ", medical=" + (bed != null && bed.Medical) +
                    ", prisoner=" + (bed != null && bed.ForPrisoners) +
                    ", allowGuests=" + Reflect.BoolMember(medPod, "allowGuests", false) +
                    ", power=" + (power == null || power.PowerOn) +
                    ", reserve=" + pawn.CanReserve(medPod) +
                    ", reject=" + MedPodRejectReason(medPod, pawn));
            }
            return "medPods=[" + string.Join("; ", parts.ToArray()) + "]";
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
