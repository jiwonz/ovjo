using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentResults;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using UAssetAPI;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    public static class LibOvjo
    {
        private const string _overdareUObjectTypeLuaPrefix = "Lua";

        public static Result Syncback(string rojoProjectPath, string umapPath, string? rbxlPath)
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "syncback --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo syncback` is required to perform `ovjo syncback`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }
            var rojoProject = JsonConvert.DeserializeObject<JObject>(
                File.ReadAllText(rojoProjectPath)
            );
            if (rojoProject == null)
            {
                return Result.Fail(_("Failed to parse rojo project file."));
            }
            var worldDataPath = GetWorldDataPath(rojoProject);
            if (worldDataPath.IsFailed)
            {
                return Result
                    .Fail(_("Failed to get WorldData path."))
                    .WithReasons(worldDataPath.Errors);
            }
            string? ovdrWorldPath = new FileInfo(umapPath).Directory?.FullName;
            if (ovdrWorldPath == null)
            {
                return Result.Fail(
                    _(
                        "Failed to get world path from umap file. Couldn't find `umap path`'s parent directory."
                    )
                );
            }

            // Initialize data files in empty BinaryStringValue .rbxm for the syncback
            {
                string[] worldDataFiles =
                [
                    Path.ChangeExtension(WORLD_DATA_MAP_NAME, "rbxm"),
                    Path.ChangeExtension(WORLD_DATA_PLAIN_FILES_NAME, "rbxm"),
                    Path.ChangeExtension(WORLD_DATA_JSON_FILES_NAME, "rbxm"),
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
            asset.SetEngineVersion(Overdare.SandboxMetadata.UnrealEngineVersion);
            var stream = asset.PathToStream(umapPath);
            if (stream == null)
            {
                return Result.Fail(_("Failed to read umap file."));
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
                        if (
                            Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            content = MessagePack.MessagePackSerializer.ConvertFromJson(
                                Encoding.UTF8.GetString(content)
                            );
                        }
                        filesData.Add(Path.GetFileNameWithoutExtension(p), content);
                    }
                    return MessagePack.MessagePackSerializer.Serialize(filesData);
                }

                RobloxFiles.BinaryStringValue jsonFilesData = new()
                {
                    Name = WORLD_DATA_JSON_FILES_NAME,
                    Value = FilesToMessagePackBinaryString(
                        Directory.GetFiles(ovdrWorldPath, "*.json")
                    ),
                    Parent = folder,
                };
                string[] plainFiles = Directory
                    .GetFiles(ovdrWorldPath)
                    .Where(file =>
                        !Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetExtension(file)
                            .Equals(".umap", StringComparison.OrdinalIgnoreCase)
                    )
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
                if (!(export is UAssetAPI.ExportTypes.NormalExport normalExport))
                    continue;

                // Skip if it's invisible in level browser
                if (
                    normalExport["bVisibleInLevelBrowser"]
                    is UAssetAPI.PropertyTypes.Objects.BoolPropertyData bVisibleInLevelBrowser
                )
                {
                    if (bVisibleInLevelBrowser.Value == false)
                        continue;
                }

                // Getting ClassType(ex. LuaPart, LuaModuleScript) of the current export
                var classTypeName = export.GetExportClassType();
                if (classTypeName == null)
                    continue;
                var classTypeNameString = classTypeName.Value;
                if (classTypeNameString == null)
                    continue;

                // Converting OVERDARE's Lua class name to Roblox class name
                string classTypeNameWithoutLuaPrefix = Regex.Replace(
                    classTypeNameString.Value,
                    $"^{Regex.Escape(_overdareUObjectTypeLuaPrefix)}",
                    ""
                );
                Log.Information(
                    $"Class: {classTypeNameWithoutLuaPrefix} Raw: {classTypeNameString} FName: {normalExport.ObjectName} PackageIndex: {packageIndex}"
                );
                bool isDataModel = classTypeNameWithoutLuaPrefix == "DataModel";
                var instance = isDataModel
                    ? robloxDataModel
                    : UtilityFunctions.TryCreateInstance<RobloxFiles.Instance>(
                        classTypeNameWithoutLuaPrefix
                    );

                // Skips DataModel with no parent set
                if (instance != null && isDataModel)
                {
                    instances.Add(packageIndex, (Instance: instance, Parent: null));
                    continue;
                }

                // Getting parent
                var parentProperty = normalExport["Parent"];
                if (
                    !(
                        parentProperty
                        is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData parentObject
                    )
                    || !parentObject.Value.IsExport()
                    || parentObject.Value.IsNull()
                )
                    continue;
                int parentIndex = parentObject.Value.Index - 1;
                bool isUnknownInstance = false;
                if (instance == null)
                {
                    instance = new RobloxFiles.Model();
                    isUnknownInstance = true;
                }

                // Setting script Roblox Instance up
                if (
                    normalExport["LuaCode"]
                    is UAssetAPI.PropertyTypes.Objects.ObjectPropertyData luaCode
                )
                {
                    if (instance is not RobloxFiles.LuaSourceContainer)
                    {
                        return Result.Fail(
                            _(
                                "LuaCode property was found in this OVERDARE Instance({0}) but its Roblox class equivalent is not a LuaSourceContainer.",
                                classTypeName
                            )
                        );
                    }
                    Log.Information($"LuaCode object index: {luaCode.Value.Index}");
                    var import = luaCode.Value.ToImport(asset);
                    if (import == null)
                    {
                        return Result.Fail(
                            _(
                                "Couldn't find Import of LuaCode ObjectPropertyData. File might be corrupted."
                            )
                        );
                    }
                    string packagePath = "";
                    // Follow the outer chain until we reach a top-level package
                    UAssetAPI.UnrealTypes.FPackageIndex currentOuter = import.OuterIndex;
                    while (!currentOuter.IsNull())
                    {
                        Import outerImport = currentOuter.ToImport(asset);
                        if (outerImport == null)
                            break;

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
                    string luaCodePath = Path.ChangeExtension(
                        Path.Combine(
                            ovdrWorldPath.FixDirectorySeparatorsForDisk(),
                            gameAssetPath.FixDirectorySeparatorsForDisk()
                        ),
                        "lua"
                    );
                    if (!File.Exists(luaCodePath))
                    {
                        return Result.Fail(_("LuaCode file not found: {0}", luaCodePath));
                    }
                    string luaCodeContent = UtilityFunctions.RemoveBom(
                        File.ReadAllText(luaCodePath)
                    );
                    switch (instance)
                    {
                        case RobloxFiles.Script script:
                            script.Source = luaCodeContent;
                            break;
                        case RobloxFiles.ModuleScript moduleScript:
                            moduleScript.Source = luaCodeContent;
                            break;
                    }
                }

                // Setting normal Roblox Instance up
                instance.SetAttribute("ObjectName", export.ObjectName.ToString());
                if (
                    normalExport["Name"]
                    is UAssetAPI.PropertyTypes.Objects.StrPropertyData nameProperty
                )
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
                if (value.Parent == null)
                    continue; // Expects a DataModel
                if (!instances.TryGetValue((int)value.Parent, out var parent))
                    continue;
                Log.Debug($"{value.Instance}'s parent is {parent.Instance}");
                value.Instance.Parent = parent.Instance;
            }

            // Write Roblox place to file system for `rojo syncback`. Path is defaulted to temp file
            string robloxPlaceFilePath = Path.ChangeExtension(
                rbxlPath == null ? Path.GetTempFileName() : rbxlPath,
                "rbxl"
            );
            robloxDataModel.Save(robloxPlaceFilePath);

            // Run `rojo syncback` from composed .rbxl place file
            Process process = UtilityFunctions.StartProcess(
                "rojo",
                $"syncback {rojoProjectPath} --input {robloxPlaceFilePath} -y"
            );
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return Result
                    .Fail(_("Failed to run `rojo syncback`."))
                    .WithReason(
                        new Error(
                            _(
                                "rojo exited with code 0 with stderr: {0}",
                                process.StandardError.ReadToEnd()
                            )
                        )
                    );
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

        public static Result Build(string rojoProjectPath, string umapPath)
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "sourcemap --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo sourcemap` is required to perform `ovjo build`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }
            var rojoProject = JsonConvert.DeserializeObject<JObject>(
                File.ReadAllText(rojoProjectPath)
            );
            if (rojoProject == null)
            {
                return Result.Fail(_("Failed to parse rojo project file."));
            }
            string? ovdrWorldPath = new FileInfo(umapPath).Directory?.FullName;
            if (ovdrWorldPath == null)
            {
                return Result.Fail(
                    _(
                        "Failed to get world path from umap file. Couldn't find `umap path`'s parent directory."
                    )
                );
            }
            var worldDataPath = GetWorldDataPath(rojoProject);
            if (worldDataPath.IsFailed)
            {
                return Result
                    .Fail(_("Failed to get WorldData path."))
                    .WithReasons(worldDataPath.Errors);
            }

            // Read UAsset from the WorldData
            var mapData = RobloxFiles
                .BinaryRobloxFile.Open(
                    Path.ChangeExtension(
                        Path.Combine(worldDataPath.Value, WORLD_DATA_MAP_NAME),
                        "rbxm"
                    )
                )
                .GetChildren()[0];
            if (mapData is not RobloxFiles.BinaryStringValue mapBinaryString)
            {
                return Result.Fail(
                    _("{{WorldData}}.{0} is not a `BinaryStringValue`.", WORLD_DATA_MAP_NAME)
                );
            }
            UAsset asset = new();
            asset.FilePath = umapPath;
            asset.Mappings = null;
            asset.CustomSerializationFlags = CustomSerializationFlags.None;
            asset.SetEngineVersion(Overdare.SandboxMetadata.UnrealEngineVersion);
            {
                MemoryStream stream = new(mapBinaryString.Value);
                AssetBinaryReader reader = new(stream, asset);
                asset.Read(reader);
            }

            // Read and parse sourcemap from rojo project
            Process process = UtilityFunctions.StartProcess("rojo", $"sourcemap {rojoProjectPath}");
            process.WaitForExit();
            var sourcemap = JsonConvert.DeserializeObject<JObject>(
                process.StandardOutput.ReadToEnd()
            );
            if (sourcemap == null)
            {
                return Result.Fail(_("Failed to deserialize sourcemap."));
            }

            // Check if the project is DataModel
            string? className = sourcemap["className"]?.ToString();
            if (className == null)
            {
                return Result.Fail(_("Expected `className` field in the sourcemap."));
            }
            if (className != "DataModel")
            {
                return Result.Fail(
                    _(
                        "Only building the DataModel project, which means the world, is supported, and other instance classes other than DataModel, which means the model, are not supported."
                    )
                );
            }

            // Visit every children of the source map and create and add new scripts and folders that do not exist in the OVERDARE World file(aka UAsset, the.umap file),
            // and compose the Lua folder of the OVERDARE World folder.
            // Checks "ObjectName" attribute from .meta.json file and validate them based on WorldData. (Verifies parent / child relationships between instances and their presence in map data)
            // Invalid things are considered as "Out of sync".and ovjo will throw an error for this and require a syncback process.
            Result VisitSourcemapChild(JToken node)
            {
                string? className = node["className"]?.ToString();
                if (className == null)
                {
                    return Result.Fail(_("Expected `className` field in the child of sourcemap."));
                }
                string? name = node["name"]?.ToString();
                if (name == null)
                {
                    return Result.Fail(_("Expected `name` field in the child of sourcemap."));
                }

                var childrenProp = node["children"];
                if (childrenProp is JArray children)
                {
                    foreach (var child in children)
                    {
                        Result visitResult = VisitSourcemapChild(child);
                        if (visitResult.IsFailed)
                        {
                            return Result
                                .Fail(
                                    _(
                                        "Failed to visit child of sourcemap's child({0}<name: {1}>).",
                                        className,
                                        name
                                    )
                                )
                                .WithReasons(visitResult.Errors);
                        }
                    }
                }

                var filePathsProp = node["filePaths"];
                string? sourceFilePath;
                string? metaFilePath;
                if (filePathsProp is not JArray filePaths)
                    filePaths = new JArray();
                //switch (className)
                //{
                //    case "Folder":

                //}

                return Result.Ok();
            }
            Result visitResult = VisitSourcemapChild(sourcemap);
            if (visitResult.IsFailed)
            {
                return Result.Fail(_("Failed to visit sourcemap.")).WithReasons(visitResult.Errors);
            }

            return Result.Ok();
        }
    }
}
