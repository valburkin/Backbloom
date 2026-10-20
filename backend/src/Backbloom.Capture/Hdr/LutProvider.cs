using Microsoft.Extensions.Logging;

namespace Backbloom.Capture.Hdr;

/// <summary>
/// Where the LUT is looked for and where the default one is written. HyperHDR's policy: the user data directory first,
/// then the application directory; the generated default goes to the data directory.
/// </summary>
public sealed record LutLocations(IReadOnlyList<string> SearchDirectories, string CreateDirectory)
{
    public static LutLocations Default(string dataDirectory) => new([dataDirectory, AppContext.BaseDirectory], dataDirectory);

    public IEnumerable<string> Files => SearchDirectories.Select(d => Path.Combine(d, LutLoader.FileName));
}

/// <summary>
/// Supplies the tone mapping table to capture code without blocking it: the first <see cref="TryGet"/> starts loading
/// in the background (or creating and saving the default table when no file exists, as Grabber::pleaseWaitForLut did),
/// and returns null until the table is ready. Loaded once per process.
/// </summary>
public sealed partial class LutProvider(LutLocations locations, ILogger<LutProvider> logger)
{
    private readonly Lock _lock = new();
    private volatile ToneMappingLut? _lut;
    private Task? _loading;

    /// <summary>The table, or null while it is still loading (or when it could not be loaded or created).</summary>
    public ToneMappingLut? TryGet()
    {
        var lut = _lut;
        if (lut is not null)
        {
            return lut;
        }

        lock (_lock)
        {
            _loading ??= Task.Run(Load);
        }

        return _lut;
    }

    /// <summary>Loads the table synchronously (tests, tools).</summary>
    public ToneMappingLut? Get()
    {
        TryGet();
        Task? loading;
        lock (_lock)
        {
            loading = _loading;
        }

        loading?.GetAwaiter().GetResult();
        return _lut;
    }

    private void Load()
    {
        try
        {
            var lut = LutLoader.Load(locations.Files, logger);
            if (lut is null)
            {
                LogCreating(logger, locations.CreateDirectory);
                var table = DefaultLut.Create();
                lut = new ToneMappingLut(table, "built-in default");
                try
                {
                    var path = DefaultLut.CreateFile(locations.CreateDirectory, table);
                    LogCreated(logger, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogSaveFailed(logger, ex, locations.CreateDirectory);
                }
            }

            _lut = lut;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No LUT file found; creating the default HDR tone mapping table in {Directory}")]
    private static partial void LogCreating(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "The default LUT has been created: {Path}")]
    private static partial void LogCreated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the default LUT in {Directory}; it is used from memory")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not load or create the HDR tone mapping table")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
