using System;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Server;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Bootstrap
{
    [DefaultExecutionOrder(-10000)]
    public sealed class NetworkBootstrap : MonoBehaviour
    {
        private static NetworkBootstrap instance;

        [SerializeField] private NetworkProcessMode mode;
        [SerializeField] private string address = "127.0.0.1";
        [SerializeField] private int port = GameNetDriver.DefaultPort;
        [SerializeField] private string displayName = "Player";
        [SerializeField] private NetworkPrefabRegistry prefabRegistry;
        [SerializeField] private int defaultPlayerPrefabId;
        [SerializeField] private int maxPlayers = 16;
        [SerializeField] private bool startOnAwake = true;

        private GameNetDriver netDriver;
        private ServerGameMode serverGameMode;

        public static NetworkBootstrap Instance => instance;
        public GameNetDriver NetDriver => netDriver;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateFromCommandLine()
        {
            NetworkLaunchOptions options;
            try
            {
                options = NetworkLaunchOptions.Parse(Environment.GetCommandLineArgs());
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Net][Bootstrap] Invalid launch arguments: {exception.Message}");
                return;
            }

            if (options.Mode == NetworkProcessMode.None)
            {
                return;
            }

            NetworkBootstrap bootstrap = instance;
            if (bootstrap == null)
            {
                GameObject bootstrapObject = new GameObject("[NetworkBootstrap]");
                bootstrap = bootstrapObject.AddComponent<NetworkBootstrap>();
                DontDestroyOnLoad(bootstrapObject);
            }

            bootstrap.mode = options.Mode;
            bootstrap.address = options.Address;
            bootstrap.port = options.Port;
            bootstrap.displayName = options.DisplayName;
            if (options.DefaultPlayerPrefabId.HasValue)
            {
                bootstrap.defaultPlayerPrefabId = options.DefaultPlayerPrefabId.Value;
            }

            if (options.MaxPlayers.HasValue)
            {
                bootstrap.maxPlayers = options.MaxPlayers.Value;
            }

            bootstrap.StartNetwork();
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            bool commandLineWillStart = false;
            try
            {
                commandLineWillStart = NetworkLaunchOptions.Parse(Environment.GetCommandLineArgs()).Mode
                    != NetworkProcessMode.None;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Net][Bootstrap] Invalid launch arguments: {exception.Message}");
                commandLineWillStart = true;
            }

            if (startOnAwake && mode != NetworkProcessMode.None && !commandLineWillStart)
            {
                StartNetwork();
            }
        }

        private void Update()
        {
            netDriver?.Tick(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            serverGameMode?.Dispose();
            serverGameMode = null;
            netDriver?.Dispose();
            netDriver = null;

            if (instance == this)
            {
                instance = null;
            }
        }

        public void StartNetwork()
        {
            if (netDriver != null && netDriver.IsRunning)
            {
                return;
            }

            serverGameMode?.Dispose();
            serverGameMode = null;
            netDriver?.Dispose();
            NetworkPrefabRegistry resolvedPrefabRegistry = prefabRegistry != null
                ? prefabRegistry
                : Resources.Load<NetworkPrefabRegistry>(NetworkPrefabRegistry.DefaultResourcesPath);
            netDriver = new GameNetDriver(prefabRegistry: resolvedPrefabRegistry);

            if (resolvedPrefabRegistry == null)
            {
                Debug.LogWarning(
                    "[Net][Bootstrap] No NetworkPrefabRegistry is assigned. Connection can start, "
                    + "but ActorChannelOpen will fail until a registry is provided.");
            }

            try
            {
                if (mode == NetworkProcessMode.DedicatedServer)
                {
                    netDriver.StartDedicatedServer((ushort)port);
                    serverGameMode = new ServerGameMode(
                        netDriver,
                        (ushort)defaultPlayerPrefabId,
                        maxPlayers);
                }
                else if (mode == NetworkProcessMode.Client)
                {
                    netDriver.StartClient(address, (ushort)port, displayName);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Net][Bootstrap] Startup failed: {exception}");
                serverGameMode?.Dispose();
                serverGameMode = null;
                netDriver.Dispose();
                netDriver = null;
            }
        }

        public void StopNetwork()
        {
            serverGameMode?.Dispose();
            serverGameMode = null;
            netDriver?.Stop();
        }

        private void OnValidate()
        {
            port = Mathf.Clamp(port, 1, ushort.MaxValue);
            defaultPlayerPrefabId = Mathf.Clamp(defaultPlayerPrefabId, 0, ushort.MaxValue);
            maxPlayers = Mathf.Max(1, maxPlayers);
        }
    }
}
