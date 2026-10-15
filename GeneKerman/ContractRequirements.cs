/*
 * ContractRequirements.cs – "Can this install even fly this contract?"
 *
 * A contract's terms are written on the ISSUER's machine, against the parts,
 * propellants and professions THEY have. The server resolves the loose ones against
 * whatever catalogue it holds for the reader (_summary_constraints → resolve_parts),
 * which is exactly why a mention it could not pin down stays loose and arrives here
 * as a requirement naming something this game may never have had. Nothing said so
 * until the submit pre-flight refused a finished craft with "Required part not
 * found" — at the end of the work rather than before it was accepted.
 *
 * This is the same question ContractConstraints.CheckCraft asks, moved one step
 * earlier and asked of the INSTALL instead of a craft: not "does this ship carry
 * an ion engine" but "does this game have one at all". Only requirements are
 * checked; a *forbidden* rule is satisfied by not having the thing, so an install
 * can never fail one.
 *
 * Four things shape it.
 *
 * • A substitute is NOT a pass. PartAliases will swap a missing part for an
 *   installed equivalent on an incoming craft, and the marketplace's compatibility
 *   check rightly discounts those — but a *requirement* is matched by exact
 *   internal name at both ends (_missing_required in data/mission_constraints.py
 *   and CheckCraft here), so a craft built with the stand-in is refused. The
 *   look-alike is reported as a detail on a failing row, never as a green light.
 *
 * • Unknown is its own answer. Asked before PartLoader has finished, every part in
 *   the game is "missing"; a checklist of red rows that are all wrong is worse than
 *   no checklist, so the state is carried through as Unknown and drawn as neither —
 *   the same distinction the website's craft compatibility draws with `known`.
 *
 * • Locked is not missing. A part present in a career save the player has not
 *   researched yet blocks a launch just as hard, but the fix is R&D rather than
 *   CKAN, so it must not end up in a modpack.
 *
 * • The mod hint is closed. A missing part cannot be resolved to a mod on the
 *   machine that hasn't got it — every mod-detection path here walks parts to a
 *   GameData folder — so the only honest sources are the contract's own declared
 *   mod list and, for a profession, ContractConstraints.TraitMods. Nothing is
 *   guessed from a part-name prefix, for the reason that table is closed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeneKerman
{
    public static class ContractRequirements
    {
        /// <summary>How a single requirement stands in this install.</summary>
        public enum Availability
        {
            /// <summary>Installed and usable here.</summary>
            Present,
            /// <summary>Installed, but this save cannot use it yet (career R&amp;D).</summary>
            Locked,
            /// <summary>Not in this install at all.</summary>
            Missing,
            /// <summary>Could not be decided — the part database was not ready.</summary>
            Unknown,
        }

        /// <summary>One requirement, as a row for the UI and (when it is missing and
        /// we can name one) the dependency that would fix it.</summary>
        public sealed class Item
        {
            /// <summary>"part" | "propellant" | "engine" | "category" | "crew" — what
            /// kind of thing this is, for grouping and for the web UI's icon.</summary>
            public string Kind = "part";
            /// <summary>What the contract asked for, as the player should read it.</summary>
            public string Label = "";
            public Availability State = Availability.Unknown;
            /// <summary>A sentence explaining a non-Present state, or null.</summary>
            public string Detail;
            /// <summary>The mod that would supply this, or null when nothing here can
            /// name one. Only ever set on a Missing row.</summary>
            public CkanGenerator.ModEntry Mod;

            public bool Blocking => State == Availability.Missing || State == Availability.Locked;
        }

        // ── The wreck's mods ─────────────────────────────────────────────────

        /// <summary>One mod a rescue's stranded vessel is built from, as the issuer's
        /// client resolved it. See <c>CkanGenerator.DescribeVesselMods</c>: the wreck
        /// exists on their machine and nowhere else, so that is the only moment its
        /// parts can be attributed to mods at all.</summary>
        public sealed class WreckMod
        {
            public CkanGenerator.ModEntry Mod;
            public List<string> Parts = new List<string>();
            /// <summary>Distinct parts this mod supplies, which may exceed
            /// <see cref="Parts"/> — both ends trim the list that travels.</summary>
            public int PartCount;
        }

        /// <summary>Read the `wreck_mods` list off a contract's `rescue_target`, or an
        /// empty list. A rescue issued before the field existed, or one whose wreck is
        /// stock, simply has nothing here — which reads as "nothing to warn about",
        /// the same answer a stock wreck should give.</summary>
        public static List<WreckMod> ParseWreckMods(Dictionary<string, object> rescueTarget)
        {
            var mods = new List<WreckMod>();
            if (rescueTarget == null) return mods;

            foreach (var o in MiniJSON.GetList(rescueTarget, "wreck_mods"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;

                string folder = MiniJSON.GetString(d, "folder", "");
                if (string.IsNullOrEmpty(folder)) continue;   // identifies nothing

                var wm = new WreckMod
                {
                    Mod = new CkanGenerator.ModEntry
                    {
                        folder = folder,
                        path = Blank(MiniJSON.GetString(d, "path", ""), folder),
                        ckan = Blank(MiniJSON.GetString(d, "ckan", ""), folder),
                        name = Blank(MiniJSON.GetString(d, "name", ""), folder),
                    },
                };

                foreach (var p in MiniJSON.GetList(d, "parts"))
                {
                    string title = p?.ToString();
                    if (!string.IsNullOrEmpty(title)) wm.Parts.Add(title);
                }
                wm.PartCount = MiniJSON.GetInt(d, "part_count", wm.Parts.Count);
                if (wm.PartCount < wm.Parts.Count) wm.PartCount = wm.Parts.Count;

                mods.Add(wm);
            }
            return mods;
        }

        private static string Blank(string value, string fallback)
            => string.IsNullOrEmpty(value) ? fallback : value;

        /// <summary>
        /// One row per mod the wreck needs, marked against this install.
        ///
        /// Asked here rather than left to the spawn. `CkanGenerator.ExtractCheckAndStripMods`
        /// already reads the node's own GKMODS block and writes a modpack for whatever is
        /// missing — but only when the wreck is *brought into the save*, which happens
        /// after the contract is accepted and the rescuer has flown out to it. By then
        /// the answer costs them the job. This is the same question asked while it is
        /// still an offer, off a list the issuer's client sent for exactly that purpose.
        /// </summary>
        private static List<Item> CheckWreckMods(List<WreckMod> wreckMods)
        {
            var items = new List<Item>();
            if (wreckMods == null || wreckMods.Count == 0) return items;

            var entries = wreckMods.Where(w => w != null && w.Mod != null)
                                   .Select(w => w.Mod).ToList();
            if (entries.Count == 0) return items;

            // One filesystem scan for the whole list, and the same definition of
            // "missing" the modpack writer uses — the checklist and the .ckan it offers
            // disagreeing about which mods are absent would be worse than either alone.
            var missing = new HashSet<string>(
                CkanGenerator.MissingMods(entries).Select(Key), StringComparer.OrdinalIgnoreCase);

            foreach (var w in wreckMods)
            {
                if (w == null || w.Mod == null) continue;
                var item = new Item { Kind = "mod", Label = w.Mod.name };

                if (missing.Contains(Key(w.Mod)))
                {
                    item.State = Availability.Missing;
                    item.Mod = w.Mod;
                    item.Detail = "Not installed. " + DescribeParts(w, "The wreck needs it for") +
                                  " KSP cannot create a part whose mod is absent, so the wreck " +
                                  "will not arrive intact.";
                }
                else
                {
                    item.State = Availability.Present;
                    item.Detail = DescribeParts(w, "Supplies");
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>"Supplies 3 parts: A, B (and 1 more)." — the part list the issuer
        /// sent, with the count it was trimmed from, so a shortened list never reads as
        /// the whole story.</summary>
        private static string DescribeParts(WreckMod w, string lead)
        {
            int total = w.PartCount > 0 ? w.PartCount : w.Parts.Count;
            if (total <= 0) return lead + " parts on the wreck.";

            string noun = total == 1 ? "part" : "parts";
            if (w.Parts.Count == 0) return $"{lead} {total} {noun} on the wreck.";

            string list = string.Join(", ", w.Parts.ToArray());
            int hidden = total - w.Parts.Count;
            string more = hidden > 0 ? $" (and {hidden} more)" : "";
            return $"{lead} {total} {noun} on the wreck: {list}{more}.";
        }

        /// <summary>What makes two mods the same mod here — the CKAN identifier when
        /// there is one, else the folder. Mirrors CkanGenerator's own dedupe key.</summary>
        private static string Key(CkanGenerator.ModEntry m)
            => m == null ? "" : (!string.IsNullOrEmpty(m.ckan) ? m.ckan : (m.folder ?? ""));

        // ── The trait → dependency table ─────────────────────────────────────

        // ContractConstraints.TraitMods answers "which mod defines this profession"
        // for a human ("USI/MKS"). CKAN needs an identifier and an install path, and
        // neither is derivable from that string — so the label is mapped on again
        // here rather than the fact being restated. Closed and deliberately tiny for
        // the same reason TraitMods is: a wrong identifier sends the player off to
        // install the wrong mod, which is worse than naming no mod at all.
        private static readonly Dictionary<string, CkanGenerator.ModEntry> TraitPacks =
            new Dictionary<string, CkanGenerator.ModEntry>(StringComparer.OrdinalIgnoreCase)
        {
            { "USI/MKS", new CkanGenerator.ModEntry {
                folder = "UmbraSpaceIndustries/MKS", path = "UmbraSpaceIndustries/MKS",
                ckan = "MKS", name = "MKS (USI Kolonization Systems)" } },
        };

        // ── The install index ────────────────────────────────────────────────
        //
        // "Is there an ion engine in this game" is a question about every loaded
        // part, and the panel that asks it is rebuilt on every MarkDirty. So the
        // scan runs once and the answers are kept: parts do not appear mid-session,
        // and the count is watched only so an index built before PartLoader finished
        // is thrown away rather than believed.

        private static HashSet<string> propellants;
        private static HashSet<string> engineCategories;
        private static HashSet<string> partCategories;
        /// <summary>Every usable part's title, for the loose half of a part rule — which
        /// is matched as a substring of a *title* at both ends. Kept alongside the sets
        /// so a mention costs one walk of this list rather than a walk of the whole
        /// catalogue with a usability test per part.</summary>
        private static List<AvailablePart> usableParts;
        private static int indexedCount = -1;

        /// <summary>Build (or reuse) the index of what this install can field. False
        /// when the part database is not ready, which every caller must read as
        /// "unknown" rather than as "nothing is installed".</summary>
        private static bool EnsureIndex()
        {
            var loaded = PartLoader.LoadedPartsList;
            if (loaded == null || loaded.Count == 0) return false;
            if (propellants != null && indexedCount == loaded.Count) return true;

            var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var engs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usable = new List<AvailablePart>(loaded.Count);

            try
            {
                foreach (var ap in loaded)
                {
                    // A part that cannot be put on a craft here answers nothing about
                    // what this install can field — the same rule PartAliases applies
                    // when choosing a substitution target.
                    if (ap == null || !PartAliases.IsUsable(ap)) continue;
                    usable.Add(ap);
                    var s = PartClassifier.Classify(ap);
                    props.UnionWith(s.Propellants);
                    engs.UnionWith(s.EngineCategories);
                    cats.UnionWith(s.PartCategories);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GeneKerman] ContractRequirements: part scan failed: " + ex.Message);
                return false;
            }

            propellants = props;
            engineCategories = engs;
            partCategories = cats;
            usableParts = usable;
            indexedCount = loaded.Count;
            return true;
        }

        // ── The check ────────────────────────────────────────────────────────

        /// <summary>Every *requirement* this contract makes, each marked against this
        /// install. Empty when the contract requires nothing — the numeric limits
        /// (part count, Δv, crew band) are deliberately absent: they are questions
        /// about a craft, not about whether this game contains the thing at all.</summary>
        public static List<Item> Check(ContractConstraints c, List<WreckMod> wreckMods = null)
        {
            var items = new List<Item>();

            // First, because it is the one requirement that is about the job rather
            // than about the craft the player will build for it — and on a rescue it is
            // usually the only row there is.
            items.AddRange(CheckWreckMods(wreckMods));

            if (c == null) return items;

            bool ready = EnsureIndex();

            foreach (var name in c.RequiredPartNames)
                items.Add(CheckExactPart(name, ready, c.PartTitles, c.PartMods));
            foreach (var mention in c.RequiredPartsUnresolved)
                items.Add(CheckLoosePart(mention, ready));

            foreach (var p in c.RequiredPropellants)
                items.Add(CheckSet("propellant", "Engine burning " + p, p, propellants, ready));
            foreach (var e in c.RequiredEngineCategories)
                items.Add(CheckSet("engine", Cap(e) + " engine", e, engineCategories, ready));
            foreach (var k in c.RequiredPartCategories)
                items.Add(CheckSet("category", Cap(k) + " part", k, partCategories, ready));

            foreach (var kvp in c.CrewTraits)
            {
                // A ceiling ("no Kolonists aboard") is satisfied by not having the mod,
                // so an install cannot fail it and naming the mod would read as advice
                // to install something in order to obey a ban. Floors only.
                if (kvp.Value == null || kvp.Value.Min <= 0) continue;
                items.Add(CheckTrait(kvp.Key, kvp.Value.Min));
            }

            return items;
        }

        /// <summary>True when anything the contract requires is missing or locked here.</summary>
        public static bool AnyBlocking(IEnumerable<Item> items)
            => items != null && items.Any(i => i != null && i.Blocking);

        /// <summary>One sentence naming what this install cannot supply, or null.
        ///
        /// "cannot supply" rather than "does not have", because a blocking row is
        /// either missing or locked and the two have different fixes — which the
        /// checklist beside this sentence already says, row by row.</summary>
        public static string Summary(IEnumerable<Item> items)
        {
            if (items == null) return null;
            var bad = items.Where(i => i != null && i.Blocking).Select(i => i.Label).ToList();
            if (bad.Count == 0) return null;
            string list = string.Join(", ", bad.Take(4).ToArray());
            if (bad.Count > 4) list += " and " + (bad.Count - 4) + " more";
            return "This contract requires " + list + ", which this install cannot supply.";
        }

        private static Item CheckExactPart(string name, bool ready,
                                           Dictionary<string, string> authorTitles = null,
                                           Dictionary<string, string> authorMods = null)
        {
            // Three ways to name the part, best first. `TitleOf` is this install's own
            // title and wins when we have the part. `authorTitles` is what the contract
            // author's install called it — the only readable name available when the
            // part is NOT installed here, which is exactly the case a player needs a
            // readable name for. The internal name is the last resort.
            string authorTitle = null;
            if (authorTitles != null && !string.IsNullOrEmpty(name))
                authorTitles.TryGetValue(name, out authorTitle);
            var item = new Item { Kind = "part", Label = TitleOf(name) ?? authorTitle ?? name };
            if (!ready) { item.State = Availability.Unknown; return item; }

            if (PartAliases.IsUsable(name))
            {
                string locked = LockedReason(name);
                item.State = locked == null ? Availability.Present : Availability.Locked;
                item.Detail = locked;
                return item;
            }

            item.State = Availability.Missing;

            // Name the mod that ships it, so `WriteCkanForMissing` can write a modpack
            // for THIS part instead of falling back to the contract's whole mod list.
            // Resolved through CkanGenerator so the entry carries a real CKAN identifier
            // where the registry knows one; a stock folder resolves to nothing, which is
            // right — you do not install Squad.
            string authorMod = null;
            if (authorMods != null && !string.IsNullOrEmpty(name))
                authorMods.TryGetValue(name, out authorMod);
            if (!string.IsNullOrEmpty(authorMod))
            {
                try
                {
                    var resolvedMods = CkanGenerator.ResolveMods(new[] { authorMod });
                    if (resolvedMods != null && resolvedMods.Count > 0) item.Mod = resolvedMods[0];
                }
                catch { /* naming the mod is a nicety; the requirement still stands */ }
            }

            // A look-alike is worth saying and is not a pass: the requirement is
            // matched by exact internal name on both ends, so a craft built with the
            // stand-in is refused by the same check that would have refused an empty
            // one. Reported so the player does not build with it and find out later.
            PartAliases.Group group;
            string sub = PartAliases.SubstituteFor(name, out group);
            item.Detail = sub != null
                ? "Not installed. You have '" + (TitleOf(sub) ?? sub) + "', which this mod would " +
                  "substitute on an incoming craft, but this contract asks for the exact part, " +
                  "so a craft built with the stand-in would still be refused."
                : BuildMissingDetail(name, authorTitle, item.Mod);
            return item;
        }

        /// <summary>What to say about a required part this install has not got. Names the
        /// mod when the contract carried one: "not installed" without saying what to
        /// install is a dead end the player cannot act on.</summary>
        private static string BuildMissingDetail(string name, string authorTitle,
                                                 CkanGenerator.ModEntry mod)
        {
            string what = authorTitle != null
                ? " The contract was written against '" + authorTitle + "' (" + name + ")."
                : "";
            string where = mod != null
                ? " Comes from " + (mod.name ?? mod.folder ?? mod.ckan) + "."
                : "";
            return ("Not installed here." + what + where).Trim();
        }

        private static Item CheckLoosePart(string mention, bool ready)
        {
            var item = new Item { Kind = "part", Label = mention };
            if (!ready) { item.State = Availability.Unknown; return item; }

            // The unresolved half of a part rule is matched against a part's *title*
            // as a substring, at both ends — so that is what is searched here.
            AvailablePart hit = null;
            foreach (var ap in usableParts)
            {
                if (ap == null || string.IsNullOrEmpty(ap.title)) continue;
                if (ap.title.IndexOf(mention, StringComparison.OrdinalIgnoreCase) >= 0) { hit = ap; break; }
            }

            if (hit == null)
            {
                item.State = Availability.Missing;
                // "title", not "name": the search above is over `ap.title`, the
                // display name, because that is the half a loose mention is written
                // against — `sspx-inflatable-hab-25-2` contains no "blimp" while
                // "PPF-B 'Blimp' Inflatable Habitation Module" does. Saying "name"
                // sent a player looking for a part whose internal name matched, which
                // is not what was checked and usually does not exist.
                item.Detail = "No part in this install has '" + mention + "' in its title.";
                return item;
            }

            string locked = LockedReason(hit.name);
            item.State = locked == null ? Availability.Present : Availability.Locked;
            item.Detail = locked ?? ("Matched by '" + hit.title + "'.");
            return item;
        }

        private static Item CheckSet(string kind, string label, string token,
                                     HashSet<string> index, bool ready)
        {
            var item = new Item { Kind = kind, Label = label };
            if (!ready || index == null) { item.State = Availability.Unknown; return item; }
            if (index.Contains(token)) { item.State = Availability.Present; return item; }
            item.State = Availability.Missing;
            item.Detail = "Nothing in this install provides it.";
            return item;
        }

        private static Item CheckTrait(string trait, int min)
        {
            var item = new Item { Kind = "crew", Label = min + "x " + trait };

            if (!ContractConstraints.TraitDefinedHere(trait))
            {
                item.State = Availability.Missing;
                string mod = ContractConstraints.TraitMod(trait);
                item.Detail = mod == null
                    ? "No mod installed here defines the '" + trait + "' profession."
                    : "No mod installed here defines the '" + trait + "' profession; it comes from " +
                      mod + ", which the contract's author has installed.";
                CkanGenerator.ModEntry pack;
                if (mod != null && TraitPacks.TryGetValue(mod, out pack)) item.Mod = pack;
                return item;
            }

            // Defined here, so the mission is flyable. Whether anyone currently holds
            // the job is a roster question with an in-game answer (hire, or train), so
            // it is a note on a green row rather than a state of its own.
            item.State = Availability.Present;
            int have = RosterCount(trait);
            if (have >= 0 && have < min)
                item.Detail = have == 0
                    ? "No " + trait + " in your roster yet, hire one at the Astronaut Complex."
                    : "Only " + have + " in your roster; this contract needs " + min + ".";
            return item;
        }

        /// <summary>How many kerbals in this save hold a profession, or -1 when there
        /// is no roster to ask (the main menu, a scene loaded without a game).</summary>
        private static int RosterCount(string trait)
        {
            try
            {
                var game = HighLogic.CurrentGame;
                if (game == null || game.CrewRoster == null) return -1;
                int n = 0;
                foreach (var pcm in game.CrewRoster.Crew)
                    if (pcm != null && !string.IsNullOrEmpty(pcm.trait) &&
                        pcm.trait.Equals(trait, StringComparison.OrdinalIgnoreCase) &&
                        pcm.rosterStatus != ProtoCrewMember.RosterStatus.Dead) n++;
                return n;
            }
            catch { return -1; }
        }

        /// <summary>Why an installed part still cannot be flown in THIS save, or null.
        /// A career part that has not been researched blocks a launch as hard as a
        /// missing one, but the fix is R&amp;D rather than CKAN — which is why it is
        /// its own state and never reaches the modpack.</summary>
        private static string LockedReason(string partName)
        {
            try
            {
                if (ResearchAndDevelopment.Instance == null) return null;   // sandbox
                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                if (ap == null) return null;
                if (!ResearchAndDevelopment.PartTechAvailable(ap))
                    return "Installed, but its tech node is not researched in this save yet.";
                if (!ResearchAndDevelopment.PartModelPurchased(ap))
                    return "Installed and researched, but not bought in this save yet.";
                return null;
            }
            catch { return null; }   // can't tell — don't invent a warning
        }

        private static string TitleOf(string partName)
        {
            try
            {
                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                return ap == null ? null : ap.title;
            }
            catch { return null; }
        }

        private static string Cap(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ── The modpack ──────────────────────────────────────────────────────

        /// <summary>
        /// Write a CKAN modpack for what this install is missing, after the player has
        /// accepted the contract anyway. No-op when nothing is missing.
        ///
        /// Two sources, and the precise one wins outright rather than being merged with
        /// the vague one.
        ///
        /// **Precise**: a requirement that names its own mod — a rescue wreck's mods,
        /// resolved part by part on the issuer's machine, and a profession's pack (see
        /// <see cref="TraitPacks"/>). When any of these exist they are the whole answer.
        ///
        /// **Vague**: the contract's declared mod list, used only when nothing precise
        /// is available. A missing *part* cannot be resolved to a mod on a machine that
        /// hasn't got it — every mod-detection path here walks an installed part to its
        /// GameData folder — so the issuer's declared list stands in.
        ///
        /// They are not merged, because on a rescue the declared list is
        /// `BuildActiveModlist()`: every GameData folder that contributed a loaded part
        /// on the issuer's machine. Adding that to three exactly-known wreck mods would
        /// bury the answer in eighty dependencies the rescuer does not need — a modpack
        /// that installs someone else's whole install to recover one ship.
        ///
        /// Where neither exists there is nothing honest to say, and the player is told
        /// that rather than handed a guess.
        /// </summary>
        public static void WriteCkanForMissing(string context, List<Item> items, string modlistCsv)
        {
            var mods = new List<CkanGenerator.ModEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (items != null)
                foreach (var i in items)
                    if (i != null && i.State == Availability.Missing && i.Mod != null &&
                        seen.Add(i.Mod.ckan ?? i.Mod.folder ?? ""))
                        mods.Add(i.Mod);

            // Only when nothing named its own mod — see the note above on why these two
            // are alternatives rather than a union.
            if (mods.Count == 0)
                foreach (var m in CkanGenerator.ResolveMods(ModlistFolders(modlistCsv)))
                    if (seen.Add(m.ckan ?? m.folder ?? "")) mods.Add(m);

            if (mods.Count == 0)
            {
                GeneKermanMod.Instance?.ShowNotification(
                    "No modpack could be written for '" + context + "'",
                    "This contract requires something this install has not got, but it declares no " +
                    "mod list and nothing here can say which mod provides it. Ask the issuer which " +
                    "mods the mission was written against.");
                return;
            }

            CkanGenerator.GenerateCkanForMissing(context, mods, "contract");
        }

        /// <summary>A short readable name for a contract, used as the modpack's title
        /// and — sanitized — as its file name. The mission's own first line, because
        /// that is what the player will recognise in GeneKerman_MissingMods/; the id
        /// stands in only when there is no text to use, since a file named after a
        /// database key tells nobody which job it belongs to.</summary>
        public static string ContextName(Dictionary<string, object> contract)
        {
            if (contract == null) return "contract";

            string mission = MiniJSON.GetString(contract, "mission", "") ?? "";
            int nl = mission.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) mission = mission.Substring(0, nl);
            mission = mission.Trim();
            if (mission.Length > 48) mission = mission.Substring(0, 48).TrimEnd();

            if (mission.Length > 0) return mission;

            string id = MiniJSON.GetString(contract, "contract_id", "") ?? "";
            return id.Length > 0 ? "contract " + id : "contract";
        }

        /// <summary>The GameData folders a contract's mod list names. Exclusion tokens
        /// ("-Squad/Expansions", the modlist's way of carving a folder back out — see
        /// EditorPartEnforcer) are dropped: they say what may NOT be used, which is
        /// never a dependency.</summary>
        private static IEnumerable<string> ModlistFolders(string modlistCsv)
        {
            if (string.IsNullOrEmpty(modlistCsv)) yield break;
            foreach (var token in modlistCsv.Split(','))
            {
                string t = token.Trim();
                if (t.Length == 0 || t[0] == '-') continue;
                yield return t;
            }
        }
    }
}
