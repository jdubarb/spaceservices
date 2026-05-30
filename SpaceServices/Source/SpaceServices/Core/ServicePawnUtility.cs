using RimWorld;
using Verse;

namespace SpaceServices
{
    public static class ServicePawnUtility
    {
        public static bool IsTerminalPawn(Pawn pawn)
        {
            return pawn == null || pawn.Destroyed || pawn.Dead;
        }

        public static bool IsPlayerOwnedPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (pawn.Faction == Faction.OfPlayer || pawn.IsColonist)
            {
                return true;
            }
            return IsTrue(pawn, "IsColonistPlayerControlled") ||
                IsTrue(pawn, "IsPrisonerOfColony") ||
                IsTrue(pawn, "IsSlaveOfColony");
        }

        private static bool IsTrue(object instance, string memberName)
        {
            object value = Reflect.GetMember(instance, memberName);
            return value is bool flag && flag;
        }
    }
}
