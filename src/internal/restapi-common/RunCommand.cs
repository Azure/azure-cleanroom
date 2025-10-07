// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Common;

public abstract class RunCommand
{
    public RunCommand(ILogger logger)
    {
        this.Logger = logger;
    }

    protected ILogger Logger { get; }

    public Task<int> ExecuteCommand(string binary, string args)
    {
        return this.ExecuteCommand(binary, args, new StringBuilder(), new StringBuilder());
    }

    public async Task<int> ExecuteCommand(
        string binary,
        string args,
        StringBuilder outputTextWriter,
        StringBuilder errorTextWriter,
        bool skipOutputLogging = false,
        int? timeout = null)
    {
        this.Logger.LogInformation($"Executing command: {binary} {args}");

        // https://stackoverflow.com/questions/139593/processstartinfo-hanging-on-waitforexit-why
        var source = new TaskCompletionSource<int>();
        var escapedArgs = args.Replace("\"", "\\\"");
        using (var process = new Process()
        {
            StartInfo = new ProcessStartInfo()
            {
                FileName = binary,
                Arguments = escapedArgs,
                RedirectStandardOutput = outputTextWriter != null,
                RedirectStandardError = errorTextWriter != null,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        })
        {
            var cancellationTokenSource = timeout.HasValue ?
                new CancellationTokenSource(timeout.Value) :
                new CancellationTokenSource();

            process.Start();

            var tasks = new List<Task>(3)
            {
                this.WaitForExitAsync(process, cancellationTokenSource.Token)
            };

            if (outputTextWriter != null)
            {
                tasks.Add(this.ReadAsync(
                    x =>
                    {
                        process.OutputDataReceived += x;
                        process.BeginOutputReadLine();
                    },
                    x => process.OutputDataReceived -= x,
                    outputTextWriter,
                    skipOutputLogging,
                    cancellationTokenSource.Token));
            }

            if (errorTextWriter != null)
            {
                tasks.Add(this.ReadAsync(
                    x =>
                    {
                        process.ErrorDataReceived += x;
                        process.BeginErrorReadLine();
                    },
                    x => process.ErrorDataReceived -= x,
                    errorTextWriter,
                    skipOutputLogging,
                    cancellationTokenSource.Token));
            }

            await Task.WhenAll(tasks);

            if (process.ExitCode == 0)
            {
                source.SetResult(0);
            }
            else
            {
                var errorString = errorTextWriter?.ToString();
                var outputString = outputTextWriter?.ToString();
                source.SetException(
                    new ExecuteCommandException($"Command '{args}' failed with exit code: " +
                    $"'{process.ExitCode}'. err: '{errorString}'. output: '{outputString}'."));
            }

            process.Dispose();
            return await source.Task;
        }
    }

    private Task WaitForExitAsync(
        Process process,
        CancellationToken cancellationToken = default)
    {
        process.EnableRaisingEvents = true;

        var taskCompletionSource = new TaskCompletionSource<object>();

#pragma warning disable IDE0039 // Use local function
        EventHandler? handler = null;
#pragma warning restore IDE0039 // Use local function
        handler = (sender, args) =>
        {
            process.Exited -= handler;
            taskCompletionSource.TrySetResult(null!);
        };
        process.Exited += handler;

        if (cancellationToken != default)
        {
            cancellationToken.Register(
                () =>
                {
                    process.Exited -= handler;
                    taskCompletionSource.TrySetCanceled();
                });
        }

        return taskCompletionSource.Task;
    }

    private Task ReadAsync(
        Action<DataReceivedEventHandler> addHandler,
        Action<DataReceivedEventHandler> removeHandler,
        StringBuilder textWriter,
        bool skipOutputLogging,
        CancellationToken cancellationToken = default)
    {
        var taskCompletionSource = new TaskCompletionSource<object>();

        DataReceivedEventHandler handler = null!;
        handler = new DataReceivedEventHandler(
            (sender, e) =>
            {
                if (e.Data == null)
                {
                    removeHandler(handler);
                    taskCompletionSource.TrySetResult(null!);
                }
                else
                {
                    if (!skipOutputLogging)
                    {
                        // Log the process output/error as its useful to see in the container logs.
                        this.Logger.LogInformation(e.Data);
                    }

                    textWriter.AppendLine(e.Data);
                }
            });

        addHandler(handler);

        if (cancellationToken != default)
        {
            cancellationToken.Register(
                () =>
                {
                    removeHandler(handler);
                    taskCompletionSource.TrySetCanceled();
                });
        }

        return taskCompletionSource.Task;
    }
}

public class ExecuteCommandException : Exception
{
    public ExecuteCommandException()
    {
    }

    public ExecuteCommandException(string? message)
        : base(message)
    {
    }

    public ExecuteCommandException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}