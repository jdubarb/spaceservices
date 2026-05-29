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
            if (!cell.IsValid || things == null || things.Count == 0)
            {
                return;
            }
            pendingShuttleArrivals.Add(new ScheduledServiceShuttleArrival
            {
                cell = cell,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks,
                shuttleThingDefName = shuttleThingDefName,
                things = things,
                showDeparture = showDeparture
            });
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
}
