using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace InstantShuttleDisembark
{
    [StaticConstructorOnStartup]
    public static class InstantShuttleDisembarkMod
    {
        static InstantShuttleDisembarkMod()
        {
            new Harmony("wishRobber.instantshuttledisembark").PatchAll();
        }
    }

    // Vanilla ShipJob_Unload.Drop() unloads exactly ONE thing per call, and ShipJob_Unload.TickInterval
    // only calls it every 60 ticks - so a landed shuttle/airship dribbles its passengers and animals out
    // about one per second. We intercept Drop() and instead run the real Drop() repeatedly within a
    // single call until nothing more leaves, ejecting the whole manifest at once. Because we re-run the
    // genuine vanilla Drop(), all of its behaviour is preserved: drop priorities (colonists, then
    // animals, then items), dropMode (All / PawnsOnly / NonRequired / None), lord/quest cleanup and the
    // job End() all happen exactly as normal - we only remove the per-call throttle.
    [HarmonyPatch(typeof(ShipJob_Unload), "Drop")]
    public static class Patch_ShipJob_Unload_Drop
    {
        private static readonly MethodInfo DropMethod = AccessTools.Method(typeof(ShipJob_Unload), "Drop");

        // Guards against the reflected Drop() invocation below re-entering this prefix: Harmony routes
        // every call (including reflection) through the patch, so the inner calls must fall through to
        // the original. ThreadStatic keeps it safe even though RimWorld unloads on the main thread only.
        [ThreadStatic]
        private static bool reentrant;

        public static bool Prefix(ShipJob_Unload __instance)
        {
            if (reentrant)
            {
                return true; // inner re-entry: let the real Drop() perform one normal pass
            }

            ThingOwner container = __instance.transportShip?.TransporterComp?.innerContainer;
            if (container == null)
            {
                return true; // unexpected state - defer entirely to vanilla
            }

            reentrant = true;
            try
            {
                int guard = 0;
                int prevCount;
                do
                {
                    prevCount = container.Count;
                    DropMethod.Invoke(__instance, null); // re-enters Prefix -> guard -> original Drop()
                }
                while (container.Count < prevCount && ++guard < 1000);
                // Loop stops when a pass removes nothing: either everything is out (vanilla Drop() has
                // called End()), or the remainder can't currently be placed - which the next 60-tick
                // TickInterval will retry, exactly like vanilla.
            }
            finally
            {
                reentrant = false;
            }

            return false; // we already drained the shuttle; skip the throttled single drop
        }
    }
}
