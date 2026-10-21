using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Backbloom.App;
using Backbloom.App.Logging;
using Backbloom.App.Updates;
using Backbloom.Engine.Diagnostics;
using Backbloom.Platform.Windows.Desktop;
using Backbloom.Server;
using Backbloom.Shell;
using Velopack;

// Velopack's install/update/uninstall hooks (they end the process) and a pending update; installed copies only.
// Main stays synchronous so that this is the first call of the entry point, as Velopack requires.
if (OperatingSystem.IsWindows() && InstallerHooks.IsInstalledCopy)
{
    VelopackApp.Build().OnBeforeUninstallFastCallback(InstallerHooks.BeforeUninstall).Run();
}

return RunAsync(args).GetAwaiter().GetResult();

static async Task<int> RunAsync(string[] args)
{
    var hasConsole = !OperatingSystem.IsWindows() || ParentConsole.Attach();
    var (commandLine, error) = CommandLine.Parse(args);
    if (commandLine is null)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine();
        Console.Error.WriteLine(CommandLine.Usage);
        return 2;
    }

    switch (commandLine.Mode)
    {
        case LaunchMode.Help:
            Console.WriteLine(CommandLine.Usage);
            return 0;
        case LaunchMode.Version:
            var (version, build) = BackbloomHost.ProductVersion();
            Console.Write(CommandLine.VersionText(version, build));
            return 0;
        case LaunchMode.Import:
            return BackbloomHost.Import(commandLine.ImportFile!, [.. commandLine.ConfigurationArgs]);
    }

    var configuration = new ConfigurationBuilder().AddEnvironmentVariables("BACKBLOOM_").AddCommandLine([.. commandLine.ConfigurationArgs]).Build();
    var options = AppOptions.FromConfiguration(configuration);

    if (commandLine.Mode == LaunchMode.Quit)
    {
        return await Launcher.QuitRunningAsync(options.DataDirectory);
    }

    using var instance = SingleInstance.TryAcquire(options.DataDirectory);
    if (instance is null)
    {
        return await Launcher.ActivateRunningAsync(options.DataDirectory);
    }

    var launch = new LaunchOptions(
        Desktop: !commandLine.Service && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763),
        OpenWindow: !commandLine.NoWindow,
        ConsoleLogging: hasConsole);

    WebApplication? app = null;

    // taken once the app runs, so a crash never resolves services (its samples stay readable after it is disposed)
    PerformanceRecorder? recorder = null;

    // Errors that would otherwise end the process without a word go to the log, the console and a message box, with
    // the performance of the last minutes.
    int Fail(string summary, Exception exception)
    {
        var log = CrashReport.Write(options.DataDirectory, summary, exception, RecentPerformance());
        if (hasConsole)
        {
            Console.Error.WriteLine($"{summary}: {exception}");
        }

        if (launch.Desktop && OperatingSystem.IsWindows())
        {
            var details = log is null ? string.Empty : $"\n\nDetails are in {log}";
            ErrorDialog.Show("Backbloom", $"{summary}:\n\n{exception.Message}{details}");
        }

        return 1;
    }

    IReadOnlyList<string>? RecentPerformance()
    {
        try
        {
            return recorder?.Recent();
        }
        catch (Exception)
        {
            // out of memory, most likely: the entry matters more
            return null;
        }
    }

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        if (e.ExceptionObject is Exception exception)
        {
            _ = Fail("Backbloom stopped after an unexpected error", exception);
        }
    };

    try
    {
        app = BackbloomHost.Build([.. commandLine.ConfigurationArgs], null, launch);
        if (hasConsole)
        {
            app.Services.GetRequiredService<GracefulShutdown>().HandleConsoleSignals();
        }

        await app.StartAsync();
        recorder = app.Services.GetRequiredService<PerformanceRecorder>();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        try
        {
            // writes what the log still has queued and releases the data directory
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            // the failed start is what gets reported
        }

        return Fail("Backbloom couldn't start", ex);
    }

    await using var running = app;
    var url = AppUrls.Ui(options);
    instance.Listen(request => Launcher.Answer(app, request, url));
    var server = app.Services.GetRequiredService<ServerOptions>();
    var startup = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Backbloom");
    var ui = server.UiDirectory ?? "not found (build ui/ or pass --ui-dir)";
    StartupLog.Started(startup, server.Version, options.DataDirectory, ui, url.AbsoluteUri);
    try
    {
        await app.WaitForShutdownAsync();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        StartupLog.StopFailed(startup, ex);
        return 1;
    }

    StartupLog.Stopped(startup);
    return 0;
}
