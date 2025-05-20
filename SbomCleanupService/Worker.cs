using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class PipelineEntry
{
    public string collection { get; set; }
    public string project { get; set; }
    public string buildNumber { get; set; }
}


namespace SbomCleanupService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly string? _connectionString; // Using environment variable SbomDbConnectionString
        // Can use variable below if you don't want to set up an environment variable.
        //private const string ConnectionString = "Host=localhost;Username=postgres;Password=postgres;Database=sbomdb";

        //Test push

        //private string _pipelineOrganization = "trafikverket";
        //private string _pipelineProject = "BTHVulnerabilityChecker";
        //private string _pipelineId = "";

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

            const string pat = ""; // <-- Set your Azure DevOps PAT here
            var encodedPat = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            var httpClient = new HttpClient();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(stoppingToken);

                    // Archive old entries
                    //string archiveSql = "UPDATE sbom_table SET archived = true WHERE updated_at < NOW() - INTERVAL '30 days';";
                    //using var archiveCmd = new NpgsqlCommand(archiveSql, conn);
                    //int rowsDeleted = await archiveCmd.ExecuteNonQueryAsync(stoppingToken);

                    // Fetch non-archived entries
                    string selectSql = @"
                        SELECT json_agg(row_to_json(t)) 
                        FROM (
                            SELECT collection, project, buildNumber 
                            FROM sbom_table 
                            WHERE archived = false
                        ) t;
                    ";

                    using var selectCmd = new NpgsqlCommand(selectSql, conn);
                    var result = await selectCmd.ExecuteScalarAsync(stoppingToken);

                    List<PipelineEntry>? entries = null;

                    if (result != DBNull.Value && result is string json)
                    {
                        entries = JsonSerializer.Deserialize<List<PipelineEntry>>(json);
                    }

                    if (entries != null && entries.Count > 0)
                    {
                        await CheckPipelineAsync(entries, encodedPat, httpClient, stoppingToken);
                    }
                    else
                    {
                        _logger.LogInformation("No non-archived SBOM entries found.");
                    }

                    _logger.LogInformation("Archived {count} old entries at {time}", rowsDeleted, DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SBOM cleanup");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }

            _logger.LogInformation("SbomCleanupService stopping at: {time}", DateTimeOffset.Now);
        }
        private async Task CheckPipelineAsync(
            List<PipelineEntry> entries,
            string encodedPat,
            HttpClient httpClient,
            CancellationToken stoppingToken)
        {
            foreach (var entry in entries)
            {
                string url = $"https://vsrm.dev.azure.com/{entry.collection}/{entry.project}/_apis/release/releases?artifactType=Build&$expand=artifacts&api-version=7.1-preview.8";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedPat);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await httpClient.SendAsync(request, stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Azure DevOps request failed for {entry}: {status} - {error}", entry, response.StatusCode, errorText);
                    continue;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                bool isActive = false;

                if (root.GetProperty("count").GetInt32() > 0)
                {
                    foreach (var release in root.GetProperty("value").EnumerateArray())
                    {
                        foreach (var artifact in release.GetProperty("artifacts").EnumerateArray())
                        {
                            var version = artifact
                                .GetProperty("definitionReference")
                                .GetProperty("version")
                                .GetProperty("name")
                                .GetString();

                            if (version == entry.buildNumber)
                            {
                                isActive = true;
                                break;
                            }
                        }

                        if (isActive) break;
                    }
                }

                if (isActive)
                {
                    _logger.LogInformation("Build {BuildNumber} is still active in Azure DevOps.", entry.buildNumber);
                }
                else
                {
                    _logger.LogInformation("Build {BuildNumber} is inactive, can be archived.", entry.buildNumber);
                    using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(stoppingToken);

                    string updateSql = @"
                        UPDATE sbom_table 
                        SET archived = true 
                        WHERE collection = @collection 
                          AND project = @project 
                          AND buildNumber = @buildNumber;
                    ";

                    using var updateCmd = new NpgsqlCommand(updateSql, conn);
                    updateCmd.Parameters.AddWithValue("collection", entry.collection);
                    updateCmd.Parameters.AddWithValue("project", entry.project);
                    updateCmd.Parameters.AddWithValue("buildNumber", entry.buildNumber);

                    int affected = await updateCmd.ExecuteNonQueryAsync(stoppingToken);
                    _logger.LogInformation("Marked {count} entry as archived.", affected);
                    // Optional: update archived flag here if desired
                }
            }
        }

    }
}