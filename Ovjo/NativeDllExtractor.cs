using System.Reflection;

public static class NativeDllExtractor
{
    public static void Extract(string dllResourceName, string dllName)
    {
        string extractPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);
        if (File.Exists(extractPath))
            return;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream =
            assembly.GetManifestResourceStream(dllResourceName)
            ?? throw new Exception($"Could not find resource: {dllResourceName}");
        using var fileStream = new FileStream(extractPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fileStream);
    }
}
