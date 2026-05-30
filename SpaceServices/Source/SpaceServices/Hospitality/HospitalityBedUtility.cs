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

        public static void PrepareGuestsForServiceDeparture(ServiceGroupRecord record)
        {
            if (record == null || record.serviceKind != "hospitality" || record.pawns == null)
            {
                return;
            }
            ServiceDebugUtility.LogAudit("Preparing Hospitality service departure " + record.id + " pawns=" + record.pawns.Count);
            foreach (Pawn pawn in record.pawns)
            {
                object comp = CompGuest(pawn);
                if (comp == null)
                {
                    ServiceDebugUtility.LogAudit("Hospitality departure pawn has no CompGuest: " + GuestDebugSummary(pawn));
                    continue;
                }

                ServiceDebugUtility.LogAudit("Before Hospitality departure prep: " + GuestDebugSummary(pawn));
                MarkLordLeaving(pawn, comp);
                ServiceDebugUtility.LogAudit("After Hospitality lord leaving prep: " + GuestDebugSummary(pawn));
                if (!Reflect.BoolMember(comp, "arrived"))
                {
                    ServiceDebugUtility.LogAudit("Skipping native Hospitality leave because arrived=false: " + GuestDebugSummary(pawn));
                    continue;
                }
                if (HospitalityPatchHandlers.TryRunNativeGuestLeave(pawn))
                {
                    ServiceDebugUtility.LogAudit("Ran Hospitality GuestUtility.Leave for service departure: " + GuestDebugSummary(pawn));
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
                ServiceDebugUtility.LogAudit("After Hospitality fallback departure prep: " + GuestDebugSummary(pawn));
            }
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
