using RobloxFiles;
using System.CommandLine;
using System.Reflection;

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

Part? part = CreateInstance<Part>("Part");
Console.WriteLine(part);

var greetCommand = new Command("greet", "Greets a person");

var nameArg = new Argument<string>("name", "Name of the person to greet");
greetCommand.AddArgument(nameArg);

greetCommand.SetHandler((string name) =>
{
    Console.WriteLine($"Hello, {name}!");
}, nameArg);

var rootCommand = new RootCommand("My CLI");
rootCommand.AddCommand(greetCommand);

return await rootCommand.InvokeAsync(args);
