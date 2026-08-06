using System;

namespace RPGDemo.GameFramework.Networking.Bootstrap
{
    public readonly struct NetworkLaunchOptions
    {
        public NetworkLaunchOptions(
            NetworkProcessMode mode,
            string address,
            ushort port,
            string displayName,
            ushort? defaultPlayerPrefabId,
            int? maxPlayers)
        {
            Mode = mode;
            Address = address;
            Port = port;
            DisplayName = displayName;
            DefaultPlayerPrefabId = defaultPlayerPrefabId;
            MaxPlayers = maxPlayers;
        }

        public NetworkProcessMode Mode { get; }
        public string Address { get; }
        public ushort Port { get; }
        public string DisplayName { get; }
        public ushort? DefaultPlayerPrefabId { get; }
        public int? MaxPlayers { get; }

        public static NetworkLaunchOptions Parse(string[] arguments)
        {
            NetworkProcessMode mode = GetCompiledDefaultMode();
            string address = "127.0.0.1";
            ushort port = GameNetDriver.DefaultPort;
            string displayName = "Player";
            ushort? defaultPlayerPrefabId = null;
            int? maxPlayers = null;

            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];

                if (EqualsOption(argument, "-server") || EqualsOption(argument, "-dedicatedServer"))
                {
                    mode = NetworkProcessMode.DedicatedServer;
                    continue;
                }

                if (EqualsOption(argument, "-client"))
                {
                    mode = NetworkProcessMode.Client;
                    continue;
                }

                if (EqualsOption(argument, "-port") && TryGetNext(arguments, ref i, out string portValue))
                {
                    if (!ushort.TryParse(portValue, out port) || port == 0)
                    {
                        throw new ArgumentException($"Invalid -port value '{portValue}'.");
                    }

                    continue;
                }

                if (EqualsOption(argument, "-name") && TryGetNext(arguments, ref i, out string nameValue))
                {
                    displayName = nameValue;
                    continue;
                }

                if (EqualsOption(argument, "-playerPrefab")
                    && TryGetNext(arguments, ref i, out string prefabValue))
                {
                    if (!ushort.TryParse(prefabValue, out ushort parsedPrefabId) || parsedPrefabId == 0)
                    {
                        throw new ArgumentException($"Invalid -playerPrefab value '{prefabValue}'.");
                    }

                    defaultPlayerPrefabId = parsedPrefabId;
                    continue;
                }

                if (EqualsOption(argument, "-maxPlayers")
                    && TryGetNext(arguments, ref i, out string maxPlayersValue))
                {
                    if (!int.TryParse(maxPlayersValue, out int parsedMaxPlayers) || parsedMaxPlayers <= 0)
                    {
                        throw new ArgumentException($"Invalid -maxPlayers value '{maxPlayersValue}'.");
                    }

                    maxPlayers = parsedMaxPlayers;
                    continue;
                }

                if (EqualsOption(argument, "-connect") && TryGetNext(arguments, ref i, out string endpointValue))
                {
                    mode = NetworkProcessMode.Client;
                    ParseEndpoint(endpointValue, ref address, ref port);
                }
            }

            return new NetworkLaunchOptions(
                mode,
                address,
                port,
                displayName,
                defaultPlayerPrefabId,
                maxPlayers);
        }

        private static void ParseEndpoint(string value, ref string address, ref ushort port)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("-connect requires an IP address.");
            }

            if (value[0] == '[')
            {
                int closingBracket = value.IndexOf(']');
                if (closingBracket < 0)
                {
                    throw new ArgumentException($"Invalid bracketed IPv6 endpoint '{value}'.");
                }

                address = value.Substring(1, closingBracket - 1);
                if (closingBracket + 1 < value.Length)
                {
                    if (value[closingBracket + 1] != ':'
                        || !TryParsePort(value.Substring(closingBracket + 2), out port))
                    {
                        throw new ArgumentException($"Invalid endpoint port in '{value}'.");
                    }
                }

                return;
            }

            int firstSeparator = value.IndexOf(':');
            int separator = value.LastIndexOf(':');
            if (separator <= 0 || firstSeparator != separator)
            {
                address = value;
                return;
            }

            if (separator == value.Length - 1
                || !TryParsePort(value.Substring(separator + 1), out ushort parsedPort))
            {
                throw new ArgumentException($"Invalid endpoint port in '{value}'.");
            }

            address = value.Substring(0, separator).Trim('[', ']');
            port = parsedPort;
        }

        private static bool TryParsePort(string value, out ushort port)
        {
            return ushort.TryParse(value, out port) && port > 0;
        }

        private static bool EqualsOption(string value, string option)
        {
            return string.Equals(value, option, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetNext(string[] arguments, ref int index, out string value)
        {
            value = null;
            if (index + 1 >= arguments.Length)
            {
                return false;
            }

            value = arguments[++index];
            return true;
        }

        private static NetworkProcessMode GetCompiledDefaultMode()
        {
#if UNITY_SERVER
            return NetworkProcessMode.DedicatedServer;
#else
            return NetworkProcessMode.None;
#endif
        }
    }
}
