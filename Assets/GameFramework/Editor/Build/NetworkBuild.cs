using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RPGDemo.GameFramework.Editor
{
    public static class NetworkBuild
    {
        private const string ServerOutput = "Builds/Server/RPGDemoServer.exe";
        private const string ClientOutput = "Builds/Client/RPGDemoClient.exe";

        [MenuItem("RPG Demo/Build/Dedicated Server (Windows)")]
        public static void BuildDedicatedServer()
        {
            Build(ServerOutput, StandaloneBuildSubtarget.Server);
        }

        [MenuItem("RPG Demo/Build/Client (Windows)")]
        public static void BuildClient()
        {
            Build(ClientOutput, StandaloneBuildSubtarget.Player);
        }

        [MenuItem("RPG Demo/Build/Server + Client (Windows)")]
        public static void BuildServerAndClient()
        {
            BuildDedicatedServer();
            BuildClient();
        }

        private static void Build(string relativeOutputPath, StandaloneBuildSubtarget subtarget)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string outputPath = Path.Combine(projectRoot, relativeOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                throw new InvalidOperationException("No enabled scenes exist in EditorBuildSettings.");
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)subtarget,
                options = BuildOptions.Development
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"{subtarget} build failed: {report.summary.totalErrors} errors, "
                    + $"{report.summary.totalWarnings} warnings.");
            }

            Debug.Log(
                $"[Build] {subtarget} succeeded: {outputPath} "
                + $"({report.summary.totalSize / (1024f * 1024f):F1} MiB).");
        }
    }
}
