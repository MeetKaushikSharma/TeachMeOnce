using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using TeachMeOnce.Automation;
using TeachMeOnce.Application;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.App;

public partial class MainWindow : Window
{
    private readonly DiagnosticLog _diagnostics;
    private readonly IWorkflowStore _workflowStore;
    private readonly IWorkflowRecorder _recorder;
    private readonly ExternalTargetSelector _targetSelector = new();
    private IWorkflowExecutor? _executor;
    private ExternalTarget? _attachedTarget;
    private ExternalRecordingSession? _externalRecording;
    private IReadOnlyList<ExternalWindowCandidate> _targetCandidates = Array.Empty<ExternalWindowCandidate>();
    private readonly List<WorkflowDocument> _workflows = new();
    private WorkflowAction? _recordedAction;
    private CancellationTokenSource? _replayCancellation;
    private bool _recording;

    public MainWindow()
    {
        InitializeComponent();
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachMeOnce");
        _diagnostics = new DiagnosticLog(Path.Combine(appData, "teachmeonce.log"));
        _workflowStore = new JsonWorkflowStore();
        _recorder = new UiAutomationWorkflowRecorder(_diagnostics, ProcessIdentityPolicy.ProcessName);
        _diagnostics.EntryAdded += Diagnostics_OnEntryAdded;
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        _executor = new UiAutomationWorkflowExecutor(
            Process.GetCurrentProcess().Id,
            Title,
            _diagnostics,
            progress: UpdateReplayProgress);
        try
        {
            RefreshTargetList();
            var loadResult = await _workflowStore.LoadAsync(CancellationToken.None);
            _workflows.AddRange(loadResult.Workflows);
            WorkflowList.ItemsSource = _workflows;
            _diagnostics.Write("Info", $"Loaded {_workflows.Count} workflow(s).");
            foreach (var issue in loadResult.Issues)
            {
                _diagnostics.Write("Warning", $"Skipped workflow '{Path.GetFileName(issue.Source)}': {issue.Message}");
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Write("Error", "Workflow loading failed; starting with an empty list.", exception);
            SetState("Error: workflow loading failed");
        }
    }

    private void RefreshTargetList()
    {
        try
        {
            _targetCandidates = _targetSelector.Enumerate();
            TargetComboBox.ItemsSource = _targetCandidates;
            TargetComboBox.SelectedIndex = _targetCandidates.Count == 0 ? -1 : 0;
            SetState(_targetCandidates.Count == 0 ? "Ready; no external windows found" : $"Ready; {_targetCandidates.Count} external window(s) available");
        }
        catch (Exception exception)
        {
            _diagnostics.Write("Error", "External window enumeration failed.", exception);
            SetState("Error: external window enumeration failed");
        }
    }

    private void AttachButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TargetComboBox.SelectedItem is not ExternalWindowCandidate candidate)
        {
            SetState("Attach unavailable: select one external window");
            _diagnostics.Write("Warning", "Attach requested without a selected external window.");
            return;
        }

        var result = _targetSelector.Attach(candidate);
        if (result.Status != ExternalTargetStatus.Attached || result.Target is null)
        {
            _attachedTarget = null;
            TargetInfoText.Text = $"Attach refused: {result.Reason}";
            SetState($"{result.Status}: external target was not attached");
            _diagnostics.Write("Warning", $"External target attach refused: {result.Reason}");
            return;
        }

        _attachedTarget = result.Target;
        TargetInfoText.Text = $"Attached: {candidate.DisplayName}; integrity={candidate.Integrity.Level}; HWND=0x{candidate.Handle.ToInt64():X}; executable={candidate.ExecutablePath ?? "unavailable"}";
        SetState("Attached: external target is ready");
        _diagnostics.Write("Info", $"Attached external target {candidate.ProcessName} (PID {candidate.ProcessId}).");
    }

    private void RefreshTargetsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_externalRecording is not null)
        {
            SetState("Refresh unavailable while recording an external target");
            return;
        }

        RefreshTargetList();
    }

    private async void RecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_attachedTarget is not null)
        {
            try
            {
                _externalRecording = new ExternalRecordingSession(
                    new ExternalRecordingSessionOptions { Target = _attachedTarget },
                    _diagnostics);
                await _externalRecording.StartAsync(CancellationToken.None);
                _recording = true;
                RecordedStepsList.ItemsSource = null;
                SetState($"Recording external target: {_attachedTarget.Window.DisplayName}");
                RecordButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                ReplayButton.IsEnabled = false;
                AttachButton.IsEnabled = false;
                RefreshTargetsButton.IsEnabled = false;
            }
            catch (Exception exception)
            {
                _diagnostics.Write("Error", "External recording could not start.", exception);
                await DisposeExternalRecordingAsync();
                AttachButton.IsEnabled = true;
                RefreshTargetsButton.IsEnabled = true;
                SetState("Error: external recording could not start");
            }

            return;
        }

        _recording = true;
        _recordedAction = null;
        RecordedStepsList.ItemsSource = null;
        SetState("Recording: click the semantic demo button, then press Stop");
        StopButton.IsEnabled = true;
        RecordButton.IsEnabled = false;
        ReplayButton.IsEnabled = false;
        _diagnostics.Write("Info", "Recording started.");
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_externalRecording is not null)
        {
            await StopExternalRecordingAsync();
            return;
        }

        _recording = false;
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        if (_recordedAction is null)
        {
            ReplayButton.IsEnabled = true;
            SetState("Recording stopped: no action captured");
            _diagnostics.Write("Warning", "Recording stopped without an action.");
            return;
        }

        var workflow = new WorkflowDocument
        {
            Name = $"Demo click {DateTime.Now:yyyy-MM-dd HH-mm-ss}",
            Actions = new[] { _recordedAction }
        };
        try
        {
            await _workflowStore.SaveAsync(workflow, CancellationToken.None);
            _workflows.Add(workflow);
            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = workflow;
            SetState("Saved: workflow ready to replay");
            _diagnostics.Write("Info", $"Saved workflow '{workflow.Name}'.");
        }
        catch (Exception exception)
        {
            SetState("Error: workflow save failed");
            _diagnostics.Write("Error", "Workflow save failed.", exception);
        }
        finally
        {
            ReplayButton.IsEnabled = true;
        }
    }

    private async void ReplayButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WorkflowList.SelectedItem is not WorkflowDocument workflow || _executor is null)
        {
            SetState("Replay unavailable: select a saved workflow");
            _diagnostics.Write("Warning", "Replay requested without a selected workflow.");
            return;
        }

        _replayCancellation?.Cancel();
        _replayCancellation?.Dispose();
        _replayCancellation = new CancellationTokenSource();
        SetState("Replaying...");
        RecordButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        ReplayButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        try
        {
            var result = await _executor.ExecuteAsync(workflow, _replayCancellation.Token);
            SetState($"{result.Status}: {result.Message}");
        }
        finally
        {
            RecordButton.IsEnabled = true;
            ReplayButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _replayCancellation?.Cancel();
        SetState("Cancelling replay...");
        _diagnostics.Write("Info", "Replay cancellation requested.");
    }

    private void DemoButton_OnClick(object sender, RoutedEventArgs e)
    {
        DemoStatusText.Text = "Status: invoked";
        _diagnostics.Write("Info", "Semantic demo button invoked.");
        if (!_recording)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var window = AutomationElement.FromHandle(handle)
                ?? throw new InvalidOperationException("The application window was not available to UI Automation.");
            _recordedAction = _recorder.RecordSemanticButtonClick(
                window,
                "ReplayDemoButton",
                "Invoke semantic demo button",
                "ReplayDemoStatus",
                "Status: invoked");
            SetState("Recording: one semantic action captured");
        }
        catch (Exception exception)
        {
            _recordedAction = null;
            SetState("Error: recording refused");
            _diagnostics.Write("Error", "Semantic action recording failed.", exception);
        }
    }

    private void Diagnostics_OnEntryAdded(object? sender, DiagnosticEntry entry)
    {
        Dispatcher.Invoke(() => DiagnosticList.Items.Add($"[{entry.Level}] {entry.Message}"));
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        _replayCancellation?.Cancel();
        _replayCancellation?.Dispose();
        var recording = _externalRecording;
        _externalRecording = null;
        if (recording is not null)
        {
            try
            {
                recording.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _diagnostics.Write("Error", "External recording cleanup failed during application shutdown.", exception);
            }
            finally
            {
                recording.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private async void Window_OnClosed(object? sender, EventArgs e)
    {
        await _diagnostics.FlushAsync();
    }

    private void SetState(string text) => StateText.Text = $"State: {text}";

    private void UpdateReplayProgress(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            SetState(message);
        }
        else
        {
            Dispatcher.BeginInvoke(() => SetState(message));
        }
    }

    private async Task StopExternalRecordingAsync()
    {
        var session = _externalRecording;
        if (session is null)
        {
            return;
        }

        _externalRecording = null;
        _recording = false;
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        AttachButton.IsEnabled = true;
        RefreshTargetsButton.IsEnabled = true;
        try
        {
            var result = await session.StopAsync(CancellationToken.None);
            RecordedStepsList.ItemsSource = result.Actions.Select((action, index) => $"{index + 1}. {action.ActionType}: {action.Description}").ToArray();
            _diagnostics.Write(
                result.Actions.Count == 0 ? "Warning" : "Info",
                $"External recording completed with {result.Actions.Count} workflow step(s), {result.CorrelatedEventCount} correlated event(s), and {result.DroppedEventCount} dropped event(s).");
            if (result.Stages is not null)
            {
                _diagnostics.Write(
                    result.Actions.Count == 0 ? "Warning" : "Info",
                    $"Recording stages: raw={result.Stages.RawEventCount}, retained={result.Stages.RetainedEventCount}, focused={result.Stages.FocusedElementCount}, descriptors={result.Stages.DescriptorCount}, supportedPatterns={result.Stages.SupportedPatternCount}, textSnapshots={result.Stages.TextSnapshotCount}, actions={result.Stages.ProducedActionCount}, rejected={result.Stages.RejectedEventCount}.");

                foreach (var rejection in result.Stages.Rejections.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _diagnostics.Write("Info", $"Recording rejection: {rejection.Key} ({rejection.Value}).");
                }
            }
            if (result.Status != RecordingSessionStatus.Completed || result.Actions.Count == 0)
            {
                ReplayButton.IsEnabled = true;
                SetState($"{result.Status}: {result.Message}");
                return;
            }

            var workflow = new WorkflowDocument
            {
                Name = $"{_attachedTarget?.Window.ProcessName ?? "External"} workflow {DateTime.Now:yyyy-MM-dd HH-mm-ss}",
                Actions = result.Actions
            };
            await _workflowStore.SaveAsync(workflow, CancellationToken.None);
            _workflows.Add(workflow);
            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = workflow;
            ReplayButton.IsEnabled = true;
            SetState($"Saved: {result.Actions.Count} external action(s) ready to replay");
            _diagnostics.Write("Info", $"Saved external workflow '{workflow.Name}' with {result.Actions.Count} action(s).");
        }
        catch (Exception exception)
        {
            ReplayButton.IsEnabled = true;
            SetState("Error: external recording could not be saved");
            _diagnostics.Write("Error", "External recording could not be saved.", exception);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task DisposeExternalRecordingAsync()
    {
        var session = _externalRecording;
        _externalRecording = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }
}
