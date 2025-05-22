using FluentResults;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace Ovjo
{
    internal class Program
    {
        private const string _defaultRojoProjectPath = "default.project.json";
        const string WORLD_DATA_NAME = "WorldData";
        const string WORLD_DATA_MAP_NAME = "Map";
        const string WORLD_DATA_PLAIN_FILES_NAME = "PlainFiles";
        const string WORLD_DATA_JSON_FILES_NAME = "JsonFiles";
        public const string AppName = "ovjo";

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

        private static async Task<int> Main(string[] args)
        {
            // Setup Result's logger
            ResultLogger resultLogger = new();
            Result.Setup(cfg =>
            {
                cfg.Logger = resultLogger;
            });

            // Setup CLI Commands
            Command syncbackCommand = new("syncback", _("Performs 'syncback' for the provided project, using the `input` file given"));
            {
                var projectArg = new Argument<string>("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                var inputOpt = new Option<string>(["--input", "-i"], _("Path to the input file"));
                var rbxlOpt = new Option<string?>("--rbxl", _("Path to the rbxl file"));

                syncbackCommand.AddArgument(projectArg);
                syncbackCommand.AddOption(inputOpt);
                syncbackCommand.AddOption(rbxlOpt);

                syncbackCommand.SetHandler((project, input, rbxl) =>
                {
                    ExpectResult(Syncback(project, input, rbxl));
                }, projectArg, inputOpt, rbxlOpt);
            }

            Command buildCommand = new("build", _("Builds rojo project into OVERDARE world"));
            {
                var projectArg = new Argument<string>("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                var outputOpt = new Option<string>(["--output", "-o"], _("Path to the output file"));

                buildCommand.AddArgument(projectArg);
                buildCommand.AddOption(outputOpt);

                buildCommand.SetHandler((project, output) =>
                {
                    Console.WriteLine($"Hello, {project}!");
                }, projectArg, outputOpt);
            }

            Command devCommand = new("dev", _("Starts developing rojo project"));
            {
                var projectArg = new Argument<string>("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                var outputOpt = new Option<string?>(["--output", "-o"], _("Path to the output file"));

                devCommand.AddArgument(projectArg);
                devCommand.AddOption(outputOpt);

                devCommand.SetHandler((project, output) =>
                {
                    Console.WriteLine($"Hello, {project}!");
                }, projectArg, outputOpt);
            }

            Command initCommand = new("init", _("Initializes a new rojo project"));
            {
                initCommand.SetHandler(() =>
                {
                    var sandboxMetadata = ExpectResult(FindSandboxMetadata());
                    string umapPath = sandboxMetadata.GetDefaultUMapPath();

                    string currentDirectoryPath = Directory.GetCurrentDirectory();
                    string directoryName = Path.GetFileName(currentDirectoryPath);
                    File.WriteAllText(_defaultRojoProjectPath, JsonConvert.SerializeObject(CreateDefaultRojoProject(directoryName), Formatting.Indented));

                    ExpectResult(Syncback(_defaultRojoProjectPath, umapPath, null));
                });
            }

            Command studioCommand = new("studio", _("Opens OVERDARE Studio"));
            {
                studioCommand.SetHandler(() =>
                {
                    var metadataResult = FindSandboxMetadata();
                    if (metadataResult.IsFailed)
                    {
                        ExpectResult(Result.Fail(_("Failed to find OVERDARE Studio metadata in the computer via Epic Games Launcher.")).WithReasons(metadataResult.Errors));
                        return;
                    }
                    UtilityFunctions.StartProcess(metadataResult.Value.ProgramPath);
                });
            }

            Option<int> verboseOption = new(["--verbose", "-v"], _("Sets the verbosity level (e.g., -v 2, --verbose 3)"))
            {
                ArgumentHelpName = "level"
            };
            RootCommand rootCommand = new(_("Enables professional-grade development tools for OVERDARE developers"));
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
                    .WriteTo.Console(outputTemplate: $"[{{Timestamp:HH:mm:ss}} {{Level:u3}} {AppName}] {{Message:lj}}{{NewLine}}{{Exception}}", standardErrorFromLevel: Serilog.Events.LogEventLevel.Warning)
                    .CreateLogger();

                // Check rojo is ok and warn if not
                UtilityFunctions.RequireProgram("rojo", "syncback --help").LogIfFailed(LogLevel.Warning);

                await next(context);
            });
            commandLineBuilder.UseDefaults();
            var parser = commandLineBuilder.Build();

            return await parser.InvokeAsync(args);
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

        // TODO: Abstract this into WorldData class
        private static Result<string> GetWorldDataPath(JObject rojoProject)
        {
            var path = rojoProject["tree"]?[WORLD_DATA_NAME]?["$path"]?.ToString();
            if (path is string validPath)
            {
                return validPath;
            }
            return Result.Fail(_("Couldn't find `tree.{0}[\"$path\"]` in project.json. This is required in ovjo.", WORLD_DATA_NAME));
        }
    }
}
