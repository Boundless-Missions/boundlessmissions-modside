/*
 * RescueWaypoints.cs — a stock map waypoint on the landing site a rescue asks for.
 *
 * A surface-mode rescue can name a spot ("Require a specific landing site" in
 * ContractForm), and that spot is the whole job: SubmissionSession refuses a delivery
 * that is more than margin_pos degrees away from it. (It is the DELIVERY target, not
 * where the wreck spawns — the wreck keeps its own snapshot orbit, which is what makes
 * it a rescue rather than a pickup. Both production callers of ImportVesselAtTarget
 * pass a null target for exactly that reason, so PlaceAtTarget does not run on the
 * wreck-spawn path at all.) Up to now the rescuer was told the number and left to fly to it —
 * ContractsPanel prints "Deliver to Duna surface at -14.2°, 63.8°", which is a
 * coordinate, not a direction. KSP already has the instrument for that: a FinePrint
 * waypoint draws on the map, in the tracking station, and — once the player activates
 * navigation on it — on the navball, which is what a landing is actually flown against.
 *
 * Four things shape the file.
 *
 * The waypoints are *derived*, never stored. Nothing is written to the save: the set is
 * recomputed from the contract list the client already holds, so a contract that ends
 * anywhere (completed, given up, cancelled, disputed, or cancelled by the issuer from
 * Discord) takes its waypoint with it on the next sync, and no stale marker can outlive
 * the deal that justified it. That is also how stock contracts do it.
 *
 * They live and die with the map camera. FinePrint.WaypointManager is a component on
 * MapView.MapCamera's GameObject, which Unity destroys on every scene load, so a
 * Waypoint added in flight is gone — along with its MapNode — the moment the player
 * returns to the Space Center. Entries are therefore *invalidated* rather than dropped
 * when the camera changes: the record survives (so navigation can still be cleared for
 * a contract that ends while there is no map at all), the node is rebuilt on arrival in
 * the next scene that has one.
 *
 * Absent lat/lon is not 0°,0°. A rescue with the landing-site switch off carries no
 * coordinates at all and means "anywhere on the body" — ContractsPanel already refuses
 * to print the zeros for one, and drawing a marker off the coast of Kerbin's equator
 * would send the rescuer to a spot nobody asked for. MiniJSON.Has, not GetDouble.
 *
 * And it only ever adds. There is no settings key, unlike the transfer modules, for the
 * reason SurfacePlacement has none: every one of those can *drop* something from a
 * craft, while this draws a marker that the contract's own end removes again.
 */

using System;
using System.Collections.Generic;
using FinePrint;
using UnityEngine;

namespace GeneKerman
{
    public static class RescueWaypoints
    {
        /// <summary>Stock icon name, resolved through FinePrint's sprite map (which
        /// lazily loads Squad/Contracts/Icons/&lt;id&gt;). "vessel" is the stranded ship
        /// this marker stands for; an id with no texture behind it would NullRef inside
        /// SetupMapNode, so this must stay one of the stock set.</summary>
        private const string Icon = "vessel";

        /// <summary>Seconds between syncs. The contract list only changes on a refresh,
        /// so this is a cheap idle poll rather than a deadline — Poke() is what makes a
        /// freshly accepted rescue draw immediately.</summary>
        private const float SyncInterval = 2f;

        /// <summary>One tracked landing site. The coordinates are kept alongside the
        /// waypoint because they outlive it: the Waypoint object dies with the map
        /// camera, and NavWaypoint matches by lat/lon rather than by reference, so a
        /// record with a null Point can still switch the navball marker off.</summary>
        private sealed class Entry
        {
            public Waypoint Point;      // null while there is no map camera to hold it
            public string Body;
            public double Lat, Lon;
            /// <summary>This site cannot be drawn on this install — the planet pack is
            /// missing, or the add threw. Set so the failure is reported once instead of
            /// re-attempted, and re-logged, on every idle sync.</summary>
            public bool Suppressed;
        }

        private static readonly Dictionary<string, Entry> live = new Dictionary<string, Entry>();

        /// <summary>The map camera the live waypoints belong to, as a plain object so the
        /// identity test below is a reference comparison and not Unity's overloaded ==.
        /// Null once normalized through that ==, which is how a destroyed camera reads.</summary>
        private static object lastCamera;

        private static float nextSync;

        /// <summary>Re-sync on the next Tick. Called after a contract refresh so an
        /// accepted rescue gets its marker without waiting out the idle interval.</summary>
        public static void Poke() => nextSync = 0f;

        /// <summary>
        /// Recompute the marker set. Safe to call every frame — the work is gated on the
        /// idle interval, and the whole body is guarded, since a throw here would come
        /// out of GeneKermanMod.Update and take the rest of the frame's bookkeeping
        /// (rescue removals, the roster sweep) with it.
        /// </summary>
        public static void Tick()
        {
            try
            {
                // Unity's == reports a destroyed camera as null while the reference is
                // still live, so normalize through it before comparing identities:
                // otherwise a torn-down scene looks like the same camera and we would
                // keep handing dead MapNodes to KSP.
                var cam = MapView.MapCamera;
                object camKey = cam == null ? null : cam;

                if (!ReferenceEquals(camKey, lastCamera))
                {
                    Invalidate();
                    lastCamera = camKey;
                    nextSync = 0f;
                }

                if (Time.realtimeSinceStartup < nextSync) return;
                nextSync = Time.realtimeSinceStartup + SyncInterval;

                Sync(camKey != null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] RescueWaypoints.Tick failed: {ex.Message}");
                // Don't retry in a tight loop after a fault.
                nextSync = Time.realtimeSinceStartup + SyncInterval;
            }
        }

        /// <summary>Forget the drawn nodes without forgetting the sites. Called when the
        /// map camera goes away: KSP has already destroyed every MapNode, so removing
        /// them properly is neither possible nor necessary — but the coordinates have to
        /// survive, or a contract ending in the Space Center leaves the navball pointing
        /// at a rescue that is over.</summary>
        private static void Invalidate()
        {
            foreach (var e in live.Values) e.Point = null;
        }

        private static void Sync(bool haveMap)
        {
            var wanted = CollectTargets();

            // Gone, or moved: drop it. Removal runs whether or not there is a map, so a
            // finished contract can still switch navigation off.
            List<string> stale = null;
            foreach (var kv in live)
            {
                Dictionary<string, object> c;
                if (wanted.TryGetValue(kv.Key, out c) && Matches(kv.Value, c)) continue;
                (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale != null)
                foreach (var cid in stale) Drop(cid);

            if (!haveMap) return;

            foreach (var kv in wanted)
            {
                Entry e;
                if (live.TryGetValue(kv.Key, out e) && (e.Point != null || e.Suppressed)) continue;
                Add(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// The rescues that should be marked right now: accepted by us, on a surface with
        /// a named site.
        ///
        /// Only "active", deliberately. A pending offer has not been taken on and a
        /// submitted or disputed one has already been flown, so a marker in either state
        /// is clutter over a place the player has no business flying to.
        /// </summary>
        private static Dictionary<string, Dictionary<string, object>> CollectTargets()
        {
            var wanted = new Dictionary<string, Dictionary<string, object>>();

            var mod = GeneKermanMod.Instance;
            if (mod == null || mod.Api == null || !mod.Api.IsLinked) return wanted;
            var list = mod.State?.ContractList;
            if (list == null) return wanted;

            foreach (var o in list)
            {
                var c = o as Dictionary<string, object>;
                if (c == null) continue;
                if (MiniJSON.GetString(c, "mission_type", "") != "rescue") continue;
                // The issuer's own copy: their ship is already there, and the marker is
                // for whoever has to go and get it.
                if (MiniJSON.GetBool(c, "is_outgoing")) continue;
                if (MiniJSON.GetString(c, "status", "") != "active") continue;

                var rt = MiniJSON.GetDict(c, "rescue_target");
                if (rt == null) continue;
                if (MiniJSON.GetString(rt, "mode", "orbit").ToLowerInvariant() != "surface") continue;
                // Both halves, or it is not a place. See the header: no lat/lon means
                // "anywhere on the body", which is not a target at 0°, 0°.
                if (!MiniJSON.Has(rt, "lat") || !MiniJSON.Has(rt, "lon")) continue;

                string cid = MiniJSON.GetString(c, "contract_id", "");
                if (string.IsNullOrEmpty(cid)) continue;

                wanted[cid] = c;
            }

            return wanted;
        }

        /// <summary>Whether a tracked entry still describes the same spot. A live
        /// contract's target cannot move, but re-deriving it is a few comparisons and
        /// beats trusting that.</summary>
        private static bool Matches(Entry e, Dictionary<string, object> c)
        {
            var rt = MiniJSON.GetDict(c, "rescue_target");
            if (rt == null) return false;
            return e.Body == MiniJSON.GetString(rt, "body", "")
                && Math.Abs(e.Lat - MiniJSON.GetDouble(rt, "lat")) < 1e-9
                && Math.Abs(e.Lon - MiniJSON.GetDouble(rt, "lon")) < 1e-9;
        }

        private static void Add(string contractId, Dictionary<string, object> c)
        {
            var rt = MiniJSON.GetDict(c, "rescue_target");
            if (rt == null) return;

            string bodyName = MiniJSON.GetString(rt, "body", "");
            double lat = MiniJSON.GetDouble(rt, "lat");
            double lon = MiniJSON.GetDouble(rt, "lon");

            Entry entry;
            if (!live.TryGetValue(contractId, out entry))
            {
                entry = new Entry();
                live[contractId] = entry;
            }
            entry.Body = bodyName;
            entry.Lat = lat;
            entry.Lon = lon;

            CelestialBody body = ResolveBody(bodyName);
            if (body == null)
            {
                // A modded target the rescuer's install hasn't got. The contract itself
                // already warns about that; recording it here is what stops the lookup
                // being retried, and re-logged, every couple of seconds.
                if (!entry.Suppressed)
                {
                    entry.Suppressed = true;
                    Debug.LogWarning($"[GeneKerman] Rescue waypoint skipped: body '{bodyName}' " +
                                     "is not installed here.");
                }
                return;
            }
            entry.Suppressed = false;

            try
            {
                var wp = new Waypoint
                {
                    // GetName(), not the display name: FinePrint resolves celestialName
                    // back to a body by that exact string, and a localized install would
                    // never match it.
                    celestialName = body.GetName(),
                    latitude = lat,
                    longitude = lon,
                    // An offset above the sampled terrain, which CachePositions fills in
                    // itself for a surface waypoint — 0 puts the marker on the ground.
                    altitude = 0.0,
                    height = 0.0,
                    id = Icon,
                    name = Label(c),
                    index = 0,
                    // Stable per contract, so the map colour of a rescue is the same one
                    // every session. A negative seed would re-roll the hue on every add.
                    seed = StableSeed(contractId),
                    isOnSurface = true,
                    landLocked = true,
                    isClustered = false,
                    // The one thing that puts it on the navball — it is what makes
                    // "Activate Navigation" appear on the node's flight context menu.
                    // Deliberately offered rather than forced: switching the player's
                    // navigation target out from under them is not ours to do.
                    isNavigatable = true,
                    nodeCaption1 = Caption(c),
                    nodeCaption2 = Tolerance(rt, body),
                };

                WaypointManager.AddWaypoint(wp);
                entry.Point = wp;
                Debug.Log($"[GeneKerman] Rescue waypoint for {contractId} at " +
                          $"{body.GetName()} {lat:F2}, {lon:F2}.");
            }
            catch (Exception ex)
            {
                entry.Point = null;
                entry.Suppressed = true;
                Debug.LogWarning($"[GeneKerman] Rescue waypoint for {contractId} failed: {ex.Message}");
            }
        }

        private static void Drop(string contractId)
        {
            Entry e;
            if (!live.TryGetValue(contractId, out e)) return;
            live.Remove(contractId);

            var wp = e.Point;
            if (wp == null)
            {
                // The node died with a scene, but navigation may still be pointing at it:
                // NavWaypoint matches on coordinates, so a stand-in carrying them is
                // enough to switch the navball marker off.
                if (e.Body == null) return;
                wp = new Waypoint
                {
                    celestialName = e.Body,
                    latitude = e.Lat,
                    longitude = e.Lon,
                    isOnSurface = true,
                };
                try { NavWaypoint.DeactivateIfWaypoint(wp); }
                catch (Exception ex)
                { Debug.LogWarning($"[GeneKerman] Rescue waypoint navigation clear failed: {ex.Message}"); }
                return;
            }

            try
            {
                NavWaypoint.DeactivateIfWaypoint(wp);
                WaypointManager.RemoveWaypoint(wp);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Rescue waypoint removal failed: {ex.Message}");
            }
        }

        // ── Presentation ────────────────────────────────────────────────────

        /// <summary>The map label. Short on purpose: it is drawn next to the icon at
        /// every zoom level, and the detail belongs in the captions under it.</summary>
        private static string Label(Dictionary<string, object> c)
        {
            string issuer = MiniJSON.GetString(c, "issuer_name", "");
            return string.IsNullOrEmpty(issuer) ? "Rescue site" : "Rescue: " + issuer;
        }

        /// <summary>Who is waiting there.</summary>
        private static string Caption(Dictionary<string, object> c)
        {
            var names = new List<string>();
            foreach (var k in MiniJSON.GetList(c, "rescue_kerbals"))
                if (k != null && !string.IsNullOrEmpty(k.ToString())) names.Add(k.ToString());
            if (names.Count == 0) return "";
            return string.Join(", ", names.ToArray());
        }

        /// <summary>How far off the spot still counts, in the two units that matter: the
        /// degrees the server actually checks, and the ground distance a pilot flies. The
        /// floor matches SubmissionSession's, so the number shown is the number enforced.</summary>
        private static string Tolerance(Dictionary<string, object> rt, CelestialBody body)
        {
            double margin = Math.Max(MiniJSON.GetDouble(rt, "margin_pos"), 0.01);
            double km = margin * Math.PI / 180.0 * body.Radius / 1000.0;
            return $"Land within {margin:F2}° (about {km:F1} km)";
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static CelestialBody ResolveBody(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var body = FlightGlobals.GetBodyByName(name);
            if (body != null) return body;
            // Same fallback as VesselTransfer.PlaceAtTarget: a body named with different
            // casing than this install spells it is still that body.
            if (FlightGlobals.Bodies != null)
                foreach (var b in FlightGlobals.Bodies)
                    if (b != null && string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase))
                        return b;
            return null;
        }

        /// <summary>A non-negative seed derived from the contract id. FNV-1a rather than
        /// string.GetHashCode, which is only promised to be stable within one process —
        /// the point is that a rescue keeps its map colour across sessions.</summary>
        private static int StableSeed(string contractId)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in contractId)
                {
                    h ^= ch;
                    h *= 16777619;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}
