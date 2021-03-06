// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace HighAvailability.Services
{
    using HighAvailability.Helpers;
    using HighAvailability.Interfaces;
    using HighAvailability.Models;
    using Microsoft.Azure.Management.Media.Models;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// This class implements logic to process job output status event updates 
    /// </summary>
    public class JobOutputStatusService : IJobOutputStatusService
    {
        /// <summary>
        /// Storage services to persist job output status events.
        /// </summary>
        /// 
        private readonly IJobOutputStatusStorageService jobOutputStatusStorageService;

        /// <summary>
        /// Storage services to persist provisioning requests.
        /// </summary>
        private readonly IProvisioningRequestStorageService provisioningRequestStorageService;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="jobOutputStatusStorageService">Storage services to persist job output status events.</param>
        /// <param name="provisioningRequestStorageService">Storage services to persist provisioning requests.</param>
        public JobOutputStatusService(IJobOutputStatusStorageService jobOutputStatusStorageService,
                                       IProvisioningRequestStorageService provisioningRequestStorageService)
        {
            this.jobOutputStatusStorageService = jobOutputStatusStorageService ?? throw new ArgumentNullException(nameof(jobOutputStatusStorageService));
            this.provisioningRequestStorageService = provisioningRequestStorageService ?? throw new ArgumentNullException(nameof(provisioningRequestStorageService));
        }

        /// <summary>
        /// Stores job output status record and submits request to provision processed assets.
        /// </summary>
        /// <param name="jobOutputStatusModel">Input data model</param>
        /// <param name="logger">Logger to log data</param>
        /// <returns>Processed job output status model</returns>
        public async Task<JobOutputStatusModel> ProcessJobOutputStatusAsync(JobOutputStatusModel jobOutputStatusModel, ILogger logger)
        {
            logger.LogInformation($"JobOutputStatusService::ProcessJobOutputStatusAsync started: jobOutputStatusModel={LogHelper.FormatObjectForLog(jobOutputStatusModel)}");

            // Provisioning request is created for all job output status events that are finished.
            if (jobOutputStatusModel.JobOutputState == JobState.Finished)
            {
                var provisioningRequestResult = await this.provisioningRequestStorageService.CreateAsync(
                    new ProvisioningRequestModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        ProcessedAssetMediaServiceAccountName = jobOutputStatusModel.MediaServiceAccountName,
                        ProcessedAssetName = jobOutputStatusModel.JobOutputAssetName,
                        StreamingLocatorName = $"streaming-{jobOutputStatusModel.JobOutputAssetName}"
                    },
                    logger).ConfigureAwait(false);

                logger.LogInformation($"JobOutputStatusService::ProcessJobOutputStatusAsync created stream provisioning request: result={LogHelper.FormatObjectForLog(provisioningRequestResult)}");
            }

            // Persist all status updates
            var jobOutputStatusResult = await this.jobOutputStatusStorageService.CreateOrUpdateAsync(jobOutputStatusModel, logger).ConfigureAwait(false);
            logger.LogInformation($"JobOutputStatusService::ProcessJobOutputStatusAsync completed: jobOutputStatusResult={LogHelper.FormatObjectForLog(jobOutputStatusResult)}");

            return jobOutputStatusResult;
        }
    }
}
