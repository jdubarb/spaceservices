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
    public sealed class ServiceGroupRecord : IExposable
    {
        // Service groups outlive the source mod's immediate incident. These records let Space Services
        // own pad reservation, extraction, and recovery without making Hospital/Hospitality save our state.
        public string id;
        public string serviceKind;
        public string state = "arrived";
        public int arrivalTick;
        public int timeoutTick;
        public int departureRequestedTick;
        public int pickupShuttleTouchdownTick;
        public int hospitalityBedlessSinceTick;
        public int nextActivePawnValidationTick;
        public int nextHospitalityBedlessCheckTick;
        public int nextLeaveStateCheckTick;
        public int lastHospitalityTransitTick;
        public string pickupShuttleThingDefName;
        public string pickupShuttleVisualDefName;
        public bool hospitalityDeparturePrepared;
        public Thing arrivalPad;
        public Thing reservedPad;
        public List<Pawn> pawns = new List<Pawn>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref serviceKind, "serviceKind");
            Scribe_Values.Look(ref state, "state", "arrived");
            Scribe_Values.Look(ref arrivalTick, "arrivalTick", 0);
            Scribe_Values.Look(ref timeoutTick, "timeoutTick", 0);
            Scribe_Values.Look(ref departureRequestedTick, "departureRequestedTick", 0);
            Scribe_Values.Look(ref pickupShuttleTouchdownTick, "pickupShuttleTouchdownTick", 0);
            Scribe_Values.Look(ref hospitalityBedlessSinceTick, "hospitalityBedlessSinceTick", 0);
            Scribe_Values.Look(ref nextActivePawnValidationTick, "nextActivePawnValidationTick", 0);
            Scribe_Values.Look(ref nextHospitalityBedlessCheckTick, "nextHospitalityBedlessCheckTick", 0);
            Scribe_Values.Look(ref nextLeaveStateCheckTick, "nextLeaveStateCheckTick", 0);
            Scribe_Values.Look(ref pickupShuttleThingDefName, "pickupShuttleThingDefName");
            Scribe_Values.Look(ref pickupShuttleVisualDefName, "pickupShuttleVisualDefName");
            Scribe_Values.Look(ref hospitalityDeparturePrepared, "hospitalityDeparturePrepared", false);
            Scribe_References.Look(ref arrivalPad, "arrivalPad");
            Scribe_References.Look(ref reservedPad, "reservedPad");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
        }
    }

    public sealed class ServiceDepartureBlock
    {
        public Map map;
        public ServiceGroupRecord record;
        public string reason;

        public Thing Culprit
        {
            get
            {
                if (record == null)
                {
                    return null;
                }
                if (record.reservedPad != null && !record.reservedPad.Destroyed)
                {
                    return record.reservedPad;
                }
                return record.pawns == null ? null : record.pawns.FirstOrDefault(pawn => pawn != null && !pawn.Destroyed);
            }
        }
    }
}
