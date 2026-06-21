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
    }
}
