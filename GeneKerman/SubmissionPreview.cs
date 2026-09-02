/*
 * SubmissionPreview.cs – Fetching the images a contractor submitted for review.
 *
 * This logic used to live inside the classic window's FetchBlueprints, writing straight into
 * that window's popup fields (blueprintTextures, telemetryTextures, blueprintStatus,
 * loadingBlueprints). Same shape as CraftDelivery before its extraction in 3b: no
 * completion point, so no second front end could use it.
 *
 * Extracted here as one coroutine that calls onDone exactly once with a result
 * object. The classic window wrapped it for its popup; the uGUI sidebar wraps it to show
 * the submission inline. One implementation, two front ends.
 *
 * The `telemetry_urls` / `telemetry_url` fallback below is exactly the kind of
 * detail a second copy loses: older submissions carry the singular key, newer
 * multi-craft ones the plural, and a front end that only reads one shows an empty
 * preview for half the contracts in the system.
 *
 * A third caller since: a rescue's *wreck* schematics — the blueprint the issuer's
 * client rendered of the stranded ship and the orbit diagram the server drew from its
 * telemetry. Those URLs ride on the contract summary rather than behind an endpoint, so
 * there is nothing to ask; `FetchImages` is the download half on its own, and `Fetch`
 * is now "ask which URLs, then FetchImages". The textures, the ownership and the
 * "no images" answer are the same job either way, which is the point.
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

        /// <summary>How many images arrived and were refused by the decode guard.
        ///
        /// Kept apart from <see cref="Error"/> because it is a different statement: the
        /// fetch worked, and the reviewer is nevertheless not seeing what was submitted.
        /// Silence here meant a submission judged blind — the panel drew "No images were
        /// submitted", which is exactly what a contractor who sent none looks like.</summary>
        public int Refused;

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

            var blueprints = new List<string>();
            var images = MiniJSON.GetList(data, "images");
            if (images != null)
            {
                foreach (var obj in images)
                {
                    var entry = obj as Dictionary<string, object>;
                    if (entry == null) continue;

                    string url = MiniJSON.GetString(entry, "url", null);
                    if (!string.IsNullOrEmpty(url)) blueprints.Add(url);
                }
            }

            yield return Download(result, blueprints, TelemetryUrls(data),
                                  "No images were submitted for this contract.");

            onDone?.Invoke(result);
        }

        /// <summary>
        /// Download an already-known set of image URLs into a preview. Calls
        /// <paramref name="onDone"/> exactly once, always with a non-null result.
        ///
        /// The other half of <see cref="Fetch"/>: that one asks an endpoint which URLs
        /// there are, this one is handed them. A rescue's blueprint and orbit diagram
        /// ride on the contract summary itself, so there is nothing to ask — but the
        /// downloading, the decode, the ownership of the textures and the "no images"
        /// answer are the same job, and the whole reason this file exists is that a
        /// second copy of that job loses a detail (see the header).
        /// </summary>
        public static IEnumerator FetchImages(
            IList<string> blueprintUrls, IList<string> telemetryUrls,
            string vesselName, string emptyMessage, Action<SubmissionPreview> onDone)
        {
            var result = new SubmissionPreview { VesselName = vesselName ?? "" };
            yield return Download(result, blueprintUrls, telemetryUrls, emptyMessage);
            onDone?.Invoke(result);
        }

        /// <summary>Fetch every URL and file the decoded textures into the preview.
        /// A URL that fails is skipped, not fatal: half a set beats none, and the
        /// caller finds out through <see cref="SubmissionPreview.HasImages"/>.</summary>
        private static IEnumerator Download(
            SubmissionPreview result, IList<string> blueprintUrls,
            IList<string> telemetryUrls, string emptyMessage)
        {
            if (telemetryUrls != null)
            {
                foreach (string url in telemetryUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    yield return GeneKermanMod.Instance.Api.DownloadFile(url, (dok, bytes) =>
                    {
                        var tex = Decode(dok, bytes, result);
                        if (tex != null) result.Telemetry.Add(tex);
                    });
                }
            }

            if (blueprintUrls != null)
            {
                foreach (string url in blueprintUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    yield return GeneKermanMod.Instance.Api.DownloadFile(url, (dok, bytes) =>
                    {
                        var tex = Decode(dok, bytes, result);
                        if (tex != null) result.Blueprints.Add(tex);
                    });
                }
            }

            if (!result.HasImages)
                // "No images" and "the images would not open here" are different answers
                // and the reviewer has to be given the right one — the first says the
                // contractor sent nothing, which is a reason to refuse a submission.
                result.Error = result.Refused > 0
                    ? $"{result.Refused} submitted image(s) could not be opened on this " +
                      "machine. Nothing is missing from the submission; report this with " +
                      "your KSP.log rather than judging it on what you can see."
                    : emptyMessage;
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

        /// <summary>Decode one downloaded image, counting a refusal on
        /// <paramref name="into"/>. A download that never arrived is not counted — that
        /// is a network failure, not an image this client would not open.</summary>
        private static Texture2D Decode(bool ok, byte[] bytes, SubmissionPreview into)
        {
            if (!ok || bytes == null) return null;
            // Peer-supplied: another player's submission screenshots. See
            // ToolActions.ImageIsSafeToDecode — the header decides the allocation.
            if (!ToolActions.ImageIsSafeToDecode(bytes, "a submission image"))
            {
                if (into != null) into.Refused++;
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (tex.LoadImage(bytes)) return tex;

            UnityEngine.Object.Destroy(tex);
            if (into != null) into.Refused++;
            return null;
        }
    }
}
