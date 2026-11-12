using System.Net.WebSockets;
using GreatMaoNet.Contracts;

namespace GreatMaoNetHive;

public sealed record BeeUpdateCommandResult(int StatusCode, BeeUpdateResponse? Response, string? Error);

public sealed class BeeUpdateCommands(HiveOptions options, BeeSessions sessions)
{
    public async Task<BeeUpdateCommandResult> UpdateAsync(string id, CancellationToken token)
    {
        if (!options.Bees.Any(bee => bee.Id == id)) return new(404, null, "Bee not found.");
        var session = sessions.Get(id);
        if (session?.Online != true) return new(503, null, "Bee is offline.");
        try
        {
            var response = await session.UpdateAsync(id, token);
            return response.Status switch
            {
                "accepted" => new(202, response, null),
                "disabled" => new(409, null, "Bee update request rejected: disabled. Check Updates:AllowRemoteUpdates/AutoDownload/AutoApply."),
                "unavailable" => new(503, null, "Bee update request rejected: unavailable. Check the updater and systemd unit."),
                _ => new(409, null, "Bee update request rejected: busy. An update is already pending or running.")
            };
        }
        catch (InvalidOperationException exception) { return new(409, null, exception.Message); }
        catch (TimeoutException exception) { return new(504, null, exception.Message); }
        catch (Exception exception) when (exception is IOException or WebSocketException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        { return new(503, null, "Bee disconnected; update execution is unknown."); }
    }
}
