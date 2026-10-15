namespace GeneKerman
{
    /// <summary>
    /// Which antenna model this install uses, resolved once per session.
    ///
    /// Stock rates an antenna with a single number, <c>ModuleDataTransmitter.antennaPower</c>,
    /// and that is what a relay-strength requirement is written in. RealAntennas does not
    /// use that model at all: its patches delete both <c>antennaType</c> and
    /// <c>antennaPower</c> from every part and describe an antenna as transmit power in
    /// dBm plus gain and frequency. There is no conversion between the two — an RA-2's
    /// "2M" and an RO dish's dBm are different quantities, not different units of one.
    ///
    /// So the model travels with the requirement, and strength is only ever compared
    /// between installs that share one. A contract written on stock still *applies* on
    /// RealAntennas — the network, the spacing and the relay flag all mean the same —
    /// it is only the strength floor that cannot be judged there, and saying so is
    /// better than either silently passing it or refusing every satellite.
    ///
    /// Detected by assembly, the same way LifeSupportRegistry finds which life-support
    /// mod is present, rather than by looking for a part or a config: the assembly is
    /// what decides which model is actually running.
    /// </summary>
    public static class AntennaModel
    {
        /// <summary>The stock model: antennaType + antennaPower on ModuleDataTransmitter.</summary>
        public const string Stock = "stock";

        /// <summary>RealAntennas: dBm transmit power, gain and frequency.</summary>
        public const string RealAntennas = "realantennas";

        /// <summary>RemoteTech: omni and dish ranges, its own network.</summary>
        public const string RemoteTech = "remotetech";

        /// <summary>One row per antenna mod that replaces the stock model. A table rather
        /// than a chain of ifs so the next such mod is an entry here and nothing else:
        /// the assembly identifies it, the module name is what its antennas actually are,
        /// and nothing in this file references either type.
        ///
        /// RealAntennas is verified against the shipped assembly: ModuleRealAntenna
        /// derives from ModuleDataTransmitter, its patches delete antennaType and
        /// antennaPower from every part, and the DLL assigns neither. RemoteTech is
        /// listed by its published module name and is NOT verified against an install
        /// here — which is safe in the direction that matters, because an unmatched
        /// module name falls back to the stock relay flag and so behaves exactly as it
        /// did before this table existed.</summary>
        private static readonly string[,] Models =
        {
            { "RealAntennas", RealAntennas, "ModuleRealAntenna" },
            { "RemoteTech",   RemoteTech,   "ModuleRTAntenna" },
        };

        private static string cached;
        private static string cachedModule;

        /// <summary>This install's antenna model. Cached: an assembly cannot appear or
        /// vanish mid-session, and this is read once per vessel in a whole-SOI scan.</summary>
        public static string Current
        {
            get
            {
                if (cached == null) Resolve();
                return cached;
            }
        }

        /// <summary>The part module this install's antennas actually are, or null on
        /// stock. Empty string is never returned, so a caller can null-test it.</summary>
        private static string RelayModule
        {
            get
            {
                if (cached == null) Resolve();
                return cachedModule;
            }
        }

        private static void Resolve()
        {
            for (int i = 0; i < Models.GetLength(0); i++)
            {
                if (!LsReflect.HasAssembly(Models[i, 0])) continue;
                cached = Models[i, 1];
                cachedModule = Models[i, 2];
                return;
            }
            cached = Stock;
            cachedModule = null;
        }

        /// <summary>Whether an antenna's strength is a number this install can report.
        /// False on RealAntennas, where the field a strength floor is written in does
        /// not exist.</summary>
        public static bool ExpressesPower => Current == Stock;

        /// <summary>Whether this part module is something that can relay.
        ///
        /// The reason this is not simply <c>antennaType == RELAY</c> is worth stating,
        /// because the stock test silently returns false for every antenna on an RO
        /// install and a network check that fails closed then refuses every submission.
        /// RealAntennas deletes <c>antennaType</c> from every part config and never
        /// assigns it in code, so the field holds ModuleDataTransmitter's constructor
        /// default, which is DIRECT. Every RO antenna therefore reads as a direct
        /// antenna no matter what it is. (The same deletion leaves <c>antennaPower</c>
        /// at its 500k default, which is why strength is reported as "unavailable"
        /// rather than read off the field: the number is there but it is furniture.)
        ///
        /// On RealAntennas the question has a different answer anyway. Relaying is not a
        /// property of a part there: it is a full RF link model in which any antenna can
        /// carry someone else's traffic, so the test is whether the part has an RA
        /// antenna at all. A contract's real terms on such an install are its count, its
        /// body and its spacing, which is exactly what the submit panel says.
        ///
        /// Matched on the type name up the inheritance chain rather than against a
        /// referenced type, because nothing here links RealAntennas — the same
        /// defensive-reflection rule the life-support adapters follow.</summary>
        public static bool CountsAsRelay(PartModule module)
        {
            if (module == null) return false;

            // A stock relay flag is honoured whichever model is running: a part that
            // kept its stock transmitter is making a claim this install can read, and
            // refusing it would be stricter than either model requires.
            var mdt = module as ModuleDataTransmitter;
            if (mdt != null && mdt.antennaType == AntennaType.RELAY) return true;

            string want = RelayModule;
            if (want == null) return false;

            for (var t = module.GetType(); t != null; t = t.BaseType)
                if (t.Name == want) return true;
            return false;
        }
    }
}
