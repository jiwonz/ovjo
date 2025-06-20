using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentResults;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    public static class LibOvjo
    {
        private const string _overdareUObjectTypeLuaPrefix = "Lua";
        private const string _overdareReferenceAttributeName = "ovdr_ref";
        private static readonly string[] _rojoMetaProperties =
        [
            "attributes",
            "properties",
            "$className",
        ];

        private static string RemoveLuaPrefix(string className)
        {
            return Regex.Replace(className, $"^{Regex.Escape(_overdareUObjectTypeLuaPrefix)}", "");
        }

        public static Result Syncback(
            string rojoProjectPath,
            string umapPath,
            string? rbxlPath = null,
            bool isResyncbacked = false
        )
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "syncback --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo syncback` is required to perform `LibOvjo.Syncback`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }

            // Convert Overdare world to Roblox place file
            var world = World.FromOverdare(umapPath);

            RobloxFiles.BinaryRobloxFile robloxDataModel = new();
            static Result ToRobloxInstanceTree(
                Overdare.UScriptClass.LuaInstance source,
                RobloxFiles.Instance robloxParent
            )
            {
                if ((source.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotBrowsable) != 0)
                    return Result.Ok();
                var sourceClassNameWithoutLuaPrefix = RemoveLuaPrefix(source.ClassName);
                var robloxSource = UtilityFunctions.TryCreateInstance(
                    sourceClassNameWithoutLuaPrefix
                );
                if (robloxSource == null)
                {
                    robloxSource = new RobloxFiles.Model
                    {
                        Name = source.Name ?? sourceClassNameWithoutLuaPrefix,
                    };
                }
                else
                {
                    robloxSource.Name = source.Name ?? robloxSource.ClassName;
                    //Log.Debug($"Name written: {robloxSource.Name}");
                }
                robloxSource.Parent = robloxParent;
                if (
                    (source.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotCreatable) != 0
                    && source.SavedActor != null
                )
                {
                    // Set attributes if this is a NotCreatable LuaInstance (my purpose is not to set Attributes to creatable instances!)
                    robloxSource.SetAttribute(
                        _overdareReferenceAttributeName,
                        source.SavedActor.ExportIndex
                    );
                    //Log.Debug(
                    //    $"Set {_overdareReferenceAttributeName} to {source.GetFullName()}({robloxSource.GetFullName()})"
                    //);
                    //Log.Debug(source.SavedActor.ExportIndex.ToString());
                }
                else
                {
                    // Remove reference attribute for Creatable LuaInstance
                    robloxSource.Attributes.Remove(_overdareReferenceAttributeName);
                    if (source is Overdare.UScriptClass.BaseLuaScript luaScript)
                    {
                        //Log.Debug($"Got Source file {luaScript.GetFullName()}: {luaScript.Source}");
                        switch (robloxSource)
                        {
                            case RobloxFiles.Script script:
                                script.Source = luaScript.Source;
                                //Log.Debug($"Source written: {script.Source.ToString().Length}");
                                break;
                            case RobloxFiles.ModuleScript moduleScript:
                                moduleScript.Source = luaScript.Source;
                                //Log.Debug($"Source written: {moduleScript.Source.ToString().Length}");
                                break;
                            default:
                                return Result.Fail(
                                    _(
                                        "LuaCode property was found in this OVERDARE Instance({0}) but its Roblox class equivalent is not a LuaSourceContainer.",
                                        source.ClassName
                                    )
                                );
                        }
                    }
                }

                foreach (var child in source.GetChildren())
                {
                    var conversionResult = ToRobloxInstanceTree(child, robloxSource);
                    if (conversionResult.IsFailed)
                    {
                        return Result
                            .Fail(
                                _(
                                    "Failed to convert LuaInstance({0}) to Roblox Instance.",
                                    child.ClassName
                                )
                            )
                            .WithReasons(conversionResult.Errors);
                    }
                }

                return Result.Ok();
            }
            foreach (var child in world.Map.LuaDataModel.GetChildren())
            {
                var conversionResult = ToRobloxInstanceTree(child, robloxDataModel);
                if (conversionResult.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "Failed to convert LuaInstance({0}) to Roblox Instance.",
                                child.ClassName
                            )
                        )
                        .WithReasons(conversionResult.Errors);
                }
            }

            // Save the world for the future use
            if (!isResyncbacked) // 싱크백 목적을 위해 빌드된 후 다시 싱크백된 경우를 의미합니다.
            {
                // 이 경우에는 빌드 후 변경이 있을 수 있고, 싱크백 목적으로 실행했으므로 월드를 저장하지 않습니다.
                world.Save();
            }

            // Write Roblox place to file system for `rojo syncback`. Path is defaulted to temp file
            string robloxPlaceFilePath = Path.ChangeExtension(
                rbxlPath ?? Path.GetTempFileName(),
                "rbxl"
            );
            robloxDataModel.Save(robloxPlaceFilePath);
            using var cleanup = new Defer(() =>
            {
                if (rbxlPath != null)
                    return;
                if (!File.Exists(robloxPlaceFilePath))
                    return;
                File.Delete(robloxPlaceFilePath);
            });

            // Run `rojo syncback` from composed .rbxl place file
            var process = UtilityFunctions.StartProcess(
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
            Log.Information(process.StandardOutput.ReadToEnd());

            // Extract attributes and properties into init.meta.json files
            // We need to do this because `rojo syncback` just edits the rojo project JSON file for the attributes and properties.
            // But if there are no attributes or properties, we are unable to sync back the attributes and properties to the Roblox place file when build.
            var syncbackProjectJson = JsonConvert.DeserializeObject<JObject>(
                File.ReadAllText(rojoProjectPath)
            );
            if (syncbackProjectJson == null)
            {
                return Result.Fail(_("Failed to parse rojo project JSON."));
            }
            var hasGit = UtilityFunctions.IsGitRepository(Directory.GetCurrentDirectory());
            if (syncbackProjectJson["tree"] is JObject tree)
            {
                bool thereWasAModification = false;
                foreach (var (key, value) in tree)
                {
                    if (key.StartsWith('$')) // Skip metadata keys
                        continue;
                    if (value is not JObject instanceJson)
                    {
                        continue;
                    }
                    var path = instanceJson["$path"]?.ToString();
                    if (string.IsNullOrEmpty(path))
                    {
                        Log.Warning($"Instance {key} has no path in rojo project JSON.");
                        continue;
                    }
                    if (!Directory.Exists(path))
                    {
                        Log.Warning($"Instance {key} path {path} does not exist.");
                        continue;
                    }

                    var props = _rojoMetaProperties
                        .Where(key => instanceJson['$' + key] is not null)
                        .Select(key =>
                        {
                            var metaKey = '$' + key;
                            var obj = instanceJson[metaKey]!;
                            instanceJson.Remove(metaKey); // Remove from original JObject as a side effect
                            thereWasAModification = true; // Mark that there was a modification to save/update modified rojo project JSON
                            return (Key: key, Value: obj);
                        })
                        .ToArray();
                    if (props.Length > 0)
                    {
                        JObject propsObject = new(
                            props.Select(tuple => new JProperty(tuple.Key, tuple.Value))
                        );
                        File.WriteAllText(
                            Path.Combine(path, "init.meta.json"),
                            propsObject.ToString(Formatting.Indented)
                        );
                    }
                    if (hasGit)
                    {
                        if (Directory.EnumerateFileSystemEntries(path).Any())
                        {
                            if (File.Exists(Path.Combine(path, ".gitkeep")))
                            {
                                File.Delete(Path.Combine(path, ".gitkeep"));
                            }
                        }
                        else
                        {
                            using (File.Create(Path.Combine(path, ".gitkeep"))) { }
                        }
                    }
                }
                if (thereWasAModification)
                {
                    Log.Information("Modified rojo project JSON with init.meta.json files.");
                    File.WriteAllText(
                        rojoProjectPath,
                        syncbackProjectJson.ToString(Formatting.Indented)
                    );
                }
            }

            return Result.Ok();
        }

        public static Result Build(string rojoProjectPath, string umapPath, string? rbxlPath = null)
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "build --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo build` is required to perform `LibOvjo.Build`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }
            var world = World.Open();
            string robloxPlaceFilePath = Path.ChangeExtension(
                rbxlPath ?? Path.GetTempFileName(),
                "rbxl"
            );
            var process = UtilityFunctions.StartProcess(
                "rojo",
                $"build {rojoProjectPath} --output {robloxPlaceFilePath}"
            );
            process.WaitForExit();
            using var cleanup = new Defer(() =>
            {
                if (rbxlPath != null)
                    return;
                if (!File.Exists(robloxPlaceFilePath))
                    return;
                File.Delete(robloxPlaceFilePath);
            });
            if (process.ExitCode != 0 || !File.Exists(robloxPlaceFilePath))
            {
                return Result
                    .Fail(_("Failed to run `rojo build`."))
                    .WithReason(
                        new Error(
                            _(
                                "rojo exited with code 0 with stderr: {0}",
                                process.StandardError.ReadToEnd()
                            )
                        )
                    );
            }

            var robloxDataModel = RobloxFiles.BinaryRobloxFile.Open(robloxPlaceFilePath);
            Dictionary<int, Overdare.UScriptClass.LuaInstance> luaInstancesFromOverdareReference =
            [];
            foreach (var luaInstance in world.Map.LuaDataModel.GetDescendants())
            {
                // Remove creatable LuaInstances
                if (
                    (luaInstance.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotCreatable)
                    == 0
                )
                {
                    luaInstance.Parent = null;
                    continue;
                }

                if (luaInstance.SavedActor == null)
                    continue;
                luaInstancesFromOverdareReference[luaInstance.SavedActor.ExportIndex] = luaInstance;
                Log.Debug($"{luaInstance.SavedActor.ExportIndex}");
            }
            // TO-DO: Creatable LuaInstances should be destroyed from the world
            //foreach (var luaInstance in world.Map.LuaDataModel.GetDescendants())
            //{
            //    // Skip non-NotCreatable LuaInstances
            //    if (
            //        (luaInstance.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotCreatable)
            //        == 0
            //    )
            //    {
            //        Log.Debug(
            //            $"{luaInstance.ClassName} is destroyed because it is non-NotCreatable LuaInstance"
            //        );
            //        luaInstance.Destroy();
            //        continue;
            //    }

            //}

            // Visits and set luaInstancesFromOverdareReference's parents (Creates new if not found)
            Result VisitRobloxInstance(
                RobloxFiles.Instance robloxInstance,
                Overdare.UScriptClass.LuaInstance ovdrParent
            )
            {
                robloxInstance.GetAttribute(_overdareReferenceAttributeName, out int? ovdrRef);
                Log.Debug($"ovdr_ref: {ovdrRef}");
                if (
                    !ovdrRef.HasValue
                    || !luaInstancesFromOverdareReference.TryGetValue(
                        ovdrRef.Value,
                        out var luaInstance
                    )
                )
                {
                    Log.Debug($"new creatable thing! the parent: {ovdrParent.GetFullName()}");
                    luaInstance = robloxInstance switch
                    {
                        RobloxFiles.LocalScript localScript =>
                            new Overdare.UScriptClass.LuaLocalScript
                            {
                                Name =
                                    robloxInstance.Name == "LocalScript"
                                        ? null
                                        : robloxInstance.Name,
                                Source = localScript.Source,
                            },
                        RobloxFiles.Script script => new Overdare.UScriptClass.LuaScript
                        {
                            Name = robloxInstance.Name == "Script" ? null : robloxInstance.Name,
                            Source = script.Source,
                        },
                        RobloxFiles.ModuleScript moduleScript =>
                            new Overdare.UScriptClass.LuaModuleScript
                            {
                                Name =
                                    robloxInstance.Name == "ModuleScript"
                                        ? null
                                        : robloxInstance.Name,
                                Source = moduleScript.Source,
                            },
                        RobloxFiles.Folder folder => new Overdare.UScriptClass.LuaFolder
                        {
                            Name = robloxInstance.Name == "Folder" ? null : robloxInstance.Name,
                        },
                        _ => throw new NotSupportedException(
                            _(
                                "Roblox Instance({0}) is not supported to be converted to LuaInstance.",
                                robloxInstance.Name
                            )
                        ),
                    };
                }

                // If luaInstance is not a default named, then set its name to Roblox Instance's name
                // should not be custom named if has LoadedActor already
                if (luaInstance.SavedActor == null)
                    luaInstance.Name = luaInstance.Name != null ? robloxInstance.Name : null;
                luaInstance.Parent = ovdrParent;
                Log.Debug($"New parent's children count: {ovdrParent.GetChildren().Length}");
                foreach (var child in robloxInstance.GetChildren())
                {
                    var visitResult = VisitRobloxInstance(child, luaInstance);
                    if (visitResult.IsFailed)
                    {
                        return Result
                            .Fail(
                                _(
                                    "Failed to visit Roblox Instance({0}) from the built place file.",
                                    child.Name
                                )
                            )
                            .WithReasons(visitResult.Errors);
                    }
                }

                return Result.Ok();
            }
            foreach (var instance in robloxDataModel.GetChildren())
            {
                var visitResult = VisitRobloxInstance(instance, world.Map.LuaDataModel);
                if (visitResult.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "Failed to visit Roblox Instance({0}) from the built place file.",
                                instance.Name
                            )
                        )
                        .WithReasons(visitResult.Errors);
                }
            }

            world.ExportAsOverdare(umapPath);

            return Result.Ok();
        }

        private class SourcemapChild
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("className")]
            public string ClassName { get; set; } = string.Empty;

            [JsonPropertyName("filePaths")]
            public List<string>? FilePaths { get; set; }

            [JsonPropertyName("children")]
            public List<SourcemapChild>? Children { get; set; }
        }

        public static Result Sync(string rojoProjectPath, string umapPath, bool watch)
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "sourcemap --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo sourcemap` is required to perform `LibOvjo.Sync`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }

            HashSet<string> watchingFiles = [];
            void VisitRobloxSourcemap(
                SourcemapChild source,
                Overdare.UScriptClass.LuaInstance ovdrParent
            )
            {
                Overdare.UScriptClass.LuaInstance? ovdrChild = null;
                // Default named
                foreach (var child in ovdrParent.GetChildren())
                {
                    if (child.Name != null)
                        continue;
                    var robloxClassName = RemoveLuaPrefix(child.ClassName);
                    if (robloxClassName == source.ClassName || robloxClassName == source.Name)
                    {
                        Log.Debug($"Found {source.ClassName} child: {child.GetFullName()}");
                        ovdrChild = child;
                        break;
                    }
                }
                if (ovdrChild == null)
                {
                    // Custom named
                    ovdrChild = ovdrParent.FindFirstChild(source.Name);
                    if (ovdrChild == null)
                    {
                        // 오버데어에 없는데 로블록스에 있는 경우 -> 경고 (오버데어 스튜디오에서 편집중에 오버데어 인스턴스를 생성해서 동기화할 수 없기때문에)
                        Log.Warning(
                            $"Sourcemap child {source.Name}({source.ClassName}) not found in Overdare LuaInstance {ovdrParent.GetFullName()}. Please re-build the project."
                        );
                        return;
                    }
                }
                // 로블록스에도 있고 오버데어에도 있는 경우 -> 스크립트 내용 동기화 (스크립트는 편집중에 동기화가 가능하기 때문에)
                Log.Information(
                    $"Two instances {source.Name}({source.ClassName}) and {ovdrChild.GetFullName()} are valid."
                );
                if (
                    source.ClassName == "Script"
                    || source.ClassName == "LocalScript"
                    || source.ClassName == "ModuleScript"
                )
                {
                    var filePaths = source.FilePaths;
                    if (filePaths == null || filePaths.Count == 0)
                    {
                        Log.Warning(
                            $"Sourcemap child {source.Name}({source.ClassName}) has no file paths. Skipping script source sync."
                        );
                        return;
                    }
                    var sourceFilePath = filePaths.First(fp =>
                    {
                        var ext = Path.GetExtension(fp);
                        return ext == ".lua" || ext == ".luau";
                    });
                    if (sourceFilePath == null)
                    {
                        Log.Warning(
                            $"Sourcemap child {source.Name}({source.ClassName}) has no Lua source file. Skipping script source sync."
                        );
                        return;
                    }
                    watchingFiles.Add(Path.GetFullPath(sourceFilePath));
                    if (ovdrChild is Overdare.UScriptClass.BaseLuaScript luaScript)
                    {
                        luaScript.Source = File.ReadAllText(sourceFilePath);
                        Log.Debug(
                            $"Synced BaseLuaScript({luaScript.GetFullName()}) with {sourceFilePath} (synced file size: {File.ReadAllText(sourceFilePath).Length})"
                        );
                        luaScript.SaveSource(); // Save the source to the world
                    }
                    else
                    {
                        Log.Warning(
                            $"Sourcemap child {source.Name}({source.ClassName}) is not a LuaScript. Skipping script source sync."
                        );
                    }
                }
                if (source.Children == null)
                    return;
                foreach (var child in source.Children)
                {
                    VisitRobloxSourcemap(child, ovdrChild);
                }
            }

            // TO-DO: VisitOverdareInstance로 오버데어에서 추가된 스크립트를 프로젝트에 추가하는 기능 구현
            // 이슈: 로블록스(현 프로젝트)에 없는, 오버데어에만 있는 스크립트를 새로 어떤 경로에 파일 생성해야 할지 모름(소스맵에서 확인이 어려움)
            // 원래 기능: 오버데어에서 삭제되거나 추가된 스크립트/폴더 인스턴스를 프로젝트에 추가하거나 삭제하는 기능 (근데 구조적으로 그냥 애초에 syncback을 활용해야하는거일수도?)
            // 현재 기능: 오버데어에서 추가된 인스턴스 위치를 알려주는 기능
            // 메모: 파일 시스템 폴더 위치로 대충 예상하거나 모든 폴더에 init.meta.json 파일을 생성해서 파일 경로를 알아내는 방법..?
            // workaround: 프로젝트에서 스크립트를 추가하고 빌드하거나 오버데어에서 추가 후 수동으로 프로젝트에 반영
            void VisitOverdareInstance(
                Overdare.UScriptClass.LuaInstance source,
                SourcemapChild sourcemapParent
            )
            {
                SourcemapChild? sourcemapChild = null;
                // Default named
                if (source.Name == null)
                {
                    sourcemapChild = sourcemapParent.Children?.FirstOrDefault(c =>
                        c.Name == RemoveLuaPrefix(source.ClassName)
                    );
                }
                if (sourcemapChild == null)
                {
                    // Custom named
                    sourcemapChild = sourcemapParent.Children?.FirstOrDefault(c =>
                        c.Name == source.Name
                    );
                    if (sourcemapChild == null)
                    {
                        // 로블록스에 없는데 오버데어에 있는 경우
                        Log.Warning(
                            $"Should add {source.GetFullName()}({source.ClassName}) in {sourcemapParent.FilePaths?[0]}"
                        );
                    }
                }
                // 로블록스에도 있고 오버데어에도 있는 경우
            }

            var world = World.FromOverdare(umapPath);
            string robloxPlaceFilePath = Path.ChangeExtension(Path.GetTempFileName(), "rbxl");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "rojo", // 실행할 프로그램
                    Arguments = $"sourcemap {rojoProjectPath}{(watch ? " --watch" : "")}", // 인자
                    RedirectStandardOutput = true, // 표준 출력 리디렉션
                    UseShellExecute = false, // 필수
                    CreateNoWindow = true, // 창 숨김(선택)
                },
            };

            string lastSourcemapData = string.Empty;
            void ReadSourcemap()
            {
                if (string.IsNullOrEmpty(lastSourcemapData))
                {
                    Log.Warning("No sourcemap data received yet.");
                    return;
                }
                var source = JsonConvert.DeserializeObject<SourcemapChild>(lastSourcemapData);
                if (source == null)
                {
                    Log.Warning("Failed to deserialize sourcemap child.");
                    return;
                }
                if (source.Children == null)
                {
                    Log.Warning(umapPath + " has no children in the sourcemap child.");
                    return;
                }

                foreach (var child in source.Children)
                {
                    Log.Verbose($"Visiting Roblox Instance: {child.Name}");
                    VisitRobloxSourcemap(child, world.Map.LuaDataModel);
                }
                foreach (var child in world.Map.LuaDataModel.GetChildren())
                {
                    Log.Verbose($"Visiting Overdare Instance: {child.GetFullName()}");
                    VisitOverdareInstance(child, source);
                }
            }
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                    return;
                Log.Debug($"[StandardOut] {e.Data}");
                lastSourcemapData = e.Data;
                ReadSourcemap();
            };

            process.Start();
            process.BeginOutputReadLine();

            Log.Debug($"watch? {watch}");
            if (watch)
            {
                var watcher = new FileSystemWatcher(Directory.GetCurrentDirectory())
                {
                    Filter = "*.*", // 모든 파일 감시
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (s, e) =>
                {
                    if (!watchingFiles.Contains(e.FullPath))
                        return;
                    watchingFiles.Clear();
                    ReadSourcemap();
                };
                // watch 모드에서는 사용자가 종료할 때까지 대기
                Console.WriteLine("Watching for changes... Press Enter to exit.");
                Console.ReadLine();
                process.Kill();
            }
            else
            {
                process.WaitForExit();
            }

            return Result.Ok();
        }
    }
}
