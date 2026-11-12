using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using GreatMaoNet.Contracts;

namespace GreatMaoNetHive;

public sealed record BeeUpdateCommandResult(int StatusCode, BeeUpdateResponse? Response, string? Error);
public sealed record BeeBatchUpdateResult(string Id, bool Skipped, BeeUpdateCommandResult Result);

public sealed class BeeUpdateCommands(HiveOptions options, BeeSessions sessions)
{
    public Task<BeeUpdateCommandResult> UpdateAsync(string id, CancellationToken token)
    {
        if (!options.Bees.Any(bee => bee.Id == id)) return Task.FromResult(new BeeUpdateCommandResult(404, null, "Bee not found."));
        return UpdateSessionAsync(id, sessions.Get(id), token);
    }

    public async IAsyncEnumerable<BeeBatchUpdateResult> UpdateAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        // Capture the sessions once: offline nodes and later reconnects must not receive deferred commands.
        var targets = options.Bees.OrderBy(bee => bee.Id, StringComparer.Ordinal)
            .Select(bee => (bee.Id, Session: sessions.Get(bee.Id)))
            .Select(target => (target.Id, Session: target.Session?.Online == true ? target.Session : null)).ToArray();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (target.Session is null)
                yield return new(target.Id, true, new(503, null, "Bee was offline when the batch started; skipped."));
            else
                yield return new(target.Id, false, await UpdateSessionAsync(target.Id, target.Session, token));
        }
    }

    private static async Task<BeeUpdateCommandResult> UpdateSessionAsync(string id, BeeSession? session, CancellationToken token)
    {
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
