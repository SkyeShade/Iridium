using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: MediaBuildVerifier <absolute-assembly-path>");
    return 2;
}

try
{
    var context = new AssemblyLoadContext("IridiumMediaBuildVerifier", isCollectible: true);
    var assembly = context.LoadFromAssemblyPath(args[0]);
    var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(value => value.Key == "IridiumMediaBuildId")
        .Select(value => value.Value)
        .ToArray();
    if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]))
    {
        Console.Error.WriteLine($"Expected exactly one non-empty IridiumMediaBuildId metadata value; found {values.Length}.");
        return 3;
    }

    var buildId = values[0]!;
    Console.WriteLine(buildId);
    context.Unload();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Could not inspect assembly metadata: {exception.GetType().Name}: {exception.Message}");
    return 4;
}
