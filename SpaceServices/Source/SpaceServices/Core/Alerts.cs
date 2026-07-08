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
    public class Alert_SpaceServicesDepartureBlocked : Alert
    {
        public Alert_SpaceServicesDepartureBlocked()
        {
            defaultLabel = "Space Services: Departure blocked";
        }

        public override TaggedString GetExplanation()
        {
            List<ServiceDepartureBlock> blocks = ServiceLifecycleUtility.BlockedDepartures();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Service visitors are ready to leave, but Space Services cannot safely extract them yet.");
            builder.AppendLine();
            AppendSection(builder, "Blocked pickup", blocks.Where(block => !IsTemporaryGuestHold(block) && !IsTemporaryLodgerHold(block)));
            AppendSection(builder, "Held as temporary guests", blocks.Where(IsTemporaryGuestHold));
            AppendSection(builder, "Held as temporary lodgers", blocks.Where(IsTemporaryLodgerHold));
            builder.AppendLine();
            builder.Append("Fix the service pad or restore a safe atmosphere at the pad.");
            return builder.ToString();
        }

        private static void AppendSection(StringBuilder builder, string title, IEnumerable<ServiceDepartureBlock> source)
        {
            List<ServiceDepartureBlock> allBlocks = source.ToList();
            List<ServiceDepartureBlock> blocks = allBlocks.Take(8).ToList();
            if (blocks.Count == 0)
            {
                return;
            }
            builder.AppendLine(title);
            foreach (ServiceDepartureBlock block in blocks)
            {
                builder.AppendLine("- " + BlockSummary(block));
            }
            int remaining = allBlocks.Count - blocks.Count;
            if (remaining > 0)
            {
                builder.AppendLine("- " + remaining + " more.");
            }
            builder.AppendLine();
        }

        private static string BlockSummary(ServiceDepartureBlock block)
        {
            ServiceGroupRecord record = block == null ? null : block.record;
            string serviceKind = DisplayLabel(record == null ? "service" : record.serviceKind ?? "service");
            string pawns = PawnSummary(record);
            string reason = block == null || string.IsNullOrEmpty(block.reason) ? "departure blocked" : block.reason;
            string summary = serviceKind + ": " + pawns + " - " + reason;
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging && record != null)
            {
                summary += " (" + record.id + " " + (record.state ?? "unknown") + DebugHoldLabel(record) + ")";
            }
            return summary;
        }

        private static string PawnSummary(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null)
            {
                return "unknown pawns";
            }
            List<string> names = record.pawns
                .Where(pawn => pawn != null && !pawn.Destroyed)
                .Select(pawn => pawn.LabelShortCap)
                .Take(5)
                .ToList();
            if (names.Count == 0)
            {
                return "unknown pawns";
            }
            int remaining = record.pawns.Count(pawn => pawn != null && !pawn.Destroyed) - names.Count;
            return string.Join(", ", names.ToArray()) + (remaining > 0 ? " +" + remaining : "");
        }

        private static string DebugHoldLabel(ServiceGroupRecord record)
        {
            if (record == null)
            {
                return "";
            }
            if (record.departureHoldQuestLodgerHandoffDone)
            {
                return " questLodger";
            }
            if (record.departureHoldHospitalityHandoffDone)
            {
                return " hospitalityGuest";
            }
            return "";
        }

        private static bool IsTemporaryGuestHold(ServiceDepartureBlock block)
        {
            ServiceGroupRecord record = block == null ? null : block.record;
            return record != null && record.serviceKind == "hospital" && record.departureHoldHospitalityHandoffDone;
        }

        private static bool IsTemporaryLodgerHold(ServiceDepartureBlock block)
        {
            ServiceGroupRecord record = block == null ? null : block.record;
            return record != null && record.departureHoldQuestLodgerHandoffDone;
        }

        private static string DisplayLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            return string.Join(" ", text.Split(' ').Select(part => part.CapitalizeFirst()).ToArray());
        }

        public override AlertReport GetReport()
        {
            List<Thing> culprits = ServiceLifecycleUtility.BlockedDepartures()
                .Select(block => block.Culprit)
                .Where(thing => thing != null)
                .Distinct()
                .ToList();
            return culprits.Count == 0 ? AlertReport.Inactive : AlertReport.CulpritsAre(culprits);
        }
    }
}
