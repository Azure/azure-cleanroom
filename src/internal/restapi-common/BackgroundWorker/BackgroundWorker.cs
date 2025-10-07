// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Controllers;

public class BackgroundWorker : BackgroundService
{
    private readonly BackgroundTaskQueue taskQueue;
    private readonly ILogger<BackgroundWorker> logger;

    public BackgroundWorker(BackgroundTaskQueue taskQueue, ILogger<BackgroundWorker> logger)
    {
        this.taskQueue = taskQueue;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await this.taskQueue.DequeueAsync(stoppingToken);
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error occurred executing background task.");
            }
        }
    }
}
