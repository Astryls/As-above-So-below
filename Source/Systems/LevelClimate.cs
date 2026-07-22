using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Linked stairwells exchange heat between their two rooms like a vent: every
    /// 250 ticks the warm side loses and the cold side gains energy proportional
    /// to the temperature difference. Outdoor rooms absorb pushes without changing
    /// temperature, which matches expectations (you cannot heat the sky).
    /// </summary>
    public static class LevelClimate
    {
        private const float ExchangeRatePerDegree = 25f;

        private const float MaxExchange = 800f;

        public static void TickGroundPairs(LevelComp groundComp)
        {
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelTemperature)
            {
                return;
            }
            List<Building_ABStairs> stairs = groundComp.Stairs;
            for (int i = 0; i < stairs.Count; i++)
            {
                Building_ABStairs a = stairs[i];
                if (a == null || !a.Spawned)
                {
                    continue;
                }
                if (a.Ext != null && a.Ext.utilityOnly)
                {
                    // Sealed utility shafts do not leak temperature.
                    continue;
                }
                ExchangeLink(a, a.Counterpart);
                // Elevator middle cars hold a second link (down).
                ExchangeLink(a, a.SecondCounterpart);
            }
        }

        private static void ExchangeLink(Building_ABStairs a, Building_ABStairs b)
        {
            if (b == null || !b.Spawned || b.Map == null || b.Map.Disposed)
            {
                return;
            }
            ExchangePair(a, b);
        }

        private static void ExchangePair(Building_ABStairs a, Building_ABStairs b)
        {
            float tempA = GenTemperature.GetTemperatureForCell(a.Position, a.Map);
            float tempB = GenTemperature.GetTemperatureForCell(b.Position, b.Map);
            float delta = tempA - tempB;
            if (Mathf.Abs(delta) < 0.5f)
            {
                return;
            }
            float energy = Mathf.Clamp(delta * ExchangeRatePerDegree, -MaxExchange, MaxExchange);
            GenTemperature.PushHeat(b.Position, b.Map, energy);
            GenTemperature.PushHeat(a.Position, a.Map, -energy);
        }
    }
}
