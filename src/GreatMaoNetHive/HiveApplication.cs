using System.Globalization;
using GreatMaoNet.Contracts;
using GreatMaoNet.Updates;

namespace GreatMaoNetHive;

public static class HiveApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
        var options = new HiveOptions();
        builder.Configuration.GetSection("Hive").Bind(options);
        options.Validate();
        options.DatabasePath = Path.GetFullPath(options.DatabasePath, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(options);
        builder.Services.AddWindowsService(x => x.ServiceName = "GreatMaoNetHive");
        builder.Services.AddSystemd();
        builder.Services.AddReleaseUpdates(builder.Configuration, "GreatMaoNetHive", Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());
        builder.Services.ConfigureHttpJsonOptions(x => x.SerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJson.Default));
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<HiveStore>();
        builder.Services.AddSingleton<DatabaseWriter>();
        builder.Services.AddHostedService(x => x.GetRequiredService<DatabaseWriter>());
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.AddSingleton<BeeSessions>();
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/healthz" && !context.Request.IsHttps && !options.AllowInsecureTransport)
            {
                await Results.Problem(statusCode: 400, title: "HTTPS is required.").ExecuteAsync(context);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api") && !HiveOptions.Matches(context.Request.Headers.Authorization, options.ReadApiKey))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Results.Problem(statusCode: 401, title: "A valid read API key is required.").ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        app.MapGet("/healthz", () => Results.Json(new HealthResponse("ok"), ProtocolJson.Default.HealthResponse));
        app.MapGet("/agent", async (HttpContext context, BeeSessions sessions) =>
        {
            var credential = options.Bees.FirstOrDefault(x => HiveOptions.Matches(context.Request.Headers.Authorization, x.Token));
            if (credential is null) { await Results.Problem(statusCode: 401, title: "Invalid Bee credential.").ExecuteAsync(context); return; }
            if (!context.WebSockets.IsWebSocketRequest) { await Results.Problem(statusCode: 400, title: "WebSocket upgrade required.").ExecuteAsync(context); return; }
            await sessions.AcceptAsync(context, credential);
        });
        app.MapGet("/api/v1/bees", (HiveStore store, BeeSessions sessions) => Results.Json(store.List().Select(node =>
        {
            var session = sessions.Get(node.Node.Id);
            return new BeeSummary(node.Node.Id, node.Node.Name, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt, node.Totals);
        }).ToArray(), ProtocolJson.Default.BeeSummaryArray));
        app.MapGet("/api/v1/bees/{id}", (string id, HiveStore store, BeeSessions sessions) =>
        {
            var node = store.Get(id);
            if (node is null) return Missing();
            var session = sessions.Get(id);
            return Results.Json(new BeeDetail(node.Node, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt, node.Totals), ProtocolJson.Default.BeeDetail);
        });
        app.MapGet("/api/v1/bees/{id}/traffic/total", (string id, HiveStore store, BeeSessions sessions) =>
        {
            var node = store.Get(id);
            if (node is null) return Missing();
            var session = sessions.Get(id);
            return Results.Json(new TotalResponse(node.Totals, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt), ProtocolJson.Default.TotalResponse);
        });
        app.MapGet("/api/v1/bees/{id}/traffic/current", CurrentAsync);
        app.MapGet("/api/v1/bees/{id}/traffic/history", (string id, HttpContext context, HiveStore store) =>
        {
            if (store.Get(id) is null) return Missing();
            if (!ParseTimestamp(context.Request.Query["from"], out var from) || !ParseTimestamp(context.Request.Query["to"], out var to))
                return Results.Problem(statusCode: 400, title: "from and to must be ISO 8601 timestamps with a UTC offset.");
            try
            {
                string step = context.Request.Query["step"].FirstOrDefault() ?? "auto";
                return Results.Json(store.History(id, HistoryQuery.Create(from, to, step, DateTimeOffset.UtcNow)), ProtocolJson.Default.HistoryResponse);
            }
            catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: exception.Message); }
        });
        return app;
    }

    private static bool ParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value is not null && (value.EndsWith('Z') || value.LastIndexOf('+') > 10 || value.LastIndexOf('-') > 10) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Bee not found.");

    private static async Task<IResult> CurrentAsync(string id, HiveStore store, BeeSessions sessions, HttpContext context)
    {
        if (store.Get(id) is null) return Missing();
        var session = sessions.Get(id);
        if (session?.Online != true) return Results.Problem(statusCode: 503, title: "Bee is offline.");
        try
        {
            var current = await session.CurrentAsync(context.RequestAborted);
            if (!current.Available) return Results.Problem(statusCode: 503, title: "Network sample unavailable.", detail: current.GapReason);
            if (Math.Abs((DateTimeOffset.UtcNow - current.SampledAt).TotalMinutes) > 5)
                return Results.Problem(statusCode: 503, title: "Bee clock differs from Hive by more than five minutes.");
            return Results.Json(current, ProtocolJson.Default.CurrentTraffic);
        }
        catch (TimeoutException exception) { return Results.Problem(statusCode: 504, title: exception.Message); }
        catch (Exception exception) when (exception is IOException or System.Net.WebSockets.WebSocketException ||
            exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        { return Results.Problem(statusCode: 503, title: "Bee disconnected."); }
    }
}
