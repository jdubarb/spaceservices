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
        public static IncidentParms PatientArrivalParms(Map map)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
            parms.target = map;
            parms.pawnKind = PawnKindDefOf.Villager;
            parms.faction = Find.FactionManager.FirstFactionOfDef(FactionDefOf.OutlanderCivil);
            return parms;
        }
    }
}
