using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

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

            var buffer = new InputEventBuffer();
            var worker = new CorrelationWorker(buffer, options.ProcessId);
            var reportTask = worker.Start();

            using (var hooks = new InputHookHost(buffer))
            {
                hooks.Start();
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
                    hooks.Stop();
                }
            }

            var report = reportTask.GetAwaiter().GetResult();
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
