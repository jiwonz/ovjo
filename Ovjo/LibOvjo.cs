using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentResults;
using Newtonsoft.Json;
using Serilog;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    public static class LibOvjo
    {
        private const string _overdareUObjectTypeLuaPrefix = "Lua";
        private const string _overdareReferenceAttributeName = "ovdr_ref";

        private static string RemoveLuaPrefix(string className)
        {
            return Regex.Replace(className, $"^{Regex.Escape(_overdareUObjectTypeLuaPrefix)}", "");
        }

        public static Result Syncback(string rojoProjectPath, string? umapPath, string? rbxlPath)
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
            var world = umapPath != null ? World.FromOverdare(umapPath) : World.Open();
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

                // Manually modify BrickColor properties to correct BrickColor.. because some BrickColors are serialized incorrectly (Roblox-File-Format issue)
                foreach ((string key, RobloxFiles.Property prop) in robloxSource.Properties)
                {
                    if (prop.Type is RobloxFiles.PropertyType.BrickColor)
                    {
                        prop.Value = RobloxFiles.DataTypes.BrickColor.Red();
                        Log.Debug($"Value written: {prop.Value}");
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
            world.Save();

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

            return Result.Ok();
        }

        public static Result Build(string rojoProjectPath, string umapPath, string? rbxlPath)
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

            var robloxDataModel = RobloxFiles.BinaryRobloxFile.Open(robloxPlaceFilePath);
            Dictionary<int, Overdare.UScriptClass.LuaInstance> luaInstancesFromOverdareReference =
            [];
            foreach (var luaInstance in world.Map.LuaDataModel.GetDescendants())
            {
                Log.Debug($"Resetting {luaInstance.GetFullName()}");
                // Reset everyone's parents
                //luaInstance.Parent = null;
                // Skip non-NotCreatable LuaInstances
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
                        // 오버데어에 없는데 로블록스에 있는 경우 -> 경고
                        Log.Warning(
                            $"Sourcemap child {source.Name}({source.ClassName}) not found in Overdare LuaInstance {ovdrParent.GetFullName()}. Please re-build the project."
                        );
                        return;
                    }
                }
                // 로블록스에도 있고 오버데어에도 있는 경우 -> 스크립트 내용 동기화
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
            //void VisitOverdareInstance(Overdare.UScriptClass.LuaInstance source, SourcemapChild sourcemapParent)
            //{
            //    SourcemapChild? sourcemapChild = null;
            //    // Default named
            //    if (source.Name == null)
            //    {
            //        sourcemapChild = sourcemapParent.Children?.FirstOrDefault(c => c.Name == RemoveLuaPrefix(source.ClassName));
            //    }
            //    if (sourcemapChild == null)
            //    {
            //        // Custom named
            //        sourcemapChild = sourcemapParent.Children?.FirstOrDefault(c => c.Name == source.Name);
            //        if (sourcemapChild == null)
            //        {
            //            // 로블록스에 없는데 오버데어에 있는 경우
            //            Log.Information($"Should add {source.ClassName} in {sourcemapParent.FilePaths?[0]}");
            //        }
            //    }
            //    // 로블록스에도 있고 오버데어에도 있는 경우

            //}

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
                    //Log.Debug($"Visiting Roblox Instance: {instance.GetFullName()}");
                    VisitRobloxSourcemap(child, world.Map.LuaDataModel);
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
