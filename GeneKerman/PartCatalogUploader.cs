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
            foreach (var ap in PartLoader.LoadedPartsList)
            {
                if (ap == null || string.IsNullOrEmpty(ap.name)) continue;
                string title = ap.title ?? ap.name;
                parts.Add(new Dictionary<string, object> { { "name", ap.name }, { "title", title } });
                names.Add(ap.name + "|" + title);
            }
            if (parts.Count == 0) return;

            names.Sort(System.StringComparer.Ordinal);
            foreach (var n in names) sb.Append(n).Append('\n');
            string hash = Fnv1a(sb.ToString());

            // Skip the network round-trip if we already uploaded this exact catalog.
            if (ReadHash() == hash)
            {
                Debug.Log("[GeneKerman] Part catalog unchanged (" + parts.Count + " parts) — upload skipped.");
                return;
            }

            string json = MiniJSON.Serialize(new Dictionary<string, object>
            {
                { "hash", hash },
                { "parts", parts },
            });

            Debug.Log("[GeneKerman] Uploading part catalog: " + parts.Count + " parts (hash " + hash + ").");
            GeneKermanMod.Instance.RunCoroutine(api.Post("/api/v1/parts/catalog", json, (ok, resp, code) =>
            {
                if (ok) WriteHash(hash);
                else Debug.LogWarning("[GeneKerman] Part catalog upload failed (" + code + ").");
            }));
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
