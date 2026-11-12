using GreatMaoNetBee;
using GreatMaoNet.Contracts;
using System.Net.NetworkInformation;
using System.Text.Json;
using GreatMaoNet.Updates;

if (await UpdateRegistration.HandleCommandAsync(args, "GreatMaoNetBee")) return;

if (args.Contains("--list-interfaces", StringComparer.Ordinal))
{
    var interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(nic =>
    {
        long speed = 0;
        try { speed = nic.Speed; } catch (NetworkInformationException) { }
        return new InterfaceInfo(nic.Id, nic.Name, speed > 0 ? speed : null);
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(interfaces, ProtocolJson.Default.InterfaceInfoArray));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
var options = new BeeOptions();
builder.Configuration.GetSection("Bee").Bind(options);
options.Validate();
options.DatabasePath = Path.GetFullPath(options.DatabasePath, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(options);
builder.Services.AddWindowsService(x => x.ServiceName = "GreatMaoNetBee");
builder.Services.AddSystemd();
builder.Services.AddReleaseUpdates(builder.Configuration, "GreatMaoNetBee", Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());
builder.Services.AddSingleton<BeeStore>();
builder.Services.AddSingleton<ICounterSource, NetworkCounterSource>();
builder.Services.AddSingleton<SamplingService>();
builder.Services.AddHostedService(x => x.GetRequiredService<SamplingService>());
builder.Services.AddSingleton<BeeConnectionState>();
builder.Services.AddSingleton<HiveConnectionService>();
builder.Services.AddHostedService(x => x.GetRequiredService<HiveConnectionService>());
builder.Services.AddSingleton<IUpgradePreparation>(x => x.GetRequiredService<HiveConnectionService>());
builder.Services.AddSingleton<IConsoleStatusProvider, BeeConsoleStatus>();
builder.Services.AddConsoleCommands(builder.Configuration, "GreatMaoNetBee", Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());
await builder.Build().RunAsync();
