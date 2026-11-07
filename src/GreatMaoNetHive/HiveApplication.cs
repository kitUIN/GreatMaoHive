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
        builder.Services.AddHiveSwagger();
        builder.Services.AddSingleton<HiveStore>();
        builder.Services.AddSingleton<DatabaseWriter>();
        builder.Services.AddHostedService(x => x.GetRequiredService<DatabaseWriter>());
        builder.Services.AddHostedService<OnlineHistoryService>();
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

        app.MapHiveSwagger();
        app.MapGet("/healthz", () => Results.Json(new HealthResponse("ok"), ProtocolJson.Default.HealthResponse))
            .WithTags("健康检查").WithSummary("存活检查").WithDescription("无需 API Key；不依赖节点在线状态。")
            .Produces<HealthResponse>();
        app.MapGet("/agent", async (HttpContext context, BeeSessions sessions) =>
        {
            var credential = options.Bees.FirstOrDefault(x => HiveOptions.Matches(context.Request.Headers.Authorization, x.Token));
            if (credential is null) { await Results.Problem(statusCode: 401, title: "Invalid Bee credential.").ExecuteAsync(context); return; }
            if (!context.WebSockets.IsWebSocketRequest) { await Results.Problem(statusCode: 400, title: "WebSocket upgrade required.").ExecuteAsync(context); return; }
            await sessions.AcceptAsync(context, credential);
        }).ExcludeFromDescription();
        app.MapGet("/api/v1/bees/traffic/quotas", (HiveStore store, BeeSessions sessions) =>
        {
            var result = store.TrafficQuotas(options.Bees, DateTimeOffset.UtcNow);
            result = result with { Bees = result.Bees.Select(bee =>
            {
                var session = sessions.Get(bee.Id);
                return bee with { Online = session?.Online == true, LastReceivedAt = session?.LastReceivedAt ?? bee.LastReceivedAt };
            }).ToArray() };
            return Results.Json(result, ProtocolJson.Default.TrafficQuotasResponse);
        }).WithTags("流量").WithSummary("查询所有小蜜蜂的周期额度及用量")
            .WithDescription("包含全部已配置节点（包括离线和从未连接的节点）。额度仅供查询，不限速或阻断。周期按配置的固定 UTC 时区重置；用量为已入库历史的接收与发送字节之和，无本周期数据时为 null。hasGaps 表示本周期采集不完整，lastSampledAt 表示数据新鲜度；无法定位时间的停机补计不计入周期用量。")
            .Produces<TrafficQuotasResponse>().ProducesProblem(400).ProducesProblem(401);
        app.MapGet("/api/v1/bees", (HiveStore store, BeeSessions sessions) => Results.Json(store.List().Select(node =>
        {
            var session = sessions.Get(node.Node.Id);
            return new BeeSummary(node.Node.Id, node.Node.Name, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt, node.Totals) { Region = node.Node.Region, Ip = node.Node.Ip };
        }).ToArray(), ProtocolJson.Default.BeeSummaryArray))
            .WithTags("节点").WithSummary("列出已注册节点")
            .WithDescription("包含小蜜蜂配置上报的 region 与 ip；未设置时为 null，离线仍返回最后保存的值。")
            .Produces<BeeSummary[]>().ProducesProblem(400).ProducesProblem(401);
        app.MapGet("/api/v1/bees/{id}", (string id, HiveStore store, BeeSessions sessions) =>
        {
            var node = store.Get(id);
            if (node is null) return Missing();
            var session = sessions.Get(id);
            return Results.Json(new BeeDetail(node.Node, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt, node.Totals), ProtocolJson.Default.BeeDetail);
        }).WithTags("节点").WithSummary("查询节点详情")
            .WithDescription("node.region 与 node.ip 来自小蜜蜂配置，重启小蜜蜂并重新连接后更新；未设置时为 null，离线仍可查询。")
            .Produces<BeeDetail>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        app.MapGet("/api/v1/bees/{id}/traffic/total", (string id, HiveStore store, BeeSessions sessions) =>
        {
            var node = store.Get(id);
            if (node is null) return Missing();
            var session = sessions.Get(id);
            return Results.Json(new TotalResponse(node.Totals, session?.Online == true, node.LastSampledAt,
                session?.LastReceivedAt ?? node.LastReceivedAt), ProtocolJson.Default.TotalResponse);
        }).WithTags("流量").WithSummary("查询累计流量").WithDescription("离线仍可查询；尚未提交首批记录时 totals 为 null。")
            .Produces<TotalResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        app.MapGet("/api/v1/bees/{id}/traffic/current", CurrentAsync)
            .WithTags("流量").WithSummary("查询实时流量").WithDescription("等待节点下一次有效采样，最多 3 秒；离线或无有效采样返回 503，超时返回 504。")
            .Produces<CurrentTraffic>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(503).ProducesProblem(504);
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
        }).WithTags("流量").WithSummary("查询历史流量").WithDescription("返回 UTC 对齐的完整时间桶；缺失桶的字节和速率为 null，正常无流量为 0。")
            .Produces<HistoryResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        app.MapGet("/api/v1/bees/{id}/online/history", (string id, HttpContext context, HiveStore store) =>
        {
            if (!options.Bees.Any(x => x.Id == id) && store.Get(id) is null) return Missing();
            if (!ParseTimestamp(context.Request.Query["from"], out var from) || !ParseTimestamp(context.Request.Query["to"], out var to))
                return Results.Problem(statusCode: 400, title: "from and to must be ISO 8601 timestamps with a UTC offset.");
            try
            {
                return Results.Json(store.OnlineHistory(id, from, to, DateTimeOffset.UtcNow, options.OnlineHistoryRetentionDays),
                    ProtocolJson.Default.OnlineHistoryResponse);
            }
            catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: exception.Message); }
        }).WithTags("节点").WithSummary("查询时间窗口内的在线记录")
            .WithDescription("按蜂巢观测返回连续的 online/offline/unknown 区间及各状态秒数。from/to 必填，窗口最多 31 天，不允许未来时间。蜂巢停机、保留期外、未配置节点及最近尚未持久化的时间为 unknown；在线表示有效 WebSocket 会话，静默 45 秒视为离线。")
            .Produces<OnlineHistoryResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
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
