/*
 * ModVersion.cs – This mod's version identity.
 *
 * `Current` is the human-readable version label shown in Settings and sent to the
 * server for display. `Sha256` is the hash of this very GeneKerman.dll on disk —
 * the server compares it against the published latest hash to gate updates, so the
 * version a player is running can't be spoofed by editing a string.
 */

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GeneKerman
{
    public static class ModVersion
    {
        /// <summary>Human-readable version label. Bump this when you ship a build.</summary>
        public const string Current = "1.0.0";

        private static string _hash;

        /// <summary>
        /// Lowercase hex SHA256 of this loaded GeneKerman.dll, computed once and cached.
        /// Empty string if the assembly file can't be read (the gate then fails open).
        /// </summary>
        public static string Sha256
        {
            get
            {
                if (_hash != null) return _hash;
                try
                {
                    string path = Assembly.GetExecutingAssembly().Location;
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(path))
                    {
                        byte[] digest = sha.ComputeHash(fs);
                        var sb = new StringBuilder(digest.Length * 2);
                        foreach (byte b in digest) sb.Append(b.ToString("x2"));
                        _hash = sb.ToString();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[GeneKerman] Could not hash own assembly for version check: " + e.Message);
                    _hash = "";
                }
                return _hash;
            }
        }

        /// <summary>
        /// Challenge-response attestation digest: lowercase hex SHA256 of
        /// (UTF8(nonce) ‖ thisDll[offset .. offset+length]). The server recomputes
        /// the same over the pristine published DLL and compares; a mismatch means
        /// this account's DLL differs from the official release. Empty string on error.
        /// </summary>
        public static string AttestDigest(string nonce, int offset, int length)
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                byte[] all = File.ReadAllBytes(path);
                if (offset < 0) offset = 0;
                if (offset > all.Length) offset = all.Length;
                if (length <= 0 || offset + length > all.Length)
                    length = Math.Max(0, all.Length - offset);

                byte[] nonceBytes = Encoding.UTF8.GetBytes(nonce ?? "");
                byte[] buf = new byte[nonceBytes.Length + length];
                Array.Copy(nonceBytes, 0, buf, 0, nonceBytes.Length);
                Array.Copy(all, offset, buf, nonceBytes.Length, length);

                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(buf);
                    var sb = new StringBuilder(digest.Length * 2);
                    foreach (byte b in digest) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GeneKerman] Attestation digest failed: " + e.Message);
                return "";
            }
        }
    }
}
