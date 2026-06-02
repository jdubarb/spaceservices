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
    public sealed class SpaceServiceShuttleVisualDef : Def
    {
        public bool enabled = true;
        public float weight = 1f;
        public List<string> serviceKinds;
        public List<string> requiredPackageIds;
        public string shipThingDefName;
        public string incomingSkyfallerDefName;
        public string leavingSkyfallerDefName;
        public Rot4 rotation = Rot4.East;
        public GraphicData graphicData;
        public float angleOffset;

        public bool AppliesTo(string serviceKind)
        {
            return enabled && SpaceServiceDefFilters.MatchesServiceKind(serviceKinds, serviceKind) &&
                SpaceServiceDefFilters.RequiredPackagesLoaded(requiredPackageIds);
        }
    }

    public sealed class SpaceServiceShuttleVisualExtension : DefModExtension
    {
        public bool enabled = true;
        public float weight = 1f;
        public List<string> serviceKinds;
        public List<string> requiredPackageIds;
        public string shipThingDefName;
        public string incomingSkyfallerDefName;
        public string leavingSkyfallerDefName;
        public Rot4 rotation = Rot4.East;
        public GraphicData graphicData;
        public float angleOffset;

        public bool AppliesTo(string serviceKind)
        {
            return enabled && SpaceServiceDefFilters.MatchesServiceKind(serviceKinds, serviceKind) &&
                SpaceServiceDefFilters.RequiredPackagesLoaded(requiredPackageIds);
        }
    }

    public sealed class SpaceServiceHazardRuleDef : Def
    {
        public bool enabled = true;
        public List<string> serviceKinds;
        public List<string> gameConditionDefNames;
        public List<string> incidentDefNames;
        public List<string> requiredPackageIds;
        public bool blockArrivals;
        public bool delayDepartures;
        public string reason;

        public bool AppliesToCondition(GameCondition condition, string serviceKind)
        {
            string conditionDefName = condition == null || condition.def == null ? null : condition.def.defName;
            return AppliesToDefName(conditionDefName, gameConditionDefNames, serviceKind);
        }

        public bool AppliesToIncident(IncidentDef incident, string serviceKind)
        {
            string incidentDefName = incident == null ? null : incident.defName;
            return AppliesToDefName(incidentDefName, incidentDefNames, serviceKind);
        }

        private bool AppliesToDefName(string defName, List<string> defNames, string serviceKind)
        {
            return enabled &&
                !string.IsNullOrEmpty(defName) &&
                !defNames.NullOrEmpty() &&
                defNames.Contains(defName) &&
                SpaceServiceDefFilters.MatchesServiceKind(serviceKinds, serviceKind) &&
                SpaceServiceDefFilters.RequiredPackagesLoaded(requiredPackageIds);
        }
    }

    public sealed class SpaceServiceHazardExtension : DefModExtension
    {
        public bool enabled = true;
        public List<string> serviceKinds;
        public List<string> requiredPackageIds;
        public bool blockArrivals;
        public bool delayDepartures;
        public string reason;

        public bool AppliesTo(string serviceKind)
        {
            return enabled && SpaceServiceDefFilters.MatchesServiceKind(serviceKinds, serviceKind) &&
                SpaceServiceDefFilters.RequiredPackagesLoaded(requiredPackageIds);
        }
    }

    public sealed class SpaceServiceVacuumApparelSetDef : Def
    {
        public bool enabled = true;
        public float weight = 1f;
        public List<string> requiredPackageIds;
        public List<string> adultApparelDefNames;
        public List<string> childApparelDefNames;

        public bool AppliesTo(Pawn pawn)
        {
            return enabled &&
                weight > 0f &&
                SpaceServiceDefFilters.RequiredPackagesLoaded(requiredPackageIds) &&
                ResolvedApparelFor(pawn).Count > 0;
        }

        public List<ThingDef> ResolvedApparelFor(Pawn pawn)
        {
            List<string> names = pawn != null && pawn.DevelopmentalStage == DevelopmentalStage.Child && !childApparelDefNames.NullOrEmpty()
                ? childApparelDefNames
                : adultApparelDefNames;
            return SpaceServiceDefFilters.ResolveThingDefs(names);
        }

    }

    public static class SpaceServiceDefFilters
    {
        public static bool MatchesServiceKind(List<string> serviceKinds, string serviceKind)
        {
            if (serviceKinds.NullOrEmpty())
            {
                return true;
            }
            string wanted = (serviceKind ?? "").Trim();
            return serviceKinds.Any(kind => string.Equals((kind ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }

        public static bool RequiredPackagesLoaded(List<string> packageIds)
        {
            if (packageIds.NullOrEmpty())
            {
                return true;
            }
            foreach (string packageId in packageIds)
            {
                if (string.IsNullOrEmpty(packageId))
                {
                    continue;
                }
                if (!ModsConfig.IsActive(packageId) && !RunningModPackageActive(packageId))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool RunningModPackageActive(string packageId)
        {
            return LoadedModManager.RunningModsListForReading.Any(mod =>
                mod != null &&
                string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }

        public static List<ThingDef> ResolveThingDefs(List<string> defNames)
        {
            List<ThingDef> defs = new List<ThingDef>();
            if (defNames.NullOrEmpty())
            {
                return defs;
            }
            foreach (string defName in defNames)
            {
                if (string.IsNullOrEmpty(defName))
                {
                    continue;
                }
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def != null)
                {
                    defs.Add(def);
                }
            }
            return defs;
        }
    }
}
