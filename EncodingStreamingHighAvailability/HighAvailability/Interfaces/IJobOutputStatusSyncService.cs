﻿namespace HighAvailability.Interfaces
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading.Tasks;

    public interface IJobOutputStatusSyncService
    {
        Task SyncJobOutputStatusAsync(DateTime currentTime, ILogger logger);
    }
}