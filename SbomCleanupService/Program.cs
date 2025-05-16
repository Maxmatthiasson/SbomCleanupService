using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SbomCleanupService;

var builder = Host.CreateApplicationBuilder(args);

// Register the Windows Service lifetime manually
builder.Services.AddWindowsService(); // Adds Windows service support

// Register your background worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
