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

    private class ResultLogger : IResultLogger
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

    private static Result RequireProgram(string program, string args)
    {
        try
        {
            Process process = StartProcess(program, args);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return Result.Fail($"Execution of `{program} {args}` failed. {PROGRAM_NAME} requires `{program}` to function properly with the provided arguments: `{args}`.")
                    .WithReason(new Error(process.StandardError.ReadToEnd()));
            }
            return Result.Ok();
        }
        catch (Exception e)
        {
            return Result.Fail($"Unable to execute `{program}`. It appears that `{program}` is not installed or is inaccessible. {PROGRAM_NAME} requires `{program}` to function properly.")
                .WithReason(new Error(e.Message));
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

        Option<int> verboseOption = new(
            aliases: ["--verbose", "-v"],
            description: "Sets the verbosity level (e.g., -v 2, --verbosity 3)."
        )
        {
            ArgumentHelpName = "level"
        };
        RootCommand rootCommand = new("ovjo");
        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddCommand(devCommand);
        rootCommand.AddCommand(initCommand);
        rootCommand.AddCommand(syncbackCommand);
        rootCommand.AddCommand(studioCommand);

        CommandLineBuilder commandLineBuilder = new(rootCommand);
        commandLineBuilder.AddMiddleware(async (context, next) =>
        {
            // Setup logger with logger verbosity level
            var verbosity = context.ParseResult.GetValueForOption(verboseOption);
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
            RequireProgram("rojo", "syncback --help").LogIfFailed(LogLevel.Warning);

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

    private static string RemoveBom(string p)
    {
        string BOMMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());
        if (p.StartsWith(BOMMarkUtf8, StringComparison.Ordinal))
            p = p.Remove(0, BOMMarkUtf8.Length);
        return p.Replace("\0", "");
    }

    private static Result Syncback(string rojoProjectPath, string umapPath, string? rbxlPath)
    {
        {
            Result rojoStatus = RequireProgram("rojo", "syncback --help");
            if (rojoStatus.IsFailed)
            {
                return Result.Fail("`rojo syncback` is required to perform `ovjo syncback`, but failed to check.").WithErrors(rojoStatus.Errors);
            }
        }
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail("Failed to parse rojo project file.");
        }
        var worldDataPath = GetWorldDataPath(rojoProject);
        if (worldDataPath.IsFailed)
        {
            return Result.Fail("Failed to get WorldData path.").WithErrors(worldDataPath.Errors);
        }
        string? ovdrWorldPath = Path.GetDirectoryName(umapPath);
        if (ovdrWorldPath == null)
        {
            return Result.Fail("Failed to get world path from umap file. Couldn't find `umap path`'s parent directory.");
        }

        // Initialize data files in empty BinaryStringValue .rbxm for the syncback
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
            if (normalExport["bVisibleInLevelBrowser"] is UAssetAPI.PropertyTypes.Objects.BoolPropertyData bVisibleInLevelBrowser)
            {
                if (bVisibleInLevelBrowser.Value == false) continue;
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

            // Setting script Roblox Instance up and sets ObjectName if needed
            bool needObjectName = false;
            if (normalExport["LuaCode"] is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData luaCode)
            {
                if (instance is not RobloxFiles.LuaSourceContainer)
                {
                    return Result.Fail($"LuaCode property was found in this OVERDARE Instance({classTypeName}) but its Roblox class equivalent is not a LuaSourceContainer.");
                }
                Log.Information($"LuaCode object index: {luaCode.Value.Index}");
                var import = luaCode.Value.ToImport(asset);
                if (import == null)
                {
                    return Result.Fail($"Couldn't find Import of ObjectPropertyData. File might be corrupted.");
                }
                string packagePath = "";
                // Follow the outer chain until we reach a top-level package  
                UAssetAPI.UnrealTypes.FPackageIndex currentOuter = import.OuterIndex;
                while (!currentOuter.IsNull())
                {
                    Import outerImport = currentOuter.ToImport(asset);
                    if (outerImport == null) break;

                    // If this is a top-level package (OuterIndex is null)  
                    if (outerImport.OuterIndex.IsNull())
                    {
                        packagePath = outerImport.ObjectName.ToString();
                        break;
                    }

                    currentOuter = outerImport.OuterIndex;
                }
                Log.Information($"LuaCode PackagePath: {packagePath}");
                // Remove /User/ prefix (Assumes /User/ is current directory aka. 'ovdrWorldPath')
                string gameAssetPath = packagePath.Substring(6);
                string luaCodePath = Path.ChangeExtension(Path.Combine(ovdrWorldPath.FixDirectorySeparatorsForDisk(), gameAssetPath.FixDirectorySeparatorsForDisk()), "lua");
                if (!File.Exists(luaCodePath))
                {
                    return Result.Fail($"LuaCode file not found: {luaCodePath}");
                }
                string luaCodeContent = RemoveBom(File.ReadAllText(luaCodePath));
                switch (instance)
                {
                    case RobloxFiles.Script script:
                        script.Source = luaCodeContent;
                        needObjectName = true;
                        break;
                    case RobloxFiles.ModuleScript moduleScript:
                        moduleScript.Source = luaCodeContent;
                        needObjectName = true;
                        break;
                }
            }
            if (instance is RobloxFiles.Folder) needObjectName = true;
            if (needObjectName) instance.SetAttribute("ObjectName", export.ObjectName.ToString());

            // Setting normal Roblox Instance up
            if (normalExport["Name"] is UAssetAPI.PropertyTypes.Objects.StrPropertyData nameProperty)
            {
                Log.Debug($"Instance Name: {nameProperty.Value.Value}");
                instance.Name = nameProperty.Value.Value;
            }
            else if (isUnknownInstance)
            {
                instance.Name = classTypeNameWithoutLuaPrefix;
                Log.Debug($"Custom Instance Added: {classTypeNameWithoutLuaPrefix}");
            }
            instances.Add(packageIndex, (Instance: instance, Parent: parentIndex));

            // Manually modify BrickColor properties to correct BrickColor.. because some BrickColors are serialized incorrectly (Roblox-File-Format issue)
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
        {
            Result rojoStatus = RequireProgram("rojo", "sourcemap --help");
            if (rojoStatus.IsFailed)
            {
                return Result.Fail("`rojo sourcemap` is required to perform `ovjo build`, but failed to check.").WithErrors(rojoStatus.Errors);
            }
        }
        var rojoProject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(rojoProjectPath));
        if (rojoProject == null)
        {
            return Result.Fail("Failed to parse rojo project file.");
        }
        var worldDataPath = GetWorldDataPath(rojoProject);
        if (worldDataPath.IsFailed)
        {
            return Result.Fail("Failed to get WorldData path.").WithErrors(worldDataPath.Errors);
        }
        var mapData = RobloxFiles.BinaryRobloxFile.Open(Path.ChangeExtension(Path.Combine(worldDataPath.Value, WORLD_DATA_MAP_NAME), "rbxm")).GetChildren()[0];
        if (mapData is not RobloxFiles.BinaryStringValue mapBinaryString)
        {
            return Result.Fail($"{{WorldData}}.{WORLD_DATA_MAP_NAME} is not a `BinaryStringValue`.");
        }
        UAsset asset = new();
        asset.FilePath = umapPath;
        asset.Mappings = null;
        asset.CustomSerializationFlags = CustomSerializationFlags.None;
        asset.SetEngineVersion(OVERDARE_UNREAL_ENGINE_VERSION);
        {
            MemoryStream stream = new(mapBinaryString.Value);
            AssetBinaryReader reader = new(stream, asset);
            asset.Read(reader);
        }
        Process process = StartProcess("rojo", $"sourcemap {rojoProjectPath}");
        process.WaitForExit();
        var sourcemap = JsonConvert.DeserializeObject<JObject>(process.StandardOutput.ReadToEnd());
        if (sourcemap == null)
        {
            return Result.Fail("Failed to deserialize sourcemap");
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
                RedirectStandardOutput = true,
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
