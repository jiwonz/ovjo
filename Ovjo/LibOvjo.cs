using FluentResults;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    public static class LibOvjo
    {
        private const string _overdareUObjectTypeLuaPrefix = "Lua";
        private const string _overdareReferenceAttributeName = "ovdr_ref";

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
                var sourceClassNameWithoutLuaPrefix = Regex.Replace(
                    source.ClassName,
                    $"^{Regex.Escape(_overdareUObjectTypeLuaPrefix)}",
                    ""
                );
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
            Process process = UtilityFunctions.StartProcess(
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

        public static Result Sync(string rojoProjectPath, string umapPath, bool watch)
        {
            {
                Result rojoStatus = UtilityFunctions.RequireProgram("rojo", "build --help");
                if (rojoStatus.IsFailed)
                {
                    return Result
                        .Fail(
                            _(
                                "`rojo build` is required to perform `LibOvjo.Sync`, but failed to check."
                            )
                        )
                        .WithReasons(rojoStatus.Errors);
                }
            }

            // Check which items have been added or removed to OVERDARE world from Roblox place file
            // Removed means that the Roblox instance has been added to the Roblox place file
            // Added things besides the BaseLuaScript and LuaFolder is always invalid
            void Diff(
                Overdare.UScriptClass.LuaInstance ovdrInstance,
                RobloxFiles.Instance robloxInstance
            )
            {
                if (ovdrInstance.SavedActor == null)
                    throw new UnreachableException();
                // Skips NotBrowsable LuaInstances
                if (
                    (ovdrInstance.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotBrowsable)
                    != 0
                )
                {
                    return;
                }

                Dictionary<string, Overdare.UScriptClass.LuaInstance> oldChildren = [];
                foreach (var ovdrChild in ovdrInstance.GetChildren())
                {
                    if (ovdrChild.SavedActor == null)
                        continue;
                    if ((ovdrChild.ClassTagFlags & Overdare.UScriptClass.ClassTagFlags.NotCreatable) != 0) continue;
                    string key = ovdrChild.Name ?? ovdrChild switch
                    {
                        Overdare.UScriptClass.LuaLocalScript => "LocalScript",
                        Overdare.UScriptClass.LuaScript => "Script",
                        Overdare.UScriptClass.LuaModuleScript => "ModuleScript",
                        Overdare.UScriptClass.LuaFolder => "Folder",
                        _ => throw new NotSupportedException(
                            _(
                                "LuaInstance({0}) is not supported to be converted to Roblox Instance.",
                                ovdrChild.ClassName
                            )
                        ),
                    };
                    //if (key == null)
                    //{
                    //    key = robloxInstance
                    //        .GetChildren()
                    //        .FirstOrDefault(rbxChild =>
                    //            rbxChild.GetAttribute(_overdareReferenceAttributeName, out int? ovdrRef) &&
                    //            ovdrRef != null &&
                    //            ovdrRef.Value == ovdrChild.SavedActor.ExportIndex
                    //        )?.Name;
                    //}
                    //if (key == null)
                    //    continue; // Skip if you can't get a key

                    oldChildren[key] = ovdrChild;
                }
                //var oldChildren = ovdrInstance
                //    .GetChildren()
                //    .ToDictionary(ovdrChild =>
                //    {
                //        if (ovdrChild.SavedActor == null)
                //            throw new UnreachableException();
                //        if (ovdrChild.Name != null)
                //            return ovdrChild.Name;
                //        // This is a default named LuaInstance, so we need to find the name from the Roblox Instance with the same ExportIndex
                //        // Also this process is needed for diffing between Roblox Instance and LuaInstance
                //        foreach (var rbxChild in robloxInstance.GetChildren())
                //        {
                //            if (
                //                rbxChild.GetAttribute(
                //                    _overdareReferenceAttributeName,
                //                    out int? ovdrRef
                //                ) && ovdrRef != null
                //                && ovdrRef.Value == ovdrChild.SavedActor.ExportIndex
                //            )
                //            {
                //                return rbxChild.Name;
                //            }
                //        }
                //        throw new UnreachableException(
                //            _(
                //                "Default named LuaInstance({0}, ExportIndex: {1}) does not have a Roblox Instance with the same ExportIndex.",
                //                ovdrChild.GetFullName(),
                //                ovdrChild.SavedActor.ExportIndex
                //            )
                //        );
                //    });
                Log.Debug(robloxInstance.GetFullName());
                Dictionary<string, RobloxFiles.Instance> newChildren = []; // Replace RobloxInstanceType with your actual type
                foreach (var c in robloxInstance.GetChildren())
                {
                    if (c is RobloxFiles.Script || c is RobloxFiles.ModuleScript || c is RobloxFiles.ModuleScript)
                    {
                        newChildren[c.Name] = c;
                    }
                }

                foreach (var name in newChildren.Keys.Except(oldChildren.Keys))
                    Console.WriteLine($"Added: {name}");

                foreach (var name in oldChildren.Keys.Except(newChildren.Keys))
                    Console.WriteLine($"Removed: {name}");

                foreach (var name in oldChildren.Keys.Intersect(newChildren.Keys))
                    Diff(oldChildren[name], newChildren[name]);
            }

            var world = World.FromOverdare(umapPath);
            string robloxPlaceFilePath = Path.ChangeExtension(Path.GetTempFileName(), "rbxl");
            Process process = UtilityFunctions.StartProcess(
                "rojo",
                $"build {rojoProjectPath} --output {robloxPlaceFilePath}"
            );
            process.WaitForExit();
            using var cleanup = new Defer(() =>
            {
                if (!File.Exists(robloxPlaceFilePath))
                    return;
                File.Delete(robloxPlaceFilePath);
            });
            var robloxDataModel = RobloxFiles.BinaryRobloxFile.Open(robloxPlaceFilePath);
            Diff(world.Map.LuaDataModel, robloxDataModel);

            return Result.Ok();
        }
    }
}
