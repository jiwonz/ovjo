using FluentResults;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UAssetAPI;

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
    const string DEFAULT_ROJO_PROJECT_PATH = "default.project.json";
    const UAssetAPI.UnrealTypes.EngineVersion OVERDARE_UNREAL_ENGINE_VERSION = UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3;
    const string OVERDARE_UOBJECT_TYPE_LUA_PREFIX = "Lua";
    const string WORLD_DATA_NAME = "WorldData";
    const string WORLD_DATA_MAP_NAME = "Map";
    const string WORLD_DATA_PLAIN_FILES_NAME = "PlainFiles";
    const string WORLD_DATA_JSON_FILES_NAME = "JsonFiles";
    const string PROGRAM_NAME = "ovjo";

    public class ResultLogger : IResultLogger
    {
        private LogEventLevel GetLogEventLevel(LogLevel logLevel) =>
            logLevel switch
            {
                LogLevel.Trace => LogEventLevel.Verbose,
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Information => LogEventLevel.Information,
                LogLevel.Warning => LogEventLevel.Warning,
                LogLevel.Error => LogEventLevel.Error,
                LogLevel.Critical => LogEventLevel.Fatal,
                _ => LogEventLevel.Verbose,
            };

        public void Log(string context, string content, ResultBase result, LogLevel logLevel)
        {
            Serilog.Log.Write(GetLogEventLevel(logLevel), FormatMessage(content, context, result, logLevel));
        }

        public void Log<TContext>(string content, ResultBase result, LogLevel logLevel)
        {
            var contextName = typeof(TContext).FullName ?? typeof(TContext).Name;
            Serilog.Log.Write(GetLogEventLevel(logLevel), FormatMessage(content, contextName, result, logLevel));
        }

        private string FormatMessage(string content, string context, ResultBase result, LogLevel level)
        {
            var mainReason = result.Reasons.FirstOrDefault();
            if (mainReason == null)
            {
                return string.Empty;
            }
            var reasonLines = result.Reasons.Skip(1).Select(reason =>
            {
                return $"  - {reason.Message}";
            });

            var reasonBlock = string.Join(Environment.NewLine, reasonLines);

            return result.Reasons.Count > 1 ? $"""
{mainReason.Message}
Reasons ({result.Reasons.Count - 1}):
{reasonBlock}
""" : $"""
{mainReason.Message}
""";
        }
    }

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

    private static Result RequireRojoSyncback()
    {
        try
        {
            Process process = StartProcess("rojo", "syncback --help");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return Result.Fail($"Failed to run `rojo syncback`(stderr: {process.StandardError.ReadToEnd()}) The `syncback` command does not exist or does not work in the currently installed rojo. Please check the version of rojo.");
            }
            return Result.Ok();
        }
        catch (Exception e)
        {
            return Result.Fail($"Failed to run rojo(error: {e.Message}) rojo may not be installed. rojo is required to use this program.");
        }
    }

    private static async Task<int> Main(string[] args)
    {
        // Setup Result's logger
        ResultLogger resultLogger = new();
        Result.Setup(cfg =>
        {
            cfg.Logger = resultLogger;
        });

        // Setup CLI Commands
        Command syncbackCommand = new("syncback", "Performs 'syncback' for the provided project, using the `input` file given");
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

        Command buildCommand = new("build", "Builds rojo project into OVERDARE world");
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

        Command devCommand = new("dev", "Starts developing rojo project");
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

        Command initCommand = new("init", "Initializes a new rojo project");
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

        Command studioCommand = new("studio", "Opens OVERDARE Studio");
        {
            studioCommand.SetHandler(() =>
            {
                var metadataResult = FindSandboxMetadata();
                if (metadataResult.IsFailed)
                {
                    ExpectResult(Result.Fail($"Failed to find OVERDARE Studio metadata in the computer via Epic Games Launcher.").WithReasons(metadataResult.Errors));
                    return;
                }
                StartProcess(metadataResult.Value.ProgramPath);
            });
        }

        var verbosityOption = new Option<int>(
            aliases: ["--verbosity", "-v"], // Common aliases
            description: "Sets the verbosity level (e.g., -v 2, --verbosity 3)."
        )
        {
            // The argument name for help display (e.g., -v <level>)
            // System.CommandLine infers the argument type is <int> from Option<int>
            ArgumentHelpName = "level"
        };
        RootCommand rootCommand = new("ovjo");
        rootCommand.AddGlobalOption(verbosityOption);
        rootCommand.AddCommand(devCommand);
        rootCommand.AddCommand(initCommand);
        rootCommand.AddCommand(syncbackCommand);
        rootCommand.AddCommand(studioCommand);

        var commandLineBuilder = new CommandLineBuilder(rootCommand);

        commandLineBuilder.AddMiddleware(async (context, next) =>
        {
            // Setup logger with logger verbosity level
            var verbosity = context.ParseResult.GetValueForOption(verbosityOption);
            LogEventLevel minimumLevel = verbosity switch
            {
                >= 3 => LogEventLevel.Verbose,
                2 => LogEventLevel.Debug,
                1 => LogEventLevel.Information,
                _ => LogEventLevel.Warning
            };
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .WriteTo.Console(outputTemplate: $"[{{Timestamp:HH:mm:ss}} {{Level:u3}} {PROGRAM_NAME}] {{Message:lj}}{{NewLine}}{{Exception}}", standardErrorFromLevel: Serilog.Events.LogEventLevel.Warning)
                .CreateLogger();

            // Check rojo is ok and warn if not
            RequireRojoSyncback().LogIfFailed(LogLevel.Warning);

            await next(context);
        });
        commandLineBuilder.UseDefaults();
        var parser = commandLineBuilder.Build();

        return await parser.InvokeAsync(args);
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
            result.Log(LogLevel.Error);
            Environment.Exit(1);
            throw new InvalidOperationException("unreachable");
        }
        return result.Value;
    }

    private static void ExpectResult(Result result)
    {
        if (result.IsFailed)
        {
            result.Log(LogLevel.Error);
            Environment.Exit(1);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static Result Syncback(string rojoProjectPath, string umapPath, string? rbxlPath)
    {
        Result rojoSyncbackStatus = RequireRojoSyncback();
        if (rojoSyncbackStatus.IsFailed)
        {
            return Result.Fail($"Rojo syncback is required to perform ovjo syncback, but got an error: {rojoSyncbackStatus.Errors[0].Message}");
        }
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail("Failed to parse rojo project file.");
        }
        var worldDataPath = GetWorldDataPath(rojoProject);
        if (worldDataPath.IsFailed)
        {
            return Result.Fail(worldDataPath.Errors[0]);
        }
        string? ovdrWorldPath = Path.GetDirectoryName(umapPath);
        if (ovdrWorldPath == null)
        {
            return Result.Fail("Failed to get world path from umap file. Couldn't find `umap path`'s parent directory.");
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
            return Result.Fail("Failed to read umap file.");
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
                Dictionary<string, byte[]> filesData = new();
                foreach (string p in files)
                {
                    byte[] content = File.ReadAllBytes(p);
                    if (Path.GetExtension(p) == ".json")
                    {
                        content = MessagePack.MessagePackSerializer.ConvertFromJson(Encoding.UTF8.GetString(content));
                    }
                    filesData.Add(Path.GetFileNameWithoutExtension(p), content);
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
            Log.Information($"Class: {classTypeNameWithoutLuaPrefix} Raw: {classTypeNameString} FName: {normalExport.ObjectName} PackageIndex: {packageIndex}");
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
                Log.Debug($"Instance Name: {namePropertyString.Value.Value}");
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
            Log.Debug($"{value.Instance}'s parent is {parent.Instance}");
            value.Instance.Parent = parent.Instance;
        }

        // Write Roblox place to file system for `rojo syncback`. Path is defaulted to temp file
        string robloxPlaceFilePath = Path.ChangeExtension(rbxlPath == null ? Path.GetTempFileName() : rbxlPath, "rbxl");
        robloxDataModel.Save(robloxPlaceFilePath);

        // Run `rojo syncback` from composed .rbxl place file
        Process process = StartProcess("rojo", $"syncback {rojoProjectPath} --input {robloxPlaceFilePath} -y");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return Result.Fail($"Failed to run `rojo syncback`").WithReason(new Error($"rojo exited with code 0 with stderr: {process.StandardError.ReadToEnd()}"));
        }

        // Delete the saved place file if it was a tempfile
        if (rbxlPath == null)
        {
            using var _ = new Defer(() =>
            {

                File.Delete(robloxPlaceFilePath);
            });
        }

        return Result.Ok();
    }

    private static Result Build(string rojoProjectPath, string umapPath)
    {
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail("Failed to parse rojo project file.");
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
                return Result.Fail($"Launch executable not found.");
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
