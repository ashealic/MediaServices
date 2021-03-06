// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace HighAvailability.Interfaces
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Interface to define methods to sync job output status from Azure Media Services APIs.
    /// </summary>
    public interface IJobOutputStatusSyncService
    {
        /// <summary>
        /// EventGrid events sometimes are delayed or lost and manual re-sync is required. This method syncs job output status records between 
        /// job output status storage and Azure Media Services APIs. 
        /// </summary>
        /// <param name="logger">Logger to log data</param>
        /// <returns>Task for async operation</returns>
        Task SyncJobOutputStatusAsync(ILogger logger);
    }
}