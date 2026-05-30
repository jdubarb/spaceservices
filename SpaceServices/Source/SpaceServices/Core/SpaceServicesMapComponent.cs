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
    public sealed class SpaceServicesMapComponent : MapComponent
    {
        public List<ServiceGroupRecord> serviceGroups = new List<ServiceGroupRecord>();
        private readonly List<ScheduledServiceShuttleArrival> pendingShuttleArrivals = new List<ScheduledServiceShuttleArrival>();
        private readonly List<ScheduledHospitalityIncident> pendingHospitalityIncidents = new List<ScheduledHospitalityIncident>();
        private const int StaleReferenceCleanupVersion = 6;
        private int nextDebugTick;
        private int nextLifecycleTick;
        private bool staleReferenceCleanupDone;
        private int staleReferenceCleanupVersion;

        public SpaceServicesMapComponent(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RunStaleReferenceCleanup();
            ReleaseTransientArrivalReservations();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref serviceGroups, "serviceGroups", LookMode.Deep);
            if (serviceGroups == null)
            {
                serviceGroups = new List<ServiceGroupRecord>();
            }
            Scribe_Values.Look(ref staleReferenceCleanupDone, "staleReferenceCleanupDone", false);
            Scribe_Values.Look(ref staleReferenceCleanupVersion, "staleReferenceCleanupVersion", 0);
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            ServiceShuttleUtility.TickPendingArrivals(map, pendingShuttleArrivals);
            TickPendingHospitalityIncidents();
            if (Find.TickManager.TicksGame >= nextLifecycleTick)
            {
                nextLifecycleTick = Find.TickManager.TicksGame + ServiceLifecycleUtility.NextTickInterval(serviceGroups);
                RunStaleReferenceCleanup();
                ServiceLifecycleUtility.Tick(map, serviceGroups);
            }
            if (Find.TickManager.TicksGame < nextDebugTick)
            {
                return;
            }
            nextDebugTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
            {
                SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(map);
                Log.Message("[Space Services] " + eligibility.ToLogString(map));
            }
        }

        private void RunStaleReferenceCleanup()
        {
            if (staleReferenceCleanupDone && staleReferenceCleanupVersion >= StaleReferenceCleanupVersion)
            {
                return;
            }
            staleReferenceCleanupDone = true;
            staleReferenceCleanupVersion = StaleReferenceCleanupVersion;
            StaleReferenceCleanupUtility.CleanupAfterLoad(map);
        }

        private void ReleaseTransientArrivalReservations()
        {
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.reservedForGroup != null && comp.reservedForGroup.StartsWith("hospitality-arrival-", StringComparison.Ordinal))
                {
                    comp.ForceRelease();
                }
            }
        }

        public void ScheduleShuttleArrival(IntVec3 cell, string shuttleThingDefName, List<Thing> things, bool showDeparture)
        {
            if (!cell.IsValid)
            {
                return;
            }
            if ((things == null || things.Count == 0) && !showDeparture)
            {
                return;
            }
            pendingShuttleArrivals.Add(new ScheduledServiceShuttleArrival
            {
                cell = cell,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks,
                shuttleThingDefName = shuttleThingDefName,
                things = things ?? new List<Thing>(),
                showDeparture = showDeparture
            });
        }

        public bool ScheduleHospitalityIncident(object worker, IncidentParms parms, Thing pad, string shuttleThingDefName)
        {
            if (worker == null || parms == null || pad == null || pad.Destroyed || !pad.Spawned)
            {
                return false;
            }
            int expectedBedDemand = HospitalityIncidentGate.EstimatedBedDemand(IncidentDefName(worker), worker);
            if (HospitalityIncidentGate.RequiresGuestBedCapacity() && HospitalityBedUtility.Report(map).freeBeds < expectedBedDemand)
            {
                return false;
            }
            string reservationId = "hospitality-arrival-" + Find.UniqueIDsManager.GetNextThingID();
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null || !comp.TryReserve(reservationId))
            {
                return false;
            }
            pendingHospitalityIncidents.Add(new ScheduledHospitalityIncident
            {
                worker = worker,
                parms = parms,
                pad = pad,
                reservationId = reservationId,
                incidentDefName = IncidentDefName(worker),
                expectedBedDemand = expectedBedDemand,
                shuttleThingDefName = shuttleThingDefName,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks
            });
            return true;
        }

        private void TickPendingHospitalityIncidents()
        {
            if (pendingHospitalityIncidents.Count == 0)
            {
                return;
            }
            for (int i = pendingHospitalityIncidents.Count - 1; i >= 0; i--)
            {
                ScheduledHospitalityIncident incident = pendingHospitalityIncidents[i];
                if (incident == null || Find.TickManager.TicksGame < incident.touchdownTick)
                {
                    continue;
                }
                pendingHospitalityIncidents.RemoveAt(i);
                if (incident.pad == null || incident.pad.Destroyed || !incident.pad.Spawned || incident.worker == null || incident.parms == null)
                {
                    continue;
                }
                if (!HospitalityStillEnabledForMap(map) || !PadStillUsableForGuests(incident.pad))
                {
                    Messages.Message("Space Services: visitor arrival canceled, landing pad is no longer usable", incident.pad, MessageTypeDefOf.RejectInput, false);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                HospitalityBedReport beds = HospitalityBedUtility.Report(map);
                int expectedBedDemand = Math.Max(1, incident.expectedBedDemand);
                if (HospitalityIncidentGate.RequiresGuestBedCapacity() && beds.freeBeds < expectedBedDemand)
                {
                    Messages.Message("Space Services: visitor arrival canceled, no free guest beds", incident.pad, MessageTypeDefOf.RejectInput, false);
                    ServiceDebugUtility.LogThrottled("hospitality-touchdown-beds-" + (incident.incidentDefName ?? ""), "Hospitality visitor arrival canceled at touchdown: need " + expectedBedDemand + ", " + beds.ToSummary(), GenDate.TicksPerHour);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position);
                    ReleaseArrivalReservation(incident);
                    continue;
                }

                ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                incident.parms.spawnCenter = incident.pad.Position;
                ReleaseArrivalReservation(incident);
                HospitalityDelayedIncidentContext.Push(map, incident.pad);
                try
                {
                    MethodInfo method = AccessTools.Method(incident.worker.GetType(), "TryExecuteWorker");
                    method?.Invoke(incident.worker, new object[] { incident.parms });
                }
                catch (Exception ex)
                {
                    Log.Warning("[Space Services] Delayed Hospitality visitor arrival failed: " + ex);
                }
                finally
                {
                    HospitalityDelayedIncidentContext.Pop();
                }
                ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position);
            }
        }

        private static bool PadStillUsableForGuests(Thing pad)
        {
            CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
            return comp != null && comp.MeetsUseRequirements(ServiceUse.Guest);
        }

        private static bool HospitalityStillEnabledForMap(Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return false;
            }
            return map != null && SpaceServiceMapDetector.IsServiceEligible(map);
        }

        private static void ReleaseArrivalReservation(ScheduledHospitalityIncident incident)
        {
            CompSpaceServicePad comp = incident == null || incident.pad == null ? null : incident.pad.TryGetComp<CompSpaceServicePad>();
            if (comp != null)
            {
                comp.Release(incident.reservationId);
            }
        }

        private static string IncidentDefName(object worker)
        {
            IncidentDef incident = Reflect.GetMember(worker, "def") as IncidentDef;
            return incident == null ? "" : incident.defName;
        }
    }

    public sealed class ScheduledServiceShuttleArrival
    {
        public IntVec3 cell;
        public int touchdownTick;
        public string shuttleThingDefName;
        public List<Thing> things = new List<Thing>();
        public bool showDeparture = true;
    }

    public sealed class ScheduledHospitalityIncident
    {
        public object worker;
        public IncidentParms parms;
        public Thing pad;
        public string reservationId;
        public string incidentDefName;
        public int expectedBedDemand;
        public string shuttleThingDefName;
        public int touchdownTick;
    }
}
