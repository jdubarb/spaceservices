using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SpaceServices
{
    public sealed class PrebuildPadModeRecord : IExposable
    {
        public IntVec3 cell = IntVec3.Invalid;
        public ServicePadMode mode = ServicePadMode.Shared;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref mode, "mode", ServicePadMode.Shared);
        }
    }

    public static class ServicePadPrebuildModeUtility
    {
        public static bool IsServicePadBuildThing(Thing thing)
        {
            return thing != null && thing.def != null && thing.def.entityDefToBuild is ThingDef target && target.defName == "JDB_ServiceLandingPad";
        }

        public static ServicePadMode GetMode(Thing thing)
        {
            SpaceServicesMapComponent comp = thing == null || thing.Map == null ? null : thing.Map.GetComponent<SpaceServicesMapComponent>();
            PrebuildPadModeRecord record = comp == null ? null : comp.prebuildPadModes.FirstOrDefault(item => item != null && item.cell == thing.Position);
            return record == null ? ServicePadMode.Shared : record.mode;
        }

        public static void SetMode(Thing thing, ServicePadMode mode)
        {
            if (thing == null || thing.Map == null)
            {
                return;
            }
            SpaceServicesMapComponent comp = thing.Map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return;
            }
            PrebuildPadModeRecord record = comp.prebuildPadModes.FirstOrDefault(item => item != null && item.cell == thing.Position);
            if (record == null)
            {
                record = new PrebuildPadModeRecord { cell = thing.Position };
                comp.prebuildPadModes.Add(record);
            }
            record.mode = mode;
        }

        public static void ApplyPendingMode(Building_ServiceLandingPad pad)
        {
            if (pad == null || pad.Map == null)
            {
                return;
            }
            SpaceServicesMapComponent comp = pad.Map.GetComponent<SpaceServicesMapComponent>();
            PrebuildPadModeRecord record = comp == null ? null : comp.prebuildPadModes.FirstOrDefault(item => item != null && item.cell == pad.Position);
            if (record == null)
            {
                return;
            }
            CompSpaceServicePad padComp = pad.GetComp<CompSpaceServicePad>();
            if (padComp != null)
            {
                padComp.activeMode = record.mode;
            }
            comp.prebuildPadModes.Remove(record);
        }

        public static IEnumerable<Gizmo> AppendModeGizmos(IEnumerable<Gizmo> gizmos, Thing thing)
        {
            foreach (Gizmo gizmo in gizmos ?? Enumerable.Empty<Gizmo>())
            {
                yield return gizmo;
            }
            if (!IsServicePadBuildThing(thing))
            {
                yield break;
            }
            foreach (Gizmo gizmo in CompSpaceServicePad.ModeCommands(() => GetMode(thing), mode => SetMode(thing, mode)))
            {
                yield return gizmo;
            }
        }
    }

    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.GetGizmos))]
    public static class ServicePadBlueprintGizmosPatch
    {
        public static void Postfix(Blueprint __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = ServicePadPrebuildModeUtility.AppendModeGizmos(__result, __instance);
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.GetGizmos))]
    public static class ServicePadFrameGizmosPatch
    {
        public static void Postfix(Frame __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = ServicePadPrebuildModeUtility.AppendModeGizmos(__result, __instance);
        }
    }
}
