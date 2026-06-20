using System;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// Reads the craft's total vacuum delta-v (Δv) from KSP's stock delta-v
    /// calculator, for verifying a contract's min/max-Δv mission limit.
    ///
    /// • In the editor (craft_build) this is the full-fuel design value — exactly
    ///   what a "build a craft with ≥X m/s Δv" limit means.
    /// • In flight (active_vessel) it's the active vessel's current value; the
    ///   stock calc starts from current fuel and can't see already-staged tanks,
    ///   so a flown craft reports its remaining Δv.
    ///
    /// Returns -1 when no value is available (e.g. the player has the stock Δv
    /// readout disabled, or the calc hasn't run yet). Callers treat -1 as "unknown"
    /// and skip the Δv check rather than failing on a value we couldn't read.
    /// </summary>
    public static class CraftDeltaV
    {
        public static double TotalVacuum()
        {
            try
            {
                VesselDeltaV dv = null;
                if (HighLogic.LoadedSceneIsEditor)
                    dv = EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                        ? EditorLogic.fetch.ship.vesselDeltaV : null;
                else if (HighLogic.LoadedSceneIsFlight)
                    dv = FlightGlobals.ActiveVessel != null
                        ? FlightGlobals.ActiveVessel.VesselDeltaV : null;

                if (dv == null) return -1;

                // IsReady is KSP's own "the result is valid now" flag: it's false
                // until the stock Δv simulation has produced a result (the calc
                // hasn't run yet, or the player has the stock Δv readout disabled).
                // Only then is a 0 a stale/unknown value, so skip the check.
                if (!dv.IsReady) return -1;

                double total = dv.TotalDeltaVVac;
                // Once the sim is ready, 0 is a *real* value — e.g. a craft with an
                // engine but no fuel tank genuinely has 0 m/s Δv and must fail a
                // min-Δv limit. Report it as-is instead of masking it as "unknown".
                return total >= 0 ? total : -1;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Could not read craft Δv: " + e.Message);
                return -1;
            }
        }
    }
}
