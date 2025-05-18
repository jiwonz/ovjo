using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using FluentResults;
using UAssetAPI.UnrealTypes;
using UAssetAPI;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class SandboxMetadata
{
    private static string sandboxBaseplateUMapPath = Path.Combine("Sandbox", "EditorResource", "Sandbox", "WorldTemplate", "Baseplate", "Baseplate.umap");

    public required string ProgramPath { get; set; }
    public required string InstallationPath { get; set; }

    public SandboxMetadata() { }

    public string GetDefaultUMapPath()
    {
        return Path.Combine(InstallationPath, sandboxBaseplateUMapPath);
    }
}

internal class Program
{
    const string SANDBOX_APP_NAME = "20687893280c48c787633578d3e0ca2e";
    const string ERROR_PREFIX = "[bold red][[ERR]][/]:";
    const string WARN_PREFIX = "[bold yellow][[WRN]][/]:";
    const string DEFAULT_ROJO_PROJECT_PATH = "default.project.json";
    const EngineVersion OVERDARE_UNREAL_ENGINE_VERSION = EngineVersion.VER_UE5_3;
    const string OVERDARE_UOBJECT_TYPE_LUA_PREFIX = "Lua";

    private static IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error)
    });

    private static Dictionary<string, object> CreateDefaultRojoProject(string name)
    {
        return new()
        {
            ["name"] = name,
            ["tree"] = new Dictionary<string, object>
            {
                ["WorldData"] = new Dictionary<string, object>
                {
                    ["$path"] = "WorldData"
                }
            },
        };
    }

    private static async Task<int> Main(string[] args)
    {
        // Check rojo is ok
        try
        {
            Process process = StartProcess("rojo", "syncback --help");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                stderrConsole.MarkupLine($"{WARN_PREFIX} Failed to run `rojo syncback`(stderr: {process.StandardError.ReadToEnd().EscapeMarkup()}) The `syncback` command does not exist or does not work in the currently installed rojo. Please check the version of rojo.");
            }
        }
        catch (Exception e)
        {
            stderrConsole.MarkupLine($"{WARN_PREFIX} Failed to run rojo(error: {e.Message}) rojo may not be installed. rojo is required to use this program.");
        }

        var syncbackCommand = new Command("syncback", "Performs 'syncback' for the provided project, using the `input` file given");
        {
            var projectOpt = new Option<string>("project", "Path to the project");
            projectOpt.SetDefaultValue("default.project.json");
            var inputOpt = new Option<string>("input", "Path to the input file");
            var rbxlOpt = new Option<string?>("rbxl", "Path to the rbxl file");

            syncbackCommand.AddOption(projectOpt);
            syncbackCommand.AddOption(inputOpt);
            syncbackCommand.AddOption(rbxlOpt);

            syncbackCommand.SetHandler((project, input, rbxl) =>
            {
                Syncback(project, input, rbxl);
            }, projectOpt, inputOpt, rbxlOpt);
        }

        var buildCommand = new Command("build", "Builds rojo project into OVERDARE world");
        {
            var projectOpt = new Option<string>("project", "Path to the project");
            projectOpt.SetDefaultValue("default.project.json");
            buildCommand.AddOption(projectOpt);
            buildCommand.SetHandler((project) =>
            {
                Console.WriteLine($"Hello, {project}!");
            }, projectOpt);
        }

        var devCommand = new Command("dev", "Starts developing rojo project");
        {
            var projectOpt = new Option<string>("project", "Path to the project");
            projectOpt.SetDefaultValue(DEFAULT_ROJO_PROJECT_PATH);
            devCommand.AddOption(projectOpt);
            devCommand.SetHandler((project) =>
            {
                Console.WriteLine($"Hello, {project}!");
            }, projectOpt);
        }

        var initCommand = new Command("init", "Initializes a new rojo project");
        {
            initCommand.SetHandler(() =>
            {
                var sandboxMetadata = ExpectResult(FindSandboxMetadata());
                string umapPath = sandboxMetadata.GetDefaultUMapPath();

                string currentDirectoryPath = Directory.GetCurrentDirectory();
                string directoryName = Path.GetFileName(currentDirectoryPath);
                File.WriteAllText(DEFAULT_ROJO_PROJECT_PATH, JsonConvert.SerializeObject(CreateDefaultRojoProject(directoryName), Formatting.Indented));

                ExpectResult(Syncback(DEFAULT_ROJO_PROJECT_PATH, umapPath, null));
            });
        }

        var rootCommand = new RootCommand("ovjo");
        rootCommand.AddCommand(devCommand);
        rootCommand.AddCommand(initCommand);
        rootCommand.AddCommand(syncbackCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static T? CreateInstance<T>(string className) where T : class
    {
        string fullClassName = $"RobloxFiles.{className}";

        Type? foundType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(asm =>
            {
                try { return asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
                catch { return Enumerable.Empty<Type>(); }
            })
            .FirstOrDefault(t => t?.FullName == fullClassName);

        if (foundType != null && typeof(T).IsAssignableFrom(foundType))
        {
            return Activator.CreateInstance(foundType) as T;
        }

        return null;
    }

    private static T ExpectResult<T>(Result<T> result)
    {
        if (result.IsFailed)
        {
            stderrConsole.MarkupLine($"{ERROR_PREFIX} {result.Errors[0].ToString().EscapeMarkup()}");
            Environment.Exit(1);
            throw new InvalidOperationException("unreachable");
        }
        return result.Value;
    }

    private static void ExpectResult(Result result)
    {
        if (result.IsFailed)
        {
            stderrConsole.MarkupLine($"{ERROR_PREFIX} {result.Errors[0].ToString().EscapeMarkup()}");
            Environment.Exit(1);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static Result Syncback(string rojoProjectPath, string umapPath, string? rbxlPath)
    {
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail($"{ERROR_PREFIX} Failed to parse rojo project file.");
        }
        var worldDataPath = GetWorldDataPath(rojoProject);
        if (worldDataPath.IsFailed)
        {
            return Result.Fail(worldDataPath.Errors[0]);
        }

        // Initialize data files in empty BinaryStringValue .rbxms
        string[] dataFiles = { "Map.rbxm", "PlainFiles", "JsonFiles" };
        foreach (string dataPath in dataFiles)
        {
            string rbxmPath = Path.Combine(worldDataPath.Value, dataPath);
            if (!File.Exists(rbxmPath))
            {
                RobloxFiles.BinaryRobloxFile modelFile = new();
                RobloxFiles.BinaryStringValue binaryStringValue = new()
                {
                    Parent = modelFile,
                };
                string? dir = Path.GetDirectoryName(rbxmPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                modelFile.Save(rbxmPath);
            }
        }

        UAsset asset = new(umapPath, OVERDARE_UNREAL_ENGINE_VERSION);
        List<RobloxFiles.Instance> instances = new();
        foreach (UAssetAPI.ExportTypes.Export export in asset.Exports)
        {
            if (!(export is UAssetAPI.ExportTypes.NormalExport normalExport)) continue;
            var parentProperty = normalExport["Parent"];
            if (parentProperty == null) continue;
            if (!(parentProperty is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData parentObject)) continue;
            var classTypeName = export.GetExportClassType();
            if (classTypeName == null) continue;
            var classTypeNameString = classTypeName.Value;
            if (classTypeNameString == null) continue;
            string classTypeNameWithoutLuaPrefix = Regex.Replace(classTypeNameString.Value, $"^{Regex.Escape(OVERDARE_UOBJECT_TYPE_LUA_PREFIX)}", "");
            Console.WriteLine($"Class: {classTypeNameWithoutLuaPrefix} Raw: {classTypeNameString} FName.String(Name): {normalExport.ObjectName.Value} FName.Number: {normalExport.ObjectName.Number}");
        }

        return Result.Ok();
    }

    private static Result Build(string rojoProjectPath, string umapPath)
    {
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail("{ERROR_PREFIX} Failed to parse rojo project file.");
        }
        var worldDataPath = GetWorldDataPath(rojoProject);
        if (worldDataPath.IsFailed)
        {
            return Result.Fail(worldDataPath.Errors[0]);
        }

        return Result.Ok();
    }

    private static Result<string> GetWorldDataPath(JObject rojoProject)
    {
        var path = rojoProject["tree"]?["WorldData"]?["$path"]?.ToString();
        if (path is string validPath)
        {
            return validPath;
        }
        return Result.Fail("Couldn't find `tree.WorldData[\"$path\"]` in project.json. This is required in ovjo");
    }

    private static Process StartProcess(string command, string args = "")
    {
        Process p = new()
        {
            StartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        p.Start();

        return p;
    }

    private static Result<SandboxMetadata> FindSandboxMetadata()
    {
        string programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string manifestsPath = Path.Combine(programDataPath, "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsPath))
        {
            return Result.Fail("Manifest folder does not exist.");
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
                return Result.Fail("Install location not found in manifest.");
            }
            var launchExecutable = manifest["LaunchExecutable"]?.ToString();
            if (launchExecutable == null)
            {
                return Result.Fail("Launch executable not found in manifest.");
            }
            string programPath = Path.Combine(installLocation, launchExecutable);
            if (!File.Exists(programPath))
            {
                return Result.Fail($"{ERROR_PREFIX} Launch executable not found.");
            }

            SandboxMetadata metadata = new()
            {
                ProgramPath = programPath,
                InstallationPath = installLocation,
            };
            return Result.Ok(metadata);
        }

        return Result.Fail("Couldn't find Sandbox");
    }
}