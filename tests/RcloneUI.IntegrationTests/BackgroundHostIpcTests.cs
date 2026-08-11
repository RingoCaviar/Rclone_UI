using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;

namespace RcloneUI.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class BackgroundHostIpcTests
{
    private static readonly string[] ClientCapabilities = ["host.snapshot", "host.events", "ui.activate"];
    [Fact]
    public async Task AuthenticatedReconnectReplaysMutationWithoutExecutingTwice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        var dataRootId = Guid.NewGuid();
        try
        {
            await using var host = BackgroundHostShell.TryCreate(root, dataRootId);
            Assert.NotNull(host);
            host.Start();
            Assert.Null(BackgroundHostShell.TryCreate(root, dataRootId));

            var first = await SendCommandAsync(host.Endpoint, "activate-ui", "same-key", cancellationToken, expectStateEvent: true);
            var replay = await SendCommandAsync(host.Endpoint, "activate-ui", "same-key", cancellationToken);
            var snapshot = await SendCommandAsync(host.Endpoint, "get-snapshot", "snapshot-key", cancellationToken);

            Assert.Equal("activated", first.GetProperty("resultType").GetString());
            Assert.Equal("activated", replay.GetProperty("resultType").GetString());
            Assert.Equal(1, snapshot.GetProperty("result").GetProperty("activationCount").GetInt32());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task InvalidHandshakeMacNeverReachesCommandDispatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        var dataRootId = Guid.NewGuid();
        try
        {
            await using var host = BackgroundHostShell.TryCreate(root, dataRootId);
            Assert.NotNull(host);
            host.Start();
            await SendInvalidHelloAsync(host.Endpoint, cancellationToken);

            var snapshot = await SendCommandAsync(host.Endpoint, "get-snapshot", "snapshot-key", cancellationToken);

            Assert.Equal(0, snapshot.GetProperty("result").GetProperty("activationCount").GetInt32());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task NegotiatedSessionRejectsVersionSwitchBeforeDispatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            await using var host = BackgroundHostShell.TryCreate(root, Guid.NewGuid());
            Assert.NotNull(host);
            host.Start();
            await Assert.ThrowsAsync<ProtocolException>(() => SendCommandAsync(host.Endpoint, "activate-ui", "wrong-version", cancellationToken, protocolMajor: 2));
            var snapshot = await SendCommandAsync(host.Endpoint, "get-snapshot", "snapshot-after-wrong-version", cancellationToken);
            Assert.Equal(0, snapshot.GetProperty("result").GetProperty("activationCount").GetInt32());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StartupReconcilesPreviouslyRunningWorkAsInterrupted()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var workId = Guid.NewGuid();
            var firstRun = new HostWorkReconciler(root);
            firstRun.Record(new(workId, DurableWorkStatus.Running));

            var restarted = new HostWorkReconciler(root);

            var state = Assert.Single(restarted.Observe());
            Assert.Equal(workId, state.WorkId);
            Assert.Equal(DurableWorkStatus.InterruptedBySystemOrCrash, state.Status);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void RestartPreservesIdempotentMutationTruth()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
            var command = ProtocolEnvelope.CreateRequest(
                MessageType.Command,
                new("durable-request"),
                1,
                new(new("client-epoch"), 0),
                new(new("durable-key"), new("durable-cancel"), deadline),
                JsonSerializer.SerializeToUtf8Bytes(new { commandType = "activate-ui" }));
            var firstRun = new HostStateAuthority(root);
            Assert.True(firstRun.Dispatch(command).StateChanged);

            var restarted = new HostStateAuthority(root);
            var replay = restarted.Dispatch(command);
            var snapshotCommand = ProtocolEnvelope.CreateRequest(
                MessageType.Command,
                new("snapshot-request"),
                1,
                new(new("client-epoch"), 0),
                new(new("snapshot-key"), new("snapshot-cancel"), deadline),
                JsonSerializer.SerializeToUtf8Bytes(new { commandType = "get-snapshot" }));
            var snapshot = restarted.Dispatch(snapshotCommand);

            Assert.False(replay.StateChanged);
            Assert.Equal(1, snapshot.Body.GetProperty("activationCount").GetInt32());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static async Task<JsonElement> SendCommandAsync(
        HostEndpoint endpoint,
        string commandType,
        string idempotencyKey,
        CancellationToken cancellationToken,
        bool expectStateEvent = false,
        int protocolMajor = HostProtocolVersion.Major)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync(10_000, cancellationToken);
        var challengeKey = Convert.FromBase64String(endpoint.ChallengeKey);
        var clientNonce = RandomNumberGenerator.GetBytes(ConnectionKeyDerivation.NonceBytes);
        var helloBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            dataRootId = endpoint.DataRootId,
            incarnation = endpoint.Incarnation,
            clientNonce = Convert.ToBase64String(clientNonce),
            protocolMajor = 1,
            minimumMinor = 0,
            maximumMinor = 0,
            capabilities = ClientCapabilities,
        });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var hello = ProtocolEnvelope.CreateRequest(
            MessageType.Hello,
            new("hello-request"),
            1,
            new(new("client-epoch"), 0),
            new(new("hello-idempotency"), new("hello-cancellation"), deadline),
            helloBody);
        await HostFrameStream.WriteAsync(client, hello, challengeKey, cancellationToken);
        using var ackFrame = await HostFrameStream.ReadAsync(client, cancellationToken);
        var ack = HostFrameCodec.Decode(ackFrame.Memory.Span, challengeKey, 1).Envelope;
        var hostNonce = Convert.FromBase64String(ack.Body.GetProperty("hostNonce").GetString()!);
        var connectionKey = ConnectionKeyDerivation.Derive(challengeKey, clientNonce, hostNonce);
        try
        {
            var commandBody = JsonSerializer.SerializeToUtf8Bytes(new { commandType });
            var command = ProtocolEnvelope.CreateRequest(
                MessageType.Command,
                new("command-request"),
                1,
                ack.State,
                new(new(idempotencyKey), new("command-cancellation"), deadline),
                commandBody) with
            { ProtocolMajor = protocolMajor };
            await HostFrameStream.WriteAsync(client, command, connectionKey, cancellationToken);
            using var responseFrame = await HostFrameStream.ReadAsync(client, cancellationToken);
            var response = HostFrameCodec.Decode(responseFrame.Memory.Span, connectionKey, 1).Envelope;
            if (expectStateEvent)
            {
                using var eventFrame = await HostFrameStream.ReadAsync(client, cancellationToken);
                var stateEvent = HostFrameCodec.Decode(eventFrame.Memory.Span, connectionKey, 2).Envelope;
                Assert.Equal(MessageType.Event, stateEvent.MessageType);
                Assert.Equal(response.State, stateEvent.State);
            }

            return response.Body.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeKey);
            CryptographicOperations.ZeroMemory(clientNonce);
            CryptographicOperations.ZeroMemory(hostNonce);
            CryptographicOperations.ZeroMemory(connectionKey);
        }
    }

    private static async Task SendInvalidHelloAsync(HostEndpoint endpoint, CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(10_000, cancellationToken);
        var invalidKey = RandomNumberGenerator.GetBytes(32);
        var hello = ProtocolEnvelope.CreateRequest(
            MessageType.Hello,
            new("invalid-hello"),
            1,
            new(new("client-epoch"), 0),
            new(new("invalid-idempotency"), new("invalid-cancellation"), DateTimeOffset.UtcNow.AddMinutes(1)),
            "{}"u8);
        await HostFrameStream.WriteAsync(client, hello, invalidKey, cancellationToken);
        var buffer = new byte[1];
        var read = await client.ReadAsync(buffer, cancellationToken);
        Assert.Equal(0, read);
    }

    private static void DeleteTemporaryRoot(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Host.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
