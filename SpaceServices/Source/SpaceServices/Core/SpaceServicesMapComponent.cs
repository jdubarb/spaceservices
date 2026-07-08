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
        public List<PrebuildPadModeRecord> prebuildPadModes = new List<PrebuildPadModeRecord>();
        private List<ScheduledServiceShuttleArrival> pendingShuttleArrivals = new List<ScheduledServiceShuttleArrival>();
        private List<ScheduledHospitalityIncident> pendingHospitalityIncidents = new List<ScheduledHospitalityIncident>();
        private const int StaleReferenceCleanupVersion = 10;
        private const int HospitalitySchedulerBlockedRetryTicks = 15000;
        private const int HospitalityLordReferenceCleanupTicks = 2500;
        private int nextDebugTick;
        private int nextLifecycleTick;
        private int nextHospitalityLordReferenceCleanupTick;
        private int nextReservationWatchTick;
        private bool servicePadCacheDirty = true;
        private List<Thing> cachedServicePads = new List<Thing>();
        private int nextHospitalityServiceVisitTick;
        private bool staleReferenceCleanupDone;
        private int staleReferenceCleanupVersion;
        private bool hospitalityGuestAreaTipShown;
        private int debugLimitSemanticsVersion;
        private Dictionary<string, float> trafficRateProgressByIncident = new Dictionary<string, float>();
        private List<string> trafficRateProgressKeys;
        private List<float> trafficRateProgressValues;
        private int lastServiceArrivalTick = -999999;
        internal const float MinimumHospitalityVisitorPoints = 40f;
        public int debugHospitalPatientLimit = -1;
        public int debugHospitalityGroupLimit = -1;
        public int debugHospitalityPawnLimit = -1;

        public SpaceServicesMapComponent(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ServiceLifecycleUtility.ClearTransientCaches();
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
            Scribe_Collections.Look(ref prebuildPadModes, "prebuildPadModes", LookMode.Deep);
            if (prebuildPadModes == null)
            {
                prebuildPadModes = new List<PrebuildPadModeRecord>();
            }
            Scribe_Values.Look(ref staleReferenceCleanupDone, "staleReferenceCleanupDone", false);
            Scribe_Values.Look(ref staleReferenceCleanupVersion, "staleReferenceCleanupVersion", 0);
            Scribe_Values.Look(ref nextHospitalityServiceVisitTick, "nextHospitalityServiceVisitTick", 0);
            Scribe_Values.Look(ref hospitalityGuestAreaTipShown, "hospitalityGuestAreaTipShown", false);
            Scribe_Values.Look(ref lastServiceArrivalTick, "lastServiceArrivalTick", -999999);
            Scribe_Collections.Look(ref trafficRateProgressByIncident, "trafficRateProgressByIncident", LookMode.Value, LookMode.Value, ref trafficRateProgressKeys, ref trafficRateProgressValues);
            if (trafficRateProgressByIncident == null)
            {
                trafficRateProgressByIncident = new Dictionary<string, float>();
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                debugLimitSemanticsVersion = 1;
            }
            Scribe_Values.Look(ref debugLimitSemanticsVersion, "debugLimitSemanticsVersion", 0);
            Scribe_Values.Look(ref debugHospitalPatientLimit, "debugHospitalPatientLimit", -1);
            Scribe_Values.Look(ref debugHospitalityGroupLimit, "debugHospitalityGroupLimit", -1);
            Scribe_Values.Look(ref debugHospitalityPawnLimit, "debugHospitalityPawnLimit", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && debugLimitSemanticsVersion < 1)
            {
                // Previous dev builds used 0 as unlimited. New UI uses 0 as "block arrivals".
                if (debugHospitalPatientLimit == 0)
                {
                    debugHospitalPatientLimit = -1;
                }
                if (debugHospitalityGroupLimit == 0)
                {
                    debugHospitalityGroupLimit = -1;
                }
                if (debugHospitalityPawnLimit == 0)
                {
                    debugHospitalityPawnLimit = -1;
                }
                debugLimitSemanticsVersion = 1;
            }
            Scribe_Collections.Look(ref pendingShuttleArrivals, "pendingShuttleArrivals", LookMode.Deep);
            Scribe_Collections.Look(ref pendingHospitalityIncidents, "pendingHospitalityIncidents", LookMode.Deep);
            if (pendingShuttleArrivals == null)
            {
                pendingShuttleArrivals = new List<ScheduledServiceShuttleArrival>();
            }
            if (pendingHospitalityIncidents == null)
            {
                pendingHospitalityIncidents = new List<ScheduledHospitalityIncident>();
            }
        }

        public bool TrafficRateAllows(string incidentDefName, int sharedCadenceTicks)
        {
            float rate = ServiceIncidentUtility.TrafficRateFor(incidentDefName);
            if (rate <= 0f)
            {
                return false;
            }
            if (!SharedTrafficCadenceReady(sharedCadenceTicks))
            {
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Service traffic blocked by shared one-hour cadence: " + (incidentDefName ?? "unknown"));
                return false;
            }
            if (rate >= 1f)
            {
                MarkSharedTrafficUsed();
                return true;
            }

            // This is deterministic pacing, not a random chance. Progress is saved with the map so
            // repeated reloads cannot reroll service traffic.
            string key = incidentDefName ?? "";
            float progress;
            trafficRateProgressByIncident.TryGetValue(key, out progress);
            progress += rate;
            if (progress >= 1f)
            {
                trafficRateProgressByIncident[key] = progress - 1f;
                MarkSharedTrafficUsed();
                return true;
            }
            trafficRateProgressByIncident[key] = progress;
            return false;
        }

        public bool TryConsumeSharedTrafficSlot(int sharedCadenceTicks)
        {
            if (!SharedTrafficCadenceReady(sharedCadenceTicks))
            {
                return false;
            }
            MarkSharedTrafficUsed();
            return true;
        }

        private bool SharedTrafficCadenceReady(int sharedCadenceTicks)
        {
            return Find.TickManager == null ||
                lastServiceArrivalTick <= 0 ||
                Find.TickManager.TicksGame - lastServiceArrivalTick >= sharedCadenceTicks;
        }

        private void MarkSharedTrafficUsed()
        {
            if (Find.TickManager != null)
            {
                lastServiceArrivalTick = Find.TickManager.TicksGame;
            }
        }

        public void NotifyServicePadPlaced(Thing pad, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || hospitalityGuestAreaTipShown || pad == null || map == null)
            {
                return;
            }
            if (AccessTools.TypeByName("Hospitality.Hospitality_MapComponent") == null)
            {
                return;
            }
            hospitalityGuestAreaTipShown = true;
            Find.WindowStack.Add(new Dialog_MessageBox("JDB_SpaceServices_Message_HospitalityGuestAreaTip".Translate(), "OK".Translate()));
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (IsDormant())
            {
                return;
            }
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
            if (Find.TickManager.TicksGame >= nextHospitalityLordReferenceCleanupTick)
            {
                nextHospitalityLordReferenceCleanupTick = Find.TickManager.TicksGame + HospitalityLordReferenceCleanupTicks;
                StaleReferenceCleanupUtility.CleanupInvalidHospitalityLordReferences(map);
            }
            if (Find.TickManager.TicksGame < nextDebugTick)
            {
                return;
            }
            nextDebugTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
            {
                SpaceServiceEligibility eligibility = SpaceServiceMapDetector.EvaluateServiceAccess(map);
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, eligibility.ToLogString(map));
            }
        }

        private bool IsDormant()
        {
            if (pendingShuttleArrivals.Count > 0 || pendingHospitalityIncidents.Count > 0)
            {
                return false;
            }
            for (int i = 0; i < serviceGroups.Count; i++)
            {
                ServiceGroupRecord group = serviceGroups[i];
                if (group != null && group.state != "completed")
                {
                    return false;
                }
            }
            List<Thing> pads = CachedServicePadBuildings();
            for (int i = 0; i < pads.Count; i++)
            {
                if (ServicePadUtility.IsActivePadBuilding(pads[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public void RequestLifecycleTickSoon(string reason)
        {
            if (Find.TickManager == null)
            {
                nextLifecycleTick = 0;
                return;
            }
            // Transition hooks call this when Hospital or Hospitality already told us something changed.
            // The periodic lifecycle tick remains as a watchdog for states we do not patch directly.
            if (ShouldResetDeparturePadReservationRetries(reason))
            {
                for (int i = 0; i < serviceGroups.Count; i++)
                {
                    if (serviceGroups[i] != null)
                    {
                        serviceGroups[i].nextDeparturePadReservationTick = 0;
                    }
                }
            }
            nextLifecycleTick = Math.Min(nextLifecycleTick, Find.TickManager.TicksGame);
            ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Core, "Lifecycle tick requested: " + (reason ?? "unspecified"));
        }

        private static bool ShouldResetDeparturePadReservationRetries(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return false;
            }
            return reason.IndexOf("reservation released", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("reservation force released", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad spawned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad despawned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad power", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad roof", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("service pad mode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("debug cleared", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("debug reset", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RunStaleReferenceCleanup()
        {
            if (staleReferenceCleanupDone && staleReferenceCleanupVersion >= StaleReferenceCleanupVersion)
            {
                return;
            }
            try
            {
                StaleReferenceCleanupUtility.CleanupAfterLoad(map);
                staleReferenceCleanupDone = true;
                staleReferenceCleanupVersion = StaleReferenceCleanupVersion;
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Core, "Stale reference cleanup failed and will retry later: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private void ReleaseTransientArrivalReservations()
        {
            // Arrival reservations are short-lived visual locks. If a save happens during touchdown and
            // the pending incident is gone on reload, release the pad instead of leaving it stuck forever.
            HashSet<string> pendingReservations = new HashSet<string>();
            foreach (ScheduledHospitalityIncident incident in pendingHospitalityIncidents)
            {
                if (incident != null && !string.IsNullOrEmpty(incident.reservationId))
                {
                    pendingReservations.Add(incident.reservationId);
                }
            }
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null &&
                    comp.reservedForGroup != null &&
                    comp.reservedForGroup.StartsWith("hospitality-arrival-", StringComparison.Ordinal) &&
                    !pendingReservations.Contains(comp.reservedForGroup))
                {
                    comp.ForceRelease();
                }
            }
        }

        public void DirtyServicePadCache()
        {
            servicePadCacheDirty = true;
        }

        public List<Thing> CachedServicePadBuildings()
        {
            if (cachedServicePads == null)
            {
                cachedServicePads = new List<Thing>();
                servicePadCacheDirty = true;
            }
            if (!servicePadCacheDirty)
            {
                cachedServicePads.RemoveAll(pad => pad == null || pad.Destroyed || pad.Map != map || pad.TryGetComp<CompSpaceServicePad>() == null);
                return cachedServicePads;
            }

            servicePadCacheDirty = false;
            cachedServicePads.Clear();
            if (map == null || map.listerBuildings == null)
            {
                return cachedServicePads;
            }
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building != null && !building.Destroyed && building.TryGetComp<CompSpaceServicePad>() != null)
                {
                    cachedServicePads.Add(building);
                }
            }
            return cachedServicePads;
        }

        public void NotifyServicePadUnavailable(Thing pad, IntVec3 oldPosition, string reason)
        {
            if (pad == null)
            {
                return;
            }

            DirtyServicePadCache();
            int recordsReset = ResetRecordsForUnavailablePad(pad, oldPosition, reason);
            int pendingCanceled = CancelPendingHospitalityIncidentsForPad(pad, oldPosition, reason);
            int shuttlesRemoved = oldPosition.IsValid
                ? ServiceShuttleUtility.CleanupServiceShuttlesForPadFootprint(map, oldPosition, pad.def == null ? new IntVec2(7, 7) : pad.def.size, null)
                : ServiceShuttleUtility.CleanupServiceShuttlesForPad(pad, null);
            RequestLifecycleTickSoon(reason ?? "service pad unavailable");
            ServiceDebugUtility.LogAudit("Service pad unavailable recovery reason=" + (reason ?? "none") + " records=" + recordsReset + " pending=" + pendingCanceled + " shuttles=" + shuttlesRemoved + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad));
        }

        public void ClearAllServiceReservations(string reason)
        {
            int cleared = 0;
            foreach (Thing pad in AllServicePadBuildingsIncludingInactive())
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && !string.IsNullOrEmpty(comp.reservedForGroup))
                {
                    comp.ForceRelease();
                    cleared++;
                }
            }
            foreach (ServiceGroupRecord record in serviceGroups)
            {
                if (record != null)
                {
                    record.reservedPad = null;
                    if (record.state == "pickupInbound" || record.state == "boardingPickup")
                    {
                        record.state = "departing";
                        record.pickupShuttleTouchdownTick = 0;
                        record.pickupShuttleThingDefName = null;
                    }
                }
            }
            foreach (ScheduledHospitalityIncident incident in pendingHospitalityIncidents)
            {
                ReleaseArrivalReservation(incident);
            }
            pendingHospitalityIncidents.Clear();
            RequestLifecycleTickSoon(reason ?? "debug cleared all service reservations");
            Messages.Message("Space Services: Cleared " + cleared + " Pad Reservations", MessageTypeDefOf.NeutralEvent, false);
        }

        private IEnumerable<Thing> AllServicePadBuildingsIncludingInactive()
        {
            if (map == null || map.listerBuildings == null)
            {
                yield break;
            }
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building != null && !building.Destroyed && building.TryGetComp<CompSpaceServicePad>() != null)
                {
                    yield return building;
                }
            }
        }

        public void DebugResetServiceTraffic(string reason)
        {
            ClearAllServiceReservations(reason ?? "debug reset service traffic");
            trafficRateProgressByIncident.Clear();
            lastServiceArrivalTick = -999999;
            int shuttlesRemoved = ServiceShuttleUtility.CleanupAllServiceShuttles(map);
            foreach (ServiceGroupRecord record in serviceGroups)
            {
                if (record == null || record.state == "completed" || record.state == "extracting")
                {
                    continue;
                }
                if (record.state == "pickupInbound" || record.state == "boardingPickup")
                {
                    record.state = "departing";
                }
                record.pickupShuttleTouchdownTick = 0;
                record.pickupShuttleThingDefName = null;
            }
            RequestLifecycleTickSoon(reason ?? "debug reset service traffic");
            Messages.Message("Space Services: Reset Service Traffic and Removed " + shuttlesRemoved + " Service Shuttles", MessageTypeDefOf.NeutralEvent, false);
        }

        private int ResetRecordsForUnavailablePad(Thing pad, IntVec3 oldPosition, string reason)
        {
            int reset = 0;
            foreach (ServiceGroupRecord record in serviceGroups)
            {
                if (record == null)
                {
                    continue;
                }
                if (record.arrivalPad == pad)
                {
                    record.arrivalPad = null;
                }
                if (record.reservedPad != pad)
                {
                    continue;
                }
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                comp?.Release(record.id);
                record.reservedPad = null;
                if (record.state == "pickupInbound" || record.state == "boardingPickup")
                {
                    record.state = "departing";
                    record.pickupShuttleTouchdownTick = 0;
                    record.pickupShuttleThingDefName = null;
                }
                reset++;
            }
            return reset;
        }

        private int CancelPendingHospitalityIncidentsForPad(Thing pad, IntVec3 oldPosition, string reason)
        {
            int canceled = 0;
            for (int i = pendingHospitalityIncidents.Count - 1; i >= 0; i--)
            {
                ScheduledHospitalityIncident incident = pendingHospitalityIncidents[i];
                if (incident == null || incident.pad != pad)
                {
                    continue;
                }
                pendingHospitalityIncidents.RemoveAt(i);
                ReleaseArrivalReservation(incident);
                canceled++;
                if (oldPosition.IsValid)
                {
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, oldPosition, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, oldPosition, "hospitality", incident.shuttleVisualDefName);
                }
            }
            return canceled;
        }

        private void WatchServicePadReservations()
        {
            if (Find.TickManager != null && Find.TickManager.TicksGame < nextReservationWatchTick)
            {
                return;
            }
            nextReservationWatchTick = (Find.TickManager == null ? 0 : Find.TickManager.TicksGame) + GenDate.TicksPerHour;
            foreach (Thing pad in ServicePadUtility.AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
                comp?.WatchReservation(serviceGroups);
            }
        }

        public void ScheduleShuttleArrival(IntVec3 cell, string shuttleThingDefName, string shuttleVisualDefName, List<Thing> things, bool showDeparture, string serviceKind = null)
        {
            if (!cell.IsValid)
            {
                return;
            }
            if ((things == null || things.Count == 0) && !showDeparture)
            {
                return;
            }
            ScheduledServiceShuttleArrival arrival = new ScheduledServiceShuttleArrival
            {
                cell = cell,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks,
                shuttleThingDefName = shuttleThingDefName,
                shuttleVisualDefName = shuttleVisualDefName,
                showDeparture = showDeparture,
                serviceKind = serviceKind
            };
            foreach (Thing thing in things ?? Enumerable.Empty<Thing>())
            {
                if (thing != null && !thing.Destroyed)
                {
                    // Keep delayed arrivals in a real holder so RimWorld does not treat owned patients as free world pawns.
                    arrival.things.TryAddOrTransfer(thing, canMergeWithExistingStacks: false);
                }
            }
            if (arrival.things.Count == 0 && !showDeparture)
            {
                return;
            }
            pendingShuttleArrivals.Add(arrival);
        }

        public bool ScheduleHospitalityIncident(object worker, IncidentParms parms, Thing pad, string shuttleThingDefName, string shuttleVisualDefName, out string reason)
        {
            reason = null;
            if (worker == null || parms == null || pad == null || pad.Destroyed || !pad.Spawned)
            {
                reason = "invalid scheduled visitor input";
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected invalid input worker=" + (worker != null) + " parms=" + (parms != null) + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad));
                return false;
            }
            int expectedBedDemand = HospitalityIncidentGate.EstimatedBedDemandForIncident(IncidentDefName(worker), worker, parms, map);
            if (HospitalityIncidentGate.RequiresGuestBedCapacity() && HospitalityBedUtility.Report(map).freeBeds < expectedBedDemand)
            {
                reason = "not enough free guest beds, need " + expectedBedDemand + " (" + HospitalityBedUtility.Report(map).ToSummary() + ")";
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected beds need=" + expectedBedDemand + " " + HospitalityBedUtility.Report(map).ToSummary());
                return false;
            }
            if (ServiceDangerUtility.HospitalityTrafficBlocked(map, out string dangerReason))
            {
                reason = dangerReason;
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected danger=" + dangerReason);
                return false;
            }
            if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospitality", out string trafficReason))
            {
                reason = trafficReason;
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected traffic hazard=" + trafficReason);
                return false;
            }
            string reservationId = "hospitality-arrival-" + Find.UniqueIDsManager.GetNextThingID();
            CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null || !comp.TryReserve(reservationId))
            {
                reason = comp == null ? "pad is not a service pad" : "pad already reserved";
                ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident rejected reservation id=" + reservationId + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " comp=" + (comp != null));
                return false;
            }
            IncidentParms savedParms = CloneHospitalityParms(parms, map, pad);
            // The Hospitality incident still does the actual pawn generation. We only delay it until the
            // shuttle visual reaches touchdown, so any native faction/guest setup remains intact.
            pendingHospitalityIncidents.Add(new ScheduledHospitalityIncident
            {
                worker = worker,
                parms = savedParms,
                pad = pad,
                incidentDef = Reflect.GetMember(worker, "def") as IncidentDef,
                faction = savedParms.faction,
                spawnCenter = savedParms.spawnCenter,
                sendLetter = savedParms.sendLetter,
                points = savedParms.points,
                pawnCount = savedParms.pawnCount,
                pawnKind = savedParms.pawnKind,
                reservationId = reservationId,
                incidentDefName = IncidentDefName(worker),
                expectedBedDemand = expectedBedDemand,
                shuttleThingDefName = shuttleThingDefName,
                shuttleVisualDefName = shuttleVisualDefName,
                touchdownTick = Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks
            });
            ServiceDebugUtility.LogAudit("ScheduleHospitalityIncident queued id=" + reservationId + " incident=" + IncidentDefName(worker) + " beds=" + expectedBedDemand + " pad=" + ServiceDebugUtility.ThingAuditSummary(pad) + " touchdownTick=" + (Find.TickManager.TicksGame + ServiceShuttleUtility.ArrivalTouchdownDelayTicks));
            return true;
        }

        private static IncidentParms CloneHospitalityParms(IncidentParms source, Map map, Thing pad)
        {
            IncidentParms clone = new IncidentParms
            {
                target = map,
                faction = source == null ? null : source.faction,
                spawnCenter = pad != null && !pad.Destroyed ? pad.Position : source == null ? IntVec3.Invalid : source.spawnCenter,
                sendLetter = source == null || source.sendLetter,
                points = Math.Max(source == null ? 0f : source.points, MinimumHospitalityVisitorPoints),
                pawnCount = source == null ? 0 : source.pawnCount,
                pawnKind = source == null ? null : source.pawnKind
            };
            if (source != null)
            {
                clone.quest = source.quest;
                clone.raidArrivalMode = source.raidArrivalMode;
                clone.raidStrategy = source.raidStrategy;
            }
            return clone;
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
                incident.EnsureRuntime(map);
                if (incident.pad == null || incident.pad.Destroyed || !incident.pad.Spawned || incident.worker == null || incident.parms == null)
                {
                    string invalidReason = "scheduled visitor data became invalid";
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents dropping invalid pending incident reservation=" + incident.reservationId + " reason=" + invalidReason + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad) + " worker=" + (incident.worker != null) + " parms=" + (incident.parms != null));
                    Messages.Message("Space Services: Visitor Arrival Waved Off: " + invalidReason, incident.pad, MessageTypeDefOf.RejectInput, false);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                string padReason = null;
                if (!HospitalityStillEnabledForMap(map) || !PadStillUsableForGuests(incident.pad, out padReason))
                {
                    string reason = !HospitalityStillEnabledForMap(map) ? "hospitality disabled or map is not enabled" : padReason ?? "landing pad is no longer usable";
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents canceled unusable pad reservation=" + incident.reservationId + " reason=" + reason + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
                    Messages.Message("Space Services: Visitor Arrival Canceled: " + reason, incident.pad, MessageTypeDefOf.RejectInput, false);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position, "hospitality", incident.shuttleVisualDefName);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                if (ServiceDangerUtility.HospitalityTrafficBlocked(map, out string dangerReason))
                {
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents waved off danger reservation=" + incident.reservationId + " reason=" + dangerReason + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
                    Messages.Message("Space Services: Visitor Arrival Waved Off: " + dangerReason, incident.pad, MessageTypeDefOf.NegativeEvent, false);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position, "hospitality", incident.shuttleVisualDefName);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                if (ServiceDangerUtility.ArrivalTrafficBlocked(map, "hospitality", out string trafficReason))
                {
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents waved off traffic hazard reservation=" + incident.reservationId + " reason=" + trafficReason + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
                    Messages.Message("Space Services: Visitor Arrival Waved Off: " + trafficReason, incident.pad, MessageTypeDefOf.NegativeEvent, false);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position, "hospitality", incident.shuttleVisualDefName);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                HospitalityBedReport beds = HospitalityBedUtility.Report(map);
                int expectedBedDemand = Math.Max(1, incident.expectedBedDemand);
                if (HospitalityIncidentGate.RequiresGuestBedCapacity() && beds.freeBeds < expectedBedDemand)
                {
                    Messages.Message("Space Services: Visitor Arrival Canceled: need " + expectedBedDemand + " free guest beds (" + beds.ToSummary() + ")", incident.pad, MessageTypeDefOf.RejectInput, false);
                    ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents canceled beds reservation=" + incident.reservationId + " need=" + expectedBedDemand + " " + beds.ToSummary());
                    ServiceDebugUtility.LogThrottled("hospitality-touchdown-beds-" + (incident.incidentDefName ?? ""), "Hospitality visitor arrival canceled at touchdown: need " + expectedBedDemand + ", " + beds.ToSummary(), GenDate.TicksPerHour);
                    ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                    ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position, "hospitality", incident.shuttleVisualDefName);
                    ReleaseArrivalReservation(incident);
                    continue;
                }
                if (!ServiceLifecycleUtility.TryClearPadFootprintForServiceShuttle(incident.pad, "hospitality", "hospitality arrival touchdown", out string clearReason))
                {
                    incident.touchdownTick = Find.TickManager.TicksGame + 250;
                    if (Find.TickManager.TicksGame - incident.lastTouchdownDelayMessageTick >= GenDate.TicksPerHour)
                    {
                        incident.lastTouchdownDelayMessageTick = Find.TickManager.TicksGame;
                        Messages.Message("Space Services: Visitor Shuttle Waiting: " + clearReason, incident.pad, MessageTypeDefOf.RejectInput, false);
                    }
                    pendingHospitalityIncidents.Add(incident);
                    ServiceDebugUtility.LogThrottled(ServiceLogIntegration.Hospitality, "hospitality-touchdown-pad-occupied-" + incident.reservationId, "Hospitality visitor touchdown delayed because the service pad could not be cleared: " + clearReason, 250);
                    continue;
                }

                ServiceShuttleUtility.CleanupTouchdownShuttle(map, incident.pad.Position, incident.shuttleThingDefName);
                incident.parms.spawnCenter = incident.pad.Position;
                ServiceDebugUtility.LogAudit("TickPendingHospitalityIncidents executing reservation=" + incident.reservationId + " incident=" + incident.incidentDefName + " spawnCenter=" + incident.parms.spawnCenter + " pad=" + ServiceDebugUtility.ThingAuditSummary(incident.pad));
                ReleaseArrivalReservation(incident);
                // This context tells Hospitality spawn hooks which service pad owns the native spawn.
                HospitalityDelayedIncidentContext.Push(map, incident.pad);
                bool executed = false;
                string executionFailureReason = null;
                try
                {
                    MethodInfo method = AccessTools.Method(incident.worker.GetType(), "TryExecuteWorker");
                    if (method == null)
                    {
                        executionFailureReason = "Hospitality TryExecuteWorker not found";
                        ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospitality, "Delayed Hospitality visitor arrival failed: " + executionFailureReason + " on " + incident.worker.GetType().FullName);
                    }
                    else
                    {
                        object result = method.Invoke(incident.worker, new object[] { incident.parms });
                        executed = result is bool && (bool)result;
                        if (!executed)
                        {
                            string report = HospitalityIncidentGate.ReadinessReport(incident.incidentDefName ?? "VisitorGroup", map, incident.worker);
                            executionFailureReason = report == "ready" ? "Hospitality rejected the visitor group after touchdown; see the log for faction and points details" : "Hospitality rejected the visitor group: " + report;
                            ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospitality, "Delayed Hospitality visitor arrival waved off: TryExecuteWorker returned false for " + (incident.incidentDefName ?? "unknown") + ", report=" + report + ", points=" + incident.parms.points + ", faction=" + (incident.parms.faction == null ? "null" : incident.parms.faction.Name));
                        }
                    }
                }
                catch (TargetInvocationException ex)
                {
                    Exception inner = ex.InnerException ?? ex;
                    executionFailureReason = "Hospitality failed: " + inner.GetType().Name + " " + inner.Message;
                    ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospitality, "Delayed Hospitality visitor arrival failed: " + inner.GetType().Name + " " + inner.Message);
                }
                catch (Exception ex)
                {
                    executionFailureReason = "Hospitality failed: " + ex.GetType().Name + " " + ex.Message;
                    ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospitality, "Delayed Hospitality visitor arrival failed: " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    HospitalityDelayedIncidentContext.Pop();
                }
                if (!executed)
                {
                    Messages.Message("Space Services: Visitor Arrival Waved Off: " + (executionFailureReason ?? "unknown Hospitality rejection"), incident.pad, MessageTypeDefOf.RejectInput, false);
                }
                ServiceShuttleUtility.SpawnDeparture(map, incident.pad.Position, "hospitality", incident.shuttleVisualDefName);
            }
        }

        private void TickNaturalHospitalityScheduler()
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.hospitalityFallbackScheduler)
            {
                return;
            }
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.trafficRateOverride && ServiceIncidentUtility.TrafficRateFor("VisitorGroup") <= 0f)
            {
                nextHospitalityServiceVisitTick = 0;
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
            ScheduleNextHospitalityServiceVisit(reason == "shared service traffic slot cooling down" ? GenDate.TicksPerHour : HospitalitySchedulerBlockedRetryTicks);
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
            if (!ServiceIncidentUtility.ShouldRouteGroundsideHospitalityThroughService(map))
            {
                reason = "groundside native Hospitality selected";
                ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Hospitality, "Skipped fallback Hospitality service visit because groundside shuttle share rolled native.");
                return true;
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
            if (!TryFindHospitalityFaction(worker, map, out Faction faction))
            {
                reason = "no friendly humanlike guest faction";
                return false;
            }
            if (!ServiceIncidentUtility.TryConsumeSharedTrafficSlot(map, "fallback Hospitality visitor scheduler"))
            {
                reason = "shared service traffic slot cooling down";
                return false;
            }

            IncidentCategoryDef category = incidentDef.category ?? IncidentCategoryDefOf.Misc;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(category, map);
            parms.target = map;
            parms.faction = faction;
            parms.spawnCenter = pad.Position;
            parms.sendLetter = true;

            ShuttleVisual visual = ShuttleVisual.Resolve("hospitality", null);
            if (!ServiceLifecycleUtility.TryClearPadFootprintForServiceShuttle(pad, "hospitality", "fallback hospitality arrival scheduling", out string clearReason))
            {
                reason = "service pad could not be cleared: " + clearReason;
                return false;
            }
            if (!ScheduleHospitalityIncident(worker, parms, pad, visual == null || visual.shipThingDef == null ? null : visual.shipThingDef.defName, visual == null ? null : visual.id, out string scheduleReason))
            {
                reason = scheduleReason ?? "could not reserve service pad";
                return false;
            }

            ServiceShuttleUtility.SpawnArrival(map, pad.Position, visual);
            Messages.Message("Space Services: Visitors Inbound", pad, MessageTypeDefOf.SilentInput, false);
            ServiceDebugUtility.Log("Queued service hospitality visitors from " + faction.Name + " at " + pad.Position);
            return true;
        }

        private static bool TryFindHospitalityFaction(object worker, Map map, out Faction faction)
        {
            List<Faction> candidates = Find.FactionManager.AllFactionsListForReading
                .Where(f => IsFallbackHospitalityFactionCandidate(worker, map, f))
                .ToList();
            if (candidates.Count == 0)
            {
                faction = null;
                return false;
            }
            faction = candidates[Rand.Range(0, candidates.Count)];
            return true;
        }

        private static bool IsFallbackHospitalityFactionCandidate(object worker, Map map, Faction faction)
        {
            if (faction == null || faction.IsPlayer || faction.defeated || faction.temporary || faction.def == null || !faction.def.humanlikeFaction || faction.def.hidden || faction.HostileTo(Faction.OfPlayer))
            {
                return false;
            }
            if (faction.def.pawnGroupMakers.NullOrEmpty() || !faction.def.pawnGroupMakers.Any(maker => maker != null && maker.kindDef == PawnGroupKindDefOf.Peaceful))
            {
                return false;
            }
            if (HospitalityWorkerAcceptsFaction(worker, map, faction, out bool accepted))
            {
                return accepted;
            }
            return true;
        }

        private static bool HospitalityWorkerAcceptsFaction(object worker, Map map, Faction faction, out bool accepted)
        {
            accepted = false;
            if (worker == null || faction == null)
            {
                return false;
            }
            foreach (MethodInfo method in worker.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(method => method.Name == "FactionCanBeGroupSource"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                bool supported = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type type = parameters[i].ParameterType;
                    if (type == typeof(Faction))
                    {
                        args[i] = faction;
                    }
                    else if (type == typeof(Map))
                    {
                        args[i] = map;
                    }
                    else if (type == typeof(IncidentParms))
                    {
                        args[i] = new IncidentParms { target = map, faction = faction, points = MinimumHospitalityVisitorPoints };
                    }
                    else
                    {
                        supported = false;
                        break;
                    }
                }
                if (!supported)
                {
                    continue;
                }
                try
                {
                    object result = method.Invoke(method.IsStatic ? null : worker, args);
                    if (result is bool)
                    {
                        accepted = (bool)result;
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
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
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.trafficRateOverride)
            {
                float rate = ServiceIncidentUtility.TrafficRateFor("VisitorGroup");
                if (rate <= 0f)
                {
                    return GenDate.TicksPerDay;
                }
                intervalDays = Mathf.Max(0.5f, 1.5f / rate);
            }
            else
            {
                intervalDays = Mathf.Clamp(intervalDays, 0.5f, 5f);
            }
            float variedDays = Rand.Range(intervalDays * 0.85f, intervalDays * 1.15f);
            return Math.Max(GenDate.TicksPerHour, Mathf.RoundToInt(variedDays * GenDate.TicksPerDay));
        }

        private static bool PadStillUsableForGuests(Thing pad, out string reason)
        {
            reason = null;
            CompSpaceServicePad comp = pad == null ? null : pad.TryGetComp<CompSpaceServicePad>();
            if (comp == null)
            {
                reason = "landing pad is no longer a service pad";
                return false;
            }
            if (!comp.MeetsUseRequirements(ServiceUse.Guest, out reason))
            {
                return false;
            }
            return true;
        }

        private static bool HospitalityStillEnabledForMap(Map map)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return false;
            }
            return map != null && SpaceServiceMapDetector.IsServiceActive(map);
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

    public sealed class ScheduledServiceShuttleArrival : IThingHolder, IExposable
    {
        public IntVec3 cell;
        public int touchdownTick;
        public string shuttleThingDefName;
        public string shuttleVisualDefName;
        public string serviceKind;
        public ThingOwner<Thing> things;
        public bool showDeparture = true;
        public IThingHolder ParentHolder => null;

        public ScheduledServiceShuttleArrival()
        {
            things = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref touchdownTick, "touchdownTick", 0);
            Scribe_Values.Look(ref shuttleThingDefName, "shuttleThingDefName");
            Scribe_Values.Look(ref shuttleVisualDefName, "shuttleVisualDefName");
            Scribe_Values.Look(ref serviceKind, "serviceKind");
            Scribe_Values.Look(ref showDeparture, "showDeparture", true);
            Scribe_Deep.Look(ref things, "things", this);
            if (things == null)
            {
                things = new ThingOwner<Thing>(this, oneStackOnly: false);
            }
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return things;
        }
    }

    public sealed class ScheduledHospitalityIncident : IExposable
    {
        public object worker;
        public IncidentParms parms;
        public Thing pad;
        public IncidentDef incidentDef;
        public Faction faction;
        public IntVec3 spawnCenter;
        public bool sendLetter = true;
        public float points;
        public int pawnCount;
        public PawnKindDef pawnKind;
        public string reservationId;
        public string incidentDefName;
        public int expectedBedDemand;
        public string shuttleThingDefName;
        public string shuttleVisualDefName;
        public int touchdownTick;
        public int lastTouchdownDelayMessageTick = -999999;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref incidentDef, "incidentDef");
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref pad, "pad");
            Scribe_Values.Look(ref spawnCenter, "spawnCenter", IntVec3.Invalid);
            Scribe_Values.Look(ref sendLetter, "sendLetter", true);
            Scribe_Values.Look(ref points, "points", 0f);
            Scribe_Values.Look(ref pawnCount, "pawnCount", 0);
            Scribe_Defs.Look(ref pawnKind, "pawnKind");
            Scribe_Values.Look(ref reservationId, "reservationId");
            Scribe_Values.Look(ref incidentDefName, "incidentDefName");
            Scribe_Values.Look(ref expectedBedDemand, "expectedBedDemand", 1);
            Scribe_Values.Look(ref shuttleThingDefName, "shuttleThingDefName");
            Scribe_Values.Look(ref shuttleVisualDefName, "shuttleVisualDefName");
            Scribe_Values.Look(ref touchdownTick, "touchdownTick", 0);
            Scribe_Values.Look(ref lastTouchdownDelayMessageTick, "lastTouchdownDelayMessageTick", -999999);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && incidentDef == null && !string.IsNullOrEmpty(incidentDefName))
            {
                incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDefName);
            }
        }

        public void EnsureRuntime(Map map)
        {
            if (worker != null && parms != null)
            {
                return;
            }
            if (incidentDef == null && !string.IsNullOrEmpty(incidentDefName))
            {
                incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDefName);
            }
            worker = incidentDef == null ? null : incidentDef.Worker;
            if (worker == null || map == null)
            {
                return;
            }
            IncidentCategoryDef category = incidentDef.category ?? IncidentCategoryDefOf.Misc;
            parms = StorytellerUtility.DefaultParmsNow(category, map);
            parms.target = map;
            parms.faction = faction;
            parms.spawnCenter = pad != null && !pad.Destroyed ? pad.Position : spawnCenter;
            parms.sendLetter = sendLetter;
            parms.points = Math.Max(points, SpaceServicesMapComponent.MinimumHospitalityVisitorPoints);
            parms.pawnCount = pawnCount;
            parms.pawnKind = pawnKind;
        }
    }
}
