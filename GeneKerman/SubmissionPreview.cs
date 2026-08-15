/*
 * SubmissionPreview.cs – Fetching the images a contractor submitted for review.
 *
 * This logic used to live inside MainWindow.FetchBlueprints, writing straight into
 * that window's popup fields (blueprintTextures, telemetryTextures, blueprintStatus,
 * loadingBlueprints). Same shape as CraftDelivery before its extraction in 3b: no
 * completion point, so no second front end could use it.
 *
 * Extracted here as one coroutine that calls onDone exactly once with a result
 * object. MainWindow wraps it to keep its popup; the uGUI sidebar wraps it to show
 * the submission inline. One implementation, two front ends.
 *
 * The `telemetry_urls` / `telemetry_url` fallback below is exactly the kind of
 * detail a second copy loses: older submissions carry the singular key, newer
 * multi-craft ones the plural, and a front end that only reads one shows an empty
 * preview for half the contracts in the system.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// The images attached to one submission. Owns its textures — whoever asked for
    /// it must call <see cref="Dispose"/>, or they leak until the scene changes.
    /// </summary>
    public sealed class SubmissionPreview
    {
        /// <summary>Name of the submitted vessel, or empty.</summary>
        public string VesselName = "";

        /// <summary>Blueprint / vessel renders, in submission order.</summary>
        public readonly List<Texture2D> Blueprints = new List<Texture2D>();

        /// <summary>
        /// Orbital telemetry diagrams — one per craft on a multi-vessel submission.
        /// Kept separate from the blueprints because they are a different kind of
        /// picture and the classic UI shows them in their own window.
        /// </summary>
        public readonly List<Texture2D> Telemetry = new List<Texture2D>();

        /// <summary>Human-readable failure, or null when the fetch worked.</summary>
        public string Error;

        public bool HasImages => Blueprints.Count > 0 || Telemetry.Count > 0;

        public void Dispose()
        {
            foreach (var t in Blueprints) if (t != null) UnityEngine.Object.Destroy(t);
            foreach (var t in Telemetry) if (t != null) UnityEngine.Object.Destroy(t);
            Blueprints.Clear();
            Telemetry.Clear();
        }
    }

    public static class SubmissionPreviewLoader
    {
        /// <summary>
        /// Fetch a contract's submission and download its images. Calls
        /// <paramref name="onDone"/> exactly once, always with a non-null result —
        /// a failure arrives as a result carrying <see cref="SubmissionPreview.Error"/>,
        /// so callers never need a separate error path.
        /// </summary>
        public static IEnumerator Fetch(string contractId, Action<SubmissionPreview> onDone)
        {
            var result = new SubmissionPreview();

            if (string.IsNullOrEmpty(contractId))
            {
                result.Error = "No contract.";
                onDone?.Invoke(result);
                yield break;
            }

            string resp = null;
            bool ok = false;
            yield return GeneKermanMod.Instance.Api.Get(
                "/api/v1/contracts/" + contractId + "/submission", (s, body, status) =>
                {
                    ok = s;
                    resp = body;
                });

            if (!ok || string.IsNullOrEmpty(resp))
            {
                result.Error = "Could not load the submission.";
                onDone?.Invoke(result);
                yield break;
            }

            var data = MiniJSON.DeserializeDict(resp);
            result.VesselName = MiniJSON.GetString(data, "vessel_name", "");

            foreach (string url in TelemetryUrls(data))
            {
                yield return GeneKermanMod.Instance.Api.DownloadFile(url, (dok, bytes) =>
                {
                    var tex = Decode(dok, bytes);
                    if (tex != null) result.Telemetry.Add(tex);
                });
            }

            var images = MiniJSON.GetList(data, "images");
            if (images != null)
            {
                foreach (var obj in images)
                {
                    var entry = obj as Dictionary<string, object>;
                    if (entry == null) continue;

                    string url = MiniJSON.GetString(entry, "url", null);
                    if (string.IsNullOrEmpty(url)) continue;

                    yield return GeneKermanMod.Instance.Api.DownloadFile(url, (dok, bytes) =>
                    {
                        var tex = Decode(dok, bytes);
                        if (tex != null) result.Blueprints.Add(tex);
                    });
                }
            }

            if (!result.HasImages)
                result.Error = "No images were submitted for this contract.";

            onDone?.Invoke(result);
        }

        /// <summary>
        /// Telemetry URLs, plural key first. A multi-craft submission carries
        /// `telemetry_urls`; older single-craft ones carry `telemetry_url`. Reading
        /// only one of the two silently empties the preview for half the contracts.
        /// </summary>
        private static List<string> TelemetryUrls(Dictionary<string, object> data)
        {
            var urls = new List<string>();

            var list = MiniJSON.GetList(data, "telemetry_urls");
            if (list != null)
                foreach (var t in list)
                {
                    string u = t as string;
                    if (!string.IsNullOrEmpty(u)) urls.Add(u);
                }

            if (urls.Count == 0)
            {
                string single = MiniJSON.GetString(data, "telemetry_url", "");
                if (!string.IsNullOrEmpty(single)) urls.Add(single);
            }

            return urls;
        }

        private static Texture2D Decode(bool ok, byte[] bytes)
        {
            if (!ok || bytes == null) return null;

            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (tex.LoadImage(bytes)) return tex;

            UnityEngine.Object.Destroy(tex);
            return null;
        }
    }
}
