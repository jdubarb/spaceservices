using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace SpaceServices
{
    public sealed class JobDriver_ServiceDepartureHold : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => pawn == null || pawn.Destroyed || pawn.Downed);
            this.FailOn(() => !ServiceLifecycleUtility.ShouldHoldPawnForServiceDeparture(pawn));
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            yield return Toils_General.Wait(ServiceLifecycleUtility.DepartureHoldWanderJobTicks);
        }
    }
}
