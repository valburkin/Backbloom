using System.Security.Cryptography;
using System.Text;
using Overglow.Settings.Auth;

namespace Overglow.Server.Auth;

public enum LoginResult
{
    Success,
    WrongPassword,
    Blocked,
}

/// <summary>An API token as listed to admins (never the secret).</summary>
public sealed record TokenInfo(string Id, string Comment, DateTimeOffset? LastUse);

/// <summary>
/// Password and token checks (port of HyperHDR's AccessManager / AuthTable semantics): one admin password
/// (factory default "overglow" until changed), a user token issued on login that is valid until the process restarts,
/// and API tokens for other apps. Secrets are stored as PBKDF2-SHA256 hashes only; hashes imported from HyperHDR
/// (salted SHA-512) keep working and are replaced by PBKDF2 the first time they are used.
/// </summary>
public sealed class AuthService
{
    public const string DefaultPassword = "overglow";
    public const int MinPasswordLength = 8;
    public const int UserTokenLength = 64;
    public const int ApiTokenLength = 36;

    private const int PasswordIterations = 210_000;
    private const int MaxFailedLogins = 10;
    private static readonly TimeSpan s_blockDuration = TimeSpan.FromMinutes(1);

    private readonly IAuthStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly byte[] _userToken;
    private AuthData _data;
    private int _failedLogins;
    private DateTimeOffset _blockedUntil;

    public AuthService(IAuthStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
        _data = store.Load();
        if (_data.Password is null)
        {
            _data = _data with { Password = Hash(DefaultPassword, PasswordIterations), IsDefaultPassword = true };
            store.Save(_data);
        }

        UserToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(UserTokenLength / 2));
        _userToken = Encoding.ASCII.GetBytes(UserToken);
    }

    /// <summary>Raised after an API token was created or deleted.</summary>
    public event Action<IReadOnlyList<TokenInfo>>? TokensChanged;

    /// <summary>Token returned by a password login; valid until restart.</summary>
    public string UserToken { get; }

    public bool HasDefaultPassword
    {
        get
        {
            lock (_lock)
            {
                return _data.IsDefaultPassword;
            }
        }
    }

    public LoginResult CheckPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        SecretHash stored;
        lock (_lock)
        {
            if (_time.GetUtcNow() < _blockedUntil)
            {
                return LoginResult.Blocked;
            }

            stored = _data.Password!;
        }

        var ok = Verify(password, stored);
        lock (_lock)
        {
            if (ok)
            {
                _failedLogins = 0;
                if (stored.Algorithm != SecretAlgorithm.Pbkdf2Sha256 && ReferenceEquals(stored, _data.Password))
                {
                    // a password imported from HyperHDR: store it the way this version does
                    _data = _data with { Password = Hash(password, PasswordIterations) };
                    _store.Save(_data);
                }

                return LoginResult.Success;
            }

            if (++_failedLogins >= MaxFailedLogins)
            {
                _failedLogins = 0;
                _blockedUntil = _time.GetUtcNow() + s_blockDuration;
                return LoginResult.Blocked;
            }

            return LoginResult.WrongPassword;
        }
    }

    public bool CheckUserToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), _userToken);
    }

    /// <summary>Checks an API token and records its use.</summary>
    public bool CheckApiToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_lock)
        {
            var match = _data.Tokens.FirstOrDefault(t => Verify(token, t.Secret));
            if (match is null)
            {
                return false;
            }

            var secret = match.Secret.Algorithm == SecretAlgorithm.Pbkdf2Sha256 ? match.Secret : Hash(token, 1);
            _data = _data with { Tokens = [.. _data.Tokens.Select(t => t.Id == match.Id ? t with { Secret = secret, LastUse = _time.GetUtcNow() } : t)] };
            _store.Save(_data);
            return true;
        }
    }

    /// <summary>Changes the admin password; false when the current one is wrong or the new one too short.</summary>
    public bool ChangePassword(string current, string next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Length < MinPasswordLength || CheckPassword(current) != LoginResult.Success)
        {
            return false;
        }

        lock (_lock)
        {
            _data = _data with { Password = Hash(next, PasswordIterations), IsDefaultPassword = next == DefaultPassword };
            _store.Save(_data);
        }

        return true;
    }

    public IReadOnlyList<TokenInfo> ListTokens()
    {
        lock (_lock)
        {
            return [.. _data.Tokens.Select(t => new TokenInfo(t.Id, t.Comment, t.LastUse))];
        }
    }

    /// <summary>Creates an API token; the returned secret is not stored and can't be shown again.</summary>
    public (TokenInfo Token, string Secret) CreateToken(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var secret = Guid.NewGuid().ToString("D");
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))[..5];
        IReadOnlyList<TokenInfo> list;
        lock (_lock)
        {
            _data = _data with { Tokens = [.. _data.Tokens, new ApiToken(id, comment, Hash(secret, 1), null)] };
            _store.Save(_data);
            list = [.. _data.Tokens.Select(t => new TokenInfo(t.Id, t.Comment, t.LastUse))];
        }

        TokensChanged?.Invoke(list);
        return (new TokenInfo(id, comment, null), secret);
    }

    public bool DeleteToken(string id)
    {
        IReadOnlyList<TokenInfo> list;
        lock (_lock)
        {
            if (!_data.Tokens.Any(t => t.Id == id))
            {
                return false;
            }

            _data = _data with { Tokens = [.. _data.Tokens.Where(t => t.Id != id)] };
            _store.Save(_data);
            list = [.. _data.Tokens.Select(t => new TokenInfo(t.Id, t.Comment, t.LastUse))];
        }

        TokensChanged?.Invoke(list);
        return true;
    }

    private static SecretHash Hash(string secret, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new SecretHash(Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32), salt, iterations);
    }

    private static bool Verify(string secret, SecretHash stored) => stored.Algorithm switch
    {
        SecretAlgorithm.Pbkdf2Sha256 => CryptographicOperations.FixedTimeEquals(
            Rfc2898DeriveBytes.Pbkdf2(secret, stored.Salt, stored.Iterations, HashAlgorithmName.SHA256, stored.Hash.Length),
            stored.Hash),
        SecretAlgorithm.LegacySha512 => CryptographicOperations.FixedTimeEquals(SHA512.HashData([.. Encoding.UTF8.GetBytes(secret), .. stored.Salt]), stored.Hash),
        _ => false,
    };
}
