using System.Text.Json.Nodes;
using GreatMaoNet.Updates;
using Microsoft.OpenApi;

namespace GreatMaoNetHive;

public static class HiveSwagger
{
    public static void AddHiveSwagger(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "GreatMaoNetHive 大猫猫网络蜂巢 API",
                    Version = UpdateOptions.CurrentVersion,
                    Description = "蜂巢网络流量查询与更新接口。点击 Authorize，查询使用 Hive.ReadApiKey，更新使用 Hive.AdminApiKey（均不含 Bearer 前缀）。" +
                        "节点 Token 不能查询 API。所有时间均为带 UTC 偏移的 ISO 8601 时间；字节为 int64，速率为 bit/s。" +
                        "小蜜蜂通过 /agent 建立 WebSocket 连接，该协议不在 Swagger UI 中调试。"
                };
                // Relative to /swagger/v1/swagger.json, including any reverse-proxy path prefix.
                document.Servers = [new OpenApiServer { Url = "../../" }];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http, Scheme = "bearer",
                        Description = "输入 Hive.ReadApiKey；不要添加 Bearer 前缀。"
                    },
                    ["AdminBearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http, Scheme = "bearer",
                        Description = "更新接口专用：输入 Hive.AdminApiKey；不要添加 Bearer 前缀。"
                    }
                };
                foreach (var (path, item) in document.Paths)
                {
                    if (!path.StartsWith("/api/", StringComparison.Ordinal) || item.Operations is null) continue;
                    foreach (var (method, operation) in item.Operations)
                    {
                        string scheme = method == HttpMethod.Get || method == HttpMethod.Head ? "Bearer" : "AdminBearer";
                        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] }];
                        bool onlineHistory = path.EndsWith("/online/history", StringComparison.Ordinal);
                        if (!onlineHistory && !path.EndsWith("/traffic/history", StringComparison.Ordinal)) continue;
                        operation.Parameters ??= [];
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = "from", In = ParameterLocation.Query, Required = true,
                            Description = "起始时间（含），ISO 8601，必须带 Z 或 UTC 偏移。",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" }
                        });
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = "to", In = ParameterLocation.Query, Required = true,
                            Description = onlineHistory ? "结束时间（不含），须晚于 from，不可晚于当前时间；窗口最多 31 天。" : "结束时间（不含），须晚于 from，最多允许超前当前时间 5 分钟。",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" }
                        });
                        if (onlineHistory) continue;
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = "step", In = ParameterLocation.Query,
                            Description = "auto 选择满足保留期且不超过 10,000 点的最细粒度。5s 保留 24 小时，1m 保留 90 天，1h 保留 2 年。",
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String, Default = JsonValue.Create("auto"),
                                Enum = [JsonValue.Create("auto")!, JsonValue.Create("5s")!, JsonValue.Create("1m")!, JsonValue.Create("1h")!]
                            }
                        });
                    }
                }
                return Task.CompletedTask;
            });
        });
    }

    public static void MapHiveSwagger(this WebApplication app)
    {
        app.MapOpenApi("/swagger/{documentName}/swagger.json").ExcludeFromDescription();
        app.MapGet("/swagger", (HttpContext context) => Results.Redirect($"{context.Request.PathBase}/swagger/index.html"))
            .ExcludeFromDescription();
        MapAsset(app, "index.html", "text/html; charset=utf-8");
        MapAsset(app, "swagger-ui.css", "text/css; charset=utf-8");
        MapAsset(app, "swagger-ui-bundle.js", "text/javascript; charset=utf-8");
        MapAsset(app, "LICENSE", "text/plain; charset=utf-8");
        MapAsset(app, "NOTICE", "text/plain; charset=utf-8");
    }

    private static void MapAsset(WebApplication app, string name, string contentType) =>
        app.MapGet("/swagger/" + name, () => Results.Stream(
            typeof(HiveSwagger).Assembly.GetManifestResourceStream("GreatMaoNetHive.SwaggerUI." + name)
                ?? throw new InvalidOperationException("Embedded Swagger UI resource missing."), contentType))
            .ExcludeFromDescription();
}
