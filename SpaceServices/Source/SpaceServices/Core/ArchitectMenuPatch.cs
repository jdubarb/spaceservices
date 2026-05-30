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
    [HarmonyPatch(typeof(DesignationCategoryDef), "ResolveReferences")]
    public static class ArchitectMenuPatch
    {
        private static readonly string[] DuplicateBuildDefs =
        {
            "JDB_ServiceLandingPad"
        };

        public static void Postfix(DesignationCategoryDef __instance)
        {
            if (__instance != null && __instance.defName == SpaceServicesBootstrap.CategoryDefName)
            {
                InjectArchitectDesignators();
            }
        }

        public static void InjectArchitectDesignators()
        {
            DesignationCategoryDef category = DefDatabase<DesignationCategoryDef>.GetNamedSilentFail(SpaceServicesBootstrap.CategoryDefName);
            if (category == null)
            {
                return;
            }
            FieldInfo field = AccessTools.Field(typeof(DesignationCategoryDef), "resolvedDesignators");
            List<Designator> designators = field == null ? null : field.GetValue(category) as List<Designator>;
            if (designators == null)
            {
                return;
            }

            HashSet<string> existing = new HashSet<string>(designators.OfType<Designator_Build>().Select(d => d.PlacingDef == null ? "" : d.PlacingDef.defName));
            for (int i = 0; i < DuplicateBuildDefs.Length; i++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(DuplicateBuildDefs[i]);
                if (def == null || existing.Contains(def.defName))
                {
                    continue;
                }
                designators.Add(new Designator_Build(def));
                existing.Add(def.defName);
            }
        }
    }
}
