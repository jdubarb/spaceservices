using HarmonyLib;
using RimWorld;
using System.Reflection;
using Verse;

namespace SpaceServices
{
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
                ServicePadUtility.RequestLifecycleTickSoon(parent.MapHeld ?? parent.Map, "service pad power signal");
            }
        }
    }

    [HarmonyPatch]
    public static class ServicePadRoofChangedPatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MapDrawer), "Notify_RoofChanged");
        }

        public static void Postfix(MapDrawer __instance, IntVec3 c)
        {
            Map map = Reflect.GetMember(__instance, "map") as Map;
            if (ServicePadUtility.CellTouchesServicePad(map, c))
            {
                // Roof access is a pad-level safety gate, so wake departures only when a pad tile changed.
                ServicePadUtility.RequestLifecycleTickSoon(map, "service pad roof changed");
            }
        }
    }
}
