using GreatMaoNetHive;
using GreatMaoNet.Updates;

if (await UpdateRegistration.HandleCommandAsync(args, "GreatMaoNetHive")) return;
await HiveApplication.Build(args).RunAsync();
