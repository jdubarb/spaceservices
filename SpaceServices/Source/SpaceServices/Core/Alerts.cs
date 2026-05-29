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
            defaultLabel = "Space Services: departure blocked";
        }

        public override TaggedString GetExplanation()
        {
            List<ServiceDepartureBlock> blocks = ServiceLifecycleUtility.BlockedDepartures();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Service visitors are ready to leave, but Space Services cannot safely extract them yet.");
            builder.AppendLine();
            foreach (ServiceDepartureBlock block in blocks.Take(8))
            {
                string serviceKind = block.record == null ? "service" : block.record.serviceKind ?? "service";
                string mapLabel = block.map == null ? "unknown map" : "map " + block.map.uniqueID;
                builder.AppendLine("- " + serviceKind + " on " + mapLabel + ": " + (block.reason ?? "departure blocked"));
            }
            if (blocks.Count > 8)
            {
                builder.AppendLine("- " + (blocks.Count - 8) + " more blocked departures.");
            }
            builder.AppendLine();
            builder.Append("Fix the service pad or restore a safe atmosphere at the pad.");
            return builder.ToString();
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
