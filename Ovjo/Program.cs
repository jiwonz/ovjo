using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using FluentResults;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    internal static class Program
    {
        private const string _defaultRojoProjectPath = "default.project.json";
        public const string AppName = "ovjo";

        private class ResultLogger : IResultLogger
        {
            private static LogEventLevel GetLogEventLevel(LogLevel logLevel) =>
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
                Serilog.Log.Write(
                    GetLogEventLevel(logLevel),
                    FormatMessage(content, context, result, logLevel)
                );
            }

            public void Log<TContext>(string content, ResultBase result, LogLevel logLevel)
            {
                var contextName = typeof(TContext).FullName ?? typeof(TContext).Name;
                Serilog.Log.Write(
                    GetLogEventLevel(logLevel),
                    FormatMessage(content, contextName, result, logLevel)
                );
            }

            private static string FormatMessage(
                string content,
                string context,
                ResultBase result,
                LogLevel level
            )
            {
                var mainReason = result.Reasons.FirstOrDefault();
                if (mainReason == null)
                {
                    return string.Empty;
                }
                var reasonLines = result.Reasons.Skip(1).Select(reason => $"  - {reason.Message}");

                var reasonBlock = string.Join(Environment.NewLine, reasonLines);

                return result.Reasons.Count > 1
                    ? $"""
                        {mainReason.Message}
                        Reasons ({result.Reasons.Count - 1}):
                        {reasonBlock}
                        """
                    : $"""
                        {mainReason.Message}
                        """;
            }
        }

        private static Dictionary<string, object> CreateDefaultRojoProject(string name)
        {
            return new()
            {
                ["name"] = name,
                ["tree"] = new Dictionary<string, object> { ["$className"] = "DataModel" },
            };
        }

        private static async Task<int> Main(string[] args)
        {
            // Setup Result's logger
            ResultLogger resultLogger = new();
            Result.Setup(cfg => cfg.Logger = resultLogger);

            // Setup CLI Commands
            Command syncbackCommand = new(
                "syncback",
                _("Performs 'syncback' for the provided project, using the `input` file given")
            );
            {
                Argument<string> projectArg = new("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                Option<string> inputOpt = new(["--input", "-i"], _("Path to the input file"));
                Option<string?> rbxlOpt = new("--rbxl", _("Path to the rbxl file"));

                syncbackCommand.AddArgument(projectArg);
                syncbackCommand.AddOption(inputOpt);
                syncbackCommand.AddOption(rbxlOpt);

                syncbackCommand.SetHandler(
                    (project, input, rbxl) => ExpectResult(LibOvjo.Syncback(project, input, rbxl)),
                    projectArg,
                    inputOpt,
                    rbxlOpt
                );
            }

            Command buildCommand = new("build", _("Builds the project into OVERDARE world"));
            {
                Argument<string> projectArg = new("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                Option<string> outputOpt = new(["--output", "-o"], _("Path to the output file"));
                Option<string?> rbxlOpt = new("--rbxl", _("Path to the rbxl file"));

                buildCommand.AddArgument(projectArg);
                buildCommand.AddOption(outputOpt);
                buildCommand.AddOption(rbxlOpt);

                buildCommand.SetHandler(
                    (project, output, rbxl) => ExpectResult(LibOvjo.Build(project, output, rbxl)),
                    projectArg,
                    outputOpt,
                    rbxlOpt
                );
            }

            Command syncCommand = new(
                "sync",
                _("Synchronizes Lua sources between the project and the input OVERDARE world")
            );
            {
                Argument<string> projectArg = new("project", _("Path to the project"));
                projectArg.SetDefaultValue(_defaultRojoProjectPath);
                Option<string> inputOpt = new(["--input", "-i"], _("Path to the input file"));
                Option<bool> watchOpt = new(["--watch", "-w"], _("Watches source files"));

                syncCommand.AddArgument(projectArg);
                syncCommand.AddOption(inputOpt);
                syncCommand.AddOption(watchOpt);

                syncCommand.SetHandler(
                    (project, input, watch) => ExpectResult(LibOvjo.Sync(project, input, watch)),
                    projectArg,
                    inputOpt,
                    watchOpt
                );
            }

            Command initCommand = new(
                "init",
                _("Initializes a new ovjo project from OVERDARE Studio")
            );
            {
                initCommand.SetHandler(() =>
                {
                    var sandboxMetadata = ExpectResult(UtilityFunctions.TryFindSandboxMetadata());
                    string umapPath = sandboxMetadata.GetDefaultUMapPath();

                    string currentDirectoryPath = Directory.GetCurrentDirectory();
                    string directoryName = Path.GetFileName(currentDirectoryPath);
                    File.WriteAllText(
                        _defaultRojoProjectPath,
                        JsonConvert.SerializeObject(
                            CreateDefaultRojoProject(directoryName),
                            Formatting.Indented
                        )
                    );

                    ExpectResult(LibOvjo.Syncback(_defaultRojoProjectPath, umapPath, null));
                });
            }

            Command studioCommand = new("studio", _("Opens OVERDARE Studio"));
            {
                studioCommand.SetHandler(() =>
                {
                    var metadataResult = UtilityFunctions.TryFindSandboxMetadata();
                    if (metadataResult.IsFailed)
                    {
                        ExpectResult(
                            Result
                                .Fail(
                                    _(
                                        "Failed to find OVERDARE Studio metadata in the computer via Epic Games Launcher."
                                    )
                                )
                                .WithReasons(metadataResult.Errors)
                        );
                        return;
                    }
                    UtilityFunctions.StartProcess(metadataResult.Value.ProgramPath);
                });
            }

            Option<int> verboseOption = new(
                ["--verbose", "-v"],
                _("Sets the verbosity level (e.g., -v 2, --verbose 3)")
            )
            {
                ArgumentHelpName = "level",
            };
            RootCommand rootCommand = new(
                _("Enables professional-grade development tools for OVERDARE developers")
            );
            rootCommand.AddGlobalOption(verboseOption);
            rootCommand.AddCommand(syncCommand);
            rootCommand.AddCommand(initCommand);
            rootCommand.AddCommand(syncbackCommand);
            rootCommand.AddCommand(studioCommand);
            rootCommand.AddCommand(buildCommand);

            CommandLineBuilder commandLineBuilder = new(rootCommand);
            commandLineBuilder.AddMiddleware(
                async (context, next) =>
                {
                    // Setup logger with logger verbosity level
                    var verbosity = context.ParseResult.GetValueForOption(verboseOption);
                    LogEventLevel minimumLevel = verbosity switch
                    {
                        >= 3 => LogEventLevel.Verbose,
                        2 => LogEventLevel.Debug,
                        1 => LogEventLevel.Information,
                        _ => LogEventLevel.Warning,
                    };
                    Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.Is(minimumLevel)
                        .WriteTo.Console(
                            outputTemplate: $"[{{Timestamp:HH:mm:ss}} {{Level:u3}} {AppName}] {{Message:lj}}{{NewLine}}{{Exception}}",
                            standardErrorFromLevel: Serilog.Events.LogEventLevel.Warning
                        )
                        .CreateLogger();

                    // Check rojo is ok and warn if not
                    UtilityFunctions
                        .RequireProgram("rojo", "syncback --help")
                        .LogIfFailed(LogLevel.Warning);

                    await next(context);
                }
            );
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
    }
}
