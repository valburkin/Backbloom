using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.App;

var app = OverglowHost.Build(args);
var options = app.Services.GetRequiredService<AppOptions>();
var startup = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Overglow");
var ui = options.UiDirectory ?? "not found (build ui/ or pass --ui-dir)";
var url = $"http://{options.BindAddress}:{options.Port}";
StartupLog.Started(startup, options.DataDirectory, ui, url);

await app.RunAsync();
