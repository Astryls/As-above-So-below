using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Waterfall lip: sits on the last river cell of a sky-level watercourse,
    /// facing the open air it pours into. Draws a two-frame animated foam band
    /// at the edge, spawns falling spray, and sustains a pitched-down rain
    /// loop. Cluster etiquette: only the "root" lip of a contiguous run plays
    /// sound (no same-def neighbor to the west or south), so a six-cell-wide
    /// fall is one voice, not six. Upstream lips (inflow: the stream arrives
    /// over the void) render foam only and stay quiet.
    /// </summary>
    [StaticConstructorOnStartup]
    public class Thing_ABWaterfall : ThingWithComps
    {
        public bool inflow;

        private Sustainer sustainer;
        private bool soundRoot;
        private int rootCheckTick = -1;

        private static Material foamA;
        private static Material foamB;

        private static Material FoamA =>
            foamA ?? (foamA = MaterialPool.MatFrom("Things/AB_WaterfallFoamA", ShaderDatabase.Transparent));

        private static Material FoamB =>
            foamB ?? (foamB = MaterialPool.MatFrom("Things/AB_WaterfallFoamB", ShaderDatabase.Transparent));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref inflow, "inflow", false);
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned)
            {
                return;
            }
            if (rootCheckTick < 0 || Find.TickManager.TicksGame - rootCheckTick >= 250)
            {
                rootCheckTick = Find.TickManager.TicksGame;
                soundRoot = ComputeSoundRoot();
            }
            if (soundRoot)
            {
                if (sustainer == null || sustainer.Ended)
                {
                    SoundInfo info = SoundInfo.InMap(this, MaintenanceType.PerTick);
                    if (inflow)
                    {
                        info.volumeFactor = 0.45f;
                    }
                    sustainer = ABDefOf.AB_WaterfallLoop.TrySpawnSustainer(info);
                }
                sustainer?.Maintain();
            }
            if (!inflow && Find.CurrentMap == Map && this.IsHashIntervalTick(26))
            {
                Vector3 spot = DrawPos + Rotation.FacingCell.ToVector3() * 0.7f;
                FleckMaker.WaterSplash(spot, Map, 2.1f, 1.4f);
            }
        }

        private bool ComputeSoundRoot()
        {
            Map map = Map;
            IntVec3 w = Position + IntVec3.West;
            IntVec3 s = Position + IntVec3.South;
            if (w.InBounds(map) && map.thingGrid.ThingAt(w, def) != null)
            {
                return false;
            }
            if (s.InBounds(map) && map.thingGrid.ThingAt(s, def) != null)
            {
                return false;
            }
            return true;
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // Foam band hugging the lip edge, flipping between two frames.
            bool frameA = Time.realtimeSinceStartup * 3.2f % 2f < 1f;
            Material mat = frameA ? FoamA : FoamB;
            Vector3 edge = Rotation.FacingCell.ToVector3();
            Vector3 pos = drawLoc + edge * 0.38f;
            pos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            Quaternion rot = Quaternion.Euler(0f, Rotation.AsAngle, 0f);
            Matrix4x4 m = Matrix4x4.TRS(pos, rot, new Vector3(1.15f, 1f, 0.55f));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
            if (!inflow)
            {
                // Second, fainter band slightly over the edge: the falling sheet.
                Vector3 pos2 = drawLoc + edge * 0.72f;
                pos2.y = AltitudeLayer.MoteOverhead.AltitudeFor() - 0.005f;
                Matrix4x4 m2 = Matrix4x4.TRS(pos2, rot, new Vector3(1.05f, 1f, 0.42f));
                Graphics.DrawMesh(MeshPool.plane10, m2, frameA ? FoamB : FoamA, 0);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            sustainer?.End();
            sustainer = null;
            base.DeSpawn(mode);
        }
    }

    /// <summary>
    /// Invisible marker on the surface river where a waterfall lands: mist,
    /// splash, and the rumble heard from ground level (the lip's own sound
    /// lives on the sky map and cannot carry down).
    /// </summary>
    public class Thing_ABWaterfallBase : ThingWithComps
    {
        private Sustainer sustainer;

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned)
            {
                return;
            }
            if (sustainer == null || sustainer.Ended)
            {
                SoundInfo info = SoundInfo.InMap(this, MaintenanceType.PerTick);
                info.volumeFactor = 0.8f;
                sustainer = ABDefOf.AB_WaterfallLoop.TrySpawnSustainer(info);
            }
            sustainer?.Maintain();
            if (Find.CurrentMap == Map && this.IsHashIntervalTick(18))
            {
                FleckMaker.WaterSplash(DrawPos, Map, 2.8f, 1.9f);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            sustainer?.End();
            sustainer = null;
            base.DeSpawn(mode);
        }
    }
}
