using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>Writes a file only its owner can read.
    ///
    /// Two files here are credentials: `PluginData/session.token` is a 30-day account
    /// bearer token, and the dev bridge's `debug_bridge.json` is the only thing standing
    /// in front of a listener that deletes vessels and edits rosters. Both were written
    /// with a plain <c>File.WriteAllText</c>, which takes the process umask — observed
    /// 0644 on disk, i.e. readable by every account on the machine.
    ///
    /// That gap matters because the threat model written next to the bridge's auth code
    /// reasons specifically about "a hostile local user **on the same account**". At 0644
    /// the reader does not need the same account, so the code was weaker than the comment
    /// justifying it. The bot half of this project moved its `.env`, service-account JSON
    /// and log to 0600 in an earlier pass; the mod half was never swept.
    ///
    /// .NET's <c>File.SetAttributes</c> cannot express Unix permissions, and this build
    /// references no Mono.Posix. So the chmod is done by REFLECTION, the same way every
    /// optional dependency in this project is reached (the life-support adapters,
    /// SimulationDetection, the VesselMover probe): resolved once, cached, and a complete
    /// no-op when the assembly is absent. On Windows — including these instances running
    /// under Proton — there is nothing to do and nothing is attempted.
    /// </summary>
    internal static class SecureFile
    {
        private static bool _probed;
        private static MethodInfo _chmod;

        private static bool IsUnix
        {
            get
            {
                // 4 and 6 are the historical Mono values for Unix and macOS; 128 is the
                // pre-.NET-Core PlatformID.Unix. Checked by value rather than by name so
                // an unexpected runtime simply reads as "not Unix" and we do nothing.
                int id = (int)Environment.OSVersion.Platform;
                return id == 4 || id == 6 || id == 128;
            }
        }

        private static MethodInfo Chmod()
        {
            if (_probed) return _chmod;
            _probed = true;
            if (!IsUnix) return null;
            try
            {
                Type syscall = Type.GetType("Mono.Unix.Native.Syscall, Mono.Posix")
                            ?? Type.GetType("Mono.Unix.Native.Syscall, Mono.Posix.NETStandard");
                if (syscall == null) return null;
                // chmod(string path, FilePermissions mode) — the second parameter is an
                // enum, so bind by name and pass the underlying integer boxed to it.
                foreach (MethodInfo m in syscall.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "chmod") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                    {
                        _chmod = m;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("[GeneKerman] SecureFile: no Mono.Posix (" + e.Message + "); "
                          + "credential files keep the default umask.");
            }
            return _chmod;
        }

        /// <summary>Best-effort 0600 on an existing file. Never throws.</summary>
        internal static void RestrictToOwner(string path)
        {
            MethodInfo chmod = Chmod();
            if (chmod == null) return;
            try
            {
                Type modeType = chmod.GetParameters()[1].ParameterType;
                // 0600 = S_IRUSR | S_IWUSR. Written as the octal value it is, because
                // the Mono enum's names are not worth resolving one at a time.
                object mode = Enum.ToObject(modeType, 0x180 /* 0600 */);
                chmod.Invoke(null, new object[] { path, mode });
            }
            catch (Exception e)
            {
                Debug.Log("[GeneKerman] SecureFile: could not restrict " + path + ": " + e.Message);
            }
        }

        /// <summary>Write text and restrict it, in that order. The window between the two
        /// is unavoidable without native open() flags; it is a few microseconds against a
        /// file that previously stayed world-readable for its whole life.</summary>
        internal static void WriteAllTextRestricted(string path, string contents)
        {
            File.WriteAllText(path, contents);
            RestrictToOwner(path);
        }
    }
}
