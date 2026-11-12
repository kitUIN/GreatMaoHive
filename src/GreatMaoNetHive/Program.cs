using GreatMaoNetHive;
using GreatMaoNet.Updates;

if (await HiveControlCommand.HandleAsync(args, Console.Out, Console.Error) is int exitCode) return exitCode;
if (await UpdateRegistration.HandleCommandAsync(args, "GreatMaoNetHive")) return 0;
await HiveApplication.Build(args).RunAsync();
return 0;
