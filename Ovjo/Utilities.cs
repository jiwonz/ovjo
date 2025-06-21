using System.Diagnostics;
using System.Reflection;
using System.Text;
using FluentResults;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    internal static class UtilityFunctions
    {
        public static T? TryCreateInstance<T>(string className)
            where T : class
        {
            string fullClassName = $"RobloxFiles.{className}";

            Type? foundType = AppDomain
                .CurrentDomain.GetAssemblies()
                .SelectMany(asm =>
                {
                    try
                    {
                        return asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        return ex.Types.Where(t => t != null)!;
                    }
                    catch
                    {
                        return [];
                    }
                })
                .FirstOrDefault(t => t?.FullName == fullClassName);

            if (foundType != null && typeof(T).IsAssignableFrom(foundType))
            {
                return Activator.CreateInstance(foundType) as T;
            }

            return null;
        }

        public static RobloxFiles.Instance? TryCreateInstance(string className)
        {
            return TryCreateInstance<RobloxFiles.Instance>(className);
        }

        public static string RemoveBom(string p)
        {
            string BOMMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());
            if (p.StartsWith(BOMMarkUtf8, StringComparison.Ordinal))
                p = p[BOMMarkUtf8.Length..];
            return p.Replace("\0", "");
        }

        public static Process StartProcess(string command, string args = "")
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

        public static Result RequireProgram(string program, string args)
        {
            try
            {
                var process = StartProcess(program, args);
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    return Result
                        .Fail(
                            _(
                                "Execution of `{0} {1}` failed. {2} requires `{0}` to function properly with the provided arguments: `{1}`.",
                                program,
                                args,
                                Program.AppName
                            )
                        )
                        .WithReason(new Error(process.StandardError.ReadToEnd()));
                }
                return Result.Ok();
            }
            catch (Exception e)
            {
                return Result
                    .Fail(
                        _(
                            "Unable to execute `{0}`. It appears that `{0}` is not installed or is inaccessible. {1} requires `{0}` to function properly.",
                            program,
                            Program.AppName
                        )
                    )
                    .WithReason(new Error(e.Message));
            }
        }

        public static Result<Overdare.SandboxMetadata> TryFindSandboxMetadata()
        {
            return Result.Try(Overdare.SandboxMetadata.FromEpicGamesLauncher);
        }

        public static Result<string> ResolveRojoProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Result.Fail(_("Rojo project path cannot be null or empty."));
            }
            if (!File.Exists(path))
            {
                var triedPath = path;
                path += ".project.json";
                if (!File.Exists(path))
                {
                    return Result.Fail(
                        _(
                            "Rojo project file does not exist at the specified path. Tried paths {0} and {1}",
                            triedPath,
                            path
                        )
                    );
                }
            }
            return Result.Ok(path);
        }

        public static Result<string> ResolveOverdareWorldInput(
            string? path,
            string? rojoProjectPath = null
        )
        {
            path ??= string.Empty;
            if (Path.GetExtension(path).Equals(".umap", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Ok(path);
            }

            string projectName = Path.GetFileName(path);
            List<string> matches = [];

            if (!string.IsNullOrWhiteSpace(path))
            {
                string withExtension = path + ".umap";
                if (File.Exists(withExtension))
                {
                    matches.Add(withExtension);
                }

                string asWorldFolder = Path.Combine(path, projectName + ".umap");
                if (File.Exists(asWorldFolder))
                {
                    matches.Add(asWorldFolder);
                }
            }

            if (rojoProjectPath != null)
            {
                var projectJson = JsonConvert.DeserializeObject<JObject>(
                    File.ReadAllText(rojoProjectPath)
                );
                if (projectJson?["name"] is JValue projectNameValue)
                {
                    var value = projectNameValue.Value<string>();
                    if (value != null && !string.IsNullOrWhiteSpace(value))
                    {
                        projectName = value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                string withExtension = projectName + ".umap";
                if (File.Exists(withExtension))
                {
                    matches.Add(withExtension);
                }

                string asWorldFolder = Path.Combine(projectName, projectName + ".umap");
                if (File.Exists(asWorldFolder))
                {
                    matches.Add(asWorldFolder);
                }
            }
            else
            {
                string asWorldFolder = Path.Combine(path, projectName + ".umap");
                if (File.Exists(asWorldFolder))
                {
                    matches.Add(asWorldFolder);
                }
            }

            if (matches.Count > 1)
            {
                return Result.Fail(
                    _(
                        "OVERDARE World file input path is ambiguous. {0} matched.",
                        string.Join(", ", matches)
                    )
                );
            }

            if (matches.Count == 0)
            {
                return Result.Fail(_("OVERDARE World file input path does not match any files."));
            }

            return Result.Ok(matches[0]);
        }

        public static Result<string> ResolveOverdareWorldOutput(
            string? path,
            string? rojoProjectPath = null
        )
        {
            path ??= string.Empty;
            if (Path.GetExtension(path).Equals(".umap", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Ok(path);
            }

            string projectName = Path.GetFileName(path);

            if (!string.IsNullOrWhiteSpace(path))
            {
                string withExtension = path + ".umap";
                if (File.Exists(withExtension))
                {
                    return Result.Ok(withExtension);
                }

                string asWorldFolder = Path.Combine(path, projectName + ".umap");
                if (File.Exists(asWorldFolder))
                {
                    return Result.Ok(asWorldFolder);
                }
            }

            if (rojoProjectPath != null)
            {
                var projectJson = JsonConvert.DeserializeObject<JObject>(
                    File.ReadAllText(rojoProjectPath)
                );
                if (projectJson?["name"] is JValue projectNameValue)
                {
                    var value = projectNameValue.Value<string>();
                    if (value != null && !string.IsNullOrWhiteSpace(value))
                    {
                        projectName = value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                string withExtension = projectName + ".umap";
                if (File.Exists(withExtension))
                {
                    return Result.Ok(withExtension);
                }

                string asWorldFolder = Path.Combine(projectName, projectName + ".umap");
                if (File.Exists(asWorldFolder))
                {
                    return Result.Ok(asWorldFolder);
                }
            }
            else
            {
                string asWorldFolder = Path.Combine(path, projectName + ".umap");
                string? dir = Path.GetDirectoryName(asWorldFolder);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return Result.Ok(asWorldFolder);
            }

            return Result.Fail(_("OVERDARE World file output path does not match any files."));
        }

        public static void SafeDelete(string filePath)
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(filePath))
                {
                    FileSystem.DeleteFile(
                        filePath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin
                    );
                    return;
                }
                if (Directory.Exists(filePath))
                {
                    FileSystem.DeleteDirectory(
                        filePath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin
                    );
                }
            }
            else
            {
                if (File.Exists(filePath))
                {
                    // No recycle bin on Linux/macOS; this will permanently delete
                    File.Delete(filePath);
                    return;
                }
                if (Directory.Exists(filePath))
                    Directory.Delete(filePath, true);
            }
        }
    }

    internal class Defer(Action disposal) : IDisposable
    {
        void IDisposable.Dispose()
        {
            disposal();
        }
    }
}
