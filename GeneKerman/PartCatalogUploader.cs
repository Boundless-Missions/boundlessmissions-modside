using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// Uploads the player's full installed part catalog (internal name + display
    /// title for every loaded part) to the bot, so it can resolve loosely-typed
    /// part mentions in mission limits ("the Thud engine", a typo'd "thudd") to
    /// the exact installed part. Hash-gated: the catalog is sent at most once per
    /// session and only re-sent when the installed parts change.
    ///
    /// It also carries the install's celestial bodies and the GameData folders its
    /// parts come from, which is what the bot's weekly mission pool is filtered on
    /// (data/install_profile.py): a stock install is not offered "Land on Venus",
    /// and an RSS install is not offered "Land on the Mun". Two lists rather than
    /// one because neither is sufficient — Real Solar System ships no parts, so it
    /// appears in the bodies and not in the folders, while Near Future and Far
    /// Future ship no bodies and appear only in the folders.
    ///
    /// Both are folded into the hash, so an install that gains a planet pack
    /// re-uploads even though its part list is unchanged. If the bodies are not
    /// available yet (they are populated after the system is built, so a very early
    /// call sees none) the upload still goes out without them and the differing
    /// hash makes the next session send the complete one.
    /// </summary>
    public static class PartCatalogUploader
    {
        private static bool attemptedThisSession;

        private static string HashPath => Path.Combine(GeneKermanMod.PluginDataPath, "catalog.hash");

        public static void EnsureUploaded(ApiClient api)
        {
            if (api == null || !api.IsLinked || attemptedThisSession) return;
            if (PartLoader.LoadedPartsList == null || PartLoader.LoadedPartsList.Count == 0) return;
            attemptedThisSession = true;

            var parts = new List<object>();
            var sb = new StringBuilder();
            var names = new List<string>();
            var folders = new List<string>();
            var folderSeen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var ap in PartLoader.LoadedPartsList)
            {
                if (ap == null || string.IsNullOrEmpty(ap.name)) continue;
                string title = ap.title ?? ap.name;
                var entry = new Dictionary<string, object> { { "name", ap.name }, { "title", title } };

                // Which mod ships this part. KSP already knows — it is the first segment
                // of partUrl — so nothing is scanned, cached or re-derived; this is the
                // same walk ContractCreation.BuildActiveModlist() does, done in the pass
                // we are already making rather than in a second one.
                //
                // Sent as the folder STRING on each part, not as an index into the list
                // below. An index was tried and was a mistake: it makes the meaning of a
                // part positional across a network hop and a datastore, and the first
                // layer to tidy that list — the server sorted it, reasonably, back when
                // it was only a set of names — silently repointed every part. An SSPX
                // part reported "Comes from kOS", which is worse than reporting nothing,
                // because it is confidently wrong and sends the player to install the
                // wrong mod. The string costs ~30 bytes a part, about 90 KB on a
                // 2,900-part install against a 1 MiB ceiling, and it cannot be
                // invalidated by anything downstream because it carries its own meaning.
                string folder = ModFolderOf(ap);
                if (folder != null)
                {
                    entry["mod"] = folder;
                    if (!folderSeen.Contains(folder))
                    {
                        folderSeen.Add(folder);
                        folders.Add(folder);
                    }
                }

                parts.Add(entry);
                // The folder goes in the hash line too, so an install that moves a part
                // between mods re-uploads even though the name and title are unchanged.
                names.Add(ap.name + "|" + title + "|" + (folder ?? ""));
            }
            if (parts.Count == 0) return;

            var bodies = new List<string>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (var b in FlightGlobals.Bodies)
                    if (b != null && !string.IsNullOrEmpty(b.bodyName))
                        bodies.Add(b.bodyName);
            }

            names.Sort(System.StringComparer.Ordinal);
            bodies.Sort(System.StringComparer.Ordinal);
            // Safe to sort now that nothing indexes into it: `folders` is just the set
            // of mods this install loaded parts from, for install detection.
            folders.Sort(System.StringComparer.Ordinal);
            var foldersForHash = folders;
            // A schema stamp, first, so the hash changes when the SHAPE of the upload
            // changes and not only when the install does. Without it a fix to how the
            // catalog is built or stored can never reach an install whose parts are
            // unchanged: the client compares hashes and skips, and the bad document
            // lives on the server forever. Bumped to 2 when the per-part mod index was
            // added and the server stopped reordering `mods` underneath it — every
            // catalog written before that has indices pointing at the wrong mod.
            sb.Append("#schema=3\n");
            foreach (var n in names) sb.Append(n).Append('\n');
            // Kept in separate sections so a part named like a folder cannot make two
            // different installs hash the same.
            sb.Append("#folders\n");
            foreach (var f in foldersForHash) sb.Append(f).Append('\n');
            sb.Append("#bodies\n");
            foreach (var b in bodies) sb.Append(b).Append('\n');
            string hash = Fnv1a(sb.ToString());

            // Skip the network round-trip if we already uploaded this exact catalog.
            if (ReadHash() == hash)
            {
                Debug.Log("[GeneKerman] Part catalog unchanged (" + parts.Count + " parts, "
                          + folders.Count + " mods, " + bodies.Count + " bodies), upload skipped.");
                return;
            }

            string json = MiniJSON.Serialize(new Dictionary<string, object>
            {
                { "hash", hash },
                { "parts", parts },
                { "mods", folders },
                { "bodies", bodies },
            });

            Debug.Log("[GeneKerman] Uploading part catalog: " + parts.Count + " parts, "
                      + folders.Count + " mods, " + bodies.Count + " bodies (hash " + hash + ").");
            GeneKermanMod.Instance.RunCoroutine(api.Post("/api/v1/parts/catalog", json, (ok, resp, code) =>
            {
                if (ok) WriteHash(hash);
                else Debug.LogWarning("[GeneKerman] Part catalog upload failed (" + code + ").");
            }));
        }

        /// <summary>The GameData folder that ships this part — the first segment of its
        /// partUrl, except under SquadExpansion where the second is kept too. Mirrors
        /// CkanGenerator.GetModFolder, which is private there; the two DLCs are bought
        /// separately, so "SquadExpansion" alone does not say whether a recipient can
        /// load the part.</summary>
        private static string ModFolderOf(AvailablePart ap)
        {
            if (ap == null || string.IsNullOrEmpty(ap.partUrl)) return null;
            string[] seg = ap.partUrl.Split('/');
            if (seg.Length < 1 || string.IsNullOrEmpty(seg[0])) return null;
            if (seg.Length > 1 && seg[0].Equals("SquadExpansion", System.StringComparison.OrdinalIgnoreCase))
                return seg[0] + "/" + seg[1];
            return seg[0];
        }

        private static string ReadHash()
        {
            try { return File.Exists(HashPath) ? File.ReadAllText(HashPath).Trim() : null; }
            catch { return null; }
        }

        private static void WriteHash(string hash)
        {
            try
            {
                Directory.CreateDirectory(GeneKermanMod.PluginDataPath);
                File.WriteAllText(HashPath, hash);
            }
            catch { /* non-fatal: we'll just re-upload next session */ }
        }

        // FNV-1a 64-bit — deterministic, dependency-free, plenty for change detection.
        private static string Fnv1a(string s)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }
}
