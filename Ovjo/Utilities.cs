﻿using FluentResults;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Ovjo
{
    internal static class UtilityFunctions
    {
        public static T? TryCreateInstance<T>(string className) where T : class
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

        public static string RemoveBom(string p)
        {
            string BOMMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());
            if (p.StartsWith(BOMMarkUtf8, StringComparison.Ordinal))
                p = p.Remove(0, BOMMarkUtf8.Length);
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
                Process process = StartProcess(program, args);
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    return Result.Fail(_("Execution of `{0} {1}` failed. {2} requires `{0}` to function properly with the provided arguments: `{1}`.", program, args, AppName))
                        .WithReason(new Error(process.StandardError.ReadToEnd()));
                }
                return Result.Ok();
            }
            catch (Exception e)
            {
                return Result.Fail(_("Unable to execute `{0}`. It appears that `{0}` is not installed or is inaccessible. {1} requires `{0}` to function properly.", program, AppName))
                    .WithReason(new Error(e.Message));
            }
        }
    }

    internal class Defer : IDisposable
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
}
