using RimWorld;
using System.Collections.Generic;
using Verse;
using Verse.AI;
using Verse.AI.Group;

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

        public static int ClearRuntimeLordReferences(Pawn pawn)
        {
            if (pawn == null)
            {
                return 0;
            }
            int cleared = 0;
            cleared += ClearJobLord(pawn.CurJob) ? 1 : 0;
            foreach (ThingComp comp in pawn.AllComps ?? new List<ThingComp>())
            {
                if (comp == null)
                {
                    continue;
                }
                Lord lord = Reflect.GetMember(comp, "lord") as Lord;
                if (lord == null)
                {
                    continue;
                }
                Reflect.SetMember(comp, "lord", null);
                cleared++;
            }
            return cleared;
        }

        public static bool ClearJobLord(Job job)
        {
            if (job != null && job.lord != null)
            {
                job.lord = null;
                return true;
            }
            return false;
        }
    }
}
