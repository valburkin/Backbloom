using System.Net;
using System.Text.Json.Nodes;
using Backbloom.Devices.Drivers.Nanoleaf;
using Backbloom.Devices.Drivers.PhilipsHue;

namespace Backbloom.Devices.Tests;

/// <summary>Nanoleaf pairing and Hue entertainment areas for the guided setup's Add lights (fake HTTP, no network).</summary>
public sealed class PairingTests
{
    [Fact]
    public async Task Nanoleaf_PairReturnsTheToken()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"auth_token":"abc123"}"""));
        var properties = await NanoleafDriver.GetPropertiesAsync(Ctx.Request(Ctx.Create(http), """{ "host": "192.0.2.30", "pair": true }"""));
        Assert.Equal("""{"token":"abc123"}""", properties.ToJsonString());
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://192.0.2.30:16021/api/v1/new", request.Uri);
    }

    [Fact]
    public async Task Nanoleaf_PairOutsidePairingMode()
    {
        var http = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var properties = await NanoleafDriver.GetPropertiesAsync(Ctx.Request(Ctx.Create(http), """{ "host": "192.0.2.30:16022", "pair": true }"""));
        Assert.StartsWith("The Nanoleaf controller isn't in pairing mode", properties["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("http://192.0.2.30:16022/api/v1/new", Assert.Single(http.Requests).Uri);
    }

    private const string Entertainment = """
        {"errors":[],"data":[
          {"id":"ent-a","type":"entertainment","renderer":true,"owner":{"rid":"dev-1","rtype":"device"},"renderer_reference":{"rid":"light-a","rtype":"light"}},
          {"id":"ent-b","type":"entertainment","renderer":true,"owner":{"rid":"dev-2","rtype":"device"}},
          {"id":"ent-bridge","type":"entertainment","renderer":false,"owner":{"rid":"dev-0","rtype":"device"}}]}
        """;

    private const string Lights = """
        {"errors":[],"data":[
          {"id":"light-a","id_v1":"/lights/1","owner":{"rid":"dev-1","rtype":"device"}},
          {"id":"light-b","id_v1":"/lights/2","owner":{"rid":"dev-2","rtype":"device"}},
          {"id":"light-c","id_v1":"/lights/3","owner":{"rid":"dev-3","rtype":"device"}}]}
        """;

    private static FakeHttpHandler Bridge(Func<string?, HttpResponseMessage>? created = null) => new((request, body) =>
        (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/clip/v2/resource/entertainment") => FakeHttpHandler.Json(Entertainment),
            ("GET", "/clip/v2/resource/light") => FakeHttpHandler.Json(Lights),
            ("POST", "/clip/v2/resource/entertainment_configuration") => created?.Invoke(body) ?? FakeHttpHandler.Json("""{"errors":[],"data":[{"rid":"area-9","rtype":"entertainment_configuration"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task Hue_CreateAreaFromTheLightsShown()
    {
        var http = Bridge();
        var properties = await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(Ctx.Create(http), """
            { "host": "192.0.2.40", "user": "app-key", "createArea": { "name": "Backbloom", "lights": ["light-a", 2, { "id": "ent-a", "x": 0.5, "y": 0.8, "z": 0.1 }] } }
            """));

        Assert.Equal("""{"id":"area-9"}""", properties.ToJsonString());
        var post = http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("https://192.0.2.40/clip/v2/resource/entertainment_configuration", post.Uri);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("screen", body["configuration_type"]!.GetValue<string>());
        Assert.Equal("Backbloom", body["metadata"]!["name"]!.GetValue<string>());
        var locations = body["locations"]!["service_locations"]!.AsArray();
        Assert.Equal(["ent-a", "ent-b", "ent-a"], locations.Select(l => l!["service"]!["rid"]!.GetValue<string>()));
        Assert.All(locations, l => Assert.Equal("entertainment", l!["service"]!["rtype"]!.GetValue<string>()));
        Assert.Equal("""[{"x":-1,"y":0,"z":0}]""", locations[0]!["positions"]!.ToJsonString());
        Assert.Equal("""[{"x":0,"y":0,"z":0}]""", locations[1]!["positions"]!.ToJsonString());
        Assert.Equal("""[{"x":0.5,"y":0.8,"z":0.1}]""", locations[2]!["positions"]!.ToJsonString());
    }

    // the errors come back in properties.error, for the user as they are (as pairing's)
    [Fact]
    public async Task Hue_CreateAreaErrors()
    {
        var context = Ctx.Create(Bridge());
        async Task<string> Error(DriverContext c, string area) =>
            (await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(c, $$"""{ "host": "192.0.2.40", "user": "k", "createArea": {{area}} }""")))["error"]!.GetValue<string>();

        Assert.StartsWith("The bridge has no entertainment light \"light-c\"", await Error(context, """{ "name": "A", "lights": ["light-c"] }"""), StringComparison.Ordinal);
        Assert.Equal("createArea.name must have 1 to 32 characters", await Error(context, """{ "name": "", "lights": ["light-a"] }"""));
        Assert.Equal("createArea.lights needs at least one light", await Error(context, """{ "name": "A", "lights": [] }"""));
        Assert.Equal("createArea must be { name, lights }", await Error(context, "true"));

        var refused = Ctx.Create(Bridge(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"errors":[{"description":"unauthorized user"}],"data":[]}""") }));
        Assert.Equal("unauthorized user", await Error(refused, """{ "name": "A", "lights": ["light-a"] }"""));

        var down = Ctx.Create(new FakeHttpHandler((_, _) => throw new HttpRequestException("No connection could be made")));
        Assert.Equal("Couldn't reach the Hue bridge at 192.0.2.40", await Error(down, """{ "name": "A", "lights": ["light-a"] }"""));
        var pair = await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(down, """{ "host": "192.0.2.40", "pair": true }"""));
        Assert.Equal("Couldn't reach the Hue bridge at 192.0.2.40", pair["error"]!.GetValue<string>());
        var nanoleaf = await NanoleafDriver.GetPropertiesAsync(Ctx.Request(down, """{ "host": "192.0.2.30", "pair": true }"""));
        Assert.Equal("Couldn't reach the Nanoleaf controller at 192.0.2.30", nanoleaf["error"]!.GetValue<string>());
    }
    private const string States = """
        [ { "entity_id": "light.desk", "state": "on", "attributes": { "friendly_name": "Desk lamp" } },
          { "entity_id": "switch.fan", "state": "off", "attributes": { "friendly_name": "Fan" } },
          { "entity_id": "light.ceiling", "state": "off", "attributes": { "friendly_name": "Ceiling" } },
          { "entity_id": "light.no_name", "state": "unavailable", "attributes": {} } ]
        """;

    [Fact]
    public async Task HomeAssistant_ListsTheLightEntities()
    {
        var http = new FakeHttpHandler((request, _) => request.Headers.Authorization?.ToString() == "Bearer secret-token"
            ? FakeHttpHandler.Json(States)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var context = Ctx.Create(http);

        var properties = await Drivers.HomeAssistant.HomeAssistantDriver.GetPropertiesAsync(Ctx.Request(context, """{ "host": "192.0.2.60", "token": "secret-token" }"""));
        Assert.Equal("""{"lights":[{"id":"light.ceiling","name":"Ceiling"},{"id":"light.desk","name":"Desk lamp"},{"id":"light.no_name","name":"light.no_name"}]}""", properties.ToJsonString());
        Assert.Equal("http://192.0.2.60:8123/api/states", http.Requests[0].Uri);

        var refused = await Drivers.HomeAssistant.HomeAssistantDriver.GetPropertiesAsync(Ctx.Request(context, """{ "host": "192.0.2.60", "token": "old" }"""));
        Assert.StartsWith("Home Assistant refused the access token (HTTP 401)", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var missing = await Drivers.HomeAssistant.HomeAssistantDriver.GetPropertiesAsync(Ctx.Request(context, """{ "host": "192.0.2.60" }"""));
        Assert.Equal("The Home Assistant address and a long-lived access token are needed", missing["error"]!.GetValue<string>());

        var down = Ctx.Create(new FakeHttpHandler((_, _) => throw new HttpRequestException("No connection could be made")));
        var unreachable = await Drivers.HomeAssistant.HomeAssistantDriver.GetPropertiesAsync(Ctx.Request(down, """{ "host": "192.0.2.61:8124", "token": "t" }"""));
        Assert.Equal("Couldn't reach Home Assistant at 192.0.2.61:8124", unreachable["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task HomeAssistant_IdentifyFlashesTheLight()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("[]"));
        await Drivers.HomeAssistant.HomeAssistantDriver.IdentifyAsync(Ctx.Request(Ctx.Create(http), """{ "host": "https://ha.local", "token": "t", "entityId": "light.desk" }"""));
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ha.local:8123/api/services/light/turn_on", request.Uri);
        Assert.Equal("""{"entity_id":"light.desk","flash":"short"}""", request.Body);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Drivers.HomeAssistant.HomeAssistantDriver.IdentifyAsync(Ctx.Request(Ctx.Create(http), """{ "host": "ha.local", "token": "t", "entityId": "switch.fan" }""")));
        var failing = Ctx.Create(new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        await Assert.ThrowsAsync<IOException>(() => Drivers.HomeAssistant.HomeAssistantDriver.IdentifyAsync(Ctx.Request(failing, """{ "host": "ha.local", "token": "t", "entityId": "light.desk" }""")));
    }

    [Fact]
    public async Task Hue_V2ResourcesWithTheApplicationKey()
    {
        var http = new FakeHttpHandler((request, _) =>
        {
            Assert.Equal("app-key", request.Headers.GetValues("hue-application-key").Single());
            return FakeHttpHandler.Json("""{"errors":[],"data":[{"id":"area-1","type":"entertainment_configuration"}]}""");
        });
        var properties = await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(Ctx.Create(http), """{ "host": "192.0.2.40", "user": "app-key", "filter": "/clip/v2/resource/entertainment_configuration" }"""));
        Assert.Equal("area-1", properties["data"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("https://192.0.2.40/clip/v2/resource/entertainment_configuration", Assert.Single(http.Requests).Uri);
    }
}
