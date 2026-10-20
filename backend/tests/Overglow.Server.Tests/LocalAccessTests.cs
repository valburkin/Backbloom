using System.Net;
using Overglow.Server.Auth;
using Overglow.Settings.Auth;

namespace Overglow.Server.Tests;

/// <summary>Who is "this computer" (full access, no password) and which loopback requests are refused.</summary>
public sealed class LocalAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:127.3.2.1", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("::2", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("::ffff:192.168.1.20", false)]
    [InlineData("fe80::1", false)]
    [InlineData("128.0.0.1", false)]
    public void IsLoopback(string address, bool expected) => Assert.Equal(expected, LocalAccess.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void IsLoopback_ScopedIPv6Loopback() => Assert.True(LocalAccess.IsLoopback(new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), 3)));

    [Fact]
    public void IsLoopback_NoAddress() => Assert.False(LocalAccess.IsLoopback(null));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LocalHost:18290", true)]
    [InlineData("localhost.", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:18290", true)]
    [InlineData("127.8.9.10:80", true)]
    [InlineData("[::1]", true)]
    [InlineData("[::1]:18290", true)]
    [InlineData("[::ffff:127.0.0.1]:18290", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("evil.localhost.example:18290", false)]
    [InlineData("192.168.1.20:18290", false)]
    [InlineData("my-pc", false)]
    [InlineData("[fe80::1]:18290", false)]
    public void IsLoopbackHost(string? host, bool expected) => Assert.Equal(expected, LocalAccess.IsLoopbackHost(host));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://127.0.0.1:18290", true)]
    [InlineData("http://localhost:4730", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://[::1]:18290", true)]
    [InlineData("null", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://127.0.0.1.evil.example", false)]
    [InlineData("http://192.168.1.20:18290", false)]
    [InlineData("file:///C:/page.html", false)]
    [InlineData("chrome-extension://abcdef", false)]
    [InlineData("http://127.0.0.1:18290, https://evil.example", false)]
    public void IsLoopbackOrigin(string? origin, bool expected) => Assert.Equal(expected, LocalAccess.IsLoopbackOrigin(origin));

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1:18290", null, ClientOrigin.ThisComputer)]
    [InlineData("::1", "[::1]:18290", "http://[::1]:18290", ClientOrigin.ThisComputer)]
    [InlineData("::ffff:127.0.0.1", "localhost:18290", "http://localhost:18290", ClientOrigin.ThisComputer)]
    [InlineData("127.0.0.1", "rebind.evil.example:18290", null, ClientOrigin.Rejected)]
    [InlineData("127.0.0.1", "127.0.0.1:18290", "https://evil.example", ClientOrigin.Rejected)]
    [InlineData("127.0.0.1", null, null, ClientOrigin.Rejected)]
    [InlineData("192.168.1.20", "192.168.1.10:18290", "http://192.168.1.10:18290", ClientOrigin.Network)]
    [InlineData("192.168.1.20", "localhost", null, ClientOrigin.Network)]
    [InlineData("::ffff:10.0.0.2", "my-pc.local:18290", "https://evil.example", ClientOrigin.Network)]
    public void Classify(string peer, string? host, string? origin, ClientOrigin expected) =>
        Assert.Equal(expected, LocalAccess.Classify(IPAddress.Parse(peer), host, origin));

    [Fact]
    public void AuthService_HasNoFactoryPassword()
    {
        var store = new MemoryAuthStore();
        var auth = new AuthService(store);

        Assert.False(auth.HasPassword);
        Assert.Null(store.Data.Password);
        Assert.Equal(LoginResult.WrongPassword, auth.CheckPassword("overglow"));
        Assert.Equal(LoginResult.WrongPassword, auth.CheckPassword("hyperhdr"));
        Assert.False(auth.ChangePassword("overglow", "long-enough-1"));
    }

    [Fact]
    public void AuthService_SetPassword_StoresAHashAndRaisesTheEvent()
    {
        var store = new MemoryAuthStore();
        var auth = new AuthService(store);
        var changes = 0;
        auth.PasswordChanged += () => changes++;

        Assert.False(auth.SetPassword("short"));
        Assert.Equal(0, changes);
        Assert.True(auth.SetPassword("long-enough-1"));
        Assert.Equal(1, changes);
        Assert.True(auth.HasPassword);
        Assert.Equal(SecretAlgorithm.Pbkdf2Sha256, store.Data.Password!.Algorithm);
        Assert.Equal(LoginResult.Success, new AuthService(store).CheckPassword("long-enough-1"));

        Assert.False(auth.ChangePassword("wrong-password", "long-enough-2"));
        Assert.True(auth.ChangePassword("long-enough-1", "long-enough-2"));
        Assert.Equal(LoginResult.Success, auth.CheckPassword("long-enough-2"));
        Assert.Equal(2, changes);
    }

    private sealed class MemoryAuthStore : IAuthStore
    {
        public AuthData Data { get; private set; } = AuthData.Empty;

        public AuthData Load() => Data;

        public void Save(AuthData data) => Data = data;
    }
}
