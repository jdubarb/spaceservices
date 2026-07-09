using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace SpaceServices
{
    public static class VgeEscapePodPatches
    {
        private const string WorkerTypeName = "VanillaGravshipExpanded.IncidentWorker_EscapePodCrash";
        private const string EscapePodSkyfallerDefName = "VGE_EscapePodSkyfaller";
        private const string DamagedEscapePodDefName = "VGE_DamagedEscapePod";

        public static void Install(Harmony harmony)
        {
            Type workerType = AccessTools.TypeByName(WorkerTypeName);
            MethodInfo method = workerType == null ? null : AccessTools.Method(workerType, "TryExecuteWorker", new[] { typeof(IncidentParms) });
            OptionalModPatches.PatchIfExists(harmony, method, typeof(VgeEscapePodPatches), prefix: nameof(TryExecuteWorkerPrefix));
        }

        public static bool TryExecuteWorkerPrefix(IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }

            ThingDef escapePodDef = DefDatabase<ThingDef>.GetNamedSilentFail(DamagedEscapePodDefName);
            ThingDef skyfallerDef = DefDatabase<ThingDef>.GetNamedSilentFail(EscapePodSkyfallerDefName);
            if (escapePodDef == null || skyfallerDef == null)
            {
                return true;
            }

            if (!CellFinder.TryFindRandomCell(map, cell => CanCrashEscapePodAt(map, cell), out IntVec3 crashCell))
            {
                __result = false;
                ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "vge-escape-pod-no-cell-" + map.uniqueID, "Blocked VGE escape pod crash on stationary service map because no non-pad crash cell was available.", GenDate.TicksPerHour);
                return false;
            }

            Thing escapePod = ThingMaker.MakeThing(escapePodDef);
            SkyfallerMaker.SpawnSkyfaller(skyfallerDef, new List<Thing> { escapePod }, crashCell, map);
            Find.LetterStack.ReceiveLetter("VGE_EscapePodCrash".Translate(), "VGE_EscapePodCrashDesc".Translate(), LetterDefOf.NegativeEvent, new TargetInfo(crashCell, map));
            __result = true;
            ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Core, "vge-escape-pod-reroute-" + map.uniqueID, "Rerouted VGE escape pod crash away from Space Services pad footprints.", GenDate.TicksPerHour);
            return false;
        }

        private static bool CanCrashEscapePodAt(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map) || ServicePadUtility.CellTouchesServicePad(map, cell))
            {
                return false;
            }
            RoofDef roof = map.roofGrid.RoofAt(cell);
            return roof != null && !roof.isNatural;
        }
    }
}
