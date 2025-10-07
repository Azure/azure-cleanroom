// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Channels;

namespace Controllers;

public class BackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> queue;

    public BackgroundTaskQueue()
    {
        this.queue = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
    }

    public async ValueTask EnqueueAsync(Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await this.queue.Writer.WriteAsync(workItem);
    }

    public async ValueTask<Func<CancellationToken, Task>> DequeueAsync(
        CancellationToken cancellationToken)
    {
        return await this.queue.Reader.ReadAsync(cancellationToken);
    }
}
