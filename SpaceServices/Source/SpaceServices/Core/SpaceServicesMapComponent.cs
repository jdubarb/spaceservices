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
        private const int HospitalitySchedulerBlockedRetryTicks = 15000;
        private int nextDebugTick;
        private int nextLifecycleTick;
        private int nextHospitalityServiceVisitTick;
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
            Scribe_Values.Look(ref nextHospitalityServiceVisitTick, "nextHospitalityServiceVisitTick", 0);
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            ServiceShuttleUtility.TickPendingArrivals(map, pendingShuttleArrivals);
            TickPendingHospitalityIncidents();
            TickNaturalHospitalityScheduler();
            if (Find.TickManager.TicksGame >= nextLifecycleTick)
            {
                nextLifecycleTick = Find.TickManager.TicksGame + ServiceLifecycleUtility.NextTickInterval(serviceGroups);
                RunStaleReferenceCleanup();
                ServiceLifecycleUtility.Tick(map, serviceGroups);
                WatchServicePadReservations();
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

        private void WatchServicePadReservations()
        {
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                comp?.WatchReservation(serviceGroups);
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
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected invalid input worker=" + (worker != null) + " parms=" + (parms != null) + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad));
                return false;
            }
            int expectedBedDemand = HospitalityIncidentGate.EstimatedBedDemand(IncidentDefName(worker), worker);
            if (HospitalityIncidentGate.RequiresGuestBedCapacity() && HospitalityBedUtility.Report(map).freeBeds < expectedBedDemand)
            {
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected beds need=" + expectedBedDemand + " " + HospitalityBedUtility.Report(map).ToSummary());
                return false;
            }
            string reservationId = "hospitality-arrival-" + Find.UniqueIDsManager.GetNextThingID();
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null || !comp.TryReserve(reservationId))
            {
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected reservation id=" + reservationId + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " comp=" + (comp != null));
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
            ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident queued id=" + reservationId + " incident=" + IncidentDefName(worker) + " beds=" + expectedBedDemand + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " touchdownTick=" + (Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks));
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
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents dropping invalid pending incident reservation=" + incident.reservationId + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad) + " worker=" + (incident.worker != null) + " parms=" + (incident.parms != null));
                    continue;
                }
                if (!HospitalityStillEnabledForMap(map) || !PadStillUsableForGuests(incident.pad))
                {
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents canceled unusable pad reservation=" + incident.reservationId + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
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
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents canceled beds reservation=" + incident.reservationId + " need=" + expectedBedDemand + " " + beds.ToSummary());
                    ServiceDebugUtility.LogThrottled("hospitality-touchdown-beds-" + (incident.incidentDefName ?? ""), "Hospitality visitor arrival canceled at touchdown: need " + expectedBedDemand + ", " + beds.ToSummary(), GenDate.TicksPerHour);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position);
                    ReleaseArrivalReservation(incident);
                    continue;
                }

                ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                incident.parms.spawnCenter = incident.pad.Position;
                ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents executing reservation=" + incident.reservationId + " incident=" + incident.incidentDefName + " spawnCenter=" + incident.parms.spawnCenter + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
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

        private void TickNaturalHospitalityScheduler()
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.hospitalityFallbackScheduler)
            {
                return;
            }
            if (nextHospitalityServiceVisitTick <= 0)
            {
                ScheduleNextHospitalityServiceVisit(InitialHospitalityFallbackDelayTicks());
                return;
            }
            if (Find.TickManager.TicksGame < nextHospitalityServiceVisitTick)
            {
                return;
            }

            string reason;
            if (TryScheduleNaturalHospitalityVisit(out reason))
            {
                ScheduleNextHospitalityServiceVisit(NextHospitalityFallbackIntervalTicks());
                return;
            }

            ServiceDebugUtility.LogThrottled("hospitality-service-scheduler-" + reason, "Hospitality service visit not scheduled: " + reason, GenDate.TicksPerHour);
            ScheduleNextHospitalityServiceVisit(HospitalitySchedulerBlockedRetryTicks);
        }

        private bool TryScheduleNaturalHospitalityVisit(out string reason)
        {
            reason = null;
            if (!HospitalityStillEnabledForMap(map))
            {
                reason = "hospitality disabled or map is not eligible";
                return false;
            }
            if (pendingHospitalityIncidents.Count > 0)
            {
                reason = "visitor shuttle already inbound";
                return false;
            }

            IncidentDef incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail("VisitorGroup");
            IncidentWorker worker = incidentDef == null ? null : incidentDef.Worker;
            if (incidentDef == null || worker == null || AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup") == null)
            {
                reason = "Hospitality visitor worker not available";
                return false;
            }

            Thing pad = ServicePadUtility.TryFindRandomServicePad(map, ServiceUse.Guest);
            if (pad == null)
            {
                reason = "no usable guest service pad";
                return false;
            }
            if (!HospitalityIncidentGate.CanAcceptHospitalityIncident(incidentDef.defName, map, worker))
            {
                reason = HospitalityIncidentGate.ReadinessReport(incidentDef.defName, map, worker);
                return false;
            }
            if (!TryFindHospitalityFaction(out Faction faction))
            {
                reason = "no friendly humanlike guest faction";
                return false;
            }

            IncidentCategoryDef category = incidentDef.category ?? IncidentCategoryDefOf.Misc;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(category, map);
            parms.target = map;
            parms.faction = faction;
            parms.spawnCenter = pad.Position;
            parms.sendLetter = true;

            ShuttleVisual visual = ShuttleVisual.Resolve();
            if (!ScheduleHospitalityIncident(worker, parms, pad, visual == null || visual.shipThingDef == null ? null : visual.shipThingDef.defName))
            {
                reason = "could not reserve service pad";
                return false;
            }

            ServiceShuttleUtility.SpawnArrival(map, pad.Position);
            Messages.Message("Space Services: visitors inbound", pad, MessageTypeDefOf.NeutralEvent, false);
            ServiceDebugUtility.Log("Queued service hospitality visitors from " + faction.Name + " at " + pad.Position);
            return true;
        }

        private static bool TryFindHospitalityFaction(out Faction faction)
        {
            List<Faction> candidates = Find.FactionManager.AllFactionsListForReading
                .Where(f => f != null && !f.IsPlayer && !f.defeated && !f.temporary && f.def != null && f.def.humanlikeFaction && !f.def.hidden && !f.HostileTo(Faction.OfPlayer))
                .ToList();
            if (candidates.Count == 0)
            {
                faction = null;
                return false;
            }
            faction = candidates[Rand.Range(0, candidates.Count)];
            return true;
        }

        private void ScheduleNextHospitalityServiceVisit(int delayTicks)
        {
            nextHospitalityServiceVisitTick = Find.TickManager.TicksGame + Math.Max(delayTicks, GenDate.TicksPerHour);
        }

        private static int InitialHospitalityFallbackDelayTicks()
        {
            return Math.Min(15000, Math.Max(GenDate.TicksPerHour, NextHospitalityFallbackIntervalTicks() / 4));
        }

        private static int NextHospitalityFallbackIntervalTicks()
        {
            float intervalDays = SpaceServicesMod.Settings == null ? 1.5f : SpaceServicesMod.Settings.hospitalityFallbackIntervalDays;
            intervalDays = Mathf.Clamp(intervalDays, 0.5f, 5f);
            float variedDays = Rand.Range(intervalDays * 0.85f, intervalDays * 1.15f);
            return Math.Max(GenDate.TicksPerHour, Mathf.RoundToInt(variedDays * GenDate.TicksPerDay));
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
