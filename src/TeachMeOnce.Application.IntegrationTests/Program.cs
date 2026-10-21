using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TeachMeOnce.Application;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Application.IntegrationTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var fixturePath = ParseFixturePath(args);
        var winForms = args.Contains("--winforms", StringComparer.OrdinalIgnoreCase);
        if (fixturePath is null)
        {
            Console.Error.WriteLine("Usage: application-integration-tests --fixture path-to-WpfFixture.exe");
            return 2;
        }

        using var fixture = Process.Start(new ProcessStartInfo
        {
            FileName = fixturePath,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("The WPF fixture could not be started.");

        try
        {
            if (!fixture.WaitForInputIdle(5000))
            {
                throw new InvalidOperationException("The WPF fixture did not become input-idle.");
            }

            await Task.Delay(500);
            if (fixture.HasExited)
            {
                throw new InvalidOperationException($"The WPF fixture exited with code {fixture.ExitCode}.");
            }

            var window = AutomationElement.FromHandle(fixture.MainWindowHandle)
                ?? throw new InvalidOperationException("The WPF fixture UIA window was unavailable.");
            var diagnostics = new DiagnosticLog();
            var executor = new UiAutomationWorkflowExecutor(
                fixture.Id,
                fixture.MainWindowTitle,
                diagnostics,
                progress: message => Console.WriteLine(message));

            await ExecuteInvokeAsync(executor, window, "InvokeButton", winForms ? "StatusLabel" : "StatusText", "Status: invoked");
            await ExecuteSetValueAsync(executor, window, "ValueTextBox", "integration value");
            await ExecuteToggleAsync(executor, window, "ToggleCheckBox", "On");
            await ExecuteSelectAsync(executor, window, winForms);
            if (!winForms)
            {
                await ExecuteExpandCollapseAsync(executor, window, "ExpandableSection", "Expanded");
            }

            Console.WriteLine(winForms
                ? "Application adapter integration checks passed: Invoke, SetValue, Toggle, Select."
                : "Application adapter integration checks passed: Invoke, SetValue, Toggle, Select, ExpandCollapse.");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            Console.Error.WriteLine($"Application adapter integration checks failed ({exception.GetType().Name}): {exception.Message}");
            return 1;
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.CloseMainWindow();
                fixture.WaitForExit(3000);
            }
        }
    }

    private static async Task ExecuteInvokeAsync(
        UiAutomationWorkflowExecutor executor,
        AutomationElement window,
        string automationId,
        string verificationId,
        string expectedValue)
    {
        var target = Find(window, automationId);
        var action = new WorkflowAction
        {
            ActionType = "Invoke",
            Description = "Invoke integration button",
            Target = BuildDescriptor(window, target),
            VerificationAutomationId = verificationId,
            ExpectedValue = expectedValue
        };
        await ExecuteAsync(executor, action);
    }

    private static async Task ExecuteSetValueAsync(
        UiAutomationWorkflowExecutor executor,
        AutomationElement window,
        string automationId,
        string value)
    {
        var target = Find(window, automationId);
        var action = new WorkflowAction
        {
            ActionType = "SetValue",
            Description = "Set integration value",
            Target = BuildDescriptor(window, target),
            InputValue = value,
            InputMethod = "ValuePattern"
        };
        await ExecuteAsync(executor, action);
    }

    private static async Task ExecuteToggleAsync(
        UiAutomationWorkflowExecutor executor,
        AutomationElement window,
        string automationId,
        string expectedState)
    {
        var target = Find(window, automationId);
        var action = new WorkflowAction
        {
            ActionType = "Toggle",
            Description = "Toggle integration checkbox",
            Target = BuildDescriptor(window, target),
            ExpectedValue = expectedState
        };
        await ExecuteAsync(executor, action);
    }

    private static async Task ExecuteSelectAsync(
        UiAutomationWorkflowExecutor executor,
        AutomationElement window,
        bool winForms)
    {
        var target = winForms
            ? FindByName(window, "List item two")
            : Find(window, "ListItemTwo");
        var action = new WorkflowAction
        {
            ActionType = "Select",
            Description = "Select integration list item",
            Target = BuildDescriptor(window, target),
            ExpectedValue = WorkflowSchema.SelectedState
        };
        await ExecuteAsync(executor, action);
    }

    private static async Task ExecuteExpandCollapseAsync(
        UiAutomationWorkflowExecutor executor,
        AutomationElement window,
        string automationId,
        string expectedState)
    {
        var target = Find(window, automationId);
        var action = new WorkflowAction
        {
            ActionType = "ExpandCollapse",
            Description = "Expand integration section",
            Target = BuildDescriptor(window, target),
            ExpectedValue = expectedState
        };
        await ExecuteAsync(executor, action);
    }

    private static async Task ExecuteAsync(UiAutomationWorkflowExecutor executor, WorkflowAction action)
    {
        var result = await executor.ExecuteAsync(
            new WorkflowDocument
            {
                Name = $"Integration {action.ActionType}",
                Actions = new[] { action }
            },
            CancellationToken.None);
        if (result.Status != ExecutionStatus.Completed || result.CompletedActionCount != 1)
        {
            throw new InvalidOperationException($"{action.ActionType} returned {result.Status}: {result.Message}");
        }
    }

    private static AutomationElement Find(AutomationElement window, string automationId)
    {
        return window.FindFirst(
                   TreeScope.Descendants,
                   new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
               ?? throw new InvalidOperationException($"Fixture element '{automationId}' was not found.");
    }

    private static AutomationElement FindByName(AutomationElement window, string name)
    {
        return window.FindFirst(
                   TreeScope.Descendants,
                   new PropertyCondition(AutomationElement.NameProperty, name))
               ?? throw new InvalidOperationException($"Fixture element named '{name}' was not found.");
    }

    private static TargetDescriptor BuildDescriptor(AutomationElement window, AutomationElement target)
    {
        return TargetDescriptorBuilder.Build(
            window,
            target,
            ProcessIdentityPolicy.ProcessName,
            matchWindowName: false);
    }

    private static string? ParseFixturePath(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--fixture", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return null;
    }
}
