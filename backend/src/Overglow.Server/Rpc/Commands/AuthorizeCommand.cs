using System.Globalization;
using System.Text.Json.Nodes;
using Overglow.Server.Auth;

namespace Overglow.Server.Rpc;

/// <summary><c>authorize</c>: login/logout, password and API tokens (port of HyperAPI::handleAuthorizeCommand).</summary>
public sealed class AuthorizeCommand(AuthService auth) : IRpcCommand
{
    public string Name => "authorize";

    public RpcAccess Access => RpcAccess.Anyone;

    public static JsonArray TokenList(IReadOnlyList<TokenInfo> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var list = new JsonArray();
        foreach (var t in tokens)
        {
            list.Add((JsonNode)new JsonObject
            {
                ["comment"] = t.Comment,
                ["id"] = t.Id,
                ["last_use"] = t.LastUse?.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
            });
        }

        return list;
    }

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = context.Session;
        switch (context.Subcommand)
        {
            case "tokenRequired":
                context.Reply(new JsonObject { ["required"] = !session.Authorized });
                break;

            case "adminRequired":
                context.Reply(new JsonObject { ["adminRequired"] = !session.Admin });
                break;

            case "newPasswordRequired":
                context.Reply(new JsonObject { ["newPasswordRequired"] = auth.HasDefaultPassword });
                break;

            case "logout":
                // Back to the rights the connection had before signing in (HyperHDR dropped all rights, which locked
                // out local clients that never needed to sign in).
                session.UnsubscribeAll();
                session.Authorized = session.InitialAuthorized;
                session.Admin = session.InitialAdmin;
                context.Reply();
                break;

            case "login":
                Login(context);
                break;

            case "newPassword":
                if (!session.Admin)
                {
                    context.Fail("No Authorization");
                }
                else if (auth.ChangePassword(context.GetString("password") ?? string.Empty, context.GetString("newPassword") ?? string.Empty))
                {
                    context.Reply();
                }
                else
                {
                    context.Fail("Failed to update user password");
                }

                break;

            case "getTokenList":
                if (session.Admin)
                {
                    context.Reply(TokenList(auth.ListTokens()));
                }
                else
                {
                    context.Fail("No Authorization");
                }

                break;

            case "createToken":
                if (session.Admin)
                {
                    var (token, secret) = auth.CreateToken(context.GetString("comment")?.Trim() ?? string.Empty);
                    context.Reply(new JsonObject { ["comment"] = token.Comment, ["id"] = token.Id, ["token"] = secret });
                }
                else
                {
                    context.Fail("No Authorization");
                }

                break;

            case "deleteToken":
                if (!session.Admin)
                {
                    context.Fail("No Authorization");
                }
                else if (auth.DeleteToken(context.GetString("id")?.Trim() ?? string.Empty))
                {
                    context.Reply();
                }
                else
                {
                    context.Fail("No token with this id");
                }

                break;

            default:
                context.Fail("Command not implemented");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void Login(RpcContext context)
    {
        var session = context.Session;
        var token = context.GetString("token")?.Trim() ?? string.Empty;
        if (token.Length > 0)
        {
            if (token.Length > AuthService.ApiTokenLength && auth.CheckUserToken(token))
            {
                session.Authorized = session.Admin = true;
                context.Reply();
            }
            else if (token.Length == AuthService.ApiTokenLength && auth.CheckApiToken(token))
            {
                session.Authorized = true;
                context.Reply();
            }
            else
            {
                context.Fail("No Authorization");
            }

            return;
        }

        var password = context.GetString("password")?.Trim() ?? string.Empty;
        if (password.Length < AuthService.MinPasswordLength)
        {
            context.Fail("Password too short");
            return;
        }

        switch (auth.CheckPassword(password))
        {
            case LoginResult.Success:
                session.Authorized = session.Admin = true;
                context.Reply(new JsonObject { ["token"] = auth.UserToken });
                break;
            case LoginResult.Blocked:
                context.Fail("Too many login attempts detected. Please wait a minute and try again.");
                break;
            default:
                context.Fail("No Authorization");
                break;
        }
    }
}
