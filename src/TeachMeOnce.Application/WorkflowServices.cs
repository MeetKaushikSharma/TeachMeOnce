using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using TeachMeOnce.AsyncWaits;
using TeachMeOnce.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Application;

public sealed class JsonWorkflowStore : IWorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;

    public JsonWorkflowStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachMeOnce",
            "Workflows");
    }

    public async Task<WorkflowLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var workflows = new List<WorkflowDocument>();
        var issues = new List<WorkflowLoadIssue>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var workflow = await JsonSerializer.DeserializeAsync<WorkflowDocument>(stream, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException($"Workflow file '{path}' was empty.");
                Validate(workflow, path);
                workflows.Add(workflow);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                issues.Add(new WorkflowLoadIssue
                {
                    Source = path,
                    Message = exception.Message
                });
            }
        }

        return new WorkflowLoadResult
        {
            Workflows = workflows.OrderBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            Issues = issues
        };
    }

    public async Task SaveAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        Validate(workflow, workflow.Name);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{workflow.Id:N}.json");
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, workflow, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }

    public Task<bool> DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_directory, $"{workflowId:N}.json");
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private static void Validate(WorkflowDocument workflow, string source)
    {
        if (workflow.SchemaVersion != WorkflowSchema.CurrentVersion)
        {
            throw new InvalidDataException($"Workflow '{source}' uses schema version {workflow.SchemaVersion}; expected {WorkflowSchema.CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            throw new InvalidDataException($"Workflow '{source}' has no name.");
        }

        if (workflow.Actions is null || workflow.Actions.Count == 0)
        {
            throw new InvalidDataException($"Workflow '{source}' has no actions.");
        }

        foreach (var action in workflow.Actions)
        {
            if (action is null || string.IsNullOrWhiteSpace(action.ActionType) || action.Target is null || action.Target.Element is null)
            {
                throw new InvalidDataException($"Workflow '{source}' contains an incomplete action.");
            }

            if (string.Equals(action.ActionType, "SetValue", StringComparison.Ordinal)
                || string.Equals(action.ActionType, "TextInput", StringComparison.Ordinal))
            {
                if (action.InputValue is null
                    || RecordingActionBuilder.RecordingPrivacyPolicy.IsSensitive(
                        action.Target.Element,
                        RecordingActionBuilder.RecordingPrivacyPolicy.DefaultSensitiveNames))
                {
                    throw new InvalidDataException($"Workflow '{source}' contains unsafe text-entry data.");
                }
            }

            if (string.Equals(action.ActionType, "Toggle", StringComparison.Ordinal)
                && (action.ExpectedValue is null || !WorkflowSchema.SupportedToggleStates.Contains(action.ExpectedValue)))
            {
                throw new InvalidDataException($"Workflow '{source}' contains a Toggle action without a canonical expected state.");
            }

            if (string.Equals(action.ActionType, "Select", StringComparison.Ordinal)
                && !string.Equals(action.ExpectedValue, WorkflowSchema.SelectedState, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Workflow '{source}' contains a Select action without the canonical expected state.");
            }
        }
    }
}

public sealed class DiagnosticLog : IDiagnosticSink
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?<prefix>\b(?:password|passwd|pwd|secret|token|api[-_]?key|authorization)\b\s*[:=]\s*)(?<value>[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<DiagnosticEntry> _entries = new();
    private readonly string? _filePath;
    private readonly SemaphoreSlim _fileWriteGate = new(1, 1);
    private readonly List<Task> _pendingWrites = new();

    public DiagnosticLog(string? filePath = null)
    {
        _filePath = filePath;
    }

    public event EventHandler<DiagnosticEntry>? EntryAdded;
    public IReadOnlyList<DiagnosticEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(string level, string message, Exception? exception = null)
    {
        var entry = new DiagnosticEntry
        {
            Level = level,
            Message = Redact(message),
            ExceptionType = exception?.GetType().Name
        };
        lock (_gate)
        {
            _entries.Add(entry);
            if (_filePath is not null)
            {
                var write = Task.Run(() => PersistAsync(entry));
                _pendingWrites.Add(write);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public async Task FlushAsync()
    {
        Task[] writes;
        lock (_gate)
        {
            writes = _pendingWrites.ToArray();
            _pendingWrites.Clear();
        }

        await Task.WhenAll(writes);
    }

    private async Task PersistAsync(DiagnosticEntry entry)
    {
        await _fileWriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath!);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                _filePath!,
                $"{entry.TimestampUtc:O} [{entry.Level}] {entry.Message}{Environment.NewLine}")
                .ConfigureAwait(false);
        }
        finally
        {
            _fileWriteGate.Release();
        }
    }

    private static string Redact(string message)
    {
        return SensitiveAssignment.Replace(message, "${prefix}[REDACTED]");
    }
}

public sealed class UiAutomationWorkflowRecorder : IWorkflowRecorder
{
    private readonly IDiagnosticSink _diagnostics;
    private readonly ProcessIdentityPolicy _identityPolicy;

    public UiAutomationWorkflowRecorder(
        IDiagnosticSink diagnostics,
        ProcessIdentityPolicy identityPolicy = ProcessIdentityPolicy.ProcessIdAndName)
    {
        _diagnostics = diagnostics;
        _identityPolicy = identityPolicy;
    }

    public WorkflowAction RecordSemanticButtonClick(
        AutomationElement window,
        string automationId,
        string description,
        string? verificationAutomationId,
        string? expectedValue)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var matches = window.FindAll(TreeScope.Descendants, condition);
        if (matches.Count != 1)
        {
            var message = $"Recording refused: expected exactly one '{automationId}' target, found {matches.Count}.";
            _diagnostics.Write("Error", message);
            throw new InvalidOperationException(message);
        }

        var target = matches[0];
        if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out _))
        {
            var message = $"Recording refused: '{automationId}' does not expose InvokePattern.";
            _diagnostics.Write("Error", message);
            throw new InvalidOperationException(message);
        }

        var descriptor = TargetDescriptorBuilder.Build(window, target, _identityPolicy);
        _diagnostics.Write("Info", $"Recorded semantic click for '{automationId}'.");
        return new WorkflowAction
        {
            ActionType = "Invoke",
            Description = description,
            Target = descriptor,
            VerificationAutomationId = verificationAutomationId,
            ExpectedValue = expectedValue
        };
    }
}

public sealed class UiAutomationWorkflowExecutor : IWorkflowExecutor
{
    private readonly UiAutomationWaiter _waiter;
    private readonly TargetResolver _resolver;
    private readonly IDiagnosticSink _diagnostics;
    private readonly WorkflowActionAdapterRegistry _adapters;
    private readonly int _processId;
    private readonly string _windowTitle;
    private readonly Action<string>? _progress;

    public UiAutomationWorkflowExecutor(
        int processId,
        string windowTitle,
        IDiagnosticSink diagnostics,
        UiAutomationWaiter? waiter = null,
        TargetResolver? resolver = null,
        Action<string>? progress = null,
        WorkflowActionAdapterRegistry? adapters = null)
    {
        _processId = processId;
        _windowTitle = windowTitle;
        _diagnostics = diagnostics;
        _waiter = waiter ?? new UiAutomationWaiter();
        _resolver = resolver ?? new TargetResolver();
        _progress = progress;
        _adapters = adapters ?? new WorkflowActionAdapterRegistry();
    }

    public async Task<ExecutionResult> ExecuteAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var completedActionCount = 0;
        if (workflow.SchemaVersion != WorkflowSchema.CurrentVersion)
        {
            return Result(ExecutionStatus.Failed, "Workflow schema version is not supported.", 0, started);
        }

        try
        {
            for (var index = 0; index < workflow.Actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = workflow.Actions[index];
                _progress?.Invoke($"Replaying action {index + 1} of {workflow.Actions.Count}: {action.Description}");
                if (!_adapters.TryGet(action.ActionType, out var adapter) || adapter is null)
                {
                    return Result(ExecutionStatus.Unsupported, $"Action type '{action.ActionType}' is not supported.", completedActionCount, started);
                }

                if (!_adapters.IsDescriptorCompatible(adapter, action.Target.Element))
                {
                    return Result(
                        ExecutionStatus.Unsupported,
                        $"Action type '{action.ActionType}' requires UI Automation capabilities that are absent from the persisted target descriptor.",
                        completedActionCount,
                        started);
                }

                var windowResult = await WaitForActionWindowAsync(action.Target, TimeSpan.FromSeconds(5), cancellationToken);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    return Result(MapWaitStatus(windowResult.Status), windowResult.Reason, completedActionCount, started);
                }

                var window = windowResult.Value;

                if (!MatchesIntegrityPolicy(window, action.Target, out var integrityReason))
                {
                    _diagnostics.Write("Warning", $"Replay refused: {integrityReason}");
                    return Result(ExecutionStatus.Unsupported, integrityReason, completedActionCount, started);
                }

                var resolution = _resolver.Resolve(window, action.Target);
                if (resolution.Status != ResolutionStatus.Resolved || resolution.Element is null)
                {
                    var status = MapResolutionStatus(resolution.Status);
                    _diagnostics.Write("Error", $"Replay refused: {resolution.Reason}");
                    return Result(status, resolution.Reason, index, started);
                }

                var adapterResult = adapter.Execute(action, resolution.Element, cancellationToken);
                if (!adapterResult.Succeeded)
                {
                    return Result(adapterResult.FailureStatus, adapterResult.Reason, completedActionCount, started);
                }

                if (string.Equals(action.ActionType, "Invoke", StringComparison.Ordinal))
                {
                    _diagnostics.Write("Info", $"Invoked action {index + 1} of {workflow.Actions.Count}.");
                }
                else if (string.Equals(action.ActionType, "Toggle", StringComparison.Ordinal))
                {
                    _diagnostics.Write("Info", $"Toggled action {index + 1} of {workflow.Actions.Count} to {action.ExpectedValue}.");
                    if (!await WaitForToggleStateAsync(action.Target, action.ExpectedValue!, cancellationToken))
                    {
                        return Result(ExecutionStatus.TimedOut, "Toggle completed without reaching the expected state.", completedActionCount, started);
                    }
                }
                else if (string.Equals(action.ActionType, "Select", StringComparison.Ordinal))
                {
                    _diagnostics.Write("Info", $"Selected action {index + 1} of {workflow.Actions.Count}.");
                    if (!await WaitForSelectionAsync(action.Target, cancellationToken))
                    {
                        return Result(ExecutionStatus.TimedOut, "Selection completed without reaching the selected state.", completedActionCount, started);
                    }
                }
                else if (string.Equals(action.ActionType, "ExpandCollapse", StringComparison.Ordinal))
                {
                    _diagnostics.Write("Info", $"Set expand/collapse action {index + 1} of {workflow.Actions.Count} to {action.ExpectedValue}.");
                    if (!await WaitForExpandCollapseStateAsync(action.Target, action.ExpectedValue!, cancellationToken))
                    {
                        return Result(ExecutionStatus.TimedOut, "Expand/collapse completed without reaching the expected state.", completedActionCount, started);
                    }
                }
                else
                {
                    var inputValue = action.InputValue!;
                    _diagnostics.Write(
                        "Info",
                        string.Equals(action.ActionType, "SetValue", StringComparison.Ordinal)
                            ? $"Set text for action {index + 1} of {workflow.Actions.Count}; value contents were not logged."
                            : $"Sent semantic text input for action {index + 1} of {workflow.Actions.Count}; value contents were not logged.");

                    if (!await WaitForTextAsync(action.Target, inputValue, cancellationToken))
                    {
                        return Result(ExecutionStatus.TimedOut, "Text entry completed without reaching the expected value.", completedActionCount, started);
                    }
                }
                if (action.VerificationAutomationId is not null && action.ExpectedValue is not null)
                {
                    var verification = await WaitForExpectedValueAsync(action.Target, action.VerificationAutomationId, action.ExpectedValue, cancellationToken);
                    if (verification.Status != VerificationStatus.Satisfied)
                    {
                        var status = verification.Status == VerificationStatus.Ambiguous
                            ? ExecutionStatus.Ambiguous
                            : ExecutionStatus.TimedOut;
                        _diagnostics.Write("Error", verification.Reason);
                        return Result(status, verification.Reason, completedActionCount, started);
                    }
                }

                completedActionCount++;
            }

            return Result(ExecutionStatus.Completed, "Workflow replay completed and verification passed.", completedActionCount, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics.Write("Info", $"Workflow replay cancelled after {completedActionCount} completed action(s).");
            return Result(ExecutionStatus.Cancelled, "Workflow replay was cancelled.", completedActionCount, started);
        }
        catch (Exception exception)
        {
            _diagnostics.Write("Error", "Workflow replay failed.", exception);
            return Result(ExecutionStatus.Failed, "Workflow replay failed; see diagnostics for the error type.", completedActionCount, started);
        }
    }

    private Task<WaitResult<AutomationElement>> WaitForActionWindowAsync(
        TargetDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return descriptor.Process.IdentityPolicy == ProcessIdentityPolicy.ProcessName
            ? _waiter.WaitForWindowAsync(descriptor, timeout, cancellationToken)
            : _waiter.WaitForWindowAsync(_processId, _windowTitle, timeout, cancellationToken);
    }

    private async Task<VerificationResult> WaitForExpectedValueAsync(
        TargetDescriptor descriptor,
        string automationId,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        string? lastReason = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var windowResult = await WaitForActionWindowAsync(descriptor, remaining, cancellationToken).ConfigureAwait(false);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    lastReason = windowResult.Reason;
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var window = windowResult.Value;
                var matches = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                if (matches.Count > 1)
                {
                    return new VerificationResult(
                        VerificationStatus.Ambiguous,
                        $"Verification target '{automationId}' is ambiguous: found {matches.Count} matches.");
                }

                var evaluation = VerificationPolicy.Evaluate(
                    Enumerable.Range(0, matches.Count)
                        .Select(index => matches[index].Current.Name)
                        .ToArray(),
                    expectedValue);
                if (evaluation.Outcome == VerificationOutcome.Satisfied)
                {
                    return new VerificationResult(VerificationStatus.Satisfied, $"Verification target '{automationId}' reached the expected value.");
                }

                lastReason = evaluation.Outcome == VerificationOutcome.NotFound
                    ? $"Verification target '{automationId}' was not found in the current UI tree."
                    : $"Verification target '{automationId}' was found, but its value did not match the expected value.";
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                lastReason = $"Verification target '{automationId}' was temporarily unavailable: {exception.GetType().Name}.";
            }

            await Task.Delay(50, cancellationToken);
        }

        return new VerificationResult(
            VerificationStatus.TimedOut,
            lastReason ?? $"Verification target '{automationId}' did not reach the expected value.");
    }

    private async Task<bool> WaitForTextAsync(
        TargetDescriptor descriptor,
        string expectedText,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var windowResult = await WaitForActionWindowAsync(descriptor, remaining, cancellationToken).ConfigureAwait(false);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var window = windowResult.Value;
                var resolution = _resolver.Resolve(window, descriptor);
                if (resolution.Status == ResolutionStatus.Resolved && resolution.Element is not null)
                {
                    if (resolution.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject)
                        && valueObject is ValuePattern valuePattern
                        && string.Equals(valuePattern.Current.Value, expectedText, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (resolution.Element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject)
                        && textObject is TextPattern textPattern
                        && string.Equals(textPattern.DocumentRange.GetText(-1), expectedText, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // The provider can be stale; retry the same descriptor within the bounded deadline.
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForToggleStateAsync(
        TargetDescriptor descriptor,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var windowResult = await WaitForActionWindowAsync(descriptor, remaining, cancellationToken).ConfigureAwait(false);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resolution = _resolver.Resolve(windowResult.Value, descriptor);
                if (resolution.Status == ResolutionStatus.Resolved
                    && resolution.Element is not null
                    && resolution.Element.TryGetCurrentPattern(TogglePattern.Pattern, out var patternObject)
                    && patternObject is TogglePattern pattern
                    && string.Equals(pattern.Current.ToggleState.ToString(), expectedState, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Reacquire the same descriptor within the bounded verification window.
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForSelectionAsync(
        TargetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var windowResult = await WaitForActionWindowAsync(descriptor, remaining, cancellationToken).ConfigureAwait(false);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resolution = _resolver.Resolve(windowResult.Value, descriptor);
                if (resolution.Status == ResolutionStatus.Resolved
                    && resolution.Element is not null
                    && resolution.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject)
                    && patternObject is SelectionItemPattern pattern
                    && pattern.Current.IsSelected)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Reacquire the same descriptor within the bounded verification window.
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForExpandCollapseStateAsync(
        TargetDescriptor descriptor,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var windowResult = await WaitForActionWindowAsync(descriptor, remaining, cancellationToken).ConfigureAwait(false);
                if (windowResult.Status != WaitStatus.Satisfied || windowResult.Value is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resolution = _resolver.Resolve(windowResult.Value, descriptor);
                if (resolution.Status == ResolutionStatus.Resolved
                    && resolution.Element is not null
                    && resolution.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var patternObject)
                    && patternObject is ExpandCollapsePattern pattern
                    && string.Equals(pattern.Current.ExpandCollapseState.ToString(), expectedState, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Reacquire the same descriptor within the bounded verification window.
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool MatchesIntegrityPolicy(AutomationElement window, TargetDescriptor descriptor, out string reason)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Process.IntegrityLevel))
        {
            reason = string.Empty;
            return true;
        }

        var processId = TargetDescriptorBuilder.ReadInt(window, AutomationElement.ProcessIdProperty);
        if (processId is null)
        {
            reason = "The target process integrity could not be inspected.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            var integrity = IntegrityInspector.ReadProcess(process);
            if (!integrity.Available)
            {
                reason = "The target process integrity could not be inspected.";
                return false;
            }

            if (!string.Equals(integrity.Level.ToString(), descriptor.Process.IntegrityLevel, StringComparison.Ordinal))
            {
                reason = "The target process integrity changed since recording; higher- or mismatched-integrity interaction is unsupported.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            reason = "The target process exited before integrity validation.";
            return false;
        }
    }

    private enum VerificationStatus
    {
        Satisfied,
        TimedOut,
        Ambiguous
    }

    private sealed record VerificationResult(VerificationStatus Status, string Reason);

    private static ExecutionStatus MapWaitStatus(WaitStatus status) => status switch
    {
        WaitStatus.Cancelled => ExecutionStatus.Cancelled,
        WaitStatus.TimedOut => ExecutionStatus.TimedOut,
        WaitStatus.NotFound => ExecutionStatus.NotFound,
        WaitStatus.Ambiguous => ExecutionStatus.Ambiguous,
        _ => ExecutionStatus.Failed
    };

    public static ExecutionStatus MapResolutionStatus(ResolutionStatus status) => status switch
    {
        ResolutionStatus.NotFound => ExecutionStatus.NotFound,
        ResolutionStatus.Ambiguous => ExecutionStatus.Ambiguous,
        ResolutionStatus.ContextMismatch => ExecutionStatus.ContextMismatch,
        _ => ExecutionStatus.Failed
    };

    private static ExecutionResult Result(ExecutionStatus status, string message, int completed, long started) => new()
    {
        Status = status,
        Message = message,
        CompletedActionCount = completed,
        Elapsed = Stopwatch.GetElapsedTime(started)
    };
}
