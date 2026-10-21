namespace TeachMeOnce.InputCorrelation;

internal sealed class CorrelationReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public uint TargetProcessId { get; init; }
    public long DroppedInputEvents { get; init; }
    public int EventCount { get; init; }
    public required IReadOnlyList<CorrelationRecord> Events { get; init; }
}

internal sealed class CorrelationRecord
{
    public long Sequence { get; init; }
    public required string InputKind { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public long MonotonicTimestamp { get; init; }
    public bool Injected { get; init; }
    public bool IsDown { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public uint? VirtualKey { get; init; }
    public uint? ScanCode { get; init; }
    public WindowSnapshot? Window { get; init; }
    public ElementSnapshot? TargetElement { get; init; }
    public ElementSnapshot? FocusedElement { get; init; }
    public bool TargetProcessMatch { get; init; }
    public string? Failure { get; init; }
}

internal sealed class WindowSnapshot
{
    public long Handle { get; init; }
    public uint ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
}

internal sealed class ElementSnapshot
{
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsPassword { get; init; }
}
