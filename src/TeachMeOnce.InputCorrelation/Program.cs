using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TeachMeOnce.Automation;

namespace TeachMeOnce.InputCorrelation;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            var options = Arguments.Parse(args);
            EnsureTargetProcess(options.ProcessId);

            var selector = new ExternalTargetSelector();
            var candidate = selector.Enumerate().SingleOrDefault(window => unchecked((uint)window.ProcessId) == options.ProcessId);
            if (candidate is null)
            {
                throw new InvalidOperationException($"No unique top-level UI Automation window was found for process {options.ProcessId}.");
            }

            var targetResult = selector.Attach(candidate);
            if (targetResult.Target is null || targetResult.Status != ExternalTargetStatus.Attached)
            {
                throw new InvalidOperationException($"The target could not be attached: {targetResult.Reason}");
            }

            var attachedTarget = targetResult.Target;
            var correlated = new List<CorrelatedInputEvent>();
            var correlationGate = new object();
            var correlator = new UiAutomationInputCorrelator();
            using var capture = new InputHookSession(2048, input =>
            {
                var result = correlator.Correlate(attachedTarget, input);
                lock (correlationGate)
                {
                    correlated.Add(result);
                }
            });

            capture.Start();
            try
            {
                if (options.Scripted)
                {
                    Thread.Sleep(250);
                    FixtureInputDriver.Run(options.ProcessId);
                    Thread.Sleep(500);
                }
                else
                {
                    Console.WriteLine($"Recording input correlation for {options.DurationSeconds} seconds. Press Ctrl+C to stop the process.");
                    Thread.Sleep(TimeSpan.FromSeconds(options.DurationSeconds));
                }
            }
            finally
            {
                capture.StopAsync().GetAwaiter().GetResult();
            }

            CorrelatedInputEvent[] correlatedEvents;
            lock (correlationGate)
            {
                correlatedEvents = correlated.OrderBy(item => item.Input.Sequence).ToArray();
            }

            var captured = capture.SnapshotResult();
            var report = new CorrelationReport
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                TargetProcessId = options.ProcessId,
                DroppedInputEvents = captured.DroppedCount,
                EventCount = correlatedEvents.Length,
                Events = correlatedEvents.Select(ToReport).ToArray()
            };
            var json = JsonSerializer.Serialize(report, JsonOptions);
            if (options.OutputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
                File.WriteAllText(options.OutputPath, json);
                Console.WriteLine($"Correlation report written to {Path.GetFullPath(options.OutputPath)}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine($"Input correlation failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static CorrelationRecord ToReport(CorrelatedInputEvent item)
    {
        return new CorrelationRecord
        {
            Sequence = item.Input.Sequence,
            InputKind = item.Input.Kind.ToString(),
            Message = DescribeMessage(item.Input),
            TimestampUtc = item.Input.TimestampUtc,
            MonotonicTimestamp = item.Input.MonotonicTimestamp,
            Injected = item.Input.Injected,
            IsDown = IsDownMessage(item.Input.Message),
            X = item.Input.Kind == RawInputKind.Mouse ? item.Input.X : null,
            Y = item.Input.Kind == RawInputKind.Mouse ? item.Input.Y : null,
            VirtualKey = item.Input.Kind == RawInputKind.Keyboard ? item.Input.VirtualKey : null,
            ScanCode = item.Input.Kind == RawInputKind.Keyboard ? item.Input.ScanCode : null,
            Window = item.Window is null ? null : new WindowSnapshot
            {
                Handle = item.Window.Handle.ToInt64(),
                ProcessId = unchecked((uint)item.Window.ProcessId),
                ProcessName = item.Window.ProcessName,
                Name = item.Window.WindowTitle,
                AutomationId = item.Window.AutomationId,
                ClassName = item.Window.ClassName,
                FrameworkId = item.Window.FrameworkId
            },
            TargetElement = ToElement(item.TargetElement),
            FocusedElement = ToElement(item.FocusedElement),
            TargetProcessMatch = item.BelongsToSelectedTarget,
            Failure = item.Failure
        };
    }

    private static ElementSnapshot? ToElement(TeachMeOnce.TargetResolution.ElementDescriptor? element)
    {
        return element is null ? null : new ElementSnapshot
        {
            Name = element.Name,
            AutomationId = element.AutomationId,
            ControlType = element.ControlType,
            ClassName = element.ClassName,
            FrameworkId = element.FrameworkId,
            IsEnabled = null,
            IsPassword = element.IsPassword
        };
    }

    private static string DescribeMessage(RawInputEvent input)
    {
        return input.Kind == RawInputKind.Mouse
            ? input.Message switch
            {
                0x0201 => "LeftButtonDown",
                0x0202 => "LeftButtonUp",
                0x0204 => "RightButtonDown",
                0x0205 => "RightButtonUp",
                0x0207 => "MiddleButtonDown",
                0x0208 => "MiddleButtonUp",
                0x020B => "XButtonDown",
                0x020C => "XButtonUp",
                _ => $"MouseMessage0x{input.Message:X4}"
            }
            : input.Message switch
            {
                0x0100 => $"KeyDown(VK_{input.VirtualKey:X2})",
                0x0101 => $"KeyUp(VK_{input.VirtualKey:X2})",
                0x0104 => $"SysKeyDown(VK_{input.VirtualKey:X2})",
                0x0105 => $"SysKeyUp(VK_{input.VirtualKey:X2})",
                _ => $"KeyboardMessage0x{input.Message:X4}"
            };
    }

    private static bool IsDownMessage(uint message) => message is 0x0100 or 0x0104 or 0x0201 or 0x0204 or 0x0207 or 0x020B;

    private static void EnsureTargetProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Target process {processId} has already exited.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Target process {processId} was not found.", exception);
        }
    }

    private sealed class Arguments
    {
        public const string Usage = "Usage: input-correlation --pid id [--output path] [--scripted] [--duration-seconds n]";

        public uint ProcessId { get; private init; }
        public string? OutputPath { get; private init; }
        public bool Scripted { get; private init; }
        public int DurationSeconds { get; private init; } = 10;

        public static Arguments Parse(string[] args)
        {
            uint? processId = null;
            string? outputPath = null;
            var scripted = false;
            var durationSeconds = 10;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pid":
                        processId = uint.Parse(Next(args, ref index, "--pid"));
                        break;
                    case "--output":
                        outputPath = Next(args, ref index, "--output");
                        break;
                    case "--scripted":
                        scripted = true;
                        break;
                    case "--duration-seconds":
                        durationSeconds = int.Parse(Next(args, ref index, "--duration-seconds"));
                        break;
                    case "--help":
                        throw new InvalidOperationException(Usage);
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (processId is null || processId == 0)
            {
                throw new ArgumentException("--pid is required and must be a positive process ID.");
            }

            if (durationSeconds <= 0)
            {
                throw new ArgumentException("--duration-seconds must be positive.");
            }

            return new Arguments
            {
                ProcessId = processId.Value,
                OutputPath = outputPath,
                Scripted = scripted,
                DurationSeconds = durationSeconds
            };
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            return args[index];
        }
    }
}
