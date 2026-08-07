using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RPGDemo.GameFramework.Networking;
using RPGDemo.GameFramework.Networking.Bootstrap;
using RPGDemo.GameFramework.Networking.Diagnostics;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Replication;
using RPGDemo.GameFramework.Networking.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace RPGDemo.GameFramework.Editor
{
    public static class NetworkDemoSetup
    {
        private const ushort PlayerPrefabId = 1;
        private const string DemoRoot = "Assets/GameFramework/Demo/Networking";
        private const string PrefabFolder = DemoRoot + "/Prefabs";
        private const string PlayerPrefabPath = PrefabFolder + "/NetworkPlayer.prefab";
        private const string RegistryPath = "Assets/Resources/NetworkPrefabRegistry.asset";
        private const string ScenePath = "Assets/Scenes/NetworkTestScene.unity";
        private const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";

        [MenuItem("RPG Demo/Networking/Create or Refresh Network Test Content")]
        public static void CreateOrRefresh()
        {
            if (!Application.isBatchMode
                && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            bool overwritesExisting = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) != null
                || AssetDatabase.LoadAssetAtPath<NetworkPrefabRegistry>(RegistryPath) != null
                || AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            if (!Application.isBatchMode
                && overwritesExisting
                && !EditorUtility.DisplayDialog(
                    "Refresh Network Test Content",
                    "This recreates the generated NetworkPlayer prefab, prefab registry entries, "
                    + "and NetworkTestScene. Continue?",
                    "Recreate",
                    "Cancel"))
            {
                return;
            }

            EnsureFolder(DemoRoot);
            EnsureFolder(PrefabFolder);
            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/Scenes");

            NetworkIdentity playerPrefab = CreatePlayerPrefab();
            NetworkPrefabRegistry registry = CreateOrUpdateRegistry(playerPrefab);
            CreateNetworkScene(registry);
            PutNetworkSceneFirstInBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    "Network Test Content Ready",
                    "Created NetworkPlayer PrefabId 1, NetworkPrefabRegistry, and NetworkTestScene. "
                    + "The network scene is now the first enabled build scene.",
                    "OK");
            }
        }

        private static NetworkIdentity CreatePlayerPrefab()
        {
            GameObject temporaryPlayer = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            temporaryPlayer.name = "NetworkPlayer";

            CapsuleCollider primitiveCollider = temporaryPlayer.GetComponent<CapsuleCollider>();
            if (primitiveCollider != null)
            {
                Object.DestroyImmediate(primitiveCollider);
            }

            temporaryPlayer.AddComponent<Character>();
            NetworkIdentity identity = temporaryPlayer.AddComponent<NetworkIdentity>();
            temporaryPlayer.AddComponent<ReplicatedHealth>();
            temporaryPlayer.AddComponent<CharacterNetworkMovement>();

            SerializedObject serializedIdentity = new SerializedObject(identity);
            serializedIdentity.FindProperty("prefabId").intValue = PlayerPrefabId;
            serializedIdentity.FindProperty("destroyOnDespawn").boolValue = true;
            serializedIdentity.ApplyModifiedPropertiesWithoutUndo();

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(temporaryPlayer, PlayerPrefabPath);
            Object.DestroyImmediate(temporaryPlayer);
            if (savedPrefab == null)
            {
                throw new InvalidOperationException($"Could not create {PlayerPrefabPath}.");
            }

            return savedPrefab.GetComponent<NetworkIdentity>();
        }

        private static NetworkPrefabRegistry CreateOrUpdateRegistry(NetworkIdentity playerPrefab)
        {
            NetworkPrefabRegistry registry
                = AssetDatabase.LoadAssetAtPath<NetworkPrefabRegistry>(RegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }

            SerializedObject serializedRegistry = new SerializedObject(registry);
            SerializedProperty entries = serializedRegistry.FindProperty("entries");
            entries.arraySize = 1;
            SerializedProperty entry = entries.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("prefabId").intValue = PlayerPrefabId;
            entry.FindPropertyRelative("prefab").objectReferenceValue = playerPrefab;
            serializedRegistry.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(registry);
            return registry;
        }

        private static void CreateNetworkScene(NetworkPrefabRegistry registry)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateGround();
            CreateLighting();
            CreateCamera();
            CreatePlayerStart("PlayerStart_01", 0, new Vector3(-2f, 1f, 0f));
            CreatePlayerStart("PlayerStart_02", 1, new Vector3(2f, 1f, 0f));
            CreateLocalPlayerController();
            CreateNetworkBootstrap(registry);

            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        private static void CreateGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
        }

        private static void CreateLighting()
        {
            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static void CreateCamera()
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = new Vector3(0f, 8f, -12f);
            cameraObject.transform.rotation = Quaternion.Euler(28f, 0f, 0f);
        }

        private static void CreatePlayerStart(string name, int spawnOrder, Vector3 position)
        {
            GameObject startObject = new GameObject(name);
            startObject.transform.position = position;
            NetworkPlayerStart playerStart = startObject.AddComponent<NetworkPlayerStart>();
            SerializedObject serializedStart = new SerializedObject(playerStart);
            serializedStart.FindProperty("spawnOrder").intValue = spawnOrder;
            serializedStart.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateLocalPlayerController()
        {
            GameObject controllerObject = new GameObject("LocalPlayerController");
            PlayerController controller = controllerObject.AddComponent<PlayerController>();
            PlayerInput playerInput = controller.GetComponent<PlayerInput>();
            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            if (actions == null)
            {
                Debug.LogWarning($"Network demo could not find {InputActionsPath}.");
                return;
            }

            SerializedObject serializedInput = new SerializedObject(playerInput);
            serializedInput.FindProperty("m_Actions").objectReferenceValue = actions;
            serializedInput.FindProperty("m_DefaultActionMap").stringValue = "Player";
            serializedInput.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateNetworkBootstrap(NetworkPrefabRegistry registry)
        {
            GameObject bootstrapObject = new GameObject("NetworkBootstrap");
            NetworkBootstrap bootstrap = bootstrapObject.AddComponent<NetworkBootstrap>();
            bootstrapObject.AddComponent<NetworkDebugHud>();
            SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
            serializedBootstrap.FindProperty("mode").enumValueIndex = (int)NetworkProcessMode.None;
            serializedBootstrap.FindProperty("port").intValue = GameNetDriver.DefaultPort;
            serializedBootstrap.FindProperty("prefabRegistry").objectReferenceValue = registry;
            serializedBootstrap.FindProperty("defaultPlayerPrefabId").intValue = PlayerPrefabId;
            serializedBootstrap.FindProperty("maxPlayers").intValue = 16;
            serializedBootstrap.FindProperty("startOnAwake").boolValue = true;
            serializedBootstrap.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void PutNetworkSceneFirstInBuildSettings()
        {
            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes
                .Where(scene => !string.Equals(scene.path, ScenePath, System.StringComparison.OrdinalIgnoreCase))
                .ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string folderName = Path.GetFileName(assetPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName))
            {
                throw new InvalidOperationException($"Invalid asset folder path '{assetPath}'.");
            }

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
