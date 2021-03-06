// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace HighAvailability.Interfaces
{
    using HighAvailability.Models;
    using Microsoft.Extensions.Logging;
    using System.Threading.Tasks;

    /// <summary>
    /// Interface to define methods to process job output status events
    /// </summary>
    public interface IJobOutputStatusService
    {
        /// <summary>
        /// Stores job output status record and submits request to provision processed assets.
        /// </summary>
        /// <param name="jobOutputStatusModel">Input data model</param>
        /// <param name="logger">Logger to log data</param>
        /// <returns>Processed job output status model</returns>
        Task<JobOutputStatusModel> ProcessJobOutputStatusAsync(JobOutputStatusModel jobOutputStatusModel, ILogger logger);
    }
}
