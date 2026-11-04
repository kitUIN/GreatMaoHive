using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace GreatMaoNet.Contracts;

public sealed class WebSocketProtocol(WebSocket socket) : IDisposable
{
    public const int MaximumMessageBytes = 512 * 1024;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.WireMessage);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Message too large.");
        await sendLock.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendLock.Release(); }
    }

    public async Task<WireMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(writer.GetMemory(4096), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Text frames required.");
            writer.Advance(result.Count);
            if (writer.WrittenCount > MaximumMessageBytes) throw new InvalidDataException("Message too large.");
        } while (!result.EndOfMessage);
        var message = JsonSerializer.Deserialize(writer.WrittenSpan, ProtocolJson.Default.WireMessage)
            ?? throw new InvalidDataException("Empty message.");
        if (message.Version != 1) throw new InvalidDataException("Unsupported protocol version.");
        return message;
    }

    public void Dispose() => sendLock.Dispose();
}
