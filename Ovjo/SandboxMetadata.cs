﻿using FluentResults;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace Ovjo
{
    public class SandboxMetadata
    {
        public const string SandboxAppName = "20687893280c48c787633578d3e0ca2e";

        private static string sandboxBaseplateUMapPath = Path.Combine("Sandbox", "EditorResource", "Sandbox", "WorldTemplate", "Baseplate", "Baseplate.umap");

        public required string ProgramPath { get; set; }
        public required string InstallationPath { get; set; }

        public string GetDefaultUMapPath()
        {
            return Path.Combine(InstallationPath, sandboxBaseplateUMapPath);
        }

        public static Result<SandboxMetadata> TryFindViaEpicGamesLauncher()
        {
            string programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string manifestsPath = Path.Combine(programDataPath, "Epic", "EpicGamesLauncher", "Data", "Manifests");

            if (!Directory.Exists(manifestsPath))
            {
                return Result.Fail(_("Manifest folder does not exist."));
            }

            string[] itemFiles = Directory.GetFiles(manifestsPath, "*.item");

            foreach (string file in itemFiles)
            {
                string content = File.ReadAllText(file);
                var manifest = JsonConvert.DeserializeObject<JObject>(content);
                if (manifest == null || manifest["AppName"]?.ToString() != SANDBOX_APP_NAME)
                {
                    continue;
                }
                var installLocation = manifest["InstallLocation"]?.ToString();
                if (installLocation == null)
                {
                    return Result.Fail(_("Install location not found in manifest."));
                }
                var launchExecutable = manifest["LaunchExecutable"]?.ToString();
                if (launchExecutable == null)
                {
                    return Result.Fail(_("Launch executable not found in manifest."));
                }
                string programPath = Path.Combine(installLocation, launchExecutable);
                if (!File.Exists(programPath))
                {
                    return Result.Fail(_("Launch executable not found."));
                }

                SandboxMetadata metadata = new()
                {
                    ProgramPath = programPath,
                    InstallationPath = installLocation,
                };
                return Result.Ok(metadata);
            }

            return Result.Fail(_("Couldn't find Sandbox. Check `OVERDARE Studio` is installed in your Epic Games Launcher library."));
        }
    }
}
