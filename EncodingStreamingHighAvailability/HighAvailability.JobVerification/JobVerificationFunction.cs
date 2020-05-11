namespace HighAvailability.JobVerification
{
    using Azure.Storage.Queues;
    using HighAvailability.Helpers;
    using HighAvailability.Models;
    using HighAvailability.Services;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using System;
    using System.Threading.Tasks;

    public static class JobVerificationFunction
    {
        private static IConfigService? configService;
        private static TableStorageService? mediaServiceInstanceHealthTableStorageService;
        private static TableStorageService? jobStatusTableStorageService;
        private static QueueClient? streamProvisioningRequestQueue;
        private static readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        private static readonly object configLock = new object();
        private static bool configLoaded = false;

        public static async Task Initialize()
        {
            var keyVaultName = Environment.GetEnvironmentVariable("KeyVaultName");
            if (keyVaultName == null)
            {
                throw new Exception("keyVaultName is not set");
            }

            configService = new ConfigService(keyVaultName);
            await configService.LoadConfigurationAsync().ConfigureAwait(false);

            var tableStorageAccount = CloudStorageAccount.Parse(configService.TableStorageAccountConnectionString);
            var tableClient = tableStorageAccount.CreateCloudTableClient();

            // Create a table client for interacting with the table service 
            var mediaServiceInstanceHealthTable = tableClient.GetTableReference(configService.MediaServiceInstanceHealthTableName);
            await mediaServiceInstanceHealthTable.CreateIfNotExistsAsync().ConfigureAwait(false);
            mediaServiceInstanceHealthTableStorageService = new TableStorageService(mediaServiceInstanceHealthTable);

            var jobStatusTable = tableClient.GetTableReference(configService.JobStatusTableName);
            await jobStatusTable.CreateIfNotExistsAsync().ConfigureAwait(false);
            jobStatusTableStorageService = new TableStorageService(jobStatusTable);

            streamProvisioningRequestQueue = new QueueClient(configService.StorageAccountConnectionString, configService.StreamProvisioningRequestQueueName);
            await streamProvisioningRequestQueue.CreateIfNotExistsAsync().ConfigureAwait(false);
        }

        [FunctionName("JobVerificationFunction")]
        public static async Task Run([QueueTrigger("job-verification-requests", Connection = "StorageAccountConnectionString")]string message, ILogger logger)
        {
            try
            {
                lock (configLock)
                {
                    if (!configLoaded)
                    {
                        Initialize().Wait();
                        configLoaded = true;
                    }
                }

                if (mediaServiceInstanceHealthTableStorageService == null)
                {
                    throw new Exception("mediaServiceInstanceHealthTableStorageService is null");
                }

                if (jobStatusTableStorageService == null)
                {
                    throw new Exception("jobStatusTableStorageService is null");
                }

                if (streamProvisioningRequestQueue == null)
                {
                    throw new Exception("streamProvisioningRequestQueue is null");
                }

                if (configService == null)
                {
                    throw new Exception("configService is null");
                }

                logger.LogInformation($"JobVerificationFunction::Run triggered, message={message}");
                var jobVerificationRequestModel = JsonConvert.DeserializeObject<JobVerificationRequestModel>(message, jsonSettings);
                var mediaServiceInstanceHealthStorageService = new MediaServiceInstanceHealthStorageService(mediaServiceInstanceHealthTableStorageService, logger);
                var mediaServiceInstanceHealthService = new MediaServiceInstanceHealthService(mediaServiceInstanceHealthStorageService, logger);
                var jobStatusStorageService = new JobStatusStorageService(jobStatusTableStorageService, logger);
                var streamProvisioningRequestStorageService = new StreamProvisioningRequestStorageService(streamProvisioningRequestQueue, logger);
                var jobVerificationService = new JobVerificationService(mediaServiceInstanceHealthService, jobStatusStorageService, streamProvisioningRequestStorageService, configService, logger);

                var result = await jobVerificationService.VerifyJobAsync(jobVerificationRequestModel).ConfigureAwait(false);

                logger.LogInformation($"JobVerificationFunction::Run completed, result={LogHelper.FormatObjectForLog(result)}");
            }
            catch (Exception e)
            {
                logger.LogError($"JobVerificationFunction::Run failed: exception={e.Message} message={message}");
                throw;
            }
        }
    }
}