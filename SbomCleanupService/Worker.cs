using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;


namespace SbomCleanupService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly string? _connectionString; // Using environment variable SbomDbConnectionString
        // Can use variable below if you don't want to set up an environment variable.
        //private const string ConnectionString = "Host=localhost;Username=postgres;Password=postgres;Database=sbomdb";

        private string _pipelineOrganization = "trafikverket";
        private string _pipelineProject = "BTHVulnerabilityChecker";
        private string _pipelineId = "";

        // GET https://dev.azure.com/{organization}/{project}/_apis/pipelines/{pipelineId}?api-version=7.1

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _connectionString = Environment.GetEnvironmentVariable("SbomDbConnectionString");

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("SbomDbConnectionString environment variable not set.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SbomCleanupService started at: {time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(stoppingToken);

                    // Sets archive to true where updated_at is older than 30 days.
                    string sql = "UPDATE sbom_table SET archived = true WHERE updated_at < NOW() - INTERVAL '30 days';";

                    using var cmd = new NpgsqlCommand(sql, conn);
                    int rowsDeleted = await cmd.ExecuteNonQueryAsync(stoppingToken);

                    _logger.LogInformation("Deleted {count} old entries at {time}", rowsDeleted, DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SBOM cleanup");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }

            _logger.LogInformation("SbomCleanupService stopping at: {time}", DateTimeOffset.Now);
        }
    }
}
