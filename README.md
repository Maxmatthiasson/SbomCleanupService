# SbomCleanupService

**SbomCleanupService** is a background worker built with .NET 8 that automates the archival of outdated Software Bill of Materials (SBOM) data. It connects to a PostgreSQL database, evaluates SBOM entries against Azure DevOps pipelines, and archives entries that are no longer associated with active builds.

## Features

* Connects to a PostgreSQL database to fetch SBOM entries.
* Authenticates with Azure DevOps using a Personal Access Token (PAT).
* Checks if SBOM-related pipelines are still active.
* Automatically marks outdated SBOM records as archived.
* Runs on a daily schedule as a Windows Service as well as on start-up.

## Project Structure

* **`Worker.cs`**: Core background service that handles the cleanup logic.
* **`Program.cs`**: Entry point that sets up the worker as a Windows Service.

## Prerequisites

* .NET 8 SDK
* PostgreSQL database
* Azure DevOps account with valid PAT
* Windows system (for running as a service)

## Environment Variables

Set the following environment variables before running the service:

| Variable Name            | Description                                    |
| ------------------------ | ---------------------------------------------- |
| `SbomDbConnectionString` | PostgreSQL connection string to the SBOM table |
| `SbomCleanupPAT`         | Azure DevOps Personal Access Token             |

Alternatively set-up another way to store the connection string and PAT.

## Setup & Running

### 1. Build the project

```bash
dotnet build
```

### 2. Set required environment variables

You can set these permanently in Windows or temporarily in a terminal:

```bash
$env:SbomDbConnectionString="Host=localhost;Username=sbom_user;Password=yourpassword;Database=sbom_db"
$env:SbomCleanupPAT="your_azure_devops_pat"
```

### 3. Run the worker manually

```bash
dotnet run
```

### 4. Publish and Install as a Windows Service

**How to publish in Visual Studio**
1. Right click the project and select Publish...
2. Set the target location to something appropriate like sbomcleanupservice\bin\release\net8.0\publish
3. Press the publish button.

You can register the worker as a Windows Service using `sc.exe` or [PowerShell scripts](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service#installing-a-windows-service).

Example using `sc.exe` (after publishing):

```bash
sc create SbomCleanupService binPath= "C:\path\to\SbomCleanupService.exe"
```

Instead of using dotnet build and dotnet run you can also start the service by running it in Visual Studio or in the Windows services menu after it has been published and created as a windows service.

## Database Schema Example

Expected table format in PostgreSQL:

```sql
CREATE TABLE sbom_table (
    id SERIAL PRIMARY KEY,
    sbom_data JSONB,
    collection VARCHAR(255),
    project VARCHAR(255),
    build_number VARCHAR(255),
    archived BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (collection, project)
);
```

## How It Works

1. Fetch all SBOM records where `archived = false`.
2. Query Azure DevOps to check if the associated build is active.
3. If the build is **not found**, the record is marked as `archived = true`.
4. Logs all actions and errors for auditing.

## Logging

* Logs include timestamps and operation statuses.
* Uses `ILogger` for structured logging.
* Can be redirected to file/event log when run as a Windows Service.

## Scheduling

The cleanup process runs on start and then **once every 24 hours** by default, using:

```csharp
await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
```
