using System.Diagnostics;
using System.Reflection;
using System.Text;
using FluentResults;
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

        public static bool IsGitRepository(string path)
        {
            var dir = new DirectoryInfo(path);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return true;
                dir = dir.Parent;
            }
            return false;
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
                    return Result.Fail(
                        _(
                            "Rojo project file does not exist at the specified path. Tried paths {0} and {1}",
                            triedPath,
                            path
                        )
                    );
            }
            return Result.Ok(path);
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
