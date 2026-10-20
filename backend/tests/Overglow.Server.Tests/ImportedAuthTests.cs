using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Server.Auth;
using Overglow.Settings;
using Overglow.Settings.Auth;

namespace Overglow.Server.Tests;

/// <summary>A password imported from the owner's HyperHDR database keeps working and is re-hashed on first use.</summary>
public sealed class ImportedAuthTests : IDisposable
{
    private readonly string _dir = TestServer.NewDataDirectory();

    public void Dispose() => TestServer.DeleteDirectory(_dir);

    [Fact]
    public void ImportedPassword_LogsInAndIsUpgradedToPbkdf2()
    {
        using var services = SettingsServices.Open(new SettingsOptions(_dir, null), TestServer.Schema(), NullLoggerFactory.Instance);
        services.ImportDatabase(Path.Combine(AppContext.BaseDirectory, "fixtures", "settings", "owner-hyperhdr-v22.db"));
        Assert.Equal(SecretAlgorithm.LegacySha512, services.Auth.Load().Password!.Algorithm);

        var auth = new AuthService(services.Auth);
        Assert.True(auth.HasPassword);
        Assert.Equal(LoginResult.WrongPassword, auth.CheckPassword("hyperhdr"));
        Assert.Equal(LoginResult.Success, auth.CheckPassword("fixture-password"));

        var upgraded = services.Auth.Load().Password!;
        Assert.Equal(SecretAlgorithm.Pbkdf2Sha256, upgraded.Algorithm);
        Assert.Equal(LoginResult.Success, new AuthService(services.Auth).CheckPassword("fixture-password"));
    }
}
