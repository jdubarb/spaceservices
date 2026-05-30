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
    }
}
