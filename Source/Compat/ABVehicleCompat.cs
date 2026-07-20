using System;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vehicle Framework soft compat. Vehicles are Pawn subclasses and would
    /// pass a naive race check here or there; they must never be routed
    /// through stairs (a truck cannot climb a ladder). Most of our systems
    /// exclude them by construction (colonist/humanlike filters), this guard
    /// covers the NPC scan paths. Type is resolved by name once; without the
    /// framework the check is a single null test.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABVehicleCompat
    {
        private static readonly Type vehicleType;

        static ABVehicleCompat()
        {
            if (ABDetect.Active("SmashPhil.VehicleFramework"))
            {
                vehicleType = AccessTools.TypeByName("Vehicles.VehiclePawn");
                if (vehicleType != null)
                {
                    ABLog.Dev("Vehicle Framework detected, vehicles excluded from stair routing.");
                }
            }
        }

        public static bool IsVehicle(Pawn p)
        {
            return vehicleType != null && p != null && vehicleType.IsInstanceOfType(p);
        }
    }
}
