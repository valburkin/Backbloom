using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Overglow.App;

namespace Overglow.Shell.Tests;

/// <summary>
/// Where the server listens: every interface only for an installed copy, loopback for builds and tests, so no
/// development or test run makes Windows Firewall ask (see also Shared/NetworkListenerGuard).
/// </summary>
public sealed class ListenAddressTests
{
    [Fact]
    public void Default_IsLoopback_ExceptForAnInstalledCopy()
    {
        Assert.Equal(IPAddress.Loopback, AppOptions.DefaultBindAddress(AppContext.BaseDirectory));
        Assert.Equal(IPAddress.Loopback, AppOptions.FromConfiguration(new ConfigurationBuilder().Build()).BindAddress);

        var root = Directory.CreateTempSubdirectory("overglow-installed-");
        try
        {
            var current = root.CreateSubdirectory("current");
            File.WriteAllText(Path.Combine(current.FullName, "sq.version"), "<package />");
            File.WriteAllBytes(Path.Combine(root.FullName, "Update.exe"), []);
            Assert.Equal(IPAddress.Any, AppOptions.DefaultBindAddress(current.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("bind", "0.0.0.0")]
    [InlineData("BIND", "192.168.1.20")]
    public void Option_OrEnvironment_Overrides(string key, string value)
    {
        var options = AppOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build());
        Assert.Equal(IPAddress.Parse(value), options.BindAddress);
    }

    [Fact]
    public async Task TestHost_ListensOnLoopbackOnly()
    {
        await using var host = await TestHost.StartAsync();
        var addresses = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.NotEmpty(addresses);
        Assert.All(addresses, a => Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(a).Host.Trim('[', ']'))), a));
    }
}
