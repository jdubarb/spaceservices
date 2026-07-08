using RimWorld;
using Verse;

namespace SpaceServices
{
    public static class ServiceJobDefUtility
    {
        private static JobDef boardServiceShuttle;
        private static JobDef serviceDepartureHold;
        private static JobDef serviceGoto;

        public static JobDef BoardServiceShuttle
        {
            get
            {
                if (boardServiceShuttle == null)
                {
                    boardServiceShuttle = DefDatabase<JobDef>.GetNamedSilentFail("JDB_BoardServiceShuttle");
                }
                return boardServiceShuttle;
            }
        }

        public static JobDef ServiceGoto
        {
            get
            {
                if (serviceGoto == null)
                {
                    serviceGoto = DefDatabase<JobDef>.GetNamedSilentFail("JDB_ServiceGoto");
                }
                return serviceGoto;
            }
        }

        public static JobDef ServiceDepartureHold
        {
            get
            {
                if (serviceDepartureHold == null)
                {
                    serviceDepartureHold = DefDatabase<JobDef>.GetNamedSilentFail("JDB_ServiceDepartureHold");
                }
                return serviceDepartureHold;
            }
        }
    }
}
