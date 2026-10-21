using System.Windows.Automation;
using System.Runtime.InteropServices;
using TeachMeOnce.Automation;

namespace TeachMeOnce.Application;

public sealed record ActionAdapterResult(bool Succeeded, ExecutionStatus FailureStatus, string Reason)
{
    public static ActionAdapterResult Success(string reason) => new(true, ExecutionStatus.Completed, reason);

    public static ActionAdapterResult Unsupported(string reason) => new(false, ExecutionStatus.Unsupported, reason);

    public static ActionAdapterResult Failed(string reason) => new(false, ExecutionStatus.Failed, reason);
}

public interface IWorkflowActionAdapter
{
    string ActionType { get; }

    IReadOnlyList<string> RequiredPatterns { get; }

    ActionAdapterResult Execute(
        WorkflowAction action,
        AutomationElement target,
        CancellationToken cancellationToken);
}

public sealed class WorkflowActionAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowActionAdapter> _adapters;

    public WorkflowActionAdapterRegistry(IEnumerable<IWorkflowActionAdapter>? adapters = null)
    {
        var registered = (adapters ?? CreateDefaultAdapters()).ToArray();
        if (registered.Any(adapter => string.IsNullOrWhiteSpace(adapter.ActionType)))
        {
            throw new ArgumentException("Every action adapter must declare an action type.", nameof(adapters));
        }

        if (registered.GroupBy(adapter => adapter.ActionType, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Action adapter types must be unique.", nameof(adapters));
        }

        _adapters = registered.ToDictionary(adapter => adapter.ActionType, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> SupportedActionTypes => _adapters.Keys.ToArray();

    public bool TryGet(string actionType, out IWorkflowActionAdapter? adapter)
    {
        return _adapters.TryGetValue(actionType, out adapter);
    }

    private static IEnumerable<IWorkflowActionAdapter> CreateDefaultAdapters()
    {
        yield return new InvokeActionAdapter();
        yield return new SetValueActionAdapter();
        yield return new TextInputActionAdapter();
    }
}

internal sealed class InvokeActionAdapter : IWorkflowActionAdapter
{
    public string ActionType => "Invoke";

    public IReadOnlyList<string> RequiredPatterns { get; } = new[] { "Invoke" };

    public ActionAdapterResult Execute(
        WorkflowAction action,
        AutomationElement target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
            || patternObject is not InvokePattern pattern)
        {
            return ActionAdapterResult.Unsupported("Resolved target does not expose InvokePattern.");
        }

        pattern.Invoke();
        return ActionAdapterResult.Success("InvokePattern execution completed.");
    }
}

internal sealed class SetValueActionAdapter : IWorkflowActionAdapter
{
    public string ActionType => "SetValue";

    public IReadOnlyList<string> RequiredPatterns { get; } = new[] { "Value" };

    public ActionAdapterResult Execute(
        WorkflowAction action,
        AutomationElement target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.InputValue is null)
        {
            return ActionAdapterResult.Unsupported("Text entry is unavailable because the value was not safely recorded.");
        }

        if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject)
            || patternObject is not ValuePattern pattern)
        {
            return ActionAdapterResult.Unsupported("Resolved target does not expose ValuePattern.");
        }

        pattern.SetValue(action.InputValue);
        return ActionAdapterResult.Success("ValuePattern execution completed.");
    }
}

internal sealed class TextInputActionAdapter : IWorkflowActionAdapter
{
    public string ActionType => "TextInput";

    public IReadOnlyList<string> RequiredPatterns { get; } = new[] { "Text or Value" };

    public ActionAdapterResult Execute(
        WorkflowAction action,
        AutomationElement target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.InputValue is null
            || RecordingActionBuilder.RecordingPrivacyPolicy.IsSensitive(
                action.Target.Element,
                RecordingActionBuilder.RecordingPrivacyPolicy.DefaultSensitiveNames))
        {
            return ActionAdapterResult.Unsupported("Text entry is unavailable because the value was not safely recorded.");
        }

        if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject)
            && valueObject is ValuePattern valuePattern)
        {
            try
            {
                valuePattern.SetValue(action.InputValue);
                return ActionAdapterResult.Success("ValuePattern execution completed for a TextInput action.");
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // Some providers advertise ValuePattern but reject writes; use the
                // explicit keyboard path only after the direct semantic write fails.
            }
        }

        if (!SemanticTextInput.TrySetText(target, action.InputValue, out var reason))
        {
            return ActionAdapterResult.Failed(reason);
        }

        return ActionAdapterResult.Success("Semantic keyboard text execution completed.");
    }
}
