namespace RcloneUI.Contracts.HostProtocol.V1;

public readonly record struct RequestId
{
    public RequestId(string value) => Value = ProtocolIdentifier.Validate(value, nameof(RequestId));

    public string Value { get; }
}

public readonly record struct IdempotencyKey
{
    public IdempotencyKey(string value) => Value = ProtocolIdentifier.Validate(value, nameof(IdempotencyKey));

    public string Value { get; }
}

public readonly record struct CancellationId
{
    public CancellationId(string value) => Value = ProtocolIdentifier.Validate(value, nameof(CancellationId));

    public string Value { get; }
}

public readonly record struct StateEpoch
{
    public StateEpoch(string value) => Value = ProtocolIdentifier.Validate(value, nameof(StateEpoch));

    public string Value { get; }
}

public readonly record struct EventId
{
    public EventId(string value) => Value = ProtocolIdentifier.Validate(value, nameof(EventId));

    public string Value { get; }
}

internal static class ProtocolIdentifier
{
    internal const int MaximumLength = 128;

    internal static string Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > MaximumLength || value.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentOutOfRangeException(name, $"Protocol identifiers must contain 1 to {MaximumLength} non-control characters.");
        }

        return value;
    }
}
