using RimWorld;
using Verse;

namespace SpaceServices
{
    public static class ServiceDangerUtility
    {
        public static bool HasActiveHostileThreat(Map map)
        {
            return map != null && GenHostility.AnyHostileActiveThreatToPlayer(map, true);
        }

        public static bool HospitalityTrafficBlocked(Map map, out string reason)
        {
            SpaceServicesMapComponent comp = map == null ? null : map.GetComponent<SpaceServicesMapComponent>();
            if (comp != null && comp.debugForceHospitalityDanger)
            {
                reason = "debug forced danger";
                return true;
            }
            if (HasActiveHostileThreat(map))
            {
                reason = "active hostile threat";
                return true;
            }
            reason = null;
            return false;
        }
    }
}
