using RobloxFiles;
using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

const string SANDBOX_APP_NAME = "20687893280c48c787633578d3e0ca2e";
const string ERROR_PREFIX = "[bold red]error[/]:";

var stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
{
    Out = new AnsiConsoleOutput(Console.Error)
});

T? CreateInstance<T>(string className) where T : class
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

Process StartProcess(string command, string args = "")
{
    Process p = new Process();
    p.StartInfo = new ProcessStartInfo(command, args);
    p.StartInfo.RedirectStandardOutput = false;
    p.StartInfo.RedirectStandardError = true;
    p.StartInfo.UseShellExecute = false;
    p.StartInfo.CreateNoWindow = true;
    p.Start();

    return p;
}

void OpenSandbox()
{
    string programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    string manifestsPath = Path.Combine(programDataPath, "Epic", "EpicGamesLauncher", "Data", "Manifests");

    if (!Directory.Exists(manifestsPath))
    {
        stderrConsole.MarkupLine($"{ERROR_PREFIX} Manifest folder does not exist.");
        Environment.Exit(1);
    }

    string[] itemFiles = Directory.GetFiles(manifestsPath, "*.item");

    foreach (string file in itemFiles)
    {
        Console.WriteLine($"Reading file: {file}");

        string content = File.ReadAllText(file);

        var manifest = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
        if (manifest == null || manifest["AppName"].ToString() != SANDBOX_APP_NAME)
        {
            continue;
        }
        Console.WriteLine($"found OVERDARE Sandbox! {content}");
        string? installLocation = manifest["InstallLocation"].ToString();
        if (installLocation == null)
        {
            stderrConsole.MarkupLine($"{ERROR_PREFIX} Install location not found in manifest.");
            Environment.Exit(1);
        }
        string? launchExecutable = manifest["LaunchExecutable"].ToString();
        if (launchExecutable == null)
        {
            stderrConsole.MarkupLine($"{ERROR_PREFIX} Launch executable not found in manifest.");
            Environment.Exit(1);
        }
        string programPath = Path.Combine(installLocation, launchExecutable);
        if (!File.Exists(programPath))
        {
            stderrConsole.MarkupLine($"{ERROR_PREFIX} Launch executable not found.");
            Environment.Exit(1);
        }
        StartProcess(programPath);
    }
}

void BuildProject()
{
}

void Syncback()
{
}

Part? part = CreateInstance<Part>("Part");
Console.WriteLine(part);

var editCommand = new Command("edit", "Edits rojo project");

var projectOpt = new Option<String>("project", "Path to the project");
editCommand.AddOption(projectOpt);

editCommand.SetHandler((string project) =>
{
    project = project ?? "default.project.json";
    Console.WriteLine($"Hello, {project}!");
}, projectOpt);

var rootCommand = new RootCommand("ovjo");
rootCommand.AddCommand(editCommand);

OpenSandbox();

stderrConsole.MarkupLine($"{ERROR_PREFIX} bruh!");
Environment.Exit(1);

return await rootCommand.InvokeAsync(args);
