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

        public void ScheduleHospitalityIncident(object worker, IncidentParms parms, Thing pad, string shuttleThingDefName)
        {
            if (worker == null || parms == null || pad == null || pad.Destroyed || !pad.Spawned)
            {
                return;
            }
            pendingHospitalityIncidents.Add(new ScheduledHospitalityIncident
            {
                worker = worker,
                parms = parms,
                pad = pad,
                shuttleThingDefName = shuttleThingDefName,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks
            });
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
                if (!HospitalityIncidentGate.CanAcceptHospitalityIncident("VisitorGroup", map) || !PadStillUsableForGuests(incident.pad))
                {
                    Messages.Message("Space Services: visitor arrival canceled, landing pad is no longer usable", incident.pad, MessageTypeDefOf.RejectInput, false);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position);
                    continue;
                }

                ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                incident.parms.spawnCenter = incident.pad.Position;
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
            return comp != null && comp.IsUsableFor(ServiceUse.Guest);
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
        public string shuttleThingDefName;
        public int touchdownTick;
    }
}
