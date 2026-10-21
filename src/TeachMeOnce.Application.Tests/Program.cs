using TeachMeOnce.Application;
using TeachMeOnce.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Application.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeachMeOnce-application-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var descriptor = new TargetDescriptor
            {
                Process = new ProcessContext
                {
                    ProcessId = 42,
                    ProcessName = "TestProcess",
                    IdentityPolicy = ProcessIdentityPolicy.ProcessName
                },
                Window = new WindowContext { Name = "Test Window", ControlType = "ControlType.Window", MatchName = false },
                Element = new ElementDescriptor
                {
                    AutomationId = "ActionButton",
                    Name = "Action",
                    ControlType = "ControlType.Button",
                    RequiredPatterns = new[] { "Invoke" }
                },
                Ancestors = Array.Empty<ElementDescriptor>()
            };
            var workflow = new WorkflowDocument
            {
                Id = Guid.NewGuid(),
                Name = "Contract test",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "Invoke",
                        Description = "Invoke action",
                        Target = descriptor,
                        VerificationAutomationId = "StatusText",
                        ExpectedValue = "Status: invoked"
                    }
                }
            };
            var store = new JsonWorkflowStore(root);
            await store.SaveAsync(workflow, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert(loaded.Workflows.Count == 1, "one workflow round-trips after a simulated restart");
            Assert(loaded.Workflows[0].SchemaVersion == WorkflowSchema.CurrentVersion, "schema version is preserved");
            Assert(loaded.Workflows[0].Actions[0].Target.Element.AutomationId == "ActionButton", "target descriptor is preserved");
            Assert(loaded.Workflows[0].Actions[0].Target.Process.IdentityPolicy == ProcessIdentityPolicy.ProcessName, "restart-safe process identity policy is preserved");
            Assert(!loaded.Workflows[0].Actions[0].Target.Window.MatchName, "restart-safe external window descriptors can ignore mutable titles");

            var actionRegistry = new WorkflowActionAdapterRegistry();
            Assert(actionRegistry.TryGet("Invoke", out var invokeAdapter) && invokeAdapter is not null, "the action registry exposes Invoke");
            Assert(actionRegistry.TryGet("SetValue", out var setValueAdapter) && setValueAdapter is not null, "the action registry exposes SetValue");
            Assert(actionRegistry.TryGet("TextInput", out var textInputAdapter) && textInputAdapter is not null, "the action registry exposes TextInput");
            Assert(actionRegistry.TryGet("Toggle", out var toggleAdapter) && toggleAdapter is not null, "the action registry exposes Toggle");
            Assert(actionRegistry.TryGet("Select", out var selectAdapter) && selectAdapter is not null, "the action registry exposes Select");
            Assert(actionRegistry.TryGet("ExpandCollapse", out var expandCollapseAdapter) && expandCollapseAdapter is not null, "the action registry exposes ExpandCollapse");
            Assert(!actionRegistry.TryGet("UnknownAction", out _), "unknown action types are rejected by the registry");
            Assert(invokeAdapter!.RequiredPatterns.Contains("Invoke"), "Invoke declares its required UIA pattern");
            Assert(setValueAdapter!.RequiredPatterns.Contains("Value"), "SetValue declares its required UIA pattern");
            Assert(toggleAdapter!.RequiredPatterns.Contains("Toggle"), "Toggle declares its required UIA pattern");
            Assert(selectAdapter!.RequiredPatterns.Contains("SelectionItem"), "Select declares its required UIA pattern");
            Assert(expandCollapseAdapter!.RequiredPatterns.Contains("ExpandCollapse"), "ExpandCollapse declares its required UIA pattern");
            Assert(actionRegistry.IsDescriptorCompatible(invokeAdapter, descriptor.Element), "Invoke descriptors must declare Invoke capability");
            Assert(!actionRegistry.IsDescriptorCompatible(toggleAdapter, descriptor.Element), "Toggle descriptors without Toggle capability are rejected");

            var secondWorkflow = new WorkflowDocument
            {
                Name = "Second workflow",
                Actions = workflow.Actions
            };
            await store.SaveAsync(secondWorkflow, CancellationToken.None);
            var textWorkflow = new WorkflowDocument
            {
                Name = "Text workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "SetValue",
                        Description = "Set ordinary text",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = new ElementDescriptor
                            {
                                AutomationId = "ValueTextBox",
                                Name = "Value",
                                ControlType = "ControlType.Edit",
                                RequiredPatterns = new[] { "Value" },
                                MatchName = false
                            },
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        InputValue = "ordinary text"
                    }
                }
            };
            await store.SaveAsync(textWorkflow, CancellationToken.None);
            var multiple = await new JsonWorkflowStore(root).LoadAsync(CancellationToken.None);
            Assert(multiple.Workflows.Count == 3, "multiple workflows reload together");
            Assert(multiple.Workflows.Single(workflowItem => workflowItem.Name == "Text workflow").Actions[0].InputValue == "ordinary text", "non-sensitive text action survives persistence");
            Assert(!multiple.Workflows.Single(workflowItem => workflowItem.Name == "Text workflow").Actions[0].Target.Element.MatchName, "text targets do not depend on mutable control names");

            var textInputWorkflow = new WorkflowDocument
            {
                Name = "Text input workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "TextInput",
                        Description = "Enter ordinary text",
                        Target = textWorkflow.Actions[0].Target,
                        InputValue = "ordinary text",
                        InputMethod = "KeyboardText"
                    }
                }
            };
            await store.SaveAsync(textInputWorkflow, CancellationToken.None);
            var textInputReloaded = await store.LoadAsync(CancellationToken.None);
            Assert(textInputReloaded.Workflows.Single(item => item.Name == "Text input workflow").Actions[0].ActionType == "TextInput", "TextInput actions survive persistence");

            var toggleWorkflow = new WorkflowDocument
            {
                Name = "Toggle workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "Toggle",
                        Description = "Toggle option to On",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = new ElementDescriptor
                            {
                                AutomationId = "ToggleCheckBox",
                                Name = "Toggle option",
                                ControlType = "ControlType.CheckBox",
                                RequiredPatterns = new[] { "Toggle" }
                            },
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        ExpectedValue = "On"
                    }
                }
            };
            await store.SaveAsync(toggleWorkflow, CancellationToken.None);
            var toggleReloaded = await store.LoadAsync(CancellationToken.None);
            Assert(toggleReloaded.Workflows.Single(item => item.Name == "Toggle workflow").Actions[0].ExpectedValue == "On", "Toggle expected state survives persistence");
            Assert(toggleReloaded.Workflows.Single(item => item.Name == "Toggle workflow").Actions[0].Target.Element.RequiredPatterns.Contains("Toggle"), "Toggle target capability survives persistence");

            var selectWorkflow = new WorkflowDocument
            {
                Name = "Select workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "Select",
                        Description = "Select second option",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = new ElementDescriptor
                            {
                                AutomationId = "SecondOption",
                                Name = "Second option",
                                ControlType = "ControlType.ListItem",
                                RequiredPatterns = new[] { "SelectionItem" }
                            },
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        ExpectedValue = WorkflowSchema.SelectedState
                    }
                }
            };
            await store.SaveAsync(selectWorkflow, CancellationToken.None);
            var selectReloaded = await store.LoadAsync(CancellationToken.None);
            Assert(selectReloaded.Workflows.Single(item => item.Name == "Select workflow").Actions[0].ExpectedValue == WorkflowSchema.SelectedState, "Select expected state survives persistence");

            var expandCollapseWorkflow = new WorkflowDocument
            {
                Name = "Expand/collapse workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "ExpandCollapse",
                        Description = "Expand section",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = new ElementDescriptor
                            {
                                AutomationId = "ExpandableSection",
                                Name = "Expandable section",
                                ControlType = "ControlType.Group",
                                RequiredPatterns = new[] { "ExpandCollapse" }
                            },
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        ExpectedValue = "Expanded"
                    }
                }
            };
            await store.SaveAsync(expandCollapseWorkflow, CancellationToken.None);
            var expandCollapseReloaded = await store.LoadAsync(CancellationToken.None);
            Assert(expandCollapseReloaded.Workflows.Single(item => item.Name == "Expand/collapse workflow").Actions[0].ExpectedValue == "Expanded", "ExpandCollapse expected state survives persistence");

            var invalidToggleRejected = false;
            try
            {
                await store.SaveAsync(new WorkflowDocument
                {
                    Name = "Invalid toggle workflow",
                    Actions = new[]
                    {
                        new WorkflowAction
                        {
                            ActionType = "Toggle",
                            Description = "Invalid toggle",
                            Target = toggleWorkflow.Actions[0].Target,
                            ExpectedValue = "Maybe"
                        }
                    }
                }, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                invalidToggleRejected = true;
            }

            Assert(invalidToggleRejected, "Toggle workflows reject non-canonical expected states");

            var path = Directory.EnumerateFiles(root, "*.json").First();
            var json = await File.ReadAllTextAsync(path);
            var invalidJson = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, invalidJson);
            var invalidResult = await store.LoadAsync(CancellationToken.None);
            Assert(invalidResult.Workflows.Count == 6, "valid workflows survive an invalid file");
            Assert(invalidResult.Issues.Count == 1, "unsupported schema versions are reported");

            var malformedPath = Path.Combine(root, "malformed.json");
            await File.WriteAllTextAsync(malformedPath, "{ not valid json");
            var malformedResult = await store.LoadAsync(CancellationToken.None);
            Assert(malformedResult.Workflows.Count == 6, "valid workflows survive malformed JSON");
            Assert(malformedResult.Issues.Count == 2, "malformed and unsupported files are both reported");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var executor = new UiAutomationWorkflowExecutor(42, "missing window", new DiagnosticLog());
            var cancelled = await executor.ExecuteAsync(workflow, cancellation.Token);
            Assert(cancelled.Status == ExecutionStatus.Cancelled, "pre-cancelled replay is safe");
            Assert(UiAutomationWorkflowExecutor.MapResolutionStatus(ResolutionStatus.NotFound) == ExecutionStatus.NotFound, "missing targets remain explicit");
            Assert(UiAutomationWorkflowExecutor.MapResolutionStatus(ResolutionStatus.Ambiguous) == ExecutionStatus.Ambiguous, "ambiguous targets remain explicit");
            Assert(UiAutomationWorkflowExecutor.MapResolutionStatus(ResolutionStatus.ContextMismatch) == ExecutionStatus.ContextMismatch, "context mismatches remain explicit");
            Assert(VerificationPolicy.Evaluate(new[] { "Status: invoked" }, "Status: invoked").Outcome == VerificationOutcome.Satisfied, "post-action verification accepts the expected value");
            Assert(VerificationPolicy.Evaluate(Array.Empty<string?>(), "Status: invoked").Outcome == VerificationOutcome.NotFound, "post-action verification does not guess a missing target");
            Assert(VerificationPolicy.Evaluate(new[] { "Status: ready" }, "Status: invoked").Outcome == VerificationOutcome.ValueMismatch, "post-action verification rejects an unexpected value");
            Assert(VerificationPolicy.Evaluate(new[] { "Status: invoked", "Status: invoked" }, "Status: invoked").Outcome == VerificationOutcome.Ambiguous, "post-action verification rejects duplicate targets");

            var diagnosticPath = Path.Combine(root, "diagnostics.log");
            var diagnostics = new DiagnosticLog(diagnosticPath);
            diagnostics.Write("Warning", "A password=super-secret and token=abc123 must not be persisted.");
            await diagnostics.FlushAsync();
            var diagnosticText = await File.ReadAllTextAsync(diagnosticPath);
            Assert(diagnostics.Entries.Single().Id != Guid.Empty, "diagnostics receive a stable identifier");
            Assert(!diagnosticText.Contains("super-secret", StringComparison.Ordinal), "diagnostics redact password values");
            Assert(!diagnosticText.Contains("abc123", StringComparison.Ordinal), "diagnostics redact token values");
            Assert(diagnosticText.Contains("[REDACTED]", StringComparison.Ordinal), "diagnostics retain a useful redaction marker");

            var sensitiveDescriptor = new ElementDescriptor
            {
                AutomationId = "PasswordBox",
                Name = "Password",
                ControlType = "ControlType.Edit",
                IsPassword = true,
                RequiredPatterns = new[] { "Value" }
            };
            Assert(RecordingActionBuilder.RecordingPrivacyPolicy.IsSensitive(
                sensitiveDescriptor,
                new HashSet<string>(new[] { "password", "token" }, StringComparer.OrdinalIgnoreCase)),
                "password controls are classified as sensitive before persistence");

            var invokeDescriptor = new TargetDescriptor
            {
                Process = descriptor.Process,
                Window = descriptor.Window,
                Element = new ElementDescriptor
                {
                    AutomationId = "InvokeButton",
                    Name = "Invoke",
                    ControlType = "ControlType.Button",
                    RequiredPatterns = new[] { "Invoke" }
                },
                Ancestors = Array.Empty<ElementDescriptor>()
            };
            var targetCandidate = new ExternalWindowCandidate(
                new IntPtr(123),
                42,
                "TestProcess",
                null,
                "Test Window",
                null,
                "Window",
                "WPF",
                "ControlType.Window",
                new IntegrityInfo(IntegrityLevel.Medium, true, "test"));
            var externalTarget = new ExternalTarget(targetCandidate, targetCandidate.Integrity, true);
            var eventData = new CorrelatedInputEvent(
                new RawInputEvent(2, RawInputKind.Mouse, 0x0201, DateTimeOffset.UtcNow, 2, false, 20, 30, 0, 0, 0, new IntPtr(123), 42, IntPtr.Zero),
                targetCandidate,
                invokeDescriptor.Element,
                null,
                invokeDescriptor,
                true,
                null);
            var ignoredInjected = eventData with
            {
                Input = eventData.Input with { Sequence = 1, Injected = true }
            };
            var actionResult = RecordingActionBuilder.Build(
                externalTarget,
                new[] { ignoredInjected, eventData, eventData with { Input = eventData.Input with { Sequence = 3 }, BelongsToSelectedTarget = false } },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new DiagnosticLog());
            Assert(actionResult.Actions.Count == 1 && actionResult.Actions[0].ActionType == "Invoke", "recording filters injected events and creates one semantic invoke action");

            var secondInvokeDescriptor = new TargetDescriptor
            {
                Process = descriptor.Process,
                Window = descriptor.Window,
                Element = new ElementDescriptor
                {
                    AutomationId = "SecondButton",
                    Name = "Second",
                    ControlType = "ControlType.Button",
                    RequiredPatterns = new[] { "Invoke" }
                },
                Ancestors = Array.Empty<ElementDescriptor>()
            };
            var secondEvent = eventData with
            {
                Input = eventData.Input with { Sequence = 1 },
                TargetElement = secondInvokeDescriptor.Element,
                TargetDescriptor = secondInvokeDescriptor
            };
            var ordered = RecordingActionBuilder.Build(
                externalTarget,
                new[] { eventData, secondEvent },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new DiagnosticLog());
            Assert(ordered.Actions.Count == 2 && ordered.Actions[0].Description.Contains("Second", StringComparison.Ordinal), "recorded actions are ordered by monotonic sequence");

            var unsafeWorkflow = new WorkflowDocument
            {
                Name = "Unsafe text workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "SetValue",
                        Description = "Unsafe",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = sensitiveDescriptor,
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        InputValue = "must-not-persist"
                    }
                }
            };
            var unsafeSaveRejected = false;
            try
            {
                await store.SaveAsync(unsafeWorkflow, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                unsafeSaveRejected = true;
            }

            Assert(unsafeSaveRejected, "unsafe password text workflows are rejected before persistence");

            var tokenWorkflow = new WorkflowDocument
            {
                Name = "Configured-sensitive text workflow",
                Actions = new[]
                {
                    new WorkflowAction
                    {
                        ActionType = "SetValue",
                        Description = "Configured-sensitive text",
                        Target = new TargetDescriptor
                        {
                            Process = descriptor.Process,
                            Window = descriptor.Window,
                            Element = new ElementDescriptor
                            {
                                AutomationId = "ApiTokenEditor",
                                Name = "API token",
                                ControlType = "ControlType.Edit",
                                RequiredPatterns = new[] { "Value" }
                            },
                            Ancestors = Array.Empty<ElementDescriptor>()
                        },
                        InputValue = "must-not-persist"
                    }
                }
            };
            var configuredSensitiveRejected = false;
            try
            {
                await store.SaveAsync(tokenWorkflow, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                configuredSensitiveRejected = true;
            }

            Assert(configuredSensitiveRejected, "configured-sensitive text workflows are rejected before persistence");

            var deleteProbe = new WorkflowDocument
            {
                Name = "Delete probe",
                Actions = workflow.Actions
            };
            await store.SaveAsync(deleteProbe, CancellationToken.None);
            Assert(await store.DeleteAsync(deleteProbe.Id, CancellationToken.None), "workflow deletion removes an existing workflow file");
            Assert(!await store.DeleteAsync(deleteProbe.Id, CancellationToken.None), "workflow deletion reports a missing file without guessing");

            var transferPath = Path.Combine(root, "exports", "toggle-export.json");
            await store.ExportAsync(toggleWorkflow, transferPath, CancellationToken.None);
            var importedWorkflow = await store.ImportAsync(transferPath, CancellationToken.None);
            Assert(importedWorkflow.Name == toggleWorkflow.Name, "exported workflow metadata survives import");
            Assert(importedWorkflow.Actions[0].ExpectedValue == "On", "exported workflow action state survives import");
            Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(transferPath)!, "*.tmp").Any(), "workflow export cleans up its temporary file");

            var renamedWorkflow = new WorkflowDocument
            {
                Id = toggleWorkflow.Id,
                Name = "Renamed toggle workflow",
                Actions = toggleWorkflow.Actions
            };
            await store.SaveAsync(renamedWorkflow, CancellationToken.None);
            var renamedReloaded = await store.LoadAsync(CancellationToken.None);
            Assert(renamedReloaded.Workflows.Any(item => item.Id == toggleWorkflow.Id && item.Name == "Renamed toggle workflow"), "workflow rename preserves identity and updates metadata");

            var unknownActionRejected = false;
            try
            {
                await store.SaveAsync(new WorkflowDocument
                {
                    Name = "Unknown action probe",
                    Actions = new[]
                    {
                        new WorkflowAction
                        {
                            ActionType = "UnknownAction",
                            Description = "Should not persist",
                            Target = descriptor
                        }
                    }
                }, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                unknownActionRejected = true;
            }

            Assert(unknownActionRejected, "unknown action types are rejected before persistence");

            var invalidDescriptorRejected = false;
            try
            {
                await store.SaveAsync(new WorkflowDocument
                {
                    Name = "Invalid descriptor probe",
                    Actions = new[]
                    {
                        new WorkflowAction
                        {
                            ActionType = "Invoke",
                            Description = "Missing target capability metadata",
                            Target = new TargetDescriptor
                            {
                                Process = descriptor.Process,
                                Window = descriptor.Window,
                                Element = new ElementDescriptor
                                {
                                    ControlType = "ControlType.Button",
                                    RequiredPatterns = null!
                                },
                                Ancestors = Array.Empty<ElementDescriptor>()
                            }
                        }
                    }
                }, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                invalidDescriptorRejected = true;
            }

            Assert(invalidDescriptorRejected, "incomplete target descriptors are rejected before persistence");

            var cancelledSave = new WorkflowDocument
            {
                Name = "Cancelled save probe",
                Actions = workflow.Actions
            };
            using var saveCancellation = new CancellationTokenSource();
            saveCancellation.Cancel();
            var cancelledSaveObserved = false;
            try
            {
                await store.SaveAsync(cancelledSave, saveCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelledSaveObserved = true;
            }

            Assert(cancelledSaveObserved, "cancelled workflow saves report cancellation");
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "cancelled workflow saves clean up their temporary file");

            using var hookSession = new InputHookSession(1);
            Assert(!hookSession.IsRunning, "a recording hook session is idle before start");

            using var queue = new BoundedInputEventQueue(1);
            var queuedFirst = CreateRawInput(1);
            var queuedSecond = CreateRawInput(2);
            Assert(queue.TryEnqueue(queuedFirst), "bounded input queues accept events while capacity is available");
            Assert(!queue.TryEnqueue(queuedSecond), "bounded input queues reject overflow without blocking the producer");
            Assert(queue.DroppedCount == 1, "bounded input queues count dropped events");
            queue.Complete();
            Assert(queue.ReadAll().Single().Sequence == 1, "bounded input queues preserve event ordering");

            await using var cancelledSession = new ExternalRecordingSession(
                new ExternalRecordingSessionOptions { Target = externalTarget },
                new DiagnosticLog());
            using var cancelledRecording = new CancellationTokenSource();
            cancelledRecording.Cancel();
            var cancelledRecordingResult = await cancelledSession.StopAsync(cancelledRecording.Token);
            Assert(cancelledRecordingResult.Status == RecordingSessionStatus.Cancelled, "recording cancellation has an explicit terminal result");

            Console.WriteLine("TeachMeOnce.Application.Tests passed.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {description}");
        }
    }

    private static RawInputEvent CreateRawInput(long sequence) => new(
        sequence,
        RawInputKind.Mouse,
        0x0201,
        DateTimeOffset.UtcNow,
        sequence,
        false,
        0,
        0,
        0,
        0,
        0,
        IntPtr.Zero,
        42,
        IntPtr.Zero);
}
