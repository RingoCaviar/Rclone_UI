using System.Collections.Concurrent;
using System.Text.Json;

namespace RcloneUI.Rclone;

public sealed record ScriptedRcloneStep(
    RclonePrimitive Primitive,
    RcloneTransferStats Stats,
    RcloneExecutionResult Result,
    TimeSpan? Delay = null);

public sealed class ScriptedRcloneRuntime : IRcloneRuntime
{
    private readonly ConcurrentQueue<ScriptedRcloneStep> steps;
    private readonly ConcurrentDictionary<Guid, ScriptedRcloneStep> active = new();
    private readonly ConcurrentDictionary<Guid, byte> cancelled = new();
    private long jobId;

    public ScriptedRcloneRuntime(RcloneCapabilitySnapshot capabilities, IEnumerable<ScriptedRcloneStep> steps)
    {
        Capabilities = capabilities;
        this.steps = new(steps);
    }

    public RcloneCapabilitySnapshot Capabilities { get; }
    public IReadOnlyCollection<RcloneExecutionRequest> Requests => requests;
    private readonly List<RcloneExecutionRequest> requests = [];

    public ValueTask<RcloneExecutionHandle> StartAsync(RcloneExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(request.ExpectedCapabilityBinding, Capabilities.Binding))
            throw new RcloneCapabilityChangedException(request.ExpectedCapabilityBinding, Capabilities.Binding);
        var endpoint = RcloneRequestMapper.Map(request).Endpoint;
        if (!Capabilities.Endpoints.Contains(endpoint))
            throw new NotSupportedException($"The scripted rclone does not expose '{endpoint}'.");
        if (!steps.TryDequeue(out var step) || step.Primitive != request.Primitive)
            throw new InvalidOperationException("The scripted rclone invocation did not match the expected step.");
        lock (requests) requests.Add(request);
        active[request.ExecutionId] = step;
        return ValueTask.FromResult(new RcloneExecutionHandle(request.ExecutionId, Interlocked.Increment(ref jobId), request.Group));
    }

    public ValueTask<RcloneTransferStats> GetStatsAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(active[handle.ExecutionId].Stats);
    }

    public async ValueTask<RcloneExecutionResult> WaitAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        if (cancelled.TryRemove(handle.ExecutionId, out _))
            return new(false, true, "cancelled", EmptyBody());
        var step = active[handle.ExecutionId];
        if (step.Delay is not null) await Task.Delay(step.Delay.Value, cancellationToken).ConfigureAwait(false);
        active.TryRemove(handle.ExecutionId, out _);
        return step.Result;
    }

    public ValueTask CancelAsync(RcloneExecutionHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (active.TryRemove(handle.ExecutionId, out _)) cancelled[handle.ExecutionId] = 0;
        return ValueTask.CompletedTask;
    }
    public ValueTask<string> ObscureAsync(string clearText, CancellationToken cancellationToken) => ValueTask.FromResult(clearText);

    public static RcloneExecutionResult Success() => new(true, false, null, EmptyBody());

    private static JsonElement EmptyBody()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
