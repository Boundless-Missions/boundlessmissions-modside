/*
 * UI/Gui/ContractForm.cs – Issuing work from the sidebar.
 *
 * WebUI/src/screens/CreateContract.tsx is the visual specification; ContractCreation.cs
 * is the implementation and is called unchanged. Nothing here decides anything the other
 * two front ends do not: the form names a *mode* ("stock", "janitor", …) and never a mod
 * list, because ContractCreation derives that from the running game — a UI that could
 * pass the folder list outright would make the mode labels a lie.
 *
 * Validation is ContractCreation.Validate, called every rebuild so the Send button can
 * say why it is disabled. The one check added on top is the balance, which that layer
 * deliberately does not do (it would have to fetch it, and the server re-checks at escrow
 * time); the profile is already in hand here, so the round trip is worth saving.
 *
 * Rescues are issued here too, and they are the one type that reads the live game: the
 * vessel being handed over is whatever is active at send time, never named by the form
 * (ContractCreation re-scans on send for exactly that reason). Sending destroys that
 * vessel in this save, so the send is gated behind an explicit acknowledgement — the
 * same gate the browser UI uses, and the reason this type was held back from the sidebar
 * until it existed here.
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman.UI.Gui
{
    internal sealed class ContractForm
    {
        private struct Option
        {
            public string Id;
            public string Label;
            public string Desc;
        }

        /// <summary>
        /// The types the sidebar issues, in the web screen's order. `auto` is not
        /// offered anywhere: letting the AI classify is a fallback for contracts that
        /// arrive without a type, not something to ask a player to choose.
        /// </summary>
        private static readonly Option[] Types =
        {
            new Option { Id = "craft_build",   Label = "Craft build",    Desc = "They submit a blueprint from the VAB or SPH." },
            new Option { Id = "active_vessel", Label = "Active mission", Desc = "They fly a craft to the target." },
            new Option { Id = "rescue",        Label = "Rescue",         Desc = "They rescue the kerbals on your current vessel." },
            new Option { Id = "flag_design",   Label = "Flag design",    Desc = "They design a flag, reviewed via Discord." },
        };

        /// <summary>What the rescuer has to bring back. Crew-only is the ordinary
        /// rescue; "vessel" makes it a salvage — they have to get the wreck home too.</summary>
        private static readonly Option[] Recoveries =
        {
            new Option { Id = ContractCreation.RecoveryCrew,   Label = "Crew only",
                         Desc = "They may strip or abandon this craft; only the kerbals have to arrive." },
            new Option { Id = ContractCreation.RecoveryVessel, Label = "Crew + this vessel",
                         Desc = "A salvage: they have to tow or fly this craft home too. Price it accordingly." },
        };

        private static readonly Option[] Modlists =
        {
            new Option { Id = ContractCreation.ModlistNone,     Label = "Any",       Desc = "No part restriction." },
            new Option { Id = ContractCreation.ModlistStock,    Label = "Stock",     Desc = "Squad parts only, no DLC." },
            new Option { Id = ContractCreation.ModlistStockDlc, Label = "Stock+DLC", Desc = "Squad plus the official expansions." },
            new Option { Id = ContractCreation.ModlistMine,     Label = "My mods",   Desc = "Every mod currently installed on your game." },
            new Option { Id = ContractCreation.ModlistJanitor,  Label = "Janitor's", Desc = "Only mods visible in your Janitor's Closet filter." },
        };

        private readonly PlayerPicker picker = new PlayerPicker();
        private readonly DatePicker duePicker = new DatePicker();
        private readonly BodyPicker bodyPicker = new BodyPicker();

        private string type = "craft_build";
        private bool auction;
        private string mission = "";
        private string payment = "";
        private string fine = "0";
        private string duration = "24";
        private string dueText = "";
        private string modlistMode = ContractCreation.ModlistNone;

        // Rescue. The scan is the live game as of the last time the type was selected
        // or Rescan was pressed; ContractCreation re-reads it on send regardless, so
        // this is only ever what the form shows, never what gets sent.
        private ContractCreation.RescueContext rescue;
        private string rescueMode = "orbit";
        private string rescueRecovery = ContractCreation.RecoveryCrew;
        private string apText = "100", peText = "100", marginAltText = "10";
        private string latText = "0", lonText = "0", marginPosText = "1";
        private string minDvText = "0";
        private bool rescueConfirmed;
        // Orbit-mode plane/regime requirement. Off by default: Ap/Pe is what a rescue
        // has always asked for, and turning a plane match on silently would make every
        // rescue issued from this form a harder job than the issuer thought.
        private bool requireIncl;
        private string inclText = "0";
        private string marginInclText = Round(ContractCreation.DefaultMarginInclDeg);
        private readonly List<string> orbitTypes = new List<string>();

        private Action markDirty;
        private Func<Action<bool, string>> begin;
        private Action created;

        internal void Attach(Action onMarkDirty, Func<Action<bool, string>> onBegin, Action onCreated)
        {
            markDirty = onMarkDirty;
            begin = onBegin;
            created = onCreated;
            picker.Attach(onMarkDirty);
            bodyPicker.Attach(onMarkDirty);
        }

        /// <summary>Names this form's scroll offset across rebuilds — see ScrollMemory.</summary>
        private const string ScrollKey = "contract-form";

        /// <summary>Back to a blank form. Called whenever it is opened.</summary>
        internal void Reset()
        {
            ScrollMemory.Forget(ScrollKey);

            type = "craft_build";
            auction = false;
            mission = "";
            payment = "";
            fine = "0";
            duration = "24";
            modlistMode = ContractCreation.ModlistNone;
            // A week out, matching the web screen's default. Local date, because the
            // player reads it against their own calendar.
            dueText = DatePicker.Print(DateTime.Now.Date.AddDays(7));

            rescue = null;
            rescueMode = "orbit";
            rescueRecovery = ContractCreation.RecoveryCrew;
            apText = "100"; peText = "100"; marginAltText = "10";
            latText = "0"; lonText = "0"; marginPosText = "1";
            minDvText = "0";
            rescueConfirmed = false;
            requireIncl = false;
            inclText = "0";
            marginInclText = Round(ContractCreation.DefaultMarginInclDeg);
            orbitTypes.Clear();

            picker.Reset();
            bodyPicker.Reset();
            duePicker.Close();
        }

        /// <summary>Re-read the game and seed the target from where the vessel actually
        /// is. Typing an orbit from scratch is how you end up describing somewhere
        /// impossible; seeding is only ever a starting point the issuer edits.</summary>
        private void ScanRescue()
        {
            rescue = ContractCreation.ScanRescueContext();
            bodyPicker.SetBodies(rescue.Bodies, rescue.Body);
            if (!rescue.Available) return;

            apText = Round(rescue.ApKm);
            peText = Round(rescue.PeKm);
            latText = Round(rescue.Lat);
            lonText = Round(rescue.Lon);
            // Seeded, not switched on: the plane requirement stays off until the issuer
            // asks for it, and this is the number they get when they do.
            inclText = Round(rescue.InclDeg);
        }

        private static string Round(double v)
            => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal void Poll()
        {
            picker.EnsureLoaded();
            picker.Tick();
        }

        internal void Dispose() => picker.Dispose();

        // ── Build ───────────────────────────────────────────────────────────

        internal void Build(El parent, bool busy)
        {
            if (string.IsNullOrEmpty(dueText)) Reset();

            var profile = GeneKermanMod.Instance?.State?.ProfileData;
            string currency = profile == null
                ? "KCoins" : MiniJSON.GetString(profile, "currency_name", "KCoins");
            int balance = profile == null ? -1 : MiniJSON.GetInt(profile, "balance");

            El body;
            // Keyed, because this form is the worst case for a rebuild resetting the
            // scroll: it is the tallest screen in the sidebar and almost every answer
            // on it — the type cards, the recipient, the date, every switch — marks
            // the panel dirty, so without this each answer threw the player back to
            // the first question. Reset() forgets it, so a new contract starts at the
            // top.
            UIF.ScrollView(parent, out body, ScrollKey).Flex(1f, 1f);

            BuildTypes(body);
            BuildRecipient(body);

            Caption(body, "MISSION");
            var missionField = UIF.TextField(body, mission, "What do they have to do?", 66, true);
            missionField.OnChanged(s => mission = s);

            BuildMoney(body, currency, balance);
            if (auction && Auctionable(type))
            {
                Caption(body, "AUCTION RUNS FOR (HOURS)");
                UIF.TextField(body, duration, "24").OnChanged(s => duration = s);
            }

            // A rescue's part restriction is not a choice: the rescuer has to be able to
            // load the wreck, so ContractCreation always sends the issuer's own mod list.
            if (type != "flag_design" && !IsRescue) BuildModlist(body);
            if (IsRescue) BuildRescue(body);

            BuildSend(body, busy, balance, currency);
        }

        private bool IsRescue => type == "rescue";

        private void BuildTypes(El parent)
        {
            Caption(parent, "WHAT KIND OF WORK?");

            foreach (var t in Types)
            {
                var option = t;
                UIF.Choice(parent, t.Label, t.Desc, type == t.Id, () =>
                {
                    type = option.Id;
                    // A rescue cannot be auctioned, so switching to it drops the mode
                    // rather than leaving it armed and ignored.
                    if (!Auctionable(type)) auction = false;
                    // Read the game when the type is picked rather than every rebuild:
                    // the scan walks every body and the active vessel's crew, and the
                    // panel rebuilds on every click.
                    if (IsRescue) ScanRescue();
                    markDirty?.Invoke();
                });
            }

            if (Auctionable(type))
            {
                UIF.Switch(parent, "Open auction",
                           "No single recipient. Anyone bids the price down in Discord and the lowest wins.",
                           auction, v => { auction = v; markDirty?.Invoke(); });
            }
        }

        private void BuildRecipient(El parent)
        {
            if (auction && Auctionable(type))
            {
                UIF.Notice(parent, "Open to everyone.",
                           "The lowest bidder in Discord gets the contract.");
                return;
            }

            Caption(parent, "WHO IS THIS FOR?");
            picker.Build(parent);
        }

        private void BuildMoney(El parent, string currency, int balance)
        {
            Caption(parent, "PAYMENT (" + currency.ToUpperInvariant() + ")");
            UIF.TextField(parent, payment, "0").OnChanged(s => payment = s);

            if (balance >= 0)
            {
                int wanted = ParseInt(payment, 0);
                bool over = wanted > balance;
                UIF.Label(parent, "Balance " + balance.ToString("N0"), Theme.FontXs,
                          over ? Theme.Destructive : Theme.MutedForeground);
            }

            Caption(parent, "FINE (" + currency.ToUpperInvariant() + ")");
            UIF.TextField(parent, fine, "0").OnChanged(s => fine = s);

            Caption(parent, "DUE DATE");
            // Typed or picked: the box stays, because a player who knows the date is
            // faster typing it than paging a calendar to it. Never into the past —
            // the server refuses a contract due yesterday, and a form that could
            // produce one would look broken at the last possible moment.
            duePicker.Build(parent, dueText, DateTime.Now.Date,
                            picked => dueText = picked,
                            typed => dueText = typed);
        }

        private void BuildModlist(El parent)
        {
            Caption(parent, "PART RESTRICTION");

            var row = UIF.Box(parent, "Modes").Row(Theme.Space1).H(24);
            foreach (var m in Modlists)
            {
                // Janitor's Closet is only a choice if the player actually has it.
                if (m.Id == ContractCreation.ModlistJanitor &&
                    !ContractCreation.IsJanitorsClosetAvailable()) continue;

                var option = m;
                var b = UIF.Button(row, m.Label, () => { modlistMode = option.Id; markDirty?.Invoke(); },
                                   modlistMode == m.Id ? BtnStyle.Primary : BtnStyle.Ghost,
                                   24, Theme.Space1);
                b.Label.Size(Theme.FontXs).Ellipsis();
                b.E.PrefW(0).Flex(1f);
            }

            foreach (var m in Modlists)
                if (m.Id == modlistMode) UIF.Muted(parent, m.Desc).Body();

            // The filter lives on the editor's part list, so it can only be read while
            // the editor is open. Said here rather than on send, where it would arrive
            // as a refusal after the player thought they were done.
            if (modlistMode == ContractCreation.ModlistJanitor &&
                !ContractCreation.IsEditorFilterReadable())
            {
                UIF.Label(parent, "Open the VAB or SPH so the Janitor's Closet filter can be read.",
                          Theme.FontXs, Theme.Destructive).Body();
            }
        }

        // ── Rescue ──────────────────────────────────────────────────────────

        /// <summary>
        /// The rescue slice: who is stranded, what has to come back, and where it has
        /// to be delivered. The vessel is never named here — ContractCreation reads
        /// FlightGlobals at send time, because a form that could name it would let the
        /// player hand over a ship they are no longer flying.
        /// </summary>
        private void BuildRescue(El parent)
        {
            if (rescue == null) ScanRescue();

            UIF.Divider(parent);

            if (!rescue.Available)
            {
                UIF.Notice(parent, "No crewed vessel to hand over.",
                           HighLogic.LoadedSceneIsFlight
                           ? "Switch to a vessel with crew aboard. A rescue hands that ship and its kerbals to the rescuer."
                           : "A rescue hands over the vessel you are flying, so you have to be in flight on a crewed ship.");
                UIF.Button(parent, "Rescan", () => { ScanRescue(); markDirty?.Invoke(); },
                           BtnStyle.Ghost, 28);
                return;
            }

            var head = UIF.Box(parent, "Stranded").Row(Theme.Space2).ChildAlign(TextAnchor.MiddleLeft);
            var who = UIF.Box(head, "Who").Column(0).PrefW(0).Flex(1f);
            UIF.Label(who, rescue.VesselName, Theme.FontSm).Ellipsis();
            UIF.Muted(who, string.Join(", ", rescue.Crew.ToArray()) +
                           " (" + rescue.Crew.Count + " aboard)").Ellipsis();
            UIF.Button(head, "Rescan", () => { ScanRescue(); markDirty?.Invoke(); }, BtnStyle.Ghost, 28);

            Caption(parent, "WHAT MUST COME BACK?");
            foreach (var r in Recoveries)
            {
                var option = r;
                UIF.Choice(parent, r.Label, r.Desc, rescueRecovery == r.Id, () =>
                {
                    rescueRecovery = option.Id;
                    markDirty?.Invoke();
                });
            }

            Caption(parent, "WHERE SHOULD THE RESCUER FIND THEM?");
            var modes = UIF.Box(parent, "Modes").Row(Theme.Space1).H(24);
            foreach (var m in new[] { "orbit", "surface" })
            {
                string option = m;
                var b = UIF.Button(modes, m == "orbit" ? "Orbit" : "Surface",
                                   () => { rescueMode = option; markDirty?.Invoke(); },
                                   rescueMode == m ? BtnStyle.Primary : BtnStyle.Ghost,
                                   24, Theme.Space1);
                b.Label.Size(Theme.FontXs);
                b.E.PrefW(0).Flex(1f);
            }

            Caption(parent, "BODY");
            bodyPicker.Build(parent);
            if (bodyPicker.SelectedIsModded)
                UIF.Muted(parent, "Modded body: the rescuer is warned they need its planet pack.").Body();

            if (rescueMode == "orbit")
            {
                Caption(parent, "APOAPSIS (KM)");
                UIF.TextField(parent, apText, "100").OnChanged(s => apText = s);
                Caption(parent, "PERIAPSIS (KM)");
                UIF.TextField(parent, peText, "100").OnChanged(s => peText = s);
                Caption(parent, "MARGIN (KM, MIN " + ContractCreation.MinMarginOrbitKm + ")");
                UIF.TextField(parent, marginAltText, "10").OnChanged(s => marginAltText = s);
                BuildOrbitShape(parent);
            }
            else
            {
                Caption(parent, "LATITUDE (°)");
                UIF.TextField(parent, latText, "0").OnChanged(s => latText = s);
                Caption(parent, "LONGITUDE (°)");
                UIF.TextField(parent, lonText, "0").OnChanged(s => lonText = s);
                Caption(parent, "MARGIN (°, MIN " + ContractCreation.MinMarginSurfaceDeg + ")");
                UIF.TextField(parent, marginPosText, "1").OnChanged(s => marginPosText = s);
            }

            Caption(parent, "Δv THEY MUST HAVE LEFT (M/S, 0 = ANY)");
            UIF.TextField(parent, minDvText, "0").OnChanged(s => minDvText = s);
            UIF.Muted(parent, "Checked on the craft that delivers the crew, so they aren't " +
                              "dropped somewhere they can't leave.").Body();

            UIF.Muted(parent, "Part restriction is automatic on a rescue: the rescuer needs " +
                              "your mods to load the wreck.").Body();

            // Explicit, because sending this destroys the ship in this save and there is
            // no undo. A Switch rather than a line of warning text: the player has to
            // move something before the send will go through.
            UIF.Switch(parent, "I understand this is permanent",
                       rescue.VesselName + " and its crew leave my save and become the " +
                       "rescuer's problem. This cannot be undone.",
                       rescueConfirmed, v => { rescueConfirmed = v; markDirty?.Invoke(); });
        }

        /// <summary>
        /// Which orbit, not just how high: the plane the rescuer has to be in, and any
        /// named regime it has to be.
        ///
        /// Both are off by default and both are orbit-mode only. Ap/Pe is all a rescue
        /// has ever asked for, and a plane match switched on by default would silently
        /// turn every rescue issued here into a much more expensive job — matching a
        /// plane is the half of a rendezvous that costs delta-v.
        /// </summary>
        private void BuildOrbitShape(El parent)
        {
            UIF.Switch(parent, "Require an orbital plane",
                       "Ap and Pe don't say which orbit this is. A plane match is what makes "
                       + "this a real intercept rather than a matching altitude.",
                       requireIncl, v => { requireIncl = v; markDirty?.Invoke(); });

            if (requireIncl)
            {
                Caption(parent, "INCLINATION (°, 0–180; OVER 90 IS RETROGRADE)");
                UIF.TextField(parent, inclText, "0").OnChanged(s => inclText = s);
                Caption(parent, "PLANE MARGIN (°, MIN " + ContractCreation.MinMarginInclDeg + ")");
                UIF.TextField(parent, marginInclText, Round(ContractCreation.DefaultMarginInclDeg))
                   .OnChanged(s => marginInclText = s);
                if (rescue != null && rescue.InOrbit)
                    UIF.Muted(parent, $"You are in a {rescue.InclDeg:F1}° orbit right now.").Body();
            }

            Caption(parent, "ORBIT TYPE (OPTIONAL)");
            var tokens = ContractCreation.OrbitTypeTokens;
            const int perRow = 3;
            for (int i = 0; i < tokens.Length; i += perRow)
            {
                var row = UIF.Box(parent, "Types" + i).Row(Theme.Space1).H(24);
                for (int j = i; j < i + perRow; j++)
                {
                    if (j >= tokens.Length) { UIF.Grow(row); break; }
                    string tok = tokens[j];
                    bool on = orbitTypes.Contains(tok);
                    var b = UIF.Button(row, ContractCreation.OrbitTypeLabel(tok), () =>
                    {
                        if (orbitTypes.Contains(tok)) orbitTypes.Remove(tok);
                        else orbitTypes.Add(tok);
                        markDirty?.Invoke();
                    }, on ? BtnStyle.Primary : BtnStyle.Ghost, 24, Theme.Space1);
                    b.Label.Size(Theme.FontXs);
                    b.E.PrefW(0).Flex(1f);
                }
            }
            UIF.Muted(parent, "Checked against the rescuer's own orbit when they submit. "
                              + "None selected = any orbit that meets the numbers above.").Body();
        }

        private void BuildSend(El parent, bool busy, int balance, string currency)
        {
            // Drawn from the state as it was at the last rebuild, which is a hint
            // rather than a gate — see below.
            string hint = Problem(BuildRequest(), balance, currency);
            if (hint != null) UIF.Muted(parent, hint).Body();

            string label = busy ? "Sending…"
                         : auction && Auctionable(type) ? "Start auction"
                         : "Send contract";

            // Enabled whenever nothing is in flight, and validated on click instead.
            //
            // A disabled-until-valid button would be wrong here in a way it is not on
            // the web: typing does not rebuild this panel (that would destroy the box
            // being typed into), so the button's enabled state is always one rebuild
            // stale. Filling in the last field and pressing Send would do nothing the
            // first time and work the second.
            UIF.Button(parent, label, () =>
            {
                var mod = GeneKermanMod.Instance;
                if (mod == null || begin == null) return;

                var req = BuildRequest();
                var done = begin();

                string problem = Problem(req, balance, currency);
                if (problem != null) { done(false, problem); return; }

                // Create validates again on its own, so a form that let something
                // through cannot become a bad request further down.
                mod.RunCoroutine(ContractCreation.Create(req, (ok, message) =>
                {
                    if (ok) created?.Invoke();
                    done(ok, message);
                }));
            }, BtnStyle.Primary, 32).Interactable(!busy);
        }

        /// <summary>
        /// Why this cannot be sent, or null. ContractCreation.Validate does the work;
        /// the balance is added on top because that layer deliberately does not fetch
        /// one, and the profile is already in hand here.
        /// </summary>
        private string Problem(ContractCreation.Request req, int balance, string currency)
        {
            string error;
            if (!ContractCreation.Validate(req, out error)) return error;

            // Validate cannot see this: the acknowledgement is a property of the form,
            // not of the request, and the browser UI gates on its own copy the same way.
            if (IsRescue && !rescueConfirmed)
                return "Confirm that your vessel will be handed over.";

            if (balance >= 0 && req.Payment > balance)
                return "You only have " + balance.ToString("N0") + " " + currency + ".";

            return null;
        }

        // ── State ───────────────────────────────────────────────────────────

        private ContractCreation.Request BuildRequest()
        {
            bool isAuction = auction && Auctionable(type);

            var req = new ContractCreation.Request
            {
                Kind = IsRescue ? "rescue" : isAuction ? "auction" : "contract",
                ContractorId = isAuction ? "" : (picker.SelectedId ?? ""),
                ContractorName = isAuction ? "" : (picker.SelectedName ?? ""),
                Mission = mission ?? "",
                Payment = ParseInt(payment, 0),
                Fine = Mathf.Max(0, ParseInt(fine, 0)),
                DueDate = (dueText ?? "").Trim(),
                ContractType = type,
                // A flag has no in-game build step to restrict and a rescue always
                // carries the issuer's own mod list — ContractCreation ignores the mode
                // for both, so it is sent as "none" and the two agree.
                ModlistMode = (type == "flag_design" || IsRescue)
                              ? ContractCreation.ModlistNone : modlistMode,
                DurationHours = ParseInt(duration, 24),
            };

            if (!IsRescue) return req;

            req.RescueBody = bodyPicker.Selected ?? "";
            req.RescueMode = rescueMode;
            req.RescueRecovery = rescueRecovery;
            req.MinDvMs = ParseDouble(minDvText, 0);
            req.ApKm = ParseDouble(apText, 0);
            req.PeKm = ParseDouble(peText, 0);
            req.MarginAltKm = ParseDouble(marginAltText, 0);
            req.Lat = ParseDouble(latText, 0);
            req.Lon = ParseDouble(lonText, 0);
            req.MarginPosDeg = ParseDouble(marginPosText, 0);
            req.RequireIncl = requireIncl;
            req.InclDeg = ParseDouble(inclText, 0);
            req.MarginInclDeg = ParseDouble(marginInclText, ContractCreation.DefaultMarginInclDeg);
            req.OrbitTypes = new List<string>(orbitTypes);
            return req;
        }

        /// <summary>Which types can be put up for open bidding. Every type except a
        /// rescue: sending one destroys the issuer's vessel, so it cannot be handed to
        /// a contractor who is not decided yet.</summary>
        private static bool Auctionable(string t) => t != "rescue";

        private static void Caption(El parent, string text)
            => UIF.Muted(parent, text);

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) ? v : fallback;
        }

        /// <summary>Invariant-culture only. A player on a comma-decimal locale typing
        /// "1,5" gets the fallback rather than a silently different number, and
        /// NumberStyles.Float excludes the "NaN"/"Infinity" literals that Any accepts —
        /// both of which would otherwise reach the rescue target as a real value.</summary>
        private static double ParseDouble(string s, double fallback)
        {
            double v;
            return double.TryParse((s ?? "").Trim(),
                                   System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v)
                   ? v : fallback;
        }
    }
}
