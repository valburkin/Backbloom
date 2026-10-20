using System.Globalization;
using Backbloom.Settings.Storage;

namespace Backbloom.Settings.Auth;

public enum SecretAlgorithm
{
    /// <summary>PBKDF2-HMAC-SHA256 with <see cref="SecretHash.Iterations"/> rounds (what this version writes).</summary>
    Pbkdf2Sha256,

    /// <summary>
    /// HyperHDR's AuthTable hash, imported so existing passwords and tokens keep working: SHA-512 of the UTF-8 secret
    /// followed by the salt bytes (the salt is HyperHDR's hex text; tokens have none). Replaced by PBKDF2 on first use.
    /// </summary>
    LegacySha512,
}

/// <summary>A hashed secret with its salt and the parameters needed to check it.</summary>
public sealed record SecretHash(byte[] Hash, byte[] Salt, int Iterations, SecretAlgorithm Algorithm = SecretAlgorithm.Pbkdf2Sha256);

/// <summary>An API token for another app; only its hash is stored, the secret is shown once when it is created.</summary>
public sealed record ApiToken(string Id, string Comment, SecretHash Secret, DateTimeOffset? LastUse);

/// <summary>Everything the access control persists; <see cref="Password"/> is null until the user sets one.</summary>
public sealed record AuthData(SecretHash? Password, IReadOnlyList<ApiToken> Tokens)
{
    public static AuthData Empty { get; } = new(null, []);
}

public interface IAuthStore
{
    AuthData Load();

    void Save(AuthData data);
}

/// <summary>
/// Credentials in the <c>admin_password</c> and <c>api_tokens</c> tables of the settings database (hashes only).
/// </summary>
public sealed class SqliteAuthStore(SettingsDatabase db, TimeProvider? time = null) : IAuthStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AuthData Load() => db.Read(c =>
    {
        SecretHash? password = null;
        using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT algorithm, hash, salt, iterations FROM admin_password WHERE id = 1";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                password = new SecretHash((byte[])reader[1], (byte[])reader[2], reader.GetInt32(3), Enum.Parse<SecretAlgorithm>(reader.GetString(0)));
            }
        }

        var tokens = new List<ApiToken>();
        using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT id, comment, algorithm, hash, salt, iterations, last_use FROM api_tokens ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var secret = new SecretHash((byte[])reader[3], (byte[])reader[4], reader.GetInt32(5), Enum.Parse<SecretAlgorithm>(reader.GetString(2)));
                DateTimeOffset? lastUse = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
                tokens.Add(new ApiToken(reader.GetString(0), reader.GetString(1), secret, lastUse));
            }
        }

        return password is null && tokens.Count == 0 ? AuthData.Empty : new AuthData(password, tokens);
    });

    public void Save(AuthData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
        db.Write((c, t) =>
        {
            SettingsDatabase.Execute(c, t, "DELETE FROM admin_password");
            if (data.Password is { } p)
            {
                SettingsDatabase.Execute(c, t, """
                    INSERT INTO admin_password (id, algorithm, hash, salt, iterations, updated_at)
                    VALUES (1, $algorithm, $hash, $salt, $iterations, $now)
                    """,
                    ("$algorithm", p.Algorithm.ToString()), ("$hash", p.Hash), ("$salt", p.Salt), ("$iterations", p.Iterations), ("$now", now));
            }

            SettingsDatabase.Execute(c, t, "DELETE FROM api_tokens");
            foreach (var token in data.Tokens)
            {
                SettingsDatabase.Execute(c, t, """
                    INSERT INTO api_tokens (id, comment, algorithm, hash, salt, iterations, last_use)
                    VALUES ($id, $comment, $algorithm, $hash, $salt, $iterations, $lastUse)
                    """,
                    ("$id", token.Id), ("$comment", token.Comment), ("$algorithm", token.Secret.Algorithm.ToString()), ("$hash", token.Secret.Hash),
                    ("$salt", token.Secret.Salt), ("$iterations", token.Secret.Iterations),
                    ("$lastUse", token.LastUse is { } u ? SettingsDatabase.Timestamp(u) : null));
            }
        });
    }
}
