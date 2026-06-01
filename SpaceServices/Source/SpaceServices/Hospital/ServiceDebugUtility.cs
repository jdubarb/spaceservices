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
    public enum ServiceLogIntegration
    {
        Core,
        Hospital,
        Hospitality
    }

    public static class ServiceDebugUtility
    {
        private static readonly Dictionary<string, int> LastLogTickByKey = new Dictionary<string, int>();
        private static int nextLogKeyPruneTick;

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

        public static bool AnyVerboseLogging
        {
            get
            {
                return VerboseLogging(ServiceLogIntegration.Core) ||
                    VerboseLogging(ServiceLogIntegration.Hospital) ||
                    VerboseLogging(ServiceLogIntegration.Hospitality);
            }
        }

        public static bool VerboseLogging(ServiceLogIntegration integration)
        {
            if (SpaceServicesMod.Settings == null)
            {
                return false;
            }
            switch (integration)
            {
                case ServiceLogIntegration.Hospital:
                    return SpaceServicesMod.Settings.verboseHospitalLogging;
                case ServiceLogIntegration.Hospitality:
                    return SpaceServicesMod.Settings.verboseHospitalityLogging;
                default:
                    return SpaceServicesMod.Settings.verboseCoreLogging;
            }
        }

        public static void Log(string message)
        {
            Log(GuessIntegration(message), message);
        }

        public static void Log(ServiceLogIntegration integration, string message)
        {
            if (NormalLogging(integration))
            {
                Verse.Log.Message("[Space Services] " + message);
            }
        }

        public static void LogWarning(ServiceLogIntegration integration, string message)
        {
            Verse.Log.Warning("[Space Services] " + message);
        }

        public static void LogError(ServiceLogIntegration integration, string message)
        {
            Verse.Log.Error("[Space Services] " + message);
        }

        public static void LogVerbose(string message)
        {
            LogVerbose(GuessIntegration(message), message);
        }

        public static void LogVerbose(ServiceLogIntegration integration, string message)
        {
            // Verbose output is for trace-level diagnostics: arrivals, lords, records, and patch decisions.
            if (VerboseLogging(integration))
            {
                Verse.Log.Message("[Space Services] [" + IntegrationLabel(integration) + "] " + message);
            }
        }

        public static void LogAudit(string message)
        {
            LogAudit(GuessIntegration(message), message);
        }

        public static void LogAudit(ServiceLogIntegration integration, string message)
        {
            if (VerboseLogging(integration))
            {
                Verse.Log.Message("[Space Services] [" + IntegrationLabel(integration) + " audit] " + message);
            }
        }

        public static void LogThrottled(string key, string message)
        {
            LogThrottled(GuessIntegration(message), key, message, GenDate.TicksPerHour);
        }

        public static void LogThrottled(string key, string message, int intervalTicks)
        {
            LogThrottled(GuessIntegration(message), key, message, intervalTicks);
        }

        public static void LogThrottled(ServiceLogIntegration integration, string key, string message)
        {
            LogThrottled(integration, key, message, GenDate.TicksPerHour);
        }

        public static void LogThrottled(ServiceLogIntegration integration, string key, string message, int intervalTicks)
        {
            if (!NormalLogging(integration) || string.IsNullOrEmpty(key))
            {
                return;
            }
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            PruneLogKeys(tick);
            int lastTick;
            // Repeated blockers can fire every tick during bad map states; throttle by stable keys.
            if (LastLogTickByKey.TryGetValue(key, out lastTick) && tick < lastTick + intervalTicks)
            {
                return;
            }
            LastLogTickByKey[key] = tick;
            Verse.Log.Message("[Space Services] [" + IntegrationLabel(integration) + "] " + message);
        }

        private static void PruneLogKeys(int tick)
        {
            if (tick < nextLogKeyPruneTick || LastLogTickByKey.Count < 256)
            {
                return;
            }
            nextLogKeyPruneTick = tick + GenDate.TicksPerDay;
            List<string> staleKeys = new List<string>();
            foreach (KeyValuePair<string, int> pair in LastLogTickByKey)
            {
                if (tick > pair.Value + GenDate.TicksPerDay * 3)
                {
                    staleKeys.Add(pair.Key);
                }
            }
            foreach (string staleKey in staleKeys)
            {
                LastLogTickByKey.Remove(staleKey);
            }
        }

        private static bool NormalLogging(ServiceLogIntegration integration)
        {
            // Core debug remains the broad "is Space Services alive" switch. Integrations require their own toggles.
            if (integration == ServiceLogIntegration.Core)
            {
                return DebugLogging || VerboseLogging(ServiceLogIntegration.Core);
            }
            return VerboseLogging(integration);
        }

        public static ServiceLogIntegration IntegrationForServiceKind(string serviceKind)
        {
            if (string.Equals(serviceKind, "hospital", StringComparison.OrdinalIgnoreCase))
            {
                return ServiceLogIntegration.Hospital;
            }
            if (string.Equals(serviceKind, "hospitality", StringComparison.OrdinalIgnoreCase))
            {
                return ServiceLogIntegration.Hospitality;
            }
            return ServiceLogIntegration.Core;
        }

        private static ServiceLogIntegration GuessIntegration(string message)
        {
            if (message == null)
            {
                return ServiceLogIntegration.Core;
            }
            if (message.IndexOf("Hospitality", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Guest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("visitor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ServiceLogIntegration.Hospitality;
            }
            if (message.IndexOf("Hospital", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Patient", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("MassCasualty", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ServiceLogIntegration.Hospital;
            }
            return ServiceLogIntegration.Core;
        }

        private static string IntegrationLabel(ServiceLogIntegration integration)
        {
            switch (integration)
            {
                case ServiceLogIntegration.Hospital:
                    return "hospital";
                case ServiceLogIntegration.Hospitality:
                    return "hospitality";
                default:
                    return "core";
            }
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
