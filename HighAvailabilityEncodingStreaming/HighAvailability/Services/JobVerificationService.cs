// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace HighAvailability.Services
{
    using HighAvailability.Helpers;
    using HighAvailability.Interfaces;
    using HighAvailability.Models;
    using Microsoft.Azure.Management.Media;
    using Microsoft.Azure.Management.Media.Models;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// Implements methods to verify that jobs are completed successfully
    /// </summary>
    public class JobVerificationService : IJobVerificationService
    {
        /// <summary>
        /// Media services instance health services is used to determine next available healthy instance to resubmit failed job.
        /// </summary>
        private readonly IMediaServiceInstanceHealthService mediaServiceInstanceHealthService;

        /// <summary>
        /// Job output status storage service is used to check current job status.
        /// </summary>
        private readonly IJobOutputStatusStorageService jobOutputStatusStorageService;

        /// <summary>
        /// Provisioning request storage service is used to persist new request to provision processed assets.
        /// </summary>
        private readonly IProvisioningRequestStorageService provisioningRequestStorageService;

        /// <summary>
        /// Job verification request storage service is used to submit new job verification request for future verification.
        /// </summary>
        private readonly IJobVerificationRequestStorageService jobVerificationRequestStorageService;

        /// <summary>
        /// Factory to get Azure Media Service instance client.
        /// </summary>
        private readonly IMediaServiceInstanceFactory mediaServiceInstanceFactory;

        /// <summary>
        /// Configuration container.
        /// </summary>
        private readonly IConfigService configService;

        /// <summary>
        /// Max number of verification retries before dropping verification logic.
        /// </summary>
        private readonly int maxNumberOfRetries = 2;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="mediaServiceInstanceHealthService">Service to load Azure Media Service instance health information</param>
        /// <param name="jobOutputStatusStorageService">Storage service for job output status records</param>
        /// <param name="provisioningRequestStorageService">Storage service to persist provisioning requests</param>
        /// <param name="jobVerificationRequestStorageService">Storage service to persist job verification requests</param>
        /// <param name="mediaServiceInstanceFactory">Factory to create Azure Media Services instance</param>
        /// <param name="configService">Configuration container</param>
        public JobVerificationService(IMediaServiceInstanceHealthService mediaServiceInstanceHealthService,
                                    IJobOutputStatusStorageService jobOutputStatusStorageService,
                                    IProvisioningRequestStorageService provisioningRequestStorageService,
                                    IJobVerificationRequestStorageService jobVerificationRequestStorageService,
                                    IMediaServiceInstanceFactory mediaServiceInstanceFactory,
                                    IConfigService configService)
        {
            this.mediaServiceInstanceHealthService = mediaServiceInstanceHealthService ?? throw new ArgumentNullException(nameof(mediaServiceInstanceHealthService));
            this.jobOutputStatusStorageService = jobOutputStatusStorageService ?? throw new ArgumentNullException(nameof(jobOutputStatusStorageService));
            this.provisioningRequestStorageService = provisioningRequestStorageService ?? throw new ArgumentNullException(nameof(provisioningRequestStorageService));
            this.jobVerificationRequestStorageService = jobVerificationRequestStorageService ?? throw new ArgumentNullException(nameof(jobVerificationRequestStorageService));
            this.mediaServiceInstanceFactory = mediaServiceInstanceFactory ?? throw new ArgumentNullException(nameof(mediaServiceInstanceFactory));
            this.configService = configService ?? throw new ArgumentNullException(nameof(configService));
        }

        /// <summary>
        /// Verifies the status of given job, implements business logic to resubmit jobs if needed
        /// </summary>
        /// <param name="jobVerificationRequestModel">Job verification request</param>
        /// <param name="logger">Logger to log data</param>
        /// <returns>Processed job verification request</returns>
        public async Task<JobVerificationRequestModel> VerifyJobAsync(JobVerificationRequestModel jobVerificationRequestModel, ILogger logger)
        {
            logger.LogInformation($"JobVerificationService::VerifyJobAsync started: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");

            // Get latest job output status from storage service.
            var jobOutputStatus = await this.jobOutputStatusStorageService.GetLatestJobOutputStatusAsync(jobVerificationRequestModel.JobName, jobVerificationRequestModel.JobOutputAssetName).ConfigureAwait(false);
            var jobOutputStatusLoadedFromAPI = false;

            // if job has not reached final state, need to reload status from Azure Media Service APIs in case of delayed or lost EventGrid event.
            if (jobOutputStatus?.JobOutputState != JobState.Finished && jobOutputStatus?.JobOutputState != JobState.Error && jobOutputStatus?.JobOutputState != JobState.Canceled)
            {
                var clientConfiguration = this.configService.MediaServiceInstanceConfiguration[jobVerificationRequestModel.MediaServiceAccountName];

                var clientInstance = this.mediaServiceInstanceFactory.GetMediaServiceInstance(jobVerificationRequestModel.MediaServiceAccountName, logger);
                logger.LogInformation($"JobVerificationService::VerifyJobAsync checking job status using API: mediaServiceInstanceName={jobVerificationRequestModel.MediaServiceAccountName}");

                // Get job data to verify status of specific job output.
                var job = await clientInstance.Jobs.GetAsync(clientConfiguration.ResourceGroup,
                            clientConfiguration.AccountName,
                            jobVerificationRequestModel.OriginalJobRequestModel.TransformName,
                            jobVerificationRequestModel.JobName).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationService::VerifyJobAsync loaded job data from API: job={LogHelper.FormatObjectForLog(job)}");

                if (job != null)
                {
                    // create job output status record using job loaded from Azure Media Service API.
                    var statusInfo = MediaServicesHelper.GetJobOutputState(job, jobVerificationRequestModel.JobOutputAssetName);
                    jobOutputStatus = new JobOutputStatusModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        EventTime = statusInfo.Item2,
                        JobOutputState = statusInfo.Item1,
                        JobName = job.Name,
                        MediaServiceAccountName = jobVerificationRequestModel.MediaServiceAccountName,
                        JobOutputAssetName = jobVerificationRequestModel.JobOutputAssetName,
                        TransformName = jobVerificationRequestModel.OriginalJobRequestModel.TransformName,
                        HasRetriableError = MediaServicesHelper.HasRetriableError(job, jobVerificationRequestModel.JobOutputAssetName) // check if job should be retried
                    };

                    jobOutputStatusLoadedFromAPI = true;

                    // persist job output status record
                    await this.jobOutputStatusStorageService.CreateOrUpdateAsync(jobOutputStatus, logger).ConfigureAwait(false);
                }
            }

            // At this point here, jobOutputStatus is either loaded from job output status storage or from Azure Media Service API.
            logger.LogInformation($"JobVerificationService::VerifyJobAsync jobOutputStatus={LogHelper.FormatObjectForLog(jobOutputStatus)}");

            // Check if job output has been successfully finished.
            if (jobOutputStatus?.JobOutputState == JobState.Finished)
            {
                await this.ProcessFinishedJobAsync(jobVerificationRequestModel, jobOutputStatusLoadedFromAPI, logger).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationService::VerifyJobAsync] job was completed successfully: jobOutputStatus={LogHelper.FormatObjectForLog(jobOutputStatus)}");
                return jobVerificationRequestModel;
            }

            // Check if job output failed.
            if (jobOutputStatus?.JobOutputState == JobState.Error)
            {
                await this.ProcessFailedJob(jobVerificationRequestModel, jobOutputStatus, logger).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationService::VerifyJobAsync] job failed: jobOutputStatus={LogHelper.FormatObjectForLog(jobOutputStatus)}");
                return jobVerificationRequestModel;
            }

            // check if job has been canceled.
            if (jobOutputStatus?.JobOutputState == JobState.Canceled)
            {
                logger.LogInformation($"JobVerificationService::VerifyJobAsync] job canceled: jobOutputStatus={LogHelper.FormatObjectForLog(jobOutputStatus)}");
                return jobVerificationRequestModel;
            }

            // At this point, job is stuck, it is not in the final state and long enough time period has passed (since this code is running for a given job).
            await this.ProcessStuckJob(jobVerificationRequestModel, logger).ConfigureAwait(false);

            logger.LogInformation($"JobVerificationService::VerifyJobAsync completed: job={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");

            return jobVerificationRequestModel;
        }

        /// <summary>
        /// Processes successfully finished job.
        /// </summary>
        /// <param name="jobVerificationRequestModel">Job verification request to process.</param>
        /// <param name="submitProvisioningRequest">Flag to indicate if provisioning request should be submitted.</param>
        /// <param name="logger">Logger to log data.</param>
        /// <returns></returns>
        private async Task ProcessFinishedJobAsync(JobVerificationRequestModel jobVerificationRequestModel, bool submitProvisioningRequest, ILogger logger)
        {
            logger.LogInformation($"JobVerificationService::ProcessFinishedJob started: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");

            // check if stream provisioning requests needs to be submitted. 
            // There are two scenarios, if job was completed and there is record in local storage, there is nothing to do, since this request was submitted as part of job output status process.
            // If job is completed, but status is missing in storage service, this means that EventGrid event was lost and provisioning request needs to be submitted.
            if (submitProvisioningRequest)
            {
                var provisioningRequestResult = await this.provisioningRequestStorageService.CreateAsync(
                   new ProvisioningRequestModel
                   {
                       Id = Guid.NewGuid().ToString(),
                       ProcessedAssetMediaServiceAccountName = jobVerificationRequestModel.MediaServiceAccountName,
                       ProcessedAssetName = jobVerificationRequestModel.JobOutputAssetName,
                       StreamingLocatorName = $"streaming-{jobVerificationRequestModel.OriginalJobRequestModel.OutputAssetName}"
                   },
                   logger).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationService::ProcessFinishedJob stream provisioning request submitted for completed job: provisioningRequestResult={LogHelper.FormatObjectForLog(provisioningRequestResult)}");
            }

            // Need to delete completed jobs not to reach max number of jobs in Azure Media Service instance 
            await this.DeleteJobAsync(jobVerificationRequestModel, logger).ConfigureAwait(false);

            logger.LogInformation($"JobVerificationService::ProcessFinishedJob completed: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
        }

        /// <summary>
        /// Processes failed job.
        /// </summary>
        /// <param name="jobVerificationRequestModel">Job verification request to process.</param>
        /// <param name="jobOutputStatusModel">Job output status request to process, it should match data in above parameter.</param>
        /// <param name="logger">Logger to log data.</param>
        /// <returns></returns>
        private async Task ProcessFailedJob(JobVerificationRequestModel jobVerificationRequestModel, JobOutputStatusModel jobOutputStatusModel, ILogger logger)
        {
            logger.LogInformation($"JobVerificationService::ProcessFailedJob started: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)} jobOutputStatusModel={LogHelper.FormatObjectForLog(jobOutputStatusModel)}");

            // Need to delete failed jobs not to reach max number of jobs in Azure Media Service instance. 
            await this.DeleteJobAsync(jobVerificationRequestModel, logger).ConfigureAwait(false);

            // If job has failed for system errors, it needs to be resubmitted.
            if (jobOutputStatusModel.HasRetriableError)
            {
                await this.ResubmitJob(jobVerificationRequestModel, logger).ConfigureAwait(false);
            }
            else
            {
                // no need to resubmit job that failed for user error.
                logger.LogInformation($"JobVerificationService::ProcessFailedJob submitted job failed, not a system error, skipping retry: result={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
            }

            logger.LogInformation($"JobVerificationService::ProcessFailedJob completed: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)} jobOutputStatusModel={LogHelper.FormatObjectForLog(jobOutputStatusModel)}");
        }

        /// <summary>
        /// Processes job that has not been completed or failed
        /// </summary>
        /// <param name="jobVerificationRequestModel">Job verification request to process</param>
        /// <param name="logger">Logger to log data</param>
        /// <returns></returns>
        private async Task ProcessStuckJob(JobVerificationRequestModel jobVerificationRequestModel, ILogger logger)
        {
            logger.LogInformation($"JobVerificationService::ProcessStuckJob started: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");

            // Job has not been completed over predefined period of time, need to submit another request to verify it in the future.
            await this.SubmitVerificationRequestAsync(jobVerificationRequestModel, logger).ConfigureAwait(false);

            logger.LogInformation($"JobVerificationService::ProcessStuckJob completed: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
        }

        /// <summary>
        /// Submits another verification request for "stuck" job.
        /// </summary>
        /// <param name="jobVerificationRequestModel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        private async Task SubmitVerificationRequestAsync(JobVerificationRequestModel jobVerificationRequestModel, ILogger logger)
        {
            // This method is called for job that has not been completed in time. 
            // To avoid indefinite loop, need to check if it is possible to submit another verification request. 
            if (jobVerificationRequestModel.RetryCount < this.maxNumberOfRetries)
            {
                jobVerificationRequestModel.RetryCount++;
                // extend verification delay.
                var verificationDelay = new TimeSpan(0, this.configService.TimeDurationInMinutesToVerifyJobStatus * jobVerificationRequestModel.RetryCount, 0);

                // submit new verification request with future visibility.
                var retryCount = 3;
                var retryTimeOut = 1000;
                // Job is submitted at this point, failing to do any calls after this point would result in reprocessing this job request and submitting duplicate one.
                // It is OK to retry and ignore exception at the end. In current implementation based on Azure storage, it is very unlikely to fail in any of the below calls.
                do
                {
                    try
                    {
                        var jobVerificationResult = await this.jobVerificationRequestStorageService.CreateAsync(jobVerificationRequestModel, verificationDelay, logger).ConfigureAwait(false);
                        logger.LogInformation($"JobVerificationService::SubmitVerificationRequestAsync successfully submitted jobVerificationModel: result={LogHelper.FormatObjectForLog(jobVerificationResult)}");
                        // no exception happened, let's break.
                        break;
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception e)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        logger.LogError($"JobVerificationService::SubmitVerificationRequestAsync got exception calling jobVerificationRequestStorageService.CreateAsync: retryCount={retryCount} message={e.Message} jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
                        retryCount--;
                        await Task.Delay(retryTimeOut).ConfigureAwait(false);
                    }
                }
                while (retryCount > 0);
            }
            else
            {
                logger.LogWarning($"JobVerificationService::SubmitVerificationRequestAsync max number of retries reached to check stuck job, this job request will not be processed, please manually check if job needs to be resubmitted, skipping request: result={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
            }
        }

        /// <summary>
        /// Deletes job info from Azure Media Service instance.
        /// </summary>
        /// <param name="jobVerificationRequestModel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        private async Task DeleteJobAsync(JobVerificationRequestModel jobVerificationRequestModel, ILogger logger)
        {
            logger.LogInformation($"JobVerificationService::DeleteJobAsync started: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");

            var clientConfiguration = this.configService.MediaServiceInstanceConfiguration[jobVerificationRequestModel.MediaServiceAccountName];
            var clientInstance = this.mediaServiceInstanceFactory.GetMediaServiceInstance(jobVerificationRequestModel.MediaServiceAccountName, logger);
            await clientInstance.Jobs.DeleteWithHttpMessagesAsync(clientConfiguration.ResourceGroup, clientConfiguration.AccountName, jobVerificationRequestModel.OriginalJobRequestModel.TransformName, jobVerificationRequestModel.JobName).ConfigureAwait(false);

            logger.LogInformation($"JobVerificationService::DeleteJobAsync completed: jobVerificationRequestModel={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
        }

        /// <summary>
        /// Resubmits failed job.
        /// </summary>
        /// <param name="jobVerificationRequestModel">Job verification request to process</param>
        /// <param name="logger">Logger to log data</param>
        /// <returns></returns>
        private async Task ResubmitJob(JobVerificationRequestModel jobVerificationRequestModel, ILogger logger)
        {
            // This method is called for failed job.
            // To avoid indefinite loop, need to check if it is possible to resubmit job
            if (jobVerificationRequestModel.RetryCount < this.maxNumberOfRetries)
            {
                var selectedInstanceName = await this.mediaServiceInstanceHealthService.GetNextAvailableInstanceAsync(logger).ConfigureAwait(false);
                var clientConfiguration = this.configService.MediaServiceInstanceConfiguration[selectedInstanceName];
                var clientInstance = this.mediaServiceInstanceFactory.GetMediaServiceInstance(selectedInstanceName, logger);
                jobVerificationRequestModel.RetryCount++;

                var transform = await clientInstance.Transforms.GetAsync(
                            clientConfiguration.ResourceGroup,
                            clientConfiguration.AccountName,
                            jobVerificationRequestModel.OriginalJobRequestModel.TransformName).ConfigureAwait(false);

                // Need to check transform output on error setting. If all outputs have continue job, no need to resubmit such job, since failure is not critical.
                if (transform.Outputs.All(t => t.OnError == OnErrorType.ContinueJob))
                {
                    logger.LogInformation($"JobVerificationService::ResubmitJob skipping request to resubmit since all transforms outputs are set to continue job: transform={LogHelper.FormatObjectForLog(transform)}");
                    return;
                }

                // Logic below is similar to JobSchedulingService implementation when initial job is submitted.
                // This logic below may need to be updated if multiple job outputs are used per single job and partial resubmit is required to process only failed job outputs.

                // Update output asset.
                var outputAsset = await clientInstance.Assets.CreateOrUpdateAsync(
                            clientConfiguration.ResourceGroup,
                            clientConfiguration.AccountName,
                            jobVerificationRequestModel.JobOutputAssetName,
                            new Asset()).ConfigureAwait(false);

                JobOutput[] jobOutputs = { new JobOutputAsset(outputAsset.Name) };

                // Old job is deleted, new job can be submitted again with the same name.
                var job = await clientInstance.Jobs.CreateAsync(
                           clientConfiguration.ResourceGroup,
                           clientConfiguration.AccountName,
                           jobVerificationRequestModel.OriginalJobRequestModel.TransformName,
                           jobVerificationRequestModel.JobName,
                           new Job
                           {
                               Input = jobVerificationRequestModel.OriginalJobRequestModel.JobInputs,
                               Outputs = jobOutputs,
                           }).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationService::ResubmitJob successfully re-submitted job: job={LogHelper.FormatObjectForLog(job)}");

                jobVerificationRequestModel.JobId = job.Id;
                jobVerificationRequestModel.MediaServiceAccountName = selectedInstanceName;
                jobVerificationRequestModel.JobName = job.Name;
                jobVerificationRequestModel.JobOutputAssetName = outputAsset.Name;

                // new verification request is submitted to verify the result of this job in future.
                await this.SubmitVerificationRequestAsync(jobVerificationRequestModel, logger).ConfigureAwait(false);

                this.mediaServiceInstanceHealthService.RecordInstanceUsage(selectedInstanceName, logger);
            }
            else
            {
                logger.LogWarning($"JobVerificationService::ResubmitJob max number of retries reached to check stuck job, this job request will not be processed, please manually check if job needs to be resubmitted, skipping request: result={LogHelper.FormatObjectForLog(jobVerificationRequestModel)}");
            }
        }
    }
}
