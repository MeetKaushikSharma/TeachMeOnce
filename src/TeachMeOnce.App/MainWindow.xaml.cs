using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Microsoft.Win32;
using TeachMeOnce.Automation;
using TeachMeOnce.Application;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.App;

public partial class MainWindow : Window
{
    private const int MaximumVisibleDiagnostics = 500;
    private readonly DiagnosticLog _diagnostics;
    private readonly IWorkflowStore _workflowStore;
    private readonly IWorkflowRecorder _recorder;
    private readonly ExternalTargetSelector _targetSelector = new();
    private IWorkflowExecutor? _executor;
    private ExternalTarget? _attachedTarget;
    private ExternalRecordingSession? _externalRecording;
    private IReadOnlyList<ExternalWindowCandidate> _targetCandidates = Array.Empty<ExternalWindowCandidate>();
    private readonly List<WorkflowDocument> _workflows = new();
    private readonly Dictionary<Guid, ExecutionResult> _lastResults = new();
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
            _lastResults[workflow.Id] = result;
            SetState($"{result.Status}: {result.Message}");
            UpdateWorkflowDetails(workflow);
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
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            while (DiagnosticList.Items.Count >= MaximumVisibleDiagnostics)
            {
                DiagnosticList.Items.RemoveAt(0);
            }

            DiagnosticList.Items.Add($"[{entry.Level}] [{entry.Id.ToString("N")[..8]}] {entry.Message}");
        }));
    }

    private void WorkflowList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DeleteWorkflowButton.IsEnabled = WorkflowList.SelectedItem is WorkflowDocument;
        RenameWorkflowButton.IsEnabled = WorkflowList.SelectedItem is WorkflowDocument;
        ExportWorkflowButton.IsEnabled = WorkflowList.SelectedItem is WorkflowDocument;
        if (WorkflowList.SelectedItem is not WorkflowDocument workflow)
        {
            WorkflowDetailsText.Text = "Select a workflow to review its safe action details.";
            return;
        }

        UpdateWorkflowDetails(workflow);
    }

    private void UpdateWorkflowDetails(WorkflowDocument workflow)
    {
        var lastResultText = _lastResults.TryGetValue(workflow.Id, out var lastResult)
            ? $"Last result: {lastResult.Status}; completed={lastResult.CompletedActionCount}; retry={lastResult.RetryEligible}; {lastResult.Message}"
            : "Last result: not run in this session";

        var details = new List<string>
        {
            $"{workflow.Actions.Count} action(s) · schema {workflow.SchemaVersion}",
            lastResultText
        };

        for (var index = 0; index < workflow.Actions.Count; index++)
        {
            var action = workflow.Actions[index];
            var element = action.Target.Element;
            var sensitive = RecordingActionBuilder.RecordingPrivacyPolicy.IsSensitive(
                element,
                RecordingActionBuilder.RecordingPrivacyPolicy.DefaultSensitiveNames);
            var capabilityText = element.RequiredPatterns.Count == 0
                ? "no recorded pattern metadata"
                : string.Join(", ", element.RequiredPatterns);
            var stateText = action.ExpectedValue is null ? string.Empty : $"; expected={action.ExpectedValue}";
            var riskText = sensitive ? "; sensitive target: values are not displayed" : string.Empty;
            details.Add(
                $"{index + 1}. {action.ActionType}: {action.Description}{stateText}{riskText}\n" +
                $"   Process={action.Target.Process.ProcessName}; control={element.ControlType}; patterns={capabilityText}");
        }

        WorkflowDetailsText.Text = string.Join(Environment.NewLine, details);
    }

    private async void DeleteWorkflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WorkflowList.SelectedItem is not WorkflowDocument workflow)
        {
            SetState("Delete unavailable: select a workflow");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Delete workflow '{workflow.Name}'? This removes the saved workflow but does not affect the target application.",
            "Delete workflow",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!await _workflowStore.DeleteAsync(workflow.Id, CancellationToken.None))
            {
                SetState("Delete failed: workflow file was not found");
                _diagnostics.Write("Warning", $"Workflow delete skipped because '{workflow.Name}' was not found.");
                return;
            }

            _workflows.Remove(workflow);
            _lastResults.Remove(workflow.Id);
            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = null;
            DeleteWorkflowButton.IsEnabled = false;
            SetState("Deleted: workflow removed from the library");
            _diagnostics.Write("Info", $"Deleted workflow '{workflow.Name}'.");
        }
        catch (Exception exception)
        {
            SetState("Error: workflow delete failed");
            _diagnostics.Write("Error", "Workflow delete failed.", exception);
        }
    }

    private async void RenameWorkflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WorkflowList.SelectedItem is not WorkflowDocument workflow)
        {
            SetState("Rename unavailable: select a workflow");
            return;
        }

        var name = PromptForWorkflowName(workflow.Name);
        if (name is null || string.Equals(name, workflow.Name, StringComparison.Ordinal))
        {
            return;
        }

        var renamed = new WorkflowDocument
        {
            Id = workflow.Id,
            SchemaVersion = workflow.SchemaVersion,
            Name = name,
            Actions = workflow.Actions
        };

        try
        {
            await _workflowStore.SaveAsync(renamed, CancellationToken.None);
            var index = _workflows.IndexOf(workflow);
            if (index >= 0)
            {
                _workflows[index] = renamed;
            }

            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = renamed;
            SetState("Renamed: workflow name updated");
            _diagnostics.Write("Info", $"Renamed workflow to '{renamed.Name}'.");
        }
        catch (Exception exception)
        {
            SetState("Error: workflow rename failed");
            _diagnostics.Write("Error", "Workflow rename failed.", exception);
        }
    }

    private string? PromptForWorkflowName(string currentName)
    {
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = currentName,
            MinWidth = 300,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var dialog = new Window
        {
            Owner = this,
            Title = "Rename workflow",
            Width = 380,
            Height = 145,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = "Workflow name", Margin = new Thickness(0, 0, 0, 6) },
                    textBox,
                    new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(0, 0, 6, 0) },
                            new System.Windows.Controls.Button { Content = "Rename", Width = 80, IsDefault = true }
                        }
                    }
                }
            }
        };
        var buttonPanel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.StackPanel)dialog.Content).Children[2];
        var renameButton = (System.Windows.Controls.Button)buttonPanel.Children[1];
        renameButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text))
            {
                dialog.DialogResult = true;
            }
        };

        return dialog.ShowDialog() == true ? textBox.Text.Trim() : null;
    }

    private async void ExportWorkflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WorkflowList.SelectedItem is not WorkflowDocument workflow)
        {
            SetState("Export unavailable: select a workflow");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export workflow",
            Filter = "TeachMeOnce workflow (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{SanitizeFileName(workflow.Name)}.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _workflowStore.ExportAsync(workflow, dialog.FileName, CancellationToken.None);
            SetState("Exported: workflow JSON was written safely");
            _diagnostics.Write("Info", $"Exported workflow '{workflow.Name}'.");
        }
        catch (Exception exception)
        {
            SetState("Error: workflow export failed");
            _diagnostics.Write("Error", "Workflow export failed.", exception);
        }
    }

    private async void ImportWorkflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import workflow",
            Filter = "TeachMeOnce workflow (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await _workflowStore.ImportAsync(dialog.FileName, CancellationToken.None);
            var workflow = new WorkflowDocument
            {
                Id = Guid.NewGuid(),
                SchemaVersion = imported.SchemaVersion,
                Name = imported.Name,
                Actions = imported.Actions
            };
            await _workflowStore.SaveAsync(workflow, CancellationToken.None);
            _workflows.Add(workflow);
            WorkflowList.Items.Refresh();
            WorkflowList.SelectedItem = workflow;
            SetState("Imported: workflow validated and added to the library");
            _diagnostics.Write("Info", $"Imported workflow '{workflow.Name}' with a new library identity.");
        }
        catch (Exception exception)
        {
            SetState("Error: workflow import was rejected");
            _diagnostics.Write("Error", "Workflow import was rejected; the library was unchanged.", exception);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "workflow" : sanitized;
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
