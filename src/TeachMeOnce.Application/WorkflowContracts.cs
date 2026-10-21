using System.Text.Json.Serialization;
using TeachMeOnce.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Application;

public static class WorkflowSchema
{
    public const int CurrentVersion = 1;

    public static readonly IReadOnlySet<string> SupportedActionTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Invoke",
            "SetValue",
            "TextInput",
            "Toggle",
            "Select",
            "ExpandCollapse"
        };

    public static readonly IReadOnlySet<string> SupportedToggleStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "On",
            "Off",
            "Indeterminate"
        };

    public const string SelectedState = "Selected";

    public static readonly IReadOnlySet<string> SupportedExpandCollapseStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Expanded",
            "Collapsed"
        };
}

public sealed class WorkflowDocument
{
    public int SchemaVersion { get; init; } = WorkflowSchema.CurrentVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required IReadOnlyList<WorkflowAction> Actions { get; init; }
}

public sealed class WorkflowAction
{
    public required string ActionType { get; init; }
    public required string Description { get; init; }
    public required TargetDescriptor Target { get; init; }
    public string? VerificationAutomationId { get; init; }
    public string? ExpectedValue { get; init; }
    public string? InputValue { get; init; }
    public string? InputMethod { get; init; }
}

public enum RecordingSessionStatus
{
    Completed,
    Cancelled,
    Overflowed,
    Unsupported,
    Failed
}

public sealed record RecordingSessionResult
{
    public required RecordingSessionStatus Status { get; init; }
    public required IReadOnlyList<WorkflowAction> Actions { get; init; }
    public required string Message { get; init; }
    public long DroppedEventCount { get; init; }
    public int CorrelatedEventCount { get; init; }
    public RecordingStageCounts? Stages { get; init; }
}

public sealed record RecordingStageCounts
{
    public int RawEventCount { get; init; }
    public int RetainedEventCount { get; init; }
    public int FocusedElementCount { get; init; }
    public int DescriptorCount { get; init; }
    public int SupportedPatternCount { get; init; }
    public int TextSnapshotCount { get; init; }
    public int ProducedActionCount { get; init; }
    public int RejectedEventCount { get; init; }
    public required IReadOnlyDictionary<string, int> Rejections { get; init; }
}

public sealed class ExternalRecordingSessionOptions
{
    public required ExternalTarget Target { get; init; }
    public int QueueCapacity { get; init; } = 2048;
    public IReadOnlySet<string> SensitiveAutomationNames { get; init; } = RecordingActionBuilder.RecordingPrivacyPolicy.DefaultSensitiveNames;
}

public enum ExecutionStatus
{
    Completed,
    Cancelled,
    TimedOut,
    NotFound,
    Ambiguous,
    ContextMismatch,
    Unsupported,
    Failed
}

public sealed class ExecutionResult
{
    public required ExecutionStatus Status { get; init; }
    public required string Message { get; init; }
    public int CompletedActionCount { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public enum VerificationOutcome
{
    Satisfied,
    NotFound,
    ValueMismatch,
    Ambiguous
}

public sealed record VerificationEvaluation(VerificationOutcome Outcome, string Reason);

public static class VerificationPolicy
{
    public static VerificationEvaluation Evaluate(IReadOnlyList<string?> observedValues, string expectedValue)
    {
        if (observedValues.Count == 0)
        {
            return new VerificationEvaluation(VerificationOutcome.NotFound, "The verification target was not found in the current UI tree.");
        }

        if (observedValues.Count > 1)
        {
            return new VerificationEvaluation(
                VerificationOutcome.Ambiguous,
                $"The verification target is ambiguous: found {observedValues.Count} matches.");
        }

        return string.Equals(observedValues[0], expectedValue, StringComparison.Ordinal)
            ? new VerificationEvaluation(VerificationOutcome.Satisfied, "The verification target reached the expected value.")
            : new VerificationEvaluation(VerificationOutcome.ValueMismatch, "The verification target was found, but its value did not match the expected value.");
    }
}

public sealed class DiagnosticEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
}

public interface IDiagnosticSink
{
    void Write(string level, string message, Exception? exception = null);
}

public interface IWorkflowStore
{
    Task<WorkflowLoadResult> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(WorkflowDocument workflow, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid workflowId, CancellationToken cancellationToken);
    Task ExportAsync(WorkflowDocument workflow, string destinationPath, CancellationToken cancellationToken);
    Task<WorkflowDocument> ImportAsync(string sourcePath, CancellationToken cancellationToken);
}

public sealed class WorkflowLoadResult
{
    public required IReadOnlyList<WorkflowDocument> Workflows { get; init; }
    public required IReadOnlyList<WorkflowLoadIssue> Issues { get; init; }
}

public sealed class WorkflowLoadIssue
{
    public required string Source { get; init; }
    public required string Message { get; init; }
}

public interface IWorkflowRecorder
{
    WorkflowAction RecordSemanticButtonClick(
        System.Windows.Automation.AutomationElement window,
        string automationId,
        string description,
        string? verificationAutomationId,
        string? expectedValue);
}

public interface IWorkflowExecutor
{
    Task<ExecutionResult> ExecuteAsync(WorkflowDocument workflow, CancellationToken cancellationToken);
}
