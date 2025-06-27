using FluentResults;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using Sharprompt;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using static Ovjo.LocalizationCatalog.Ovjo;

namespace Ovjo
{
    internal static class Program
    {
        private const string _defaultRojoProjectPath = "default.project.json";
        public const string AppName = "ovjo";
        private const string _ovdrForumUrl = "https://forum.overdare.com/";
        private const string _ovdrCreatorUrl = "https://create.overdare.com/";
        private const string _ovdrDocsUrl = "https://docs.overdare.com/";

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
                        {_("Reasons ({0})", result.Reasons.Count - 1)}:
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

        private static Result<string> SelectInputFromGui()
        {
            var result = NativeFileDialogSharp.Dialog.FileOpen(
                "umap",
                Directory.GetCurrentDirectory()
            );
            if (result.IsError)
            {
                return Result
                    .Fail(_("Failed to open the file dialog."))
                    .WithReason(new Error(result.ErrorMessage));
            }
            if (result.IsCancelled)
            {
                return Result
                    .Fail(_("No input file was given. Please provide a valid .umap file."))
                    .WithReason(new Error(_("Aborted by the user.")));
            }
            return Result.Ok(result.Path);
        }

        private static Result<string> TryGetUMapInput(string project, string action)
        {
            string[] messages = [_("Select an input file via the GUI"), _("Abort {0}", action)];
            var choice = Prompt.Select(
                _("No input file was given. Please choose from the following available options"),
                Enumerable.Range(0, messages.Length),
                textSelector: i => messages[i]
            );
            switch (choice)
            {
                case 0:
                    var result = SelectInputFromGui();
                    if (result.IsFailed)
                    {
                        return Result
                            .Fail(_("Failed to open the file dialog."))
                            .WithReasons(result.Errors);
                    }
                    return result.Value;
                default:
                    return Result
                        .Fail(_("No input file was given. Please provide a valid .umap file."))
                        .WithReason(new Error(_("Aborted by the user.")));
            }
        }

        private static Result<(string Input, bool IsResyncbacked)> TryGetUMapInputForSyncback(
            string project
        )
        {
            string[] messages =
            [
                _("Select an input file via the GUI"),
                _(
                    "Perform re-syncback(build the current world and then perform a syncback, but the world will not be updated.)"
                ),
                _("Abort {0}", "syncback"),
            ];
            var choice = Prompt.Select(
                _("No input file was given. Please choose from the following available options"),
                Enumerable.Range(0, messages.Length),
                textSelector: i => messages[i]
            );
            switch (choice)
            {
                case 0:
                    var result = SelectInputFromGui();
                    if (result.IsFailed)
                    {
                        return Result
                            .Fail(_("Failed to open the file dialog."))
                            .WithReasons(result.Errors);
                    }
                    return Result.Ok((result.Value, false));
                case 1:
                    // This fallback is supported because the .ovjowld file is a build artifact of Overdare, which contains the world data in a compressed format.
                    // .ovjowld는 스크립트 소스를 포함하지 않으므로, 해당 .ovjowld를 빌드한 후, 오버데어 월드로 불러와야 합니다.
                    var tempFile = Path.GetTempFileName();
                    File.Delete(tempFile);
                    Directory.CreateDirectory(tempFile);
                    var newUmapPath = Path.ChangeExtension(
                        Path.Combine(tempFile, Path.GetFileNameWithoutExtension(tempFile)),
                        "umap"
                    );
                    ExpectResult(LibOvjo.Build(project, newUmapPath, null));
                    AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                    {
                        if (Directory.Exists(tempFile))
                            Directory.Delete(tempFile, true); // Clean up the temp directory
                    };
                    return Result.Ok((newUmapPath, true));
                default:
                    return Result
                        .Fail(_("No input file was given. Please provide a valid .umap file."))
                        .WithReason(new Error(_("Aborted by the user.")));
            }
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
                Option<string?> inputOpt = new(["--input", "-i"], _("Path to the input file"));
                Option<string?> rbxlOpt = new("--rbxl", _("Path to the rbxl file"));

                syncbackCommand.AddArgument(projectArg);
                syncbackCommand.AddOption(inputOpt);
                syncbackCommand.AddOption(rbxlOpt);

                syncbackCommand.SetHandler(
                    (project, input, rbxl) =>
                    {
                        project = ExpectResult(UtilityFunctions.ResolveRojoProject(project));
                        bool isResyncbacked = false;
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            var inputResult = ExpectResult(TryGetUMapInputForSyncback(project));
                            input = inputResult.Input;
                            isResyncbacked = inputResult.IsResyncbacked;
                        }
                        else
                        {
                            input = ExpectResult(
                                UtilityFunctions.ResolveOverdareWorldInput(input, project)
                            );
                        }
                        ExpectResult(LibOvjo.Syncback(project, input, rbxl, isResyncbacked));
                    },
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
                outputOpt.SetDefaultValue("build");
                Option<string?> rbxlOpt = new("--rbxl", _("Path to the rbxl file"));
                Option<bool> yesOpt = new(
                    ["--yes", "-y"],
                    _("Assumes 'yes' to all prompts, useful for scripting")
                );

                buildCommand.AddArgument(projectArg);
                buildCommand.AddOption(outputOpt);
                buildCommand.AddOption(rbxlOpt);
                buildCommand.AddOption(yesOpt);

                buildCommand.SetHandler(
                    (project, output, rbxl, yes) =>
                    {
                        project = ExpectResult(UtilityFunctions.ResolveRojoProject(project));
                        // TO-DO: Make optional saving file via GUI if not given
                        output = ExpectResult(
                            UtilityFunctions.ResolveOverdareWorldOutput(output, project)
                        );
                        var filesExceptCurrent = Directory
                            .GetFileSystemEntries(
                                Path.GetDirectoryName(output) ?? Directory.GetCurrentDirectory()
                            )
                            .Where(f =>
                                !Path.GetFullPath(f)
                                    .Equals(
                                        Path.GetFullPath(output),
                                        StringComparison.OrdinalIgnoreCase
                                    )
                            )
                            .ToArray();
                        if (filesExceptCurrent.Length > 0)
                        {
                            if (
                                yes
                                || Prompt.Confirm(
                                    _(
                                        "The output directory already contains files other than the current output file. Would you like to recycle those files?"
                                    ),
                                    defaultValue: false
                                )
                            )
                            {
                                foreach (var file in filesExceptCurrent)
                                {
                                    UtilityFunctions.SafeDelete(file);
                                }
                            }
                        }
                        ExpectResult(LibOvjo.Build(project, output, rbxl));
                    },
                    projectArg,
                    outputOpt,
                    rbxlOpt,
                    yesOpt
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
                    (project, input, watch) =>
                    {
                        project = ExpectResult(UtilityFunctions.ResolveRojoProject(project));
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            input = ExpectResult(TryGetUMapInput(project, "sync"));
                        }
                        else
                        {
                            input = ExpectResult(
                                UtilityFunctions.ResolveOverdareWorldInput(input, project)
                            );
                        }
                        ExpectResult(LibOvjo.Sync(project, input, watch));
                    },
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

                    ExpectResult(LibOvjo.Syncback(_defaultRojoProjectPath, umapPath));
                });
            }

            Command ovdrCommand = new("ovdr", _("Useful commands for OVERDARE"));
            {
                Option<bool> dryRun = new(["--dry-run"], _("Prints what will be opened"));
                Command studioCommand = new("studio", _("Opens OVERDARE Studio"));
                studioCommand.AddOption(dryRun);
                Command docsCommand = new("docs", _("Opens OVERDARE documentation in the browser"));
                docsCommand.AddOption(dryRun);
                Command creatorCommand = new(
                    "creator",
                    _("Opens OVERDARE Creator Hub in the browser")
                );
                creatorCommand.AddOption(dryRun);
                Command forumCommand = new("forum", _("Opens OVERDARE Forum in the browser"));
                forumCommand.AddOption(dryRun);

                studioCommand.SetHandler(
                    (dryRun) =>
                    {
                        var metadata = ExpectResult(UtilityFunctions.TryFindSandboxMetadata());
                        if (dryRun)
                        {
                            Console.WriteLine(metadata.ProgramPath);
                            return;
                        }
                        Log.Information($"Opening OVERDARE Studio at {metadata.ProgramPath}");
                        UtilityFunctions.StartProcess(metadata.ProgramPath);
                    },
                    dryRun
                );

                static void OpenUrl(string url)
                {
                    try
                    {
                        Log.Debug($"Opening URL {url}");
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Failed to open URL: {url}");
                    }
                }

                docsCommand.SetHandler(
                    (dryRun) =>
                    {
                        if (dryRun)
                        {
                            Console.WriteLine(_ovdrDocsUrl);
                            return;
                        }
                        Log.Information($"Opening OVERDARE Documentation at {_ovdrDocsUrl}");
                        OpenUrl(_ovdrDocsUrl);
                    },
                    dryRun
                );

                creatorCommand.SetHandler(
                    (dryRun) =>
                    {
                        if (dryRun)
                        {
                            Console.WriteLine(_ovdrCreatorUrl);
                            return;
                        }
                        Log.Information($"Opening OVERDARE Creator at {_ovdrCreatorUrl}");
                        OpenUrl(_ovdrCreatorUrl);
                    },
                    dryRun
                );

                forumCommand.SetHandler(
                    (dryRun) =>
                    {
                        if (dryRun)
                        {
                            Console.WriteLine(_ovdrForumUrl);
                            return;
                        }
                        Log.Information($"Opening OVERDARE Forum at {_ovdrForumUrl}");
                        OpenUrl(_ovdrForumUrl);
                    },
                    dryRun
                );

                ovdrCommand.AddCommand(studioCommand);
                ovdrCommand.AddCommand(docsCommand);
                ovdrCommand.AddCommand(creatorCommand);
                ovdrCommand.AddCommand(forumCommand);
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
            rootCommand.AddCommand(ovdrCommand);
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
