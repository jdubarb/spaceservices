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
    public static class HospitalPatientFallback
    {
        public static bool TryExecutePatientArrival(object worker, IncidentParms parms, Map map, IntVec3 forcedCell)
        {
            worker = ResolvePatientArrivalWorker(worker);
            MethodInfo spawnPatient = worker == null ? null : worker.GetType().GetMethods(AccessTools.all).FirstOrDefault(method =>
                method.Name == "SpawnPatient" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(Map) &&
                method.GetParameters()[1].ParameterType == typeof(Pawn));
            if (worker == null || spawnPatient == null)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospital, "Hospital fallback could not find Hospital.IncidentWorker_PatientArrives.SpawnPatient.");
                return false;
            }

            List<Pawn> pawns = ResolvePatientPawns(worker, parms, map);
            if (pawns.Count == 0)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospital, "Hospital fallback could not generate a patient pawn.");
                return false;
            }

            IntVec3 cell = forcedCell.IsValid ? forcedCell : IntVec3.Invalid;
            if (!cell.IsValid && !ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out cell))
            {
                cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 12);
            }

            foreach (Pawn pawn in pawns)
            {
                VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
                HospitalLandingRedirectContext.PushForced(map, cell);
                try
                {
                    spawnPatient.Invoke(worker, new object[] { map, pawn });
                }
                finally
                {
                    HospitalLandingRedirectContext.Pop();
                }
            }

            TryCreateHospitalLord(worker, parms, map, pawns);
            ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
            SendPatientArrivalNotice(pawns[0]);
            ServiceDebugUtility.Log(ServiceLogIntegration.Hospital, "Hospital fallback ran real patient arrival pawns=" + pawns.Count);
            return true;
        }

        private static object ResolvePatientArrivalWorker(object worker)
        {
            if (worker != null)
            {
                return worker;
            }
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("PatientArrives");
            worker = def == null ? null : def.Worker;
            if (worker != null)
            {
                return worker;
            }
            Type type = AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives");
            return type == null ? null : Activator.CreateInstance(type);
        }

        private static List<Pawn> ResolvePatientPawns(object worker, IncidentParms parms, Map map)
        {
            Faction faction = parms.faction ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.OutlanderCivil);
            parms.faction = faction;
            Type helper = AccessTools.TypeByName("Hospital.IncidentHelper");
            MethodInfo generatePawn = helper == null ? null : AccessTools.Method(helper, "GeneratePawn", new[] { typeof(Faction) });
            Pawn pawn = null;
            if (generatePawn != null)
            {
                try
                {
                    pawn = generatePawn.Invoke(null, new object[] { faction }) as Pawn;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Space Services] Hospital fallback could not use IncidentHelper.GeneratePawn: " + ex.Message);
                }
            }
            if (pawn == null)
            {
                PawnKindDef kind = parms.pawnKind ?? PawnKindDefOf.Villager;
                pawn = PawnGenerator.GeneratePawn(kind, faction, map.Tile);
            }
            return pawn == null ? new List<Pawn>() : new List<Pawn> { pawn };
        }

        private static void TryCreateHospitalLord(object worker, IncidentParms parms, Map map, List<Pawn> pawns)
        {
            if (worker == null || parms == null || map == null || pawns == null || pawns.Count == 0)
            {
                return;
            }
            MethodInfo createLordJob = worker.GetType().GetMethods(AccessTools.all).FirstOrDefault(method =>
                method.Name == "CreateLordJob" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(IncidentParms));
            if (createLordJob == null)
            {
                return;
            }
            try
            {
                LordJob lordJob = createLordJob.Invoke(worker, new object[] { parms, pawns }) as LordJob;
                if (lordJob != null)
                {
                    LordMaker.MakeNewLord(parms.faction, lordJob, map, pawns);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Hospital fallback could not create patient lord: " + ex.Message);
            }
        }

        private static void SendPatientArrivalNotice(Pawn pawn)
        {
            Messages.Message("Space Services: Patient Arrived", pawn, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
