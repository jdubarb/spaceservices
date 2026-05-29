using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class OptionalPatchHandlers
    {
        public static void SpaceportsIsMapInSpacePostfix(Map map, ref bool __result)
        {
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.enableSpaceportsBridge && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                __result = true;
            }
        }

        public static bool SpaceportsSuitUpPawnsPrefix(List<Pawn> pawns)
        {
            VacSuitUtility.SuitPawnsForVacuum(pawns);
            return true;
        }

        public static void SpaceportsHospitalityShuttleCheckPostfix(Map map, Faction faction, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (faction != null && faction.def != null && faction.def.techLevel == TechLevel.Neolithic)
            {
                return;
            }
            __result = true;
        }

        public static void SpaceportsCheckIfClearForLandingPostfix(Map map, int typeVal, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (typeVal != 1 && typeVal != 3)
            {
                return;
            }
            if (!SpaceServicesMod.Settings.enableHospitality || ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return;
            }
            if (HasBlockingLandingCondition(map))
            {
                return;
            }
            __result = true;
        }

        public static void HospitalLandingSpotPostfix(object[] __args, ref IntVec3 __result)
        {
            Map map = FindMap(__args);
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 cell))
            {
                __result = cell;
            }
        }

        public static void HospitalCanSpawnPatientPostfix(Map map, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            __result = ServiceIncidentUtility.ShouldForceAllow("PatientArrives", map);
        }

        public static bool HospitalPatientArrivesTryExecutePrefix(object __instance, IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            string incidentDefName = incident == null ? (IsMassCasualtyIncident(__instance, null) ? "MassCasualtyEvent" : "PatientArrives") : incident.defName;
            if (HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map))
            {
                HospitalArrivalIncidentContext.Push(map, IsMassCasualtyIncident(__instance, incidentDefName));
                return true;
            }
            Log.Message("[Space Services] Hospital patient incident blocked: " + HospitalIncidentGate.ReadinessReport(incidentDefName, map));
            __result = false;
            return false;
        }

        public static void HospitalTryFindEntryCellPostfix(Map map, ref IntVec3 cell, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 serviceCell))
            {
                cell = serviceCell;
                __result = true;
            }
        }

        public static void HospitalSpawnPatientPrefix(MethodBase __originalMethod, object[] __args)
        {
            Map map = FindMap(__args);
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalLandingRedirectContext.Push(null, IntVec3.Invalid, null);
                return;
            }

            IntVec3 cell;
            if (!HospitalLandingRedirectContext.TryGetForcedCell(map, out cell) &&
                !HospitalArrivalIncidentContext.TryGetNextArrivalCell(map, out cell) &&
                !ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out cell))
            {
                cell = IntVec3.Invalid;
            }
            foreach (Pawn pawn in PawnsFromArgs(__args))
            {
                VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
            }
            Thing tempLandingSpot = HospitalLandingRedirectContext.CreateTemporaryPatientLandingSpot(map, cell);
            HospitalLandingRedirectContext.Push(map, cell, tempLandingSpot);
        }

        public static void HospitalSpawnPatientPostfix(object[] __args)
        {
            try
            {
                Map map = FindMap(__args);
                List<Pawn> pawns = PawnsFromArgs(__args).Distinct().ToList();
                IntVec3 arrivalCell = IntVec3.Invalid;
                bool hasArrivalCell = map != null && HospitalLandingRedirectContext.TryGetActiveCell(map, out arrivalCell);
                foreach (Pawn pawn in pawns)
                {
                    IntVec3 cell = hasArrivalCell ? arrivalCell : pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
                    VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
                }
                if (map != null && pawns.Count > 0 && SpaceServiceMapDetector.IsServiceEligible(map))
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
                }
            }
            finally
            {
                HospitalLandingRedirectContext.Pop();
            }
        }

        public static bool HospitalDropPodAtPrefix(ref IntVec3 c, Map map, ActiveTransporterInfo info, Faction faction)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            if (HospitalLandingRedirectContext.TryGetActiveCell(map, out IntVec3 cell) && cell.IsValid)
            {
                c = cell;
            }
            if (info != null && info.innerContainer != null)
            {
                List<Pawn> pawns = new List<Pawn>();
                foreach (Thing thing in info.innerContainer)
                {
                    Pawn pawn = thing as Pawn;
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
                VacSuitUtility.SuitPawnsForEnvironment(pawns, map, c);
            }
            HospitalArrivalIncidentContext.ArrivalVisualFlags(map, out bool showArrival, out bool showDeparture);
            if (HospitalLandingRedirectContext.TryGetActiveCell(map, out IntVec3 activeCell) && activeCell.IsValid && ServiceShuttleUtility.TryReplaceDropPodWithArrivalShuttle(c, map, info, faction, showArrival, showDeparture))
            {
                return false;
            }
            return true;
        }

        public static void HospitalPatientDeparturePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospital requested patient departure");
        }

        public static void HospitalPatientGonePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital removed patient from map");
        }

        public static void SuitPawnsInArgsPostfix(MethodBase __originalMethod, object[] __args)
        {
            Map map = FindMap(__args);
            if (map != null && !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            List<Pawn> pawns = PawnsFromArgs(__args).Distinct().ToList();
            foreach (Pawn pawn in pawns)
            {
                IntVec3 cell = pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
                VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
            }
            string methodName = __originalMethod == null || __originalMethod.DeclaringType == null ? "" : __originalMethod.DeclaringType.FullName ?? "";
            if (map != null && pawns.Count > 0)
            {
                if (methodName.IndexOf("Hospital.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
                }
                else if (methodName.IndexOf("Hospitality.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns);
                }
            }
        }

        public static void HospitalPatientArrivesTryExecutePostfix(object __instance, IncidentParms parms, ref bool __result)
        {
            try
            {
                if (__result || __instance == null || parms == null)
                {
                    return;
                }
                Map map = parms.target as Map;
                if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
                {
                    return;
                }
                if (!HospitalIncidentGate.CanAcceptHospitalIncident("PatientArrives", map))
                {
                    Log.Message("[Space Services] Hospital PatientArrives returned false; fallback blocked: " + HospitalIncidentGate.ReadinessReport("PatientArrives", map));
                    return;
                }
                try
                {
                    __result = HospitalPatientFallback.TryExecutePatientArrival(__instance, parms, map, IntVec3.Invalid);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Space Services] Hospital patient fallback failed: " + ex);
                }
            }
            finally
            {
                HospitalArrivalIncidentContext.Pop();
            }
        }

        public static void HospitalMassCasualtyTryExecutePostfix()
        {
            HospitalArrivalIncidentContext.Pop();
        }

        private static bool HasBlockingLandingCondition(Map map)
        {
            if (map == null)
            {
                return true;
            }
            GameConditionDef kessler = DefDatabase<GameConditionDef>.GetNamedSilentFail("Spaceports_KesslerSyndrome");
            if (kessler != null && map.gameConditionManager.ConditionIsActive(kessler))
            {
                return true;
            }
            if (GenHostility.AnyHostileActiveThreatToPlayer(map, true))
            {
                return true;
            }
            return false;
        }

        private static IEnumerable<Pawn> PawnsFromArgs(object[] args)
        {
            foreach (object arg in args ?? new object[0])
            {
                Pawn pawn = arg as Pawn;
                if (pawn != null)
                {
                    yield return pawn;
                    continue;
                }
                IEnumerable<Pawn> pawns = arg as IEnumerable<Pawn>;
                if (pawns != null)
                {
                    foreach (Pawn p in pawns)
                    {
                        yield return p;
                    }
                }
            }
        }

        private static Map FindMap(object[] args)
        {
            foreach (object arg in args ?? new object[0])
            {
                Map map = arg as Map;
                if (map != null)
                {
                    return map;
                }
                IncidentParms parms = arg as IncidentParms;
                if (parms != null)
                {
                    Map targetMap = parms.target as Map;
                    if (targetMap != null)
                    {
                        return targetMap;
                    }
                }
                Pawn pawn = arg as Pawn;
                if (pawn != null && pawn.MapHeld != null)
                {
                    return pawn.MapHeld;
                }
            }
            return Find.CurrentMap;
        }

        private static bool IsMassCasualtyIncident(object worker, string incidentDefName)
        {
            if (incidentDefName == "MassCasualtyEvent")
            {
                return true;
            }
            string typeName = worker == null ? "" : worker.GetType().FullName ?? "";
            return typeName.IndexOf("MassCasualty", StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }

}
