using System.Net;
using System.Text.Json;
using GreatMaoNetHive;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GreatMaoNet.Tests;

public sealed class SwaggerTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/")]
    [InlineData("/swagger/index.html")]
    public async Task UiIsAvailableWithoutCredentialsAndUsesLocalAssets(string path)
    {
        using var client = new HttpClient { BaseAddress = fixture.Address };
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("SwaggerUIBundle", html);
        Assert.Contains("./swagger-ui.css", html);
        Assert.Contains("./swagger-ui-bundle.js", html);
        Assert.Contains("./v1/swagger.json", html);
        Assert.Contains("validatorUrl: null", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain(ApiFixture.ReadKey, html);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/bees")).StatusCode);
    }

    [Theory]
    [InlineData("swagger-ui.css", "text/css", 100000)]
    [InlineData("swagger-ui-bundle.js", "text/javascript", 1000000)]
    [InlineData("LICENSE", "text/plain", 10000)]
    public async Task EmbeddedAssetsAreServedInProduction(string asset, string mediaType, int minimumLength)
    {
        using var response = await fixture.Client.GetAsync("/swagger/" + asset);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > minimumLength);
    }

    [Fact]
    public async Task OpenApiDescribesActualQueriesResponsesAndBearerAuthorization()
    {
        using var anonymous = new HttpClient { BaseAddress = fixture.Address };
        string json = await anonymous.GetStringAsync("/swagger/v1/swagger.json");
        Assert.DoesNotContain(ApiFixture.ReadKey, json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());
        Assert.Equal("../../", root.GetProperty("servers")[0].GetProperty("url").GetString());
        var paths = root.GetProperty("paths");
        Assert.Equal(8, paths.EnumerateObject().Count());
        Assert.True(paths.TryGetProperty("/api/v1/bees/traffic/quotas", out _));
        var online = paths.GetProperty("/api/v1/bees/{id}/online/history").GetProperty("get");
        Assert.Equal(new[] { "from", "id", "to" }, online.GetProperty("parameters").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).OrderBy(x => x));
        Assert.False(paths.TryGetProperty("/agent", out _));
        Assert.False(paths.GetProperty("/healthz").GetProperty("get").TryGetProperty("security", out _));
        foreach (var path in paths.EnumerateObject().Where(x => x.Name.StartsWith("/api/", StringComparison.Ordinal)))
        {
            var operation = path.Value.GetProperty("get");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        }
        var components = root.GetProperty("components");
        var bearer = components.GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        var schemas = components.GetProperty("schemas");
        Assert.True(schemas.GetProperty("BeeSummary").GetProperty("properties").TryGetProperty("lastSampledAt", out _));
        Assert.True(schemas.GetProperty("CurrentTraffic").GetProperty("properties").TryGetProperty("downloadBitsPerSecond", out _));
        var current = paths.GetProperty("/api/v1/bees/{id}/traffic/current").GetProperty("get").GetProperty("responses");
        Assert.True(current.TryGetProperty("503", out _));
        Assert.True(current.TryGetProperty("504", out _));
        var parameters = paths.GetProperty("/api/v1/bees/{id}/traffic/history").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(4, parameters.Count);
        Assert.True(parameters["id"].GetProperty("required").GetBoolean());
        Assert.True(parameters["from"].GetProperty("required").GetBoolean());
        Assert.True(parameters["to"].GetProperty("required").GetBoolean());
        Assert.Equal("date-time", parameters["from"].GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal(new[] { "auto", "5s", "1m", "1h" }, parameters["step"].GetProperty("schema").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/swagger/v2/swagger.json")).StatusCode);
    }

    [Fact]
    public async Task SwaggerKeepsExistingHttpsRequirement()
    {
        using var directory = new TemporaryDirectory();
        await using var app = HiveApplication.Build(["--urls", "http://127.0.0.1:0", "--Updates:Enabled", "false",
            "--Hive:DatabasePath", directory.File("hive.db"), "--Hive:ReadApiKey", ApiFixture.ReadKey,
            "--Hive:Bees:0:Id", "bee-01", "--Hive:Bees:0:Token", ApiFixture.BeeKey,
            "--Hive:AllowInsecureTransport", "false", "--Logging:LogLevel:Default", "None"]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        await app.StopAsync();
    }
}
