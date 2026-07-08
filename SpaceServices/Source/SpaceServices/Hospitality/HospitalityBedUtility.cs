using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class HospitalityBedUtility
    {
        private const string GuestBedTypeName = "Hospitality.Building_GuestBed";
        private const string CompGuestTypeName = "Hospitality.CompGuest";
        private static bool checkedDelayGuestCreateLordMethod;
        private static MethodInfo delayGuestCreateLordMethod;

        public static bool HasFreeGuestBed(Map map)
        {
            return Report(map).freeBeds > 0;
        }

        public static HospitalityBedReport Report(Map map)
        {
            HospitalityBedReport report = new HospitalityBedReport();
            foreach (Thing bed in GuestBeds(map))
            {
                report.totalBeds++;
                if (AssignedGuestCount(bed) > 0)
                {
                    report.assignedBeds++;
                }
                else
                {
                    report.freeBeds++;
                }
            }
            return report;
        }

        public static bool TryFindBedlessServiceGuest(ServiceGroupRecord record, out Pawn pawn, out string reason)
        {
            pawn = null;
            reason = null;
            if (record == null || record.pawns == null)
            {
                return false;
            }
            foreach (Pawn candidate in record.pawns)
            {
                if (!NeedsHospitalityBed(candidate))
                {
                    continue;
                }
                if (HasGuestBed(candidate))
                {
                    continue;
                }
                pawn = candidate;
                reason = candidate.LabelShortCap + " has no claimed guest bed";
                return true;
            }
            return false;
        }

        public static bool IsRescuedGuest(Pawn pawn)
        {
            object comp = CompGuest(pawn);
            return comp != null && Reflect.BoolMember(comp, "rescued");
        }

        public static bool IsArrivedGuest(Pawn pawn)
        {
            object comp = CompGuest(pawn);
            return comp != null && Reflect.BoolMember(comp, "arrived");
        }

        public static bool TryConvertHospitalPatientsToDelayGuests(ServiceGroupRecord record, Map map, out string reason)
        {
            reason = null;
            if (record == null || record.serviceKind != "hospital" || record.departureHoldHospitalityHandoffDone)
            {
                return false;
            }
            if (map == null)
            {
                map = record.pawns == null ? null : record.pawns.FirstOrDefault(pawn => pawn != null && pawn.Spawned)?.Map;
            }
            if (map == null || record.pawns == null)
            {
                reason = "missing map or pawns";
                return false;
            }

            MethodInfo createLord = DelayGuestCreateLordMethod();
            if (createLord == null)
            {
                reason = "Hospitality visitor lord API unavailable";
                return false;
            }

            List<Pawn> guests = record.pawns
                .Where(pawn => CanConvertToDelayGuest(pawn, map))
                .Distinct()
                .ToList();
            if (guests.Count == 0)
            {
                reason = "no eligible patients";
                return false;
            }

            try
            {
                createLord.Invoke(null, new object[]
                {
                    guests[0].Faction,
                    guests[0].Position,
                    guests,
                    map,
                    false,
                    false,
                    GenDate.TicksPerDay
                });
                int assignedBeds = AssignDelayGuestBeds(map, guests);
                record.departureHoldHospitalityHandoffDone = true;
                Messages.Message("Space Services: Delayed pickup converted " + guests.Count + " patient" + (guests.Count == 1 ? "" : "s") + " into temporary Hospitality guest" + (guests.Count == 1 ? "" : "s") + " because shuttle departure is blocked.", MessageTypeDefOf.NeutralEvent, false);
                ServiceDebugUtility.LogAudit("Converted hospital departure hold patients to Hospitality delay guests record=" + record.id + " beds=" + assignedBeds + "/" + guests.Count + " pawns=" + string.Join(", ", guests.Select(pawn => pawn.LabelShortCap)));
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospital, "hospital-delay-guest-convert-" + record.id, "Could not convert held hospital patients to Hospitality delay guests: " + reason, GenDate.TicksPerHour);
                return false;
            }
        }

        public static bool DelayGuestApiAvailable()
        {
            return DelayGuestCreateLordMethod() != null;
        }

        private static MethodInfo DelayGuestCreateLordMethod()
        {
            if (checkedDelayGuestCreateLordMethod)
            {
                return delayGuestCreateLordMethod;
            }
            checkedDelayGuestCreateLordMethod = true;
            delayGuestCreateLordMethod = AccessTools.Method(
                    AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup"),
                    "CreateLord",
                    new[] { typeof(Faction), typeof(IntVec3), typeof(List<Pawn>), typeof(Map), typeof(bool), typeof(bool), typeof(int) });
            return delayGuestCreateLordMethod;
        }

        public static void PrepareGuestsForServiceDeparture(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospitality" || record.pawns == null)
            {
                return;
            }
            ServiceDebugUtility.LogAudit("Preparing Hospitality service departure " + record.id + " pawns=" + record.pawns.Count);
            foreach (Pawn pawn in record.pawns)
            {
                PrepareGuestPawnForServiceDeparture(pawn, "Hospitality departure pawn has no CompGuest");
            }
        }

        public static void PrepareDelayGuestsForServiceDeparture(ServiceGroupRecord record)
        {
            if (record == null || !record.departureHoldHospitalityHandoffDone || record.pawns == null)
            {
                return;
            }
            ServiceDebugUtility.LogAudit("Preparing Hospitality delay guests for service departure " + record.id + " pawns=" + record.pawns.Count);
            foreach (Pawn pawn in record.pawns)
            {
                PrepareGuestPawnForServiceDeparture(pawn, "Delay guest departure pawn has no CompGuest");
            }
        }

        public static bool DetachPreparedDepartureGuest(Pawn pawn, string reason)
        {
            object comp = CompGuest(pawn);
            if (comp == null)
            {
                return false;
            }
            Lord compLord = Reflect.GetMember(comp, "lord") as Lord;
            Lord pawnLord = pawn == null ? null : pawn.GetLord();
            bool arrived = Reflect.BoolMember(comp, "arrived");
            int removed = RemoveFromLord(compLord, pawn);
            if (pawnLord != compLord)
            {
                removed += RemoveFromLord(pawnLord, pawn);
            }
            if (compLord != null)
            {
                Reflect.SetMember(comp, "lord", null);
            }
            if (arrived)
            {
                Reflect.SetMember(comp, "arrived", false);
            }
            MarkGuestLeavingByService(comp);
            int runtimeLordRefs = ServicePawnUtility.ClearRuntimeLordReferences(pawn);
            bool changed = compLord != null || pawnLord != null || arrived || removed > 0 || runtimeLordRefs > 0;
            if (changed)
            {
                ServiceDebugUtility.LogAudit("Detached prepared Hospitality departure guest reason=" + (reason ?? "none") + " removedLordPawns=" + removed + " runtimeLordRefs=" + runtimeLordRefs + " oldCompLord=" + LordLabel(compLord) + " oldPawnLord=" + LordLabel(pawnLord) + " pawn=" + GuestDebugSummary(pawn));
            }
            return changed;
        }

        private static void PrepareGuestPawnForServiceDeparture(Pawn pawn, string missingCompMessage)
        {
            object comp = CompGuest(pawn);
            if (comp == null)
            {
                ServiceDebugUtility.LogAudit((missingCompMessage ?? "Departure pawn has no CompGuest") + ": " + GuestDebugSummary(pawn));
                return;
            }

            ServiceDebugUtility.LogAudit("Before Hospitality departure prep: " + GuestDebugSummary(pawn));
            MarkLordLeaving(pawn, comp);
            ServiceDebugUtility.LogAudit("After Hospitality lord leaving prep: " + GuestDebugSummary(pawn));
            if (!Reflect.BoolMember(comp, "arrived"))
            {
                MarkGuestLeavingByService(comp);
                ServiceDebugUtility.LogAudit("Skipping native Hospitality leave because arrived=false: " + GuestDebugSummary(pawn));
                return;
            }
            if (HospitalityPatchHandlers.TryRunNativeGuestLeave(pawn))
            {
                MarkGuestLeavingByService(comp);
                ClearGuestRuntimeReferences(pawn, comp, "native leave");
                ServiceDebugUtility.LogAudit("Ran Hospitality GuestUtility.Leave for service departure: " + GuestDebugSummary(pawn));
                return;
            }
            MethodInfo leave = AccessTools.Method(comp.GetType(), "Leave", new[] { typeof(bool) });
            if (leave != null)
            {
                try
                {
                    // Space Services owns the shuttle transfer now; leaving Hospitality prevents guest-area logic from pulling pawns back.
                    leave.Invoke(comp, new object[] { false });
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogVerbose("Hospitality CompGuest.Leave failed during service departure: " + ex.Message);
                }
            }
            else
            {
                Reflect.SetMember(comp, "arrived", false);
            }
            MarkGuestLeavingByService(comp);
            ClearGuestRuntimeReferences(pawn, comp, "fallback leave");
            ServiceDebugUtility.LogAudit("After Hospitality fallback departure prep: " + GuestDebugSummary(pawn));
        }

        private static void MarkGuestLeavingByService(object comp)
        {
            if (comp == null)
            {
                return;
            }
            Reflect.SetMember(comp, "sentAway", true);
            Reflect.SetMember(comp, "rescued", false);
        }

        private static bool CanConvertToDelayGuest(Pawn pawn, Map map)
        {
            return pawn != null &&
                pawn.Spawned &&
                pawn.Map == map &&
                !pawn.Dead &&
                !pawn.Destroyed &&
                !pawn.Downed &&
                pawn.RaceProps != null &&
                pawn.RaceProps.Humanlike &&
                pawn.Faction != null &&
                !ServicePawnUtility.IsPlayerOwnedPawn(pawn) &&
                !IsArrivedGuest(pawn) &&
                CompGuest(pawn) != null;
        }

        private static int AssignDelayGuestBeds(Map map, List<Pawn> pawns)
        {
            if (map == null || pawns.NullOrEmpty())
            {
                return 0;
            }
            List<Thing> beds = GuestBeds(map).ToList();
            int assigned = 0;
            foreach (Pawn pawn in pawns)
            {
                if (TryAssignDelayGuestBed(pawn, beds))
                {
                    assigned++;
                }
            }
            return assigned;
        }

        private static bool TryAssignDelayGuestBed(Pawn pawn, List<Thing> beds)
        {
            if (pawn == null || beds.NullOrEmpty())
            {
                return false;
            }
            object comp = CompGuest(pawn);
            MethodInfo claimBed = comp == null ? null : AccessTools.Method(comp.GetType(), "ClaimBed");
            if (claimBed == null)
            {
                return false;
            }
            Area area = Reflect.GetMember(comp, "GuestArea") as Area;
            foreach (Thing bed in beds
                .Where(bed => DelayGuestBedAvailable(pawn, bed, area))
                .OrderBy(bed => bed.Position.DistanceToSquared(pawn.Position)))
            {
                try
                {
                    claimBed.Invoke(comp, new object[] { bed });
                    ServiceDebugUtility.LogAudit("Assigned delay guest bed pawn=" + pawn.LabelShortCap + " bed=" + ThingLabel(bed));
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogVerbose("Hospitality delay guest bed claim failed: " + ex.Message);
                }
            }
            return false;
        }

        private static bool DelayGuestBedAvailable(Pawn pawn, Thing bed, Area area)
        {
            if (!IsGuestBed(bed) || pawn == null || pawn.Map == null || bed.Map != pawn.Map)
            {
                return false;
            }
            if (area != null && area.Map == pawn.Map && !area[bed.Position])
            {
                return false;
            }
            object anyUnowned = Reflect.GetMember(bed, "AnyUnownedSleepingSlot");
            if (anyUnowned is bool && !(bool)anyUnowned)
            {
                return false;
            }
            if (AssignedGuestCount(bed) > 0 && !(anyUnowned is bool))
            {
                return false;
            }
            return !bed.IsForbidden(pawn) &&
                !bed.IsBurning() &&
                pawn.CanReserveAndReach(bed, PathEndMode.OnCell, Danger.Some);
        }

        public static string GuestDebugSummary(Pawn pawn)
        {
            if (pawn == null)
            {
                return "pawn=null";
            }
            object comp = CompGuest(pawn);
            object bed = comp == null ? null : Reflect.GetMember(comp, "bed");
            object compLord = comp == null ? null : Reflect.GetMember(comp, "lord");
            return pawn.LabelShortCap +
                " [" + pawn.ThingID + "]" +
                " spawned=" + pawn.Spawned +
                " destroyed=" + pawn.Destroyed +
                " pos=" + (pawn.Spawned ? pawn.Position.ToString() : "unspawned") +
                " guestStatus=" + GuestStatusLabel(pawn) +
                " arrived=" + (comp == null ? "n/a" : Reflect.BoolMember(comp, "arrived").ToString()) +
                " sentAway=" + (comp == null ? "n/a" : Reflect.BoolMember(comp, "sentAway").ToString()) +
                " rescued=" + (comp == null ? "n/a" : Reflect.BoolMember(comp, "rescued").ToString()) +
                " bed=" + ThingLabel(bed) +
                " compLord=" + LordLabel(compLord as Lord) +
                " pawnLord=" + LordLabel(pawn.GetLord()) +
                " curJob=" + JobLabel(pawn.CurJob);
        }

        private static string GuestStatusLabel(Pawn pawn)
        {
            object status = pawn == null || pawn.guest == null ? null : Reflect.GetMember(pawn.guest, "GuestStatus");
            return status == null ? "null" : status.ToString();
        }

        private static string ThingLabel(object thing)
        {
            Thing t = thing as Thing;
            if (t == null)
            {
                return thing == null ? "null" : thing.ToString();
            }
            return t.def.defName + "[" + t.ThingID + "]";
        }

        private static string LordLabel(Lord lord)
        {
            if (lord == null)
            {
                return "null";
            }
            string job = lord.LordJob == null ? "nullJob" : lord.LordJob.GetType().Name;
            return lord.loadID + "/" + job + "/pawns=" + (lord.ownedPawns == null ? 0 : lord.ownedPawns.Count);
        }

        private static string JobLabel(Job job)
        {
            if (job == null)
            {
                return "null";
            }
            return job.def.defName + "/lord=" + LordLabel(job.lord);
        }

        private static void ClearGuestRuntimeReferences(Pawn pawn, object comp, string reason)
        {
            if (comp == null)
            {
                return;
            }
            object oldLord = Reflect.GetMember(comp, "lord");
            int removed = RemoveFromLord(oldLord as Lord, pawn);
            Lord pawnLord = pawn == null ? null : pawn.GetLord();
            if (pawnLord != oldLord)
            {
                removed += RemoveFromLord(pawnLord, pawn);
            }
            if (oldLord != null)
            {
                Reflect.SetMember(comp, "lord", null);
            }
            int runtimeLordRefs = ServicePawnUtility.ClearRuntimeLordReferences(pawn);
            ServiceDebugUtility.LogAudit("Cleared Hospitality runtime refs reason=" + reason + " oldCompLord=" + LordLabel(oldLord as Lord) + " oldPawnLord=" + LordLabel(pawnLord) + " removedLordPawns=" + removed + " runtimeLordRefs=" + runtimeLordRefs + " pawn=" + GuestDebugSummary(pawn));
        }

        private static int RemoveFromLord(Lord lord, Pawn pawn)
        {
            if (lord == null || lord.ownedPawns == null)
            {
                return 0;
            }
            return lord.ownedPawns.RemoveAll(owned => owned == null || owned == pawn || owned.Destroyed || owned.Dead);
        }

        private static IEnumerable<Thing> GuestBeds(Map map)
        {
            if (map == null)
            {
                yield break;
            }

            List<Thing> hospitalityBeds = GuestBedsFromHospitalityUtility(map).ToList();
            if (hospitalityBeds.Count > 0)
            {
                foreach (Thing bed in hospitalityBeds)
                {
                    yield return bed;
                }
                yield break;
            }

            IEnumerable buildings = Reflect.GetMember(map.listerBuildings, "allBuildingsColonist") as IEnumerable;
            if (buildings == null)
            {
                buildings = Reflect.GetMember(map.listerBuildings, "AllBuildingsColonist") as IEnumerable;
            }
            foreach (object obj in buildings ?? Enumerable.Empty<object>())
            {
                Thing thing = obj as Thing;
                if (IsGuestBed(thing))
                {
                    yield return thing;
                }
            }
        }

        private static IEnumerable<Thing> GuestBedsFromHospitalityUtility(Map map)
        {
            Type type = AccessTools.TypeByName("Hospitality.Utilities.BedUtility");
            MethodInfo method = type == null ? null : AccessTools.Method(type, "GetGuestBeds");
            if (method == null)
            {
                yield break;
            }

            object result = null;
            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                result = parameters.Length == 2 ? method.Invoke(null, new object[] { map, null }) : method.Invoke(null, new object[] { map });
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogVerbose("Hospitality BedUtility.GetGuestBeds failed: " + ex.Message);
            }

            IEnumerable enumerable = result as IEnumerable;
            if (enumerable == null)
            {
                yield break;
            }
            foreach (object obj in enumerable)
            {
                Thing thing = obj as Thing;
                if (thing != null && !thing.Destroyed && thing.Spawned)
                {
                    yield return thing;
                }
            }
        }

        private static bool NeedsHospitalityBed(Pawn pawn)
        {
            object comp = CompGuest(pawn);
            if (comp == null)
            {
                return false;
            }
            return Reflect.BoolMember(comp, "arrived") &&
                !Reflect.BoolMember(comp, "rescued") &&
                !Reflect.BoolMember(comp, "sentAway");
        }

        private static bool HasGuestBed(Pawn pawn)
        {
            object comp = CompGuest(pawn);
            if (comp == null)
            {
                return true;
            }
            object hasBed = Reflect.GetMember(comp, "HasBed");
            if (hasBed is bool && (bool)hasBed)
            {
                return true;
            }
            Thing bed = Reflect.GetMember(comp, "bed") as Thing;
            return IsGuestBed(bed) && bed.Spawned && !bed.Destroyed;
        }

        private static object CompGuest(Pawn pawn)
        {
            if (pawn == null || pawn.AllComps == null)
            {
                return null;
            }
            Type compType = AccessTools.TypeByName(CompGuestTypeName);
            foreach (ThingComp comp in pawn.AllComps)
            {
                if (comp == null)
                {
                    continue;
                }
                Type type = comp.GetType();
                if ((compType != null && compType.IsAssignableFrom(type)) || type.FullName == CompGuestTypeName)
                {
                    return comp;
                }
            }
            return null;
        }

        private static void MarkLordLeaving(Pawn pawn, object comp)
        {
            Lord lord = pawn == null ? null : pawn.GetLord();
            if (lord == null)
            {
                lord = Reflect.GetMember(comp, "lord") as Lord;
            }
            object lordJob = lord == null ? null : lord.LordJob;
            if (lordJob == null)
            {
                return;
            }
            MethodInfo onLeave = AccessTools.Method(lordJob.GetType(), "OnLeaveTriggered");
            if (onLeave != null)
            {
                try
                {
                    ServiceDebugUtility.LogAudit("Hospitality LordJob.OnLeaveTriggered before pawn=" + GuestDebugSummary(pawn));
                    onLeave.Invoke(lordJob, null);
                    ServiceDebugUtility.LogAudit("Hospitality LordJob.OnLeaveTriggered after pawn=" + GuestDebugSummary(pawn));
                    return;
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogAudit("Hospitality LordJob.OnLeaveTriggered failed during service departure: " + ex);
                }
            }
            Reflect.SetMember(lordJob, "leaving", true);
            ServiceDebugUtility.LogAudit("Hospitality lord leaving field set pawn=" + GuestDebugSummary(pawn));
        }

        private static bool IsGuestBed(Thing thing)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned)
            {
                return false;
            }
            Type guestBedType = AccessTools.TypeByName(GuestBedTypeName);
            Type thingType = thing.GetType();
            if (guestBedType != null && guestBedType.IsAssignableFrom(thingType))
            {
                return true;
            }
            return thingType.FullName == GuestBedTypeName;
        }

        private static int AssignedGuestCount(Thing bed)
        {
            IEnumerable assigned = Reflect.GetMember(bed, "AssignedPawnsForReading") as IEnumerable;
            if (assigned == null)
            {
                object compAssignable = Reflect.GetMember(bed, "CompAssignableToPawn");
                assigned = Reflect.GetMember(compAssignable, "AssignedPawnsForReading") as IEnumerable;
            }
            if (assigned == null)
            {
                assigned = Reflect.GetMember(bed, "assignedPawns") as IEnumerable;
            }
            if (assigned == null)
            {
                return 0;
            }
            int count = 0;
            foreach (object obj in assigned)
            {
                Pawn pawn = obj as Pawn;
                if (pawn != null && !pawn.Destroyed)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public sealed class HospitalityBedReport
    {
        public int totalBeds;
        public int assignedBeds;
        public int freeBeds;

        public string ToSummary()
        {
            return "freeGuestBeds=" + freeBeds + ", assignedGuestBeds=" + assignedBeds + ", totalGuestBeds=" + totalBeds;
        }
    }
}
