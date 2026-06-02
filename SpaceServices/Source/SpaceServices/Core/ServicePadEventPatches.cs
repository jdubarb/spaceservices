using HarmonyLib;
using RimWorld;
using System.Reflection;
using Verse;

namespace SpaceServices
{
    public static class ServicePadEventPatches
    {
        public static void Install(Harmony harmony)
        {
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(MapDrawer), "Notify_RoofChanged"), typeof(ServicePadRoofChangedPatch), postfix: nameof(ServicePadRoofChangedPatch.Postfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(typeof(MapDrawer), "NotifyRoofChanged"), typeof(ServicePadRoofChangedPatch), postfix: nameof(ServicePadRoofChangedPatch.Postfix));
        }
    }

    [HarmonyPatch]
    public static class ServicePadPowerSignalPatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CompPowerTrader), "ReceiveCompSignal");
        }

        public static void Postfix(CompPowerTrader __instance)
        {
            Thing parent = __instance == null ? null : __instance.parent;
            if (parent != null && parent.TryGetComp<CompSpaceServicePad>() != null)
            {
                ServiceEnvironmentUtility.ClearPadVacuumCache(parent);
                ServicePadUtility.RequestLifecycleTickSoon(parent.MapHeld ?? parent.Map, "service pad power signal");
            }
        }
    }

    public static class ServicePadRoofChangedPatch
    {
        public static void Postfix(MapDrawer __instance, IntVec3 c)
        {
            Map map = Reflect.GetMember(__instance, "map") as Map;
            if (ServicePadUtility.CellTouchesServicePad(map, c))
            {
                // Roof access is a pad-level safety gate, so wake departures only when a pad tile changed.
                ServiceEnvironmentUtility.ClearPadVacuumCache(map);
                ServicePadUtility.RequestLifecycleTickSoon(map, "service pad roof changed");
            }
        }
    }
}
