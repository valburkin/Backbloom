namespace Overglow.App;

/// <summary>A random id per data directory, reported as <c>sysinfo.overglow.id</c>.</summary>
internal static class InstanceId
{
    private const string FileName = "instance-id";

    public static string LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id))
        {
            return id.ToString();
        }

        var created = Guid.NewGuid().ToString();
        File.WriteAllText(path, created);
        return created;
    }
}
