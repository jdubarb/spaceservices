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
    public static class ServiceDebugUtility
    {
        private static readonly Dictionary<string, int> LastLogTickByKey = new Dictionary<string, int>();

        public static IncidentParms PatientArrivalParms(Map map)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
            parms.target = map;
            parms.pawnKind = PawnKindDefOf.Villager;
            parms.faction = Find.FactionManager.FirstFactionOfDef(FactionDefOf.OutlanderCivil);
            return parms;
        }

        public static bool DebugLogging
        {
            get
            {
                return SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging;
            }
        }

        public static bool VerboseLogging
        {
            get
            {
                return SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.verboseDevLogging;
            }
        }

        public static void Log(string message)
        {
            if (DebugLogging)
            {
                Verse.Log.Message("[Space Services] " + message);
            }
        }

        public static void LogVerbose(string message)
        {
            if (VerboseLogging)
            {
                Verse.Log.Message("[Space Services] " + message);
            }
        }

        public static void LogAudit(string message)
        {
            LogVerbose("[audit] " + message);
        }

        public static void LogThrottled(string key, string message)
        {
            LogThrottled(key, message, GenDate.TicksPerHour);
        }

        public static void LogThrottled(string key, string message, int intervalTicks)
        {
            if (!DebugLogging || string.IsNullOrEmpty(key))
            {
                return;
            }
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            int lastTick;
            if (LastLogTickByKey.TryGetValue(key, out lastTick) && tick < lastTick + intervalTicks)
            {
                return;
            }
            LastLogTickByKey[key] = tick;
            Verse.Log.Message("[Space Services] " + message);
        }

        public static string PawnAuditSummary(Pawn pawn)
        {
            if (pawn == null)
            {
                return "pawn=null";
            }
            return pawn.LabelShortCap +
                " [" + pawn.ThingID + "]" +
                " spawned=" + pawn.Spawned +
                " destroyed=" + pawn.Destroyed +
                " dead=" + pawn.Dead +
                " downed=" + pawn.Downed +
                " pos=" + (pawn.Spawned ? pawn.Position.ToString() : "unspawned") +
                " mapHeld=" + (pawn.MapHeld == null ? "null" : pawn.MapHeld.uniqueID.ToString()) +
                " faction=" + (pawn.Faction == null ? "null" : pawn.Faction.Name) +
                " lord=" + LordAuditSummary(SafeLord(pawn)) +
                " curJob=" + JobAuditSummary(pawn.CurJob);
        }

        public static string ThingAuditSummary(Thing thing)
        {
            if (thing == null)
            {
                return "thing=null";
            }
            return thing.def.defName +
                " [" + thing.ThingID + "]" +
                " spawned=" + thing.Spawned +
                " destroyed=" + thing.Destroyed +
                " pos=" + (thing.Spawned ? thing.Position.ToString() : "unspawned") +
                " map=" + (thing.MapHeld == null ? "null" : thing.MapHeld.uniqueID.ToString());
        }

        public static string LordAuditSummary(Lord lord)
        {
            if (lord == null)
            {
                return "null";
            }
            string job = lord.LordJob == null ? "nullJob" : lord.LordJob.GetType().Name;
            int count = lord.ownedPawns == null ? 0 : lord.ownedPawns.Count;
            return lord.loadID + "/" + job + "/pawns=" + count;
        }

        public static string JobAuditSummary(Job job)
        {
            if (job == null)
            {
                return "null";
            }
            string defName = job.def == null ? "nullDef" : job.def.defName;
            return defName + "/lord=" + LordAuditSummary(job.lord);
        }

        private static Lord SafeLord(Pawn pawn)
        {
            try
            {
                return pawn == null ? null : pawn.GetLord();
            }
            catch
            {
                return null;
            }
        }
    }
}
