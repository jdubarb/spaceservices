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
    public sealed class ShuttleVisual
    {
        public ThingDef shipThingDef;
        public ThingDef incomingSkyfallerDef;
        public ThingDef leavingSkyfallerDef;

        public static ShuttleVisual Resolve()
        {
            ThingDef payload = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttlePayload");
            if (payload == null)
            {
                return null;
            }

            ThingDef incoming = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttleIncoming");
            ThingDef leaving = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttleLeaving");
            if (incoming == null || leaving == null)
            {
                return null;
            }
            return new ShuttleVisual
            {
                shipThingDef = payload,
                incomingSkyfallerDef = incoming,
                leavingSkyfallerDef = leaving
            };
        }
    }

}
