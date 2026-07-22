using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Giddy-Up soft compat. A mounted rider must dismount before taking stairs:
    /// StairTransfer despawns the pawn, which would strand the mount with a live
    /// linkage on the source map. Detection is def-based (the mount runs the
    /// "Mounted" job targeting its rider, identical across Giddy-Up forks), so no
    /// reflection into any fork's internals is needed. Everything fails open when
    /// no Giddy-Up variant is active.
    /// </summary>
    internal static class ABGiddyUpCompat
    {
        private static bool resolved;
        private static JobDef mountedDef;

        private static JobDef MountedDef
        {
            get
            {
                if (!resolved)
                {
                    // Sentinel flag, not a null check: the def is legitimately
                    // absent without Giddy-Up and must not be re-looked-up per call.
                    resolved = true;
                    if (ABDetect.Active("MemeGoddess.GiddyUp")
                        || ABDetect.Active("Owlchemist.GiddyUp")
                        || ABDetect.Active("Roolo.GiddyUpCore"))
                    {
                        mountedDef = DefDatabase<JobDef>.GetNamedSilentFail("Mounted");
                    }
                }
                return mountedDef;
            }
        }

        /// <summary>True when some animal on the rider's map is running the
        /// Mounted job targeting them. Called only at stairs job start and at the
        /// transfer moment, never per tick.</summary>
        public static bool IsMounted(Pawn rider)
        {
            JobDef def = MountedDef;
            if (def == null || rider == null || !rider.Spawned || rider.Map == null)
            {
                return false;
            }
            IReadOnlyList<Pawn> pawns = rider.Map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.CurJobDef == def && p.CurJob?.targetA.Thing == rider)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when this animal is currently carrying a rider; such
        /// animals are skipped by the pet follow pull.</summary>
        public static bool IsCarryingRider(Pawn animal)
        {
            JobDef def = MountedDef;
            return def != null && animal?.CurJobDef == def;
        }

        /// <summary>Blocks a stairs interaction for mounted riders with a player
        /// facing message. Returns true when blocked.</summary>
        public static bool BlockForMount(Pawn rider)
        {
            if (!IsMounted(rider))
            {
                return false;
            }
            Messages.Message("AB_DismountFirst".Translate(), rider, MessageTypeDefOf.RejectInput, historical: false);
            return true;
        }
    }
}
