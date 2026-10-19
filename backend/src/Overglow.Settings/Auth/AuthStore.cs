using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overglow.Settings.Auth;

/// <summary>A hashed secret: PBKDF2-SHA256 with its salt and iteration count.</summary>
public sealed record SecretHash(byte[] Hash, byte[] Salt, int Iterations);

/// <summary>An API token for another app; only its hash is stored, the secret is shown once when it is created.</summary>
public sealed record ApiToken(string Id, string Comment, SecretHash Secret, DateTimeOffset? LastUse);

/// <summary>Everything the access control persists.</summary>
public sealed record AuthData(SecretHash? Password, bool IsDefaultPassword, IReadOnlyList<ApiToken> Tokens)
{
    public static AuthData Empty { get; } = new(null, true, []);
}

public interface IAuthStore
{
    AuthData Load();

    void Save(AuthData data);
}

/// <summary>Credentials in <c>auth.json</c> next to the settings (hashes only, never plain secrets).</summary>
public sealed class JsonFileAuthStore : IAuthStore
{
    public const string FileName = "auth.json";

    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();

    public JsonFileAuthStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public AuthData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return AuthData.Empty;
            }

            var o = JsonNode.Parse(File.ReadAllText(_path))?.AsObject() ?? throw new InvalidDataException($"{_path} is empty.");
            var tokens = (o["tokens"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(t => new ApiToken(
                    t["id"]!.GetValue<string>(),
                    t["comment"]?.GetValue<string>() ?? string.Empty,
                    ReadHash(t["secret"]) ?? throw new InvalidDataException($"Token without secret in {_path}."),
                    t["lastUse"] is JsonValue v && v.TryGetValue(out string? s) && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null))
                .ToList();
            return new AuthData(ReadHash(o["password"]), o["isDefaultPassword"]?.GetValue<bool>() ?? false, tokens);
        }
    }

    public void Save(AuthData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tokens = new JsonArray();
        foreach (var t in data.Tokens)
        {
            tokens.Add((JsonNode)new JsonObject
            {
                ["id"] = t.Id,
                ["comment"] = t.Comment,
                ["secret"] = WriteHash(t.Secret),
                ["lastUse"] = t.LastUse?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        var document = new JsonObject
        {
            ["password"] = data.Password is null ? null : WriteHash(data.Password),
            ["isDefaultPassword"] = data.IsDefaultPassword,
            ["tokens"] = tokens,
        };

        lock (_lock)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, document.ToJsonString(s_writeOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static SecretHash? ReadHash(JsonNode? node) => node is JsonObject h
        ? new SecretHash(Convert.FromBase64String(h["hash"]!.GetValue<string>()), Convert.FromBase64String(h["salt"]!.GetValue<string>()), h["iterations"]!.GetValue<int>())
        : null;

    private static JsonObject WriteHash(SecretHash hash) => new()
    {
        ["hash"] = Convert.ToBase64String(hash.Hash),
        ["salt"] = Convert.ToBase64String(hash.Salt),
        ["iterations"] = hash.Iterations,
    };
}
