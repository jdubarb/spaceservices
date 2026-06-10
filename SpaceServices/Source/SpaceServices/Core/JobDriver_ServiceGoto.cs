using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public sealed class JobDriver_ServiceGoto : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => pawn == null || pawn.Destroyed || pawn.Downed);
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
        }
    }
}
