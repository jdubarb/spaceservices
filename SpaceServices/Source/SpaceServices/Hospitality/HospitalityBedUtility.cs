using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class HospitalityBedUtility
    {
        private const string GuestBedTypeName = "Hospitality.Building_GuestBed";
        private const string CompGuestTypeName = "Hospitality.CompGuest";

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

        public static void PrepareGuestsForServiceDeparture(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospitality" || record.pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                object comp = CompGuest(pawn);
                if (comp == null)
                {
                    continue;
                }

                MarkLordLeaving(pawn, comp);
                if (!Reflect.BoolMember(comp, "arrived"))
                {
                    continue;
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
            }
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
                    onLeave.Invoke(lordJob, null);
                    return;
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogVerbose("Hospitality LordJob.OnLeaveTriggered failed during service departure: " + ex.Message);
                }
            }
            Reflect.SetMember(lordJob, "leaving", true);
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
