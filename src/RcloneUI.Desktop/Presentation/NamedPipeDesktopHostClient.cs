using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Desktop.Presentation;

public sealed class NamedPipeDesktopHostClient(string dataRootPath) : IDesktopHostClient
{
    private static readonly string[] Capabilities = ["host.snapshot", "host.events", "ui.activate"];
    private static readonly JsonSerializerOptions EndpointJson = new(JsonSerializerDefaults.Web);
    private readonly string dataRootPath = Path.GetFullPath(dataRootPath);

    public async ValueTask<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var arguments = JsonDocument.Parse("{}");
        var response = await ExchangeCommandAsync("get-snapshot", arguments.RootElement, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(response.Body.GetProperty("resultType").GetString(), "snapshot")) throw new InvalidDataException("Host did not return a snapshot.");
        return new(response.State, DateTimeOffset.UtcNow, response.Body.GetProperty("result").Clone());
    }

    public async ValueTask<JsonElement> SendCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken)
    {
        var response = await ExchangeCommandAsync(commandType, arguments, cancellationToken).ConfigureAwait(false);
        return response.Body.Clone();
    }

    private ValueTask<ProtocolEnvelope> ExchangeCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { commandType, arguments }));
        return ExchangeAsync(commandType, body.RootElement.Clone(), cancellationToken);
    }

    private async ValueTask<ProtocolEnvelope> ExchangeAsync(string operation, JsonElement body, CancellationToken cancellationToken)
    {
        var endpoint = ReadEndpoint();
        await using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
        var challengeKey = Convert.FromBase64String(endpoint.ChallengeKey);
        var clientNonce = RandomNumberGenerator.GetBytes(ConnectionKeyDerivation.NonceBytes);
        byte[]? connectionKey = null;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            var helloBytes = JsonSerializer.SerializeToUtf8Bytes(new { dataRootId = endpoint.DataRootId, incarnation = endpoint.Incarnation, clientNonce = Convert.ToBase64String(clientNonce), protocolMajor = HostProtocolVersion.Major, minimumMinor = 0, maximumMinor = HostProtocolVersion.CurrentMinor, capabilities = Capabilities });
            var hello = ProtocolEnvelope.CreateRequest(MessageType.Hello, new($"hello-{Guid.NewGuid():N}"), 1, new(new("desktop"), 0), new(new($"hello-{Guid.NewGuid():N}"), new($"cancel-{Guid.NewGuid():N}"), deadline), helloBytes);
            await WriteAsync(pipe, hello, challengeKey, cancellationToken).ConfigureAwait(false);
            var ack = HostFrameCodec.Decode(await ReadAsync(pipe, cancellationToken).ConfigureAwait(false), challengeKey, 1).Envelope;
            if (ack.MessageType != MessageType.HelloAck) throw new InvalidDataException("Host handshake response is invalid.");
            var hostNonce = Convert.FromBase64String(ack.Body.GetProperty("hostNonce").GetString()!);
            try { connectionKey = ConnectionKeyDerivation.Derive(challengeKey, clientNonce, hostNonce); }
            finally { CryptographicOperations.ZeroMemory(hostNonce); }
            var request = ProtocolEnvelope.CreateRequest(MessageType.Command, new($"request-{Guid.NewGuid():N}"), 1, ack.State, new(new($"{operation}-{Guid.NewGuid():N}"), new($"cancel-{Guid.NewGuid():N}"), deadline), JsonSerializer.SerializeToUtf8Bytes(body));
            await WriteAsync(pipe, request, connectionKey, cancellationToken).ConfigureAwait(false);
            return HostFrameCodec.Decode(await ReadAsync(pipe, cancellationToken).ConfigureAwait(false), connectionKey, 1).Envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeKey); CryptographicOperations.ZeroMemory(clientNonce);
            if (connectionKey is not null) CryptographicOperations.ZeroMemory(connectionKey);
        }
    }

    private EndpointRecord ReadEndpoint()
    {
        var path = Path.Combine(dataRootPath, "runtime", "endpoint.json");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 4096) throw new InvalidDataException("Host endpoint record is invalid.");
        var value = JsonSerializer.Deserialize<EndpointRecord>(bytes, EndpointJson) ?? throw new InvalidDataException("Host endpoint record is invalid.");
        if (value.Format != 1 || value.DataRootId == Guid.Empty || value.Incarnation == Guid.Empty || value.ProtocolMajor != HostProtocolVersion.Major) throw new InvalidDataException("Host endpoint identity is incompatible.");
        return value;
    }

    private static async ValueTask WriteAsync(Stream stream, ProtocolEnvelope envelope, byte[] key, CancellationToken cancellationToken)
    {
        var frame = HostFrameCodec.Encode(envelope, key);
        try { await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(frame); }
    }
    private static async ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length > HostFrameCodec.MaximumJsonBytes) throw new ProtocolException(ProtocolErrorCode.FrameTooLarge);
        var frame = new byte[4 + checked((int)length) + HostFrameCodec.AuthenticationTagBytes]; prefix.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), cancellationToken).ConfigureAwait(false); return frame;
    }
    private sealed record EndpointRecord(int Format, Guid DataRootId, string PipeName, Guid Incarnation, string ChallengeKey, int ProtocolMajor);
}
