/// <summary>
/// SbomCleanupService is a background worker that connects to a PostgreSQL database,
/// fetches entries from a table in a database containing SBOM information, 
/// checks their associated Azure DevOps pipelines, and archives those no longer active.
/// </summary>

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public class PipelineEntry
{
    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("build_number")]
    public string? BuildNumber { get; set; }
}

namespace SbomCleanupService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly string? _connectionString; // Using environment variable SbomDbConnectionString
        private readonly string? _pat; // Using environment variable SbomCleanupPAT
        private int _rowsArchived;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _connectionString = Environment.GetEnvironmentVariable("SbomDbConnectionString");
            _pat = Environment.GetEnvironmentVariable("SbomCleanupPAT");
            _rowsArchived = 0;

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("SbomDbConnectionString environment variable not set.");
            }

            if (string.IsNullOrWhiteSpace(_pat))
            {
                throw new InvalidOperationException("PAT not set in environment variable SbomCleanupPAT.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SbomCleanupService started at: {time}", DateTimeOffset.Now);

            var encodedPat = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_pat}"));
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encodedPat);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(stoppingToken);

                    // Fetch non-archived entries
                    string selectSql = @"
                        SELECT json_agg(row_to_json(t)) 
                        FROM (
                            SELECT collection, project, build_number 
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

                    _logger.LogInformation("Archived {count} old entries at {time}", _rowsArchived, DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SBOM cleanup");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }

            _logger.LogInformation("SbomCleanupService stopping at: {time}", DateTimeOffset.Now);
        }

        // Checks each SBOM entry's associated pipeline in Azure DevOps
        // and archives the entry in the database if the pipeline is no longer active
        private async Task CheckPipelineAsync(
            List<PipelineEntry> entries,
            string encodedPat,
            HttpClient httpClient,
            CancellationToken stoppingToken)
        {
            foreach (var entry in entries)
            {
                string url = $"https://vsrm.dev.azure.com/{entry.Collection}/{entry.Project}/_apis/release/releases?artifactType=Build&$expand=artifacts&api-version=7.1-preview.8";

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

                            if (version == entry.BuildNumber)
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
                    _logger.LogInformation("Build {BuildNumber} is still active in Azure DevOps.", entry.BuildNumber);
                }
                else
                {
                    _logger.LogInformation("Build {BuildNumber} is inactive, can be archived.", entry.BuildNumber);
                    using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(stoppingToken);

                    string updateSql = @"
                        UPDATE sbom_table 
                        SET archived = true 
                        WHERE collection = @collection 
                          AND project = @project 
                          AND build_number = @buildNumber;
                    ";

                    using var updateCmd = new NpgsqlCommand(updateSql, conn);
                    updateCmd.Parameters.AddWithValue("collection", entry.Collection);
                    updateCmd.Parameters.AddWithValue("project", entry.Project);
                    updateCmd.Parameters.AddWithValue("buildNumber", entry.BuildNumber);

                    int affected = await updateCmd.ExecuteNonQueryAsync(stoppingToken);
                    _rowsArchived += affected;
                    _logger.LogInformation("Marked {count} entry as archived.", affected);
                }
            }
        }
    }
}
