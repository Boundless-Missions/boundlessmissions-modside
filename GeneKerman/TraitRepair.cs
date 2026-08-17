/*
 * TraitRepair.cs – Put a kerbal back to work whose profession no installed mod defines.
 *
 * A trait string is just a name. KSP resolves it to an EXPERIENCE_TRAIT config on
 * demand, and when nothing matches, `pcm.experienceTrait` stays null while `pcm.trait`
 * keeps the unresolvable name. That pair is a landmine: every stock screen built out of
 * CrewListItem (Astronaut Complex, crew assignment) reads `experienceTrait.Title` and
 * throws part-way through drawing the list, taking out the rest of the list, the other
 * tabs, and any chance of telling which kerbal caused it. See VesselTransfer.ApplyTrait,
 * which refuses to *create* this state on import — this file is for the saves that
 * already have it, from a mod uninstalled between sessions.
 *
 * Why this used to be read-only, and what changed: the trait string is the only record
 * of the profession, so overwriting it costs the player a kerbal's job for good — which
 * is why the scan reported and never wrote. Three things make writing safe now:
 *
 *   1. It only happens when the player presses Fix. Nothing here runs on its own.
 *   2. The original is copied out of the save first (PluginData/trait_repairs.cfg), so
 *      the name survives the overwrite that removes it from the roster.
 *   3. `RestoreRecovered` puts it back by itself once the defining mod is installed
 *      again, which makes the repair a loan rather than a deletion.
 *
 * Records are keyed by save folder as well as kerbal name: two saves can hold two
 * different Bob Kermans, and a repair in one says nothing about the other.
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeneKerman
{
    public static class TraitRepair
    {
        private const string RootNode = "GeneKermanTraitRepairs";
        private const string RecordNode = "REPAIR";

        private static string FilePath =>
            Path.Combine(GeneKermanMod.PluginDataPath, "trait_repairs.cfg");

        /// <summary>
        /// The stock profession to stand in for a modded one. Unlike
        /// <see cref="ContractConstraints.TraitMod"/> — a fact about which mod owns a
        /// profession — this is a *usability guess* about which stock job is closest,
        /// and the two are kept apart for the same reason PartAliases keeps look-alikes
        /// out of its substitution table: one is provable, the other is judgement.
        ///
        /// Which is why the guess is only ever applied to a kerbal who is already
        /// unusable, is recorded before it is applied, and is undone the moment the real
        /// profession is available again.
        /// </summary>
        private static readonly Dictionary<string, string> StockEquivalent =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // USI/MKS. The colony trades are hands-on work → Engineer; the survey and
            // lab trades read data → Scientist; a Scout flies ahead → Pilot.
            { "Kolonist", "Engineer" },
            { "Miner", "Engineer" },
            { "Mechanic", "Engineer" },
            { "Technician", "Engineer" },
            { "Quartermaster", "Engineer" },
            { "Medic", "Scientist" },
            { "Biologist", "Scientist" },
            { "Geologist", "Scientist" },
            { "Botanist", "Scientist" },
            { "Chemist", "Scientist" },
            { "Farmer", "Scientist" },
            { "Scout", "Pilot" },
        };

        /// <summary>True when this install can resolve a trait name to a config — the one
        /// test that separates a working kerbal from one that breaks crew screens.
        ///
        /// Errs towards "defined" when it cannot tell (no GameDatabase yet, a throw), so a
        /// database that isn't ready never reads as a roster full of broken kerbals. Use
        /// <see cref="CanDefine"/> for the opposite need — deciding what to *write*.
        /// </summary>
        public static bool IsDefined(string trait)
        {
            if (string.IsNullOrEmpty(trait)) return false;
            try
            {
                var configs = GameDatabase.Instance != null
                    ? GameDatabase.Instance.ExperienceConfigs : null;
                if (configs == null) return true;
                return configs.GetExperienceTraitConfig(trait) != null;
            }
            catch { return true; }
        }

        /// <summary>The same question with the benefit of the doubt reversed: only true
        /// when a config was actually found. Every path that assigns a trait asks this
        /// one, because the lenient answer above would happily hand a kerbal a name this
        /// install cannot resolve — which is the exact state all of this exists to undo.</summary>
        private static bool CanDefine(string trait)
        {
            if (string.IsNullOrEmpty(trait)) return false;
            try
            {
                var configs = GameDatabase.Instance != null
                    ? GameDatabase.Instance.ExperienceConfigs : null;
                return configs != null && configs.GetExperienceTraitConfig(trait) != null;
            }
            catch { return false; }
        }

        /// <summary>Every kerbal in the loaded save whose profession nothing installed
        /// defines. Statuses include Dead and Missing on purpose: KSP draws those in the
        /// Astronaut Complex too, so one of them breaks the screen exactly as a live
        /// kerbal does.</summary>
        public static List<ProtoCrewMember> BrokenCrew()
        {
            var found = new List<ProtoCrewMember>();
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return found;

            try
            {
                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Available,
                    ProtoCrewMember.RosterStatus.Assigned,
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                foreach (var pcm in roster.Kerbals(statuses))
                {
                    if (pcm == null || string.IsNullOrEmpty(pcm.trait)) continue;
                    if (!IsDefined(pcm.trait)) found.Add(pcm);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Trait scan failed: {ex.Message}");
            }
            return found;
        }

        /// <summary>
        /// Give every broken kerbal a profession this install defines, remembering what
        /// they were. Returns a sentence for the player either way — this is wired to a
        /// button, and a button that reports nothing reads as a button that did nothing.
        /// </summary>
        public static string Repair()
        {
            var broken = BrokenCrew();
            if (broken.Count == 0)
                return "Nothing to repair — every kerbal's profession resolves on this install.";

            var records = Load();
            var done = new List<string>();
            var failed = new List<string>();

            foreach (var pcm in broken)
            {
                string original = pcm.trait;
                string target = PickLocalTrait(original);
                if (target == null)
                {
                    failed.Add($"{pcm.name} ({original})");
                    continue;
                }

                // Record before writing: after SetExperienceTrait the original name is
                // gone from the save, and this file is then the only copy of it.
                Remember(records, pcm.name, original, target);
                try
                {
                    KerbalRoster.SetExperienceTrait(pcm, target);
                    done.Add($"{pcm.name}: {original} → {target}");
                }
                catch (Exception ex)
                {
                    failed.Add($"{pcm.name} ({original})");
                    Debug.LogWarning($"[GeneKerman] Trait repair failed for {pcm.name}: {ex.Message}");
                }
            }

            if (done.Count > 0)
            {
                Store(records);
                PersistIfSafe();
            }

            var sb = new System.Text.StringBuilder();
            if (done.Count > 0)
            {
                sb.Append($"Repaired {done.Count} kerbal(s): ")
                  .Append(string.Join("; ", done.ToArray()))
                  .Append(". Crew screens work again. Their original professions are ")
                  .Append("remembered — install the mod that defines them and they are ")
                  .Append("restored automatically.");
            }
            if (failed.Count > 0)
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append($"Could not repair {failed.Count}: ")
                  .Append(string.Join(", ", failed.ToArray()))
                  .Append(" — this install defines no profession to put them in.");
            }
            string msg = sb.ToString();
            Debug.Log($"[GeneKerman] TraitRepair: {msg}");
            return msg;
        }

        /// <summary>One kerbal who is not carrying the profession they should be, and what
        /// they are carrying instead. Lives here rather than in the import path because
        /// both producers feed the same record file: a repair the player asked for, and a
        /// profession the crew-import path had to refuse (VesselTransfer.ApplyTrait).</summary>
        public class Downgrade
        {
            public string Name;
            public string Original;
            public string Given;
        }

        /// <summary>
        /// Remember professions that were lost somewhere other than <see cref="Repair"/> —
        /// today, crew arriving from another player's install. Same file, so
        /// <see cref="RestoreRecovered"/> hands those back too, which is what makes
        /// "install the mod and they get their job back" true of an imported kerbal and
        /// not only of a repaired one.
        ///
        /// Writes the record file once for the whole batch and never the save: the caller
        /// is mid-import and owns when the game is written.
        /// </summary>
        public static void RememberDowngrades(List<Downgrade> downgrades)
        {
            if (downgrades == null || downgrades.Count == 0) return;
            var records = Load();
            foreach (var d in downgrades)
            {
                if (d == null || string.IsNullOrEmpty(d.Name) || string.IsNullOrEmpty(d.Original))
                    continue;
                Remember(records, d.Name, d.Original, d.Given);
            }
            Store(records);
        }

        /// <summary>
        /// Hand back every profession whose mod has come back, and forget the record.
        /// Returns one label per kerbal restored ("Bob Kerman → Kolonist"), for the
        /// caller to report; empty when there is nothing to undo.
        ///
        /// A record is dropped as soon as its profession is definable again, restored or
        /// not: if the kerbal is gone from the roster, or the player has since chosen a
        /// different job for them, then their choice stands and there is nothing left
        /// for us to remember.
        /// </summary>
        public static List<string> RestoreRecovered()
        {
            var restored = new List<string>();
            var records = Load();
            if (records.Count == 0) return restored;

            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return restored;

            string save = SaveKey();
            bool changed = false;

            for (int i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];
                if (r.save != save) continue;
                // Strict: restoring a name this install still cannot resolve would put
                // the roster straight back into the state the repair got it out of.
                if (!CanDefine(r.original)) continue;

                ProtoCrewMember pcm = Find(roster, r.kerbal);

                // Moot rather than restorable: the kerbal is no longer in the roster, or
                // is not carrying the profession we gave them — either way the player's
                // save has moved on and there is nothing of ours left to hand back.
                if (pcm == null ||
                    !string.Equals(pcm.trait, r.given, StringComparison.OrdinalIgnoreCase))
                {
                    records.RemoveAt(i);
                    changed = true;
                    continue;
                }

                try
                {
                    KerbalRoster.SetExperienceTrait(pcm, r.original);
                    restored.Add($"{r.kerbal} → {r.original}");
                    // Only now: the record is the only copy of the original, so it is
                    // dropped when the profession is safely back on the kerbal and not
                    // one line earlier.
                    records.RemoveAt(i);
                    changed = true;
                }
                catch (Exception ex)
                {
                    // Keep the record and try again next visit.
                    Debug.LogWarning($"[GeneKerman] Trait restore failed for {r.kerbal}: {ex.Message}");
                }
            }

            if (changed) Store(records);
            if (restored.Count > 0)
            {
                PersistIfSafe();
                Debug.Log($"[GeneKerman] TraitRepair: restored {restored.Count} profession(s): " +
                          string.Join(", ", restored.ToArray()));
            }
            return restored;
        }

        /// <summary>A kerbal by name across every roster status, or null. Walks the
        /// statuses rather than trusting the name indexer alone, because a repaired kerbal
        /// may well be one of the dead or missing ones — those break the Astronaut Complex
        /// exactly as the living do, so the repair covers them and so must the undo.</summary>
        private static ProtoCrewMember Find(KerbalRoster roster, string name)
        {
            if (roster == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var statuses = new[]
                {
                    ProtoCrewMember.RosterStatus.Available,
                    ProtoCrewMember.RosterStatus.Assigned,
                    ProtoCrewMember.RosterStatus.Dead,
                    ProtoCrewMember.RosterStatus.Missing,
                };
                foreach (var pcm in roster.Kerbals(statuses))
                    if (pcm != null && pcm.name == name) return pcm;
                return roster[name];
            }
            catch { return null; }
        }

        /// <summary>
        /// Write the roster change out, unless we're in flight — `SaveNow` must not run
        /// there (it would serialize a half-torn-down vessel), and the button is reachable
        /// from the flight scene like everything else in the window.
        ///
        /// Skipping it costs nothing that matters: the trait is already changed in the
        /// live roster, so KSP persists it at the next ordinary save. Our own record file
        /// is written either way, and it is the copy that must not be lost — if the game
        /// never saves, the next session finds the kerbal still carrying the original
        /// trait, does not recognise it as repaired, and drops the stale record.
        /// </summary>
        private static void PersistIfSafe()
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                Debug.Log("[GeneKerman] TraitRepair: in flight — leaving the roster change " +
                          "for KSP's next save.");
                return;
            }
            VesselTransfer.SaveNow();
        }

        /// <summary>A profession this install actually defines, preferring the nearest
        /// stock stand-in. Falls through to whatever the game does define (minus Tourist,
        /// which is a passenger rather than a job) so a total conversion with its own
        /// trait set is still repairable.</summary>
        private static string PickLocalTrait(string original)
        {
            string eq;
            if (original != null && StockEquivalent.TryGetValue(original, out eq) && CanDefine(eq))
                return eq;

            foreach (var stock in new[] { "Engineer", "Pilot", "Scientist" })
                if (CanDefine(stock)) return stock;

            try
            {
                var configs = GameDatabase.Instance != null
                    ? GameDatabase.Instance.ExperienceConfigs : null;
                if (configs != null)
                    foreach (string name in configs.TraitNamesNoTourist)
                        if (!string.IsNullOrEmpty(name) && CanDefine(name)) return name;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not enumerate professions: {ex.Message}");
            }
            return null;
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private class Record
        {
            public string save;
            public string kerbal;
            public string original;
            public string given;
        }

        /// <summary>The save a record belongs to. Two saves can hold two different Bob
        /// Kermans, so a repair in one must not be undone in the other.</summary>
        private static string SaveKey()
        {
            return string.IsNullOrEmpty(HighLogic.SaveFolder) ? "?" : HighLogic.SaveFolder;
        }

        private static void Remember(List<Record> records, string kerbal, string original, string given)
        {
            string save = SaveKey();
            foreach (var r in records)
            {
                if (r.save == save && r.kerbal == kerbal)
                {
                    // Keep the *first* original: it is the real profession. `given` moves,
                    // since that is what we must recognise on the kerbal to undo it.
                    r.given = given;
                    return;
                }
            }
            records.Add(new Record { save = save, kerbal = kerbal, original = original, given = given });
        }

        private static List<Record> Load()
        {
            var records = new List<Record>();
            try
            {
                if (!File.Exists(FilePath)) return records;
                ConfigNode root = ConfigNode.Load(FilePath);
                ConfigNode node = root != null ? root.GetNode(RootNode) : null;
                if (node == null) return records;

                foreach (ConfigNode rn in node.GetNodes(RecordNode))
                {
                    string kerbal = rn.GetValue("kerbal");
                    string original = rn.GetValue("original");
                    if (string.IsNullOrEmpty(kerbal) || string.IsNullOrEmpty(original)) continue;
                    records.Add(new Record
                    {
                        save = rn.GetValue("save") ?? "?",
                        kerbal = kerbal,
                        original = original,
                        given = rn.GetValue("given") ?? "",
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not read trait_repairs.cfg: {ex.Message}");
            }
            return records;
        }

        private static void Store(List<Record> records)
        {
            try
            {
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);

                // An empty list means every profession has been handed back; leaving the
                // file behind would only be a stale one to read next session.
                if (records.Count == 0)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return;
                }

                var root = new ConfigNode();
                var node = root.AddNode(RootNode);
                foreach (var r in records)
                {
                    var rn = node.AddNode(RecordNode);
                    rn.AddValue("save", r.save);
                    rn.AddValue("kerbal", r.kerbal);
                    rn.AddValue("original", r.original);
                    rn.AddValue("given", r.given);
                }
                root.Save(FilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Could not write trait_repairs.cfg: {ex.Message}");
            }
        }
    }
}
