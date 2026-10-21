using System.Runtime.InteropServices;
using System.Windows.Automation;
using TeachMeOnce.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Application;

public sealed class ExternalRecordingSession : IAsyncDisposable
{
    private readonly ExternalRecordingSessionOptions _options;
    private readonly IDiagnosticSink _diagnostics;
    private readonly InputHookSession _capture;
    private readonly List<CorrelatedInputEvent> _correlated = new();
    private readonly object _correlationGate = new();
    private int _started;
    private int _stopped;

    public ExternalRecordingSession(ExternalRecordingSessionOptions options, IDiagnosticSink diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
        _capture = new InputHookSession(options.QueueCapacity, CorrelateInput);
    }

    public ExternalTarget Target => _options.Target;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The recording session has already started.");
        }

        return Task.Run(() =>
        {
            try
            {
                _capture.Start();
                _diagnostics.Write("Info", $"Recording started for {_options.Target.Window.ProcessName} (PID {_options.Target.Window.ProcessId}).");
            }
            catch
            {
                Interlocked.Exchange(ref _stopped, 1);
                throw;
            }
        }, cancellationToken);
    }

    public async Task<RecordingSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new RecordingSessionResult
            {
                Status = RecordingSessionStatus.Cancelled,
                Actions = Array.Empty<WorkflowAction>(),
                Message = "Recording was cancelled before it started."
            };
        }

        if (Volatile.Read(ref _started) == 0)
        {
            return new RecordingSessionResult
            {
                Status = RecordingSessionStatus.Failed,
                Actions = Array.Empty<WorkflowAction>(),
                Message = "The recording session was stopped before it started."
            };
        }

        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return new RecordingSessionResult
            {
                Status = RecordingSessionStatus.Failed,
                Actions = Array.Empty<WorkflowAction>(),
                Message = "The recording session was already stopped."
            };
        }

        try
        {
            var capture = await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
            if (capture.Overflowed)
            {
                _diagnostics.Write("Error", $"Recording stopped because {capture.DroppedCount} input event(s) exceeded the bounded queue.");
                return new RecordingSessionResult
                {
                    Status = RecordingSessionStatus.Overflowed,
                    Actions = Array.Empty<WorkflowAction>(),
                    Message = "Recording stopped because the bounded input queue overflowed.",
                    DroppedEventCount = capture.DroppedCount
                };
            }

            var targetState = new ExternalTargetSelector().Attach(_options.Target.Window);
            if (targetState.Status != ExternalTargetStatus.Attached)
            {
                _diagnostics.Write("Warning", $"Recording target ownership was lost: {targetState.Reason}");
                return new RecordingSessionResult
                {
                    Status = RecordingSessionStatus.Unsupported,
                    Actions = Array.Empty<WorkflowAction>(),
                    Message = "Recording stopped because the selected target was no longer safely attached.",
                    DroppedEventCount = capture.DroppedCount
                };
            }

            CorrelatedInputEvent[] correlated;
            lock (_correlationGate)
            {
                correlated = _correlated.OrderBy(item => item.Input.Sequence).ToArray();
            }
            var actionResult = RecordingActionBuilder.Build(
                _options.Target,
                correlated,
                _options.SensitiveAutomationNames,
                _diagnostics,
                capture.Events.Count);
            return actionResult with
            {
                DroppedEventCount = capture.DroppedCount,
                CorrelatedEventCount = correlated.Length
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics.Write("Info", "Recording cancellation requested.");
            return new RecordingSessionResult
            {
                Status = RecordingSessionStatus.Cancelled,
                Actions = Array.Empty<WorkflowAction>(),
                Message = "Recording was cancelled."
            };
        }
        catch (Exception exception)
        {
            _diagnostics.Write("Error", "External recording failed.", exception);
            return new RecordingSessionResult
            {
                Status = RecordingSessionStatus.Failed,
                Actions = Array.Empty<WorkflowAction>(),
                Message = "External recording failed; see diagnostics for the error type."
            };
        }
    }

    private void CorrelateInput(RawInputEvent input)
    {
        var correlated = new UiAutomationInputCorrelator(_options.SensitiveAutomationNames).Correlate(_options.Target, input);
        lock (_correlationGate)
        {
            _correlated.Add(correlated);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _started) == 1 && Volatile.Read(ref _stopped) == 0)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _capture.Dispose();
    }
}

public static class RecordingActionBuilder
{
    public static RecordingSessionResult Build(
        ExternalTarget target,
        IReadOnlyList<CorrelatedInputEvent> correlated,
        IReadOnlySet<string> sensitiveNames,
        IDiagnosticSink diagnostics,
        int rawEventCount = 0)
    {
        var actions = new List<(long Sequence, WorkflowAction Action)>();
        var textCandidates = new Dictionary<string, TextCandidate>(StringComparer.Ordinal);
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var retainedCount = 0;
        var focusedCount = 0;
        var descriptorCount = 0;
        var supportedPatternCount = 0;
        var textSnapshotCount = 0;

        void Reject(string reason)
        {
            rejections[reason] = rejections.TryGetValue(reason, out var count) ? count + 1 : 1;
        }

        foreach (var eventData in correlated.OrderBy(item => item.Input.Sequence))
        {
            if (eventData.Input.Injected)
            {
                Reject("injected input");
                continue;
            }

            if (!eventData.BelongsToSelectedTarget)
            {
                Reject("unrelated window or process");
                continue;
            }

            retainedCount++;
            if (eventData.FocusedElement is not null)
            {
                focusedCount++;
            }

            if (eventData.TargetDescriptor is null)
            {
                Reject(eventData.Failure ?? "no semantic descriptor");
                continue;
            }

            descriptorCount++;
            if (eventData.ValuePatternSupported || eventData.TextPatternSupported)
            {
                supportedPatternCount++;
            }

            if (eventData.TextSnapshot is not null)
            {
                textSnapshotCount++;
            }

            if (eventData.Failure is not null)
            {
                Reject(eventData.Failure);
                continue;
            }

            if (eventData.Input.Kind == RawInputKind.Mouse
                && IsLeftButtonDown(eventData.Input.Message)
                && eventData.TargetElement is not null
                && HasPattern(eventData.TargetElement, "Invoke"))
            {
                actions.Add((eventData.Input.Sequence, new WorkflowAction
                {
                    ActionType = "Invoke",
                    Description = $"Invoke {Describe(eventData.TargetElement)}",
                    Target = eventData.TargetDescriptor
                }));
            }

            if (eventData.Input.Kind == RawInputKind.Keyboard && eventData.TargetDescriptor is not null)
            {
                var key = DescriptorKey(eventData.TargetDescriptor);
                if (!textCandidates.TryGetValue(key, out var existing))
                {
                    textCandidates[key] = new TextCandidate(
                        eventData.Input.Sequence,
                        eventData.TargetDescriptor,
                        eventData.ValuePatternSupported,
                        eventData.TextPatternSupported,
                        eventData.TextSnapshot);
                }
                else
                {
                    textCandidates[key] = existing with
                    {
                        Sequence = eventData.Input.Sequence,
                        ValuePatternSupported = existing.ValuePatternSupported || eventData.ValuePatternSupported,
                        TextPatternSupported = existing.TextPatternSupported || eventData.TextPatternSupported,
                        TextSnapshot = !string.IsNullOrEmpty(eventData.TextSnapshot)
                            ? eventData.TextSnapshot
                            : existing.TextSnapshot
                    };
                }
            }
        }

        var root = TryGetWindow(target.Window.Handle);
        foreach (var candidate in textCandidates.Values)
        {
            var element = candidate.Descriptor.Element;
            if (element.IsPassword == true || RecordingPrivacyPolicy.IsSensitive(element, sensitiveNames))
            {
                Reject("sensitive text target");
                diagnostics.Write("Warning", $"Text entry for {DescribeMetadata(element)} was refused because the target is sensitive.");
                continue;
            }

            if (candidate.TextPatternSupported && !string.IsNullOrEmpty(candidate.TextSnapshot))
            {
                actions.Add((candidate.Sequence, new WorkflowAction
                {
                    ActionType = "TextInput",
                    Description = $"Enter text in {DescribeMetadata(element)}",
                    Target = candidate.Descriptor,
                    InputValue = candidate.TextSnapshot,
                    InputMethod = "KeyboardText"
                }));
                diagnostics.Write(
                    "Info",
                    $"Recorded TextInput target metadata: {DescribeMetadata(element)}, ValuePattern={candidate.ValuePatternSupported}, TextPattern={candidate.TextPatternSupported}.");
                continue;
            }

            if (root is null)
            {
                Reject("target window unavailable during action conversion");
                continue;
            }

            var resolution = new TargetResolver().Resolve(root, candidate.Descriptor);
            if (resolution.Status != ResolutionStatus.Resolved || resolution.Element is null)
            {
                Reject($"text target resolution {resolution.Status}");
                diagnostics.Write("Warning", $"Text entry was not recorded because the focused target metadata was {DescribeMetadata(element)} and resolution was {resolution.Status}.");
                continue;
            }

            try
            {
                diagnostics.Write(
                    "Info",
                    $"Text target metadata: {DescribeMetadata(element)}, ValuePattern={candidate.ValuePatternSupported}, TextPattern={candidate.TextPatternSupported}.");

                if (candidate.ValuePatternSupported
                    && resolution.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject)
                    && valueObject is ValuePattern pattern)
                {
                    var value = pattern.Current.Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        actions.Add((candidate.Sequence, new WorkflowAction
                        {
                            ActionType = "SetValue",
                            Description = $"Set text in {DescribeMetadata(element)}",
                            Target = candidate.Descriptor,
                            InputValue = value,
                            InputMethod = "ValuePattern"
                        }));
                    }

                    continue;
                }

                if (candidate.TextPatternSupported
                    && resolution.Element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject)
                    && textObject is TextPattern textPattern)
                {
                    var text = textPattern.DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(text))
                    {
                        actions.Add((candidate.Sequence, new WorkflowAction
                        {
                            ActionType = "TextInput",
                            Description = $"Enter text in {DescribeMetadata(element)}",
                            Target = candidate.Descriptor,
                            InputValue = text,
                            InputMethod = "KeyboardText"
                        }));
                    }

                    continue;
                }

                Reject("focused target exposed neither writable ValuePattern nor readable TextPattern");
                diagnostics.Write("Warning", $"Text entry was not recorded because {DescribeMetadata(element)} exposed neither writable ValuePattern nor readable TextPattern.");
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                Reject("text pattern read failed");
                diagnostics.Write("Warning", "Text entry was not recorded because the focused target became unavailable.", exception);
            }
        }

        var stageCounts = new RecordingStageCounts
        {
            RawEventCount = rawEventCount,
            RetainedEventCount = retainedCount,
            FocusedElementCount = focusedCount,
            DescriptorCount = descriptorCount,
            SupportedPatternCount = supportedPatternCount,
            TextSnapshotCount = textSnapshotCount,
            ProducedActionCount = actions.Count,
            RejectedEventCount = rejections.Values.Sum(),
            Rejections = rejections
        };

        return new RecordingSessionResult
        {
            Status = RecordingSessionStatus.Completed,
            Actions = actions.OrderBy(item => item.Sequence).Select(item => item.Action).ToArray(),
            Message = actions.Count == 0
                ? $"Recording completed without a supported semantic action. Raw={stageCounts.RawEventCount}, retained={stageCounts.RetainedEventCount}, focused={stageCounts.FocusedElementCount}, descriptors={stageCounts.DescriptorCount}, supportedPatterns={stageCounts.SupportedPatternCount}, textSnapshots={stageCounts.TextSnapshotCount}, rejected={stageCounts.RejectedEventCount}."
                : $"Recorded {actions.Count} semantic action(s).",
            Stages = stageCounts
        };
    }

    private static AutomationElement? TryGetWindow(IntPtr handle)
    {
        try
        {
            return AutomationElement.FromHandle(handle);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public static class RecordingPrivacyPolicy
    {
        public static IReadOnlySet<string> DefaultSensitiveNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwordbox", "passwd", "pwd", "token", "secret", "authorization", "credential"
        };

        public static bool IsSensitive(ElementDescriptor element, IReadOnlySet<string> names)
        {
            return element.IsPassword == true || new[] { element.AutomationId, element.Name, element.ClassName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => names.Any(name => value!.Contains(name, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string DescriptorKey(TargetDescriptor descriptor)
    {
        var element = descriptor.Element;
        var name = element.MatchName ? element.Name : null;
        return $"{element.AutomationId}|{name}|{element.ControlType}|{element.ClassName}|{element.FrameworkId}";
    }

    private static string Describe(ElementDescriptor element) => element.Name ?? element.AutomationId ?? element.ControlType;

    private static string DescribeMetadata(ElementDescriptor element)
    {
        return $"controlType={element.ControlType}, class={element.ClassName ?? "<none>"}, framework={element.FrameworkId ?? "<none>"}, automationIdPresent={!string.IsNullOrWhiteSpace(element.AutomationId)}";
    }

    private static bool IsLeftButtonDown(uint message) => message == 0x0201;

    private static bool IsKeyDown(uint message) => message is 0x0100 or 0x0104;

    private static bool HasPattern(ElementDescriptor element, string patternName)
    {
        return element.RequiredPatterns.Any(pattern =>
            string.Equals(pattern, patternName, StringComparison.Ordinal)
            || string.Equals(pattern, patternName + "Pattern", StringComparison.Ordinal)
            || pattern.EndsWith("." + patternName, StringComparison.Ordinal)
            || pattern.EndsWith("." + patternName + "Pattern", StringComparison.Ordinal));
    }

    private sealed record TextCandidate(
        long Sequence,
        TargetDescriptor Descriptor,
        bool ValuePatternSupported,
        bool TextPatternSupported,
        string? TextSnapshot);
}
