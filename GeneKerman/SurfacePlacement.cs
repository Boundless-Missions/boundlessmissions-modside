/*
 * SurfacePlacement.cs – Put a transferred LANDED craft on the recipient's ground,
 * not on the sender's.
 *
 * A vessel's landed position is stored as latitude, longitude and `alt` — and `alt` is
 * an altitude above SEA LEVEL, not above the ground. It is therefore only meaningful
 * against the terrain that produced it. Hand a craft parked at alt = 2140 m on a stock
 * Duna to someone running a terrain mod whose hills there are 60 m lower and the craft
 * is 60 m in the air; hand it the other way and it is 60 m inside a mountain. Nothing in
 * the craft file can be used to notice this, because both saves agree on every number in
 * it — they disagree about where the ground is, and only the sender's install knows
 * where the ground was. So the datum is carried, in a GKLAND block, exactly as
 * TextureTransfer carries the pack a texture set came from: the fact the recipient
 * cannot re-derive.
 *
 * The correction is therefore RELATIVE, never absolute:
 *
 *     agl   = alt_sender    - terrain_sender     (computed on the sender, carried)
 *     alt   = terrain_local + agl                (computed on the recipient)
 *
 * which keeps the craft the same distance off the ground it was actually resting on,
 * and is the only arithmetic that survives two installs disagreeing about the terrain.
 *
 * That gets the craft to roughly the right height. The last few metres are KSP's own
 * job, and the second half of this file is about not letting the game skip it. Stock
 * already has the whole machinery, built for a player changing their terrain-detail
 * setting between sessions:
 *
 *   - Vessel.Load() runs `altitude = Math.Max(altitude, pqs.GetSurfaceHeight(...) -
 *     Radius)` for a landed vessel, which lifts a buried craft to *exactly* ground
 *     level — i.e. with its landing legs still under the surface, since `alt` describes
 *     the vessel's reference point and not its lowest point. A clamp, not a fix.
 *   - Vessel.GoOffRails() then calls CheckGroundCollision(), which raycasts against the
 *     real collider mesh and moves the craft up or down onto it ("ground contact! -
 *     error. Moving Vessel up 3.271m"). This is the part that actually seats a craft,
 *     and the only part that can cope with a collider that isn't where the PQS heightmap
 *     says it is — Parallax's tessellated terrain and its scatter colliders being the
 *     case in hand.
 *
 * But GoOffRails skips that seating whenever `skipGroundPositioning` is set, and it sets
 * it by comparing the PQSMin/PQSMax the vessel was parked with against the local PQS
 * levels: same subdivision, assume the terrain is unchanged, don't bother. Between two
 * players on default terrain detail those numbers match, so an imported craft from a
 * completely different terrain mod is precisely the case stock decides it can skip. The
 * fix is to import the vessel the way KSP spawns one: `vesselSpawning = True` makes
 * GoOffRails run CheckGroundCollision regardless of that comparison (`if (vesselSpawning
 * || !skipGroundPositioning)`), and it is cleared by GoOffRails itself once the craft is
 * seated. Both fields are persistent on the VESSEL node, so this is a two-value edit and
 * no reflection.
 *
 * Which is why this file is deliberately not a placement algorithm of its own: writing a
 * "correct" altitude that stock then re-derives differently is how a craft ends up
 * fighting the ground. We supply the datum KSP cannot know and switch on the seating it
 * already has.
 *
 * GKLAND also carries the body NAME. A VESSEL node identifies its body by
 * `ORBIT { REF = n }`, an index into FlightGlobals.Bodies — and installing a planet pack
 * reorders that list, so the same index is a different world on the two installs. A
 * craft landed on Minmus can arrive landed on Ike. The name is checked on import and the
 * index rewritten to match; a body this install doesn't have at all is reported and left
 * alone, since guessing a substitute world is worse than saying so.
 *
 * There is no settings key for any of this. Every other transfer module has one because
 * it can DROP something (paint, scale, fuel configs) and a player might reasonably want
 * the raw article; putting a craft on the ground instead of inside it has no such side,
 * and settings.cfg's list is kept to keys that mean something.
 *
 * Back-compat both ways. A craft from a client that never wrote GKLAND still gets the
 * body check it can (via REF), still gets the forced re-seat — which alone fixes the
 * buried case, since stock's clamp plus CheckGroundCollision is most of the answer — and
 * simply keeps its own altitude where no sender datum exists to correct it against.
 * A GKLAND block read by an older client is an unknown child node of VESSEL, which
 * ProtoVessel ignores exactly as it ignores GKTU and GKCREW.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    public static class SurfacePlacement
    {
        /// <summary>Side-channel block name. A child of the VESSEL node, like GKTU/GKRF —
        /// read and stripped before the ProtoVessel is built.</summary>
        private const string BLOCK = "GKLAND";

        /// <summary>Format version of the block, so a later shape change can be told from
        /// this one rather than guessed at by which keys are present.</summary>
        private const int BLOCK_VERSION = 1;

        /// <summary>Correction below which nobody is told. A metre or two is the ordinary
        /// disagreement between two PQS evaluations and the craft would have been seated
        /// through it anyway; it is not news that the transfer worked.</summary>
        private const double QuietDelta = 25.0;

        /// <summary>Height above ground beyond which a "landed" craft's carried datum is
        /// treated as noise rather than as a measurement. A landed vessel's reference
        /// point sits within its own height of the ground — tens of metres for the very
        /// largest craft — so a kilometre means the snapshot and the terrain sample were
        /// never describing the same place, and re-seating from scratch beats honouring
        /// it.</summary>
        private const double MaxTrustedAgl = 1000.0;

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>
        /// Record the sender's ground level under a landed/splashed vessel, plus the body
        /// it is actually on, as a GKLAND child of its VESSEL node. No-op for a vessel in
        /// flight or orbit — an orbit is defined against the body's centre, which both
        /// installs agree on, so there is nothing a recipient cannot re-derive.
        /// </summary>
        public static void EmbedInNode(ConfigNode vesselNode, Vessel vessel)
        {
            if (vesselNode == null || vessel == null) return;

            try
            {
                // Strip any block from a previous pass so a re-export can't stack two.
                while (vesselNode.GetNode(BLOCK) != null) vesselNode.RemoveNode(BLOCK);

                if (!IsOnSurface(vesselNode)) return;

                CelestialBody body = vessel.mainBody;
                if (body == null) return;

                double lat, lon, alt;
                if (!TryReadPlacement(vesselNode, out lat, out lon, out alt)) return;

                // The datum, sampled at the craft's own lat/lon: where THIS install puts
                // the ground under it. allowNegative, because the ground genuinely is
                // below sea level in places (Duna's basins, any ocean floor) and the
                // clamped overload would report those as sea level and bias the agl.
                double terrain = SampleTerrain(body, lat, lon);
                if (double.IsNaN(terrain)) return;

                ConfigNode block = vesselNode.AddNode(BLOCK);
                block.AddValue("v", BLOCK_VERSION);
                block.AddValue("body", body.bodyName);
                block.AddValue("ref", body.flightGlobalsIndex);
                block.AddValue("radius", body.Radius.ToString("G17"));
                block.AddValue("terrain", terrain.ToString("G17"));
                block.AddValue("alt", alt.ToString("G17"));
                block.AddValue("agl", (alt - terrain).ToString("G17"));

                Debug.Log($"[GeneKerman] SurfacePlacement: '{vessel.vesselName}' on {body.bodyName} " +
                          $"lat={lat:F5} lon={lon:F5} alt={alt:F1} ground={terrain:F1} " +
                          $"agl={(alt - terrain):F1}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement.EmbedInNode failed: {ex.Message}");
            }
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>
        /// Read and strip the GKLAND block, point the vessel at the right body, re-derive
        /// its altitude against the local terrain, and arm KSP's own ground seating.
        /// Safe to call on any VESSEL node: one that isn't on a surface returns after the
        /// body check, and one with no block still gets the check and the re-seat.
        /// </summary>
        public static void ExtractAndReseat(ConfigNode innerNode, string context)
        {
            if (innerNode == null) return;

            var report = new Report(context);
            try
            {
                ConfigNode block = innerNode.GetNode(BLOCK);
                while (innerNode.GetNode(BLOCK) != null) innerNode.RemoveNode(BLOCK);

                CelestialBody body = ResolveBody(innerNode, block, report);
                if (!IsOnSurface(innerNode)) return;      // orbits need none of this
                if (body == null) return;                 // reported by ResolveBody

                if (body.pqsController == null)
                {
                    // A landed craft on a body with no terrain locally: nothing to sample
                    // and nothing sane to write. Leave every number alone and say so.
                    report.NoTerrain(body.bodyName);
                    return;
                }

                double lat, lon, alt;
                if (!TryReadPlacement(innerNode, out lat, out lon, out alt)) return;

                double terrain = SampleTerrain(body, lat, lon);
                if (double.IsNaN(terrain)) return;

                RadiusCheck(block, body, report);

                bool splashed = IsSplashed(innerNode);
                double agl;
                bool haveAgl = TryReadAgl(block, out agl);

                if (splashed)
                {
                    // Sea level is sea level on any install — the one thing that can have
                    // moved is the coastline. Dry land where the sender had water is the
                    // only case worth acting on, and the craft has to become landed for it
                    // (a floating vessel inside a hill is the failure we are here to
                    // prevent). Water where the sender had water needs no edit at all.
                    if (terrain > 0.0)
                    {
                        // MaxTrustedAgl applies here too. The landed branches below both
                        // gate on it and this one did not, so it was the one path that
                        // would use an absurd carried height directly — the same "is this
                        // a measurement or a claim" question, asked about the same field.
                        double useAgl = (haveAgl && agl <= MaxTrustedAgl) ? agl : 0.0;
                        double newAlt = terrain + Math.Max(0.0, useAgl);
                        MakeLanded(innerNode, body, newAlt);
                        ForceGroundReseat(innerNode);
                        report.Beached(body.bodyName, alt, newAlt);
                    }
                    return;
                }

                double corrected;
                if (haveAgl && agl <= MaxTrustedAgl)
                {
                    // The real fix. A negative agl is sampling noise, not a craft below
                    // its own ground — the export samples the PQS while the craft rests on
                    // a collider built from it, and the two differ by centimetres.
                    corrected = terrain + Math.Max(0.0, agl);
                }
                else
                {
                    // No datum carried (an older client), or one too large to be a
                    // measurement: fall back to stock's own rule — never below the local
                    // ground — and let CheckGroundCollision do the rest. Applying it here
                    // rather than waiting for Vessel.Load also means the craft reads
                    // correctly in the Tracking Station before anyone flies to it.
                    corrected = Math.Max(alt, terrain);
                    if (haveAgl) report.Implausible(agl);
                }

                innerNode.SetValue("alt", corrected.ToString("G17"), true);

                // hgt is KSP's height-above-terrain hint; it is recomputed by raycast once
                // the craft is in physics, but it seeds CheckGroundCollision's first lift
                // and a value measured against someone else's terrain is worse than ours.
                if (haveAgl && agl <= MaxTrustedAgl)
                    innerNode.SetValue("hgt", ((float)Math.Max(0.0, agl)).ToString("G9"), true);

                ForceGroundReseat(innerNode);
                report.Corrected(body.bodyName, alt, corrected, terrain);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement.ExtractAndReseat failed: {ex.Message}");
            }
            finally
            {
                report.Post();
            }
        }

        /// <summary>
        /// Make KSP seat this vessel on the ground itself when it enters physics, instead
        /// of trusting the altitude in the node. `vesselSpawning` is the switch stock uses
        /// for a vessel it has just placed: GoOffRails runs CheckGroundCollision when it
        /// is set — raycasting the actual collider and moving the craft onto it — and
        /// clears it once that has happened. `skipGroundPositioning` is cleared alongside
        /// it because it is the flag that would otherwise suppress the same call, and it
        /// arrives from the sender describing THEIR terrain settings.
        ///
        /// Public because the rescue placement path writes its own lat/lon/alt and needs
        /// the same guarantee; there is exactly one definition of "seat this properly".
        /// </summary>
        public static void ForceGroundReseat(ConfigNode innerNode)
        {
            if (innerNode == null) return;
            innerNode.SetValue("vesselSpawning", "True", true);
            innerNode.SetValue("skipGroundPositioning", "False", true);
        }

        /// <summary>Undo <see cref="ForceGroundReseat"/> for a node that is no longer
        /// going to a surface. Only the rescue placement path needs it: it can take a
        /// snapshot that arrived landed (and armed) and put it in orbit instead, and a
        /// spawning flag left set is a flag KSP clears on its own terms rather than
        /// ours.</summary>
        public static void ClearGroundReseat(ConfigNode innerNode)
        {
            if (innerNode == null) return;
            innerNode.SetValue("vesselSpawning", "False", true);
        }

        // ── Node helpers ─────────────────────────────────────────────────────

        /// <summary>Whether this VESSEL node describes a craft resting on a surface —
        /// landed, pre-launch or splashed. Read from the flags first and the situation
        /// second, since a node hand-edited by another mod may carry only one.
        ///
        /// Assembly-visible because VesselTransfer's incoming-orbit guard asks the same
        /// question and must get the same answer: a surface craft's ORBIT is not what
        /// places it, and KSP writes `SMA = NaN` into one itself.</summary>
        internal static bool IsOnSurface(ConfigNode node)
        {
            if (node == null) return false;
            if (ReadBool(node, "landed") || ReadBool(node, "splashed")) return true;
            string sit = node.GetValue("sit");
            return sit == "LANDED" || sit == "SPLASHED" || sit == "PRELAUNCH";
        }

        private static bool IsSplashed(ConfigNode node)
        {
            return ReadBool(node, "splashed") || node.GetValue("sit") == "SPLASHED";
        }

        private static bool ReadBool(ConfigNode node, string key)
        {
            bool b;
            string s = node.GetValue(key);
            return !string.IsNullOrEmpty(s) && bool.TryParse(s, out b) && b;
        }

        /// <summary>Parse one number from a peer's node: invariant culture, and finite.
        ///
        /// Both halves matter and both were missing in places. INVARIANT because a
        /// GKLAND block is written on one machine and read on another — the writer
        /// formats with "G17" (invariant by construction for round-tripping), while a
        /// culture-default parse on a comma-decimal locale rejects "-12.3456" outright,
        /// so the whole placement silently fell back to no correction at all. FINITE
        /// because `TryParse` accepts "NaN" and "Infinity", and the arithmetic these
        /// feed writes an altitude into the save.</summary>
        private static bool ParseNum(string s, out double v)
        {
            v = 0.0;
            return !string.IsNullOrEmpty(s)
                && double.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v)
                && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool TryReadPlacement(ConfigNode node, out double lat, out double lon, out double alt)
        {
            lat = lon = alt = 0.0;
            // IsInfinity as well as IsNaN: double.TryParse("Infinity") succeeds, and the
            // value arrives from a peer's node, so "it parsed" says nothing about it
            // being a number the arithmetic below can survive.
            return node != null
                && ParseNum(node.GetValue("lat"), out lat)
                && ParseNum(node.GetValue("lon"), out lon)
                && ParseNum(node.GetValue("alt"), out alt);
        }

        private static bool TryReadAgl(ConfigNode block, out double agl)
        {
            agl = 0.0;
            if (block == null) return false;

            // Prefer the precomputed agl; fall back to the pair it was derived from, so a
            // block written by a version that only carried the datum still resolves.
            //
            // The finiteness test is the same one TryReadPlacement applies, and it was
            // missing here: `double.TryParse("Infinity")` succeeds and is not NaN, so an
            // `agl = Infinity` in a peer's GKLAND passed. The landed branch happened to
            // reject it against MaxTrustedAgl, but the SPLASHED→beached branch used it
            // directly and wrote a non-finite `alt` into the VESSEL node — and the
            // terrain/alt fallback was worse still, since two Infinities give
            // `Inf - Inf = NaN`, which `Math.Max(0, NaN)` propagates.
            if (ParseNum(block.GetValue("agl"), out agl))
                return true;

            double terrain, alt;
            if (ParseNum(block.GetValue("terrain"), out terrain)
                && ParseNum(block.GetValue("alt"), out alt))
            {
                agl = alt - terrain;
                return !double.IsNaN(agl) && !double.IsInfinity(agl);
            }
            return false;
        }

        /// <summary>Local ground level under a point, allowing negative values (below sea
        /// level really is where the ground is on a basin floor). NaN when the body has no
        /// terrain to sample.</summary>
        private static double SampleTerrain(CelestialBody body, double lat, double lon)
        {
            if (body == null || body.pqsController == null) return double.NaN;
            try { return body.TerrainAltitude(lat, lon, true); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement: terrain sample failed on " +
                                 $"{body.bodyName}: {ex.Message}");
                return double.NaN;
            }
        }

        /// <summary>Turn a splashed node into a landed one at a given altitude. Used only
        /// when the recipient has dry land where the sender had water.</summary>
        private static void MakeLanded(ConfigNode node, CelestialBody body, double alt)
        {
            node.SetValue("sit", "LANDED", true);
            node.SetValue("landed", "True", true);
            node.SetValue("splashed", "False", true);
            node.SetValue("landedAt", body.bodyName, true);
            node.SetValue("alt", alt.ToString("G17"), true);
        }

        // ── Body resolution ──────────────────────────────────────────────────

        /// <summary>
        /// The body this vessel should be on, and — when the carried name says the node's
        /// index points somewhere else — the rewrite that makes ORBIT { REF } agree. The
        /// index is a position in FlightGlobals.Bodies, which a planet pack reorders, so
        /// it is the one placement field that can be wrong by a whole world.
        /// </summary>
        private static CelestialBody ResolveBody(ConfigNode innerNode, ConfigNode block, Report report)
        {
            ConfigNode orbit = innerNode.GetNode("ORBIT");
            int refIdx = -1;
            if (orbit == null || !int.TryParse(orbit.GetValue("REF"), out refIdx)) refIdx = -1;

            CelestialBody byIndex = BodyByIndex(refIdx);
            string name = block != null ? block.GetValue("body") : null;
            if (string.IsNullOrEmpty(name)) return byIndex;

            CelestialBody byName = BodyByName(name);
            if (byName == null)
            {
                // The sender's world does not exist here. Nothing can put the craft in the
                // right place, and quietly leaving it on whatever body shares the index is
                // how a Minmus lander turns up on Ike without explanation.
                report.UnknownBody(name, byIndex != null ? byIndex.bodyName : "unknown");
                return byIndex;
            }

            if (byIndex != byName)
            {
                if (orbit == null) orbit = innerNode.AddNode("ORBIT");
                orbit.SetValue("REF", byName.flightGlobalsIndex.ToString(), true);
                report.Rebased(name, byIndex != null ? byIndex.bodyName : "nothing", refIdx,
                               byName.flightGlobalsIndex);
            }
            return byName;
        }

        private static CelestialBody BodyByIndex(int idx)
        {
            if (idx < 0 || FlightGlobals.Bodies == null) return null;
            foreach (var b in FlightGlobals.Bodies)
                if (b != null && b.flightGlobalsIndex == idx) return b;
            return null;
        }

        private static CelestialBody BodyByName(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null) return null;
            foreach (var b in FlightGlobals.Bodies)
                if (b != null && string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase))
                    return b;
            return null;
        }

        /// <summary>Note a body that is the same world by name but a different size here —
        /// a rescale (Sigma Dimensions, a Kopernicus config) rather than a terrain mod.
        /// The relative correction still applies, but the landscape it lands in is not the
        /// one the craft was parked in, so the player is told rather than left wondering.</summary>
        private static void RadiusCheck(ConfigNode block, CelestialBody body, Report report)
        {
            double senderRadius;
            if (block == null || body == null) return;
            if (!ParseNum(block.GetValue("radius"), out senderRadius)) return;
            if (senderRadius <= 0.0 || body.Radius <= 0.0) return;

            double ratio = body.Radius / senderRadius;
            if (ratio > 1.01 || ratio < 0.99)
                report.Rescaled(body.bodyName, senderRadius, body.Radius);
        }

        // ── Reporting ────────────────────────────────────────────────────────

        /// <summary>One message per craft, and only when something happened that the
        /// player would otherwise have to work out from a craft standing in mid-air. An
        /// ordinary few-metre correction is logged and nothing more — it is the transfer
        /// working, not an event.</summary>
        private class Report
        {
            private readonly string context;
            private readonly List<string> notes = new List<string>();
            private string headline;

            public Report(string context)
            {
                this.context = string.IsNullOrEmpty(context) ? "This craft" : context;
            }

            public void Corrected(string body, double oldAlt, double newAlt, double terrain)
            {
                double delta = newAlt - oldAlt;
                Debug.Log($"[GeneKerman] SurfacePlacement: '{context}' on {body} " +
                          $"alt {oldAlt:F1} → {newAlt:F1} (local ground {terrain:F1}, " +
                          $"moved {delta:F1} m); ground seating armed.");

                if (Math.Abs(delta) < QuietDelta) return;

                headline = $"'{context}' was re-seated on your terrain";
                notes.Add(delta > 0
                    ? $"Your ground on {body} is about {Math.Abs(delta):F0} m higher there than the " +
                      "sender's, so the craft would have arrived buried. It has been raised to match, "
                    : $"Your ground on {body} is about {Math.Abs(delta):F0} m lower there than the " +
                      "sender's, so the craft would have arrived hanging in the air. It has been lowered to match, ");
                notes.Add("and KSP will settle it onto the surface when you fly to it.");
            }

            public void Beached(string body, double oldAlt, double newAlt)
            {
                headline = $"'{context}' arrived on dry land";
                notes.Add($"It was floating on {body} where the sender plays, but that spot is above " +
                          $"water on your install, so it has been placed on the ground there " +
                          $"(alt {oldAlt:F0} → {newAlt:F0} m) rather than left inside the hillside.");
            }

            public void UnknownBody(string wanted, string fallback)
            {
                headline = $"'{context}' came from a world you don't have";
                notes.Add($"It was landed on {wanted}, which isn't installed here, so its position " +
                          $"can't be checked. KSP will place it on {fallback}, the body its save slot " +
                          "points at, which is unlikely to be anywhere useful.");
            }

            public void Rebased(string wanted, string wouldHaveBeen, int oldIdx, int newIdx)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement: '{context}' is landed on {wanted}, " +
                                 $"but its body slot ({oldIdx}) is {wouldHaveBeen} here, re-pointed to " +
                                 $"slot {newIdx}.");
                headline = $"'{context}' was pointed back at {wanted}";
                notes.Add($"Craft files identify their world by a slot number, and your planet list " +
                          $"is ordered differently from the sender's; slot {oldIdx} is {wouldHaveBeen} " +
                          $"on this install. It has been corrected to {wanted}.");
            }

            public void NoTerrain(string body)
            {
                headline = $"'{context}' can't be placed on {body}";
                notes.Add($"The craft is recorded as landed, but {body} has no terrain on this " +
                          "install to land it on. It has been left exactly as it arrived.");
            }

            public void Rescaled(string body, double senderRadius, double localRadius)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement: {body} is {senderRadius / 1000.0:F0} km " +
                                 $"for the sender and {localRadius / 1000.0:F0} km here.");
                notes.Add($"Note that {body} is a different size on your install " +
                          $"({localRadius / 1000.0:F0} km against {senderRadius / 1000.0:F0} km), so the " +
                          "landscape at those coordinates is not the one it was parked in.");
                if (headline == null) headline = $"'{context}' came from a rescaled {body}";
            }

            public void Implausible(double agl)
            {
                Debug.LogWarning($"[GeneKerman] SurfacePlacement: '{context}' carried an implausible " +
                                 $"height above ground ({agl:F0} m), re-seating from the local terrain instead.");
            }

            public void Post()
            {
                if (headline == null || notes.Count == 0) return;

                string body = string.Join(" ", notes.ToArray());
                Debug.LogWarning($"[GeneKerman] {headline}: {body}");

                GeneKermanMod mod = GeneKermanMod.Instance;
                if (mod == null) return;
                try { mod.ShowNotification(headline, body); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GeneKerman] SurfacePlacement: notification failed: {ex.Message}");
                }
            }
        }
    }
}
