using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using FluentResults;
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

class Defer : IDisposable
{
    private readonly Action _disposal;

    public Defer(Action disposal)
    {
        _disposal = disposal;
    }

    void IDisposable.Dispose()
    {
        _disposal();
    }
}

internal class Program
{
    const string SANDBOX_APP_NAME = "20687893280c48c787633578d3e0ca2e";
    const string ERROR_PREFIX = "[bold red][[ERR]][/]";
    const string WARN_PREFIX = "[bold yellow][[WRN]][/]";
    const string DEFAULT_ROJO_PROJECT_PATH = "default.project.json";
    const UAssetAPI.UnrealTypes.EngineVersion OVERDARE_UNREAL_ENGINE_VERSION = UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3;
    const string OVERDARE_UOBJECT_TYPE_LUA_PREFIX = "Lua";
    const string WORLD_DATA_NAME = "WorldData";
    const string WORLD_DATA_MAP_NAME = "Map";
    const string WORLD_DATA_PLAIN_FILES_NAME = "PlainFiles";
    const string WORLD_DATA_JSON_FILES_NAME = "JsonFiles";

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
                ["$className"] = "DataModel",
                [WORLD_DATA_NAME] = new Dictionary<string, object>
                {
                    ["$path"] = WORLD_DATA_NAME
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
            var projectArg = new Argument<string>("project", "Path to the project");
            projectArg.SetDefaultValue(DEFAULT_ROJO_PROJECT_PATH);
            var inputOpt = new Option<string>(["--input", "-i"], "Path to the input file");
            var rbxlOpt = new Option<string?>("--rbxl", "Path to the rbxl file");

            syncbackCommand.AddArgument(projectArg);
            syncbackCommand.AddOption(inputOpt);
            syncbackCommand.AddOption(rbxlOpt);

            syncbackCommand.SetHandler((project, input, rbxl) =>
            {
                Syncback(project, input, rbxl);
            }, projectArg, inputOpt, rbxlOpt);
        }

        var buildCommand = new Command("build", "Builds rojo project into OVERDARE world");
        {
            var projectArg = new Argument<string>("project", "Path to the project");
            projectArg.SetDefaultValue(DEFAULT_ROJO_PROJECT_PATH);
            var outputOpt = new Option<string>(["--output", "-o"], "Path to the output file");

            buildCommand.AddArgument(projectArg);
            buildCommand.AddOption(outputOpt);

            buildCommand.SetHandler((project, output) =>
            {
                Console.WriteLine($"Hello, {project}!");
            }, projectArg, outputOpt);
        }

        var devCommand = new Command("dev", "Starts developing rojo project");
        {
            var projectArg = new Argument<string>("project", "Path to the project");
            projectArg.SetDefaultValue(DEFAULT_ROJO_PROJECT_PATH);
            var outputOpt = new Option<string?>(["--output", "-o"], "Path to the output file");

            devCommand.AddArgument(projectArg);
            devCommand.AddOption(outputOpt);

            devCommand.SetHandler((project, output) =>
            {
                Console.WriteLine($"Hello, {project}!");
            }, projectArg, outputOpt);
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

    private static T? TryCreateInstance<T>(string className) where T : class
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
        string? ovdrWorldPath = Path.GetDirectoryName(umapPath);
        if (ovdrWorldPath == null)
        {
            return Result.Fail($"{ERROR_PREFIX} Failed to get world path from umap file. Couldn't find `umap path`'s parent directory");
        }

        // Initialize data files in empty BinaryStringValue .rbxms for the syncback
        {
            string[] worldDataFiles =
            [
                Path.ChangeExtension(WORLD_DATA_MAP_NAME, "rbxm"),
                Path.ChangeExtension(WORLD_DATA_PLAIN_FILES_NAME, "rbxm"),
                Path.ChangeExtension(WORLD_DATA_JSON_FILES_NAME, "rbxm")
            ];
            foreach (string dataPath in worldDataFiles)
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
        }

        // Read umap file into memory
        UAsset asset = new();
        asset.FilePath = umapPath;
        asset.Mappings = null;
        asset.CustomSerializationFlags = CustomSerializationFlags.None;
        asset.SetEngineVersion(OVERDARE_UNREAL_ENGINE_VERSION);
        var stream = asset.PathToStream(umapPath);
        if (stream == null)
        {
            return Result.Fail($"{ERROR_PREFIX} Failed to read umap file.");
        }
        {
            AssetBinaryReader reader = new(stream, asset);
            asset.Read(reader);
        }

        // Create Roblox place file(aka. DataModel instance) with WorldData loaded (and loaded WorldData is going to be used for syncback too)
        RobloxFiles.BinaryRobloxFile robloxDataModel = new();
        {
            RobloxFiles.Folder folder = new()
            {
                Name = WORLD_DATA_NAME,
                Parent = robloxDataModel,
            };
            RobloxFiles.BinaryStringValue mapData = new()
            {
                Name = WORLD_DATA_MAP_NAME,
                Value = stream.ToArray(),
                Parent = folder,
            };

            static byte[] FilesToMessagePackBinaryString(string[] files)
            {
                Dictionary<string, string> filesData = new();
                foreach (string p in files)
                {
                    filesData.Add(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p));
                }
                return MessagePack.MessagePackSerializer.Serialize(filesData);
            }

            RobloxFiles.BinaryStringValue jsonFilesData = new()
            {
                Name = WORLD_DATA_JSON_FILES_NAME,
                Value = FilesToMessagePackBinaryString(Directory.GetFiles(ovdrWorldPath, "*.json")),
                Parent = folder,
            };
            string[] plainFiles = Directory.GetFiles(ovdrWorldPath)
                     .Where(file =>
                        !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        !file.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                     .ToArray();
            RobloxFiles.BinaryStringValue plainFilesData = new()
            {
                Name = WORLD_DATA_PLAIN_FILES_NAME,
                Value = FilesToMessagePackBinaryString(plainFiles),
                Parent = folder,
            };
        }

        Dictionary<int, (RobloxFiles.Instance Instance, int? Parent)> instances = new(); // Key integer is the PackageIndex number of the export in the asset
        for (int packageIndex = 0; packageIndex < asset.Exports.Count; packageIndex++)
        {
            // Getting the current export and NormalExport
            UAssetAPI.ExportTypes.Export export = asset.Exports[packageIndex];
            if (!(export is UAssetAPI.ExportTypes.NormalExport normalExport)) continue;

            // Skip if it's invisible in level browser
            var bVisibleInLevelBrowser = normalExport["bVisibleInLevelBrowser"];
            if (bVisibleInLevelBrowser is UAssetAPI.PropertyTypes.Objects.BoolPropertyData bVisibleInLevelBrowserBool)
            {
                if (bVisibleInLevelBrowserBool.Value == false) continue;
            }

            // Getting ClassType(ex. LuaPart, LuaModuleScript) of the current export
            var classTypeName = export.GetExportClassType();
            if (classTypeName == null) continue;
            var classTypeNameString = classTypeName.Value;
            if (classTypeNameString == null) continue;

            // Converting OVERDARE's Lua class name to Roblox class name
            string classTypeNameWithoutLuaPrefix = Regex.Replace(classTypeNameString.Value, $"^{Regex.Escape(OVERDARE_UOBJECT_TYPE_LUA_PREFIX)}", "");
            Console.WriteLine($"Class: {classTypeNameWithoutLuaPrefix} Raw: {classTypeNameString} FName: {normalExport.ObjectName} PackageIndex: {packageIndex}");
            bool isDataModel = classTypeNameWithoutLuaPrefix == "DataModel";
            var instance = isDataModel ? robloxDataModel : TryCreateInstance<RobloxFiles.Instance>(classTypeNameWithoutLuaPrefix);

            // Skips DataModel with no parent set
            if (instance != null && isDataModel)
            {
                instances.Add(packageIndex, (Instance: instance, Parent: null));
                continue;
            }

            // Getting parent
            var parentProperty = normalExport["Parent"];
            if (!(parentProperty is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData parentObject) || !parentObject.Value.IsExport() || parentObject.Value.IsNull()) continue;
            int parentIndex = parentObject.Value.Index - 1;
            bool isUnknownInstance = false;
            if (instance == null)
            {
                instance = new RobloxFiles.Model();
                isUnknownInstance = true;
            }

            // Setting normal Roblox Instance up
            var nameProperty = normalExport["Name"];
            if (nameProperty is UAssetAPI.PropertyTypes.Objects.StrPropertyData namePropertyString)
            {
                Console.WriteLine($"Instance Name: {namePropertyString.Value.Value}");
                instance.Name = namePropertyString.Value.Value;
            }
            else if (isUnknownInstance)
            {
                instance.Name = classTypeNameWithoutLuaPrefix;
            }
            instance.SetAttribute("ObjectName", export.ObjectName.ToString());
            instances.Add(packageIndex, (Instance: instance, Parent: parentIndex));

            foreach ((string key, RobloxFiles.Property prop) in instance.Properties)
            {
                if (prop.Type is RobloxFiles.PropertyType.BrickColor)
                {
                    prop.Value = RobloxFiles.DataTypes.BrickColor.Red();
                }
            }
        }
        // Setting instances' parent (Not for DataModel)
        foreach (var (key, value) in instances)
        {
            if (value.Parent == null) continue; // Expects a DataModel
            if (!instances.TryGetValue((int)value.Parent, out var parent)) continue;
            Console.WriteLine($"{value.Instance}'s parent is {parent.Instance}");
            value.Instance.Parent = parent.Instance;
        }

        // Write Roblox place to file system for `rojo syncback`. Path is defaulted to temp file
        string robloxPlaceFilePath = rbxlPath == null ? Path.GetTempFileName() : rbxlPath;
        robloxDataModel.Save(robloxPlaceFilePath);
        if (rbxlPath == null)
        {
            using var _ = new Defer(() =>
            {

                File.Delete(robloxPlaceFilePath);
            });
        }

        Process process = StartProcess("rojo", $"syncback {rojoProjectPath} --input {robloxPlaceFilePath} -y");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return Result.Fail($"Failed to run `rojo syncback`(stderr: {process.StandardError.ReadToEnd().EscapeMarkup()})");
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
        var path = rojoProject["tree"]?[WORLD_DATA_NAME]?["$path"]?.ToString();
        if (path is string validPath)
        {
            return validPath;
        }
        return Result.Fail($"Couldn't find `tree.{WORLD_DATA_NAME}[\"$path\"]` in project.json. This is required in ovjo");
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
