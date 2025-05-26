// This is the main entry point for the Worker Service application.
// It sets up the host builder, registers the service lifetime for running as a Windows Service,
// and adds the background worker (Worker) as a hosted service to run in the background.

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SbomCleanupService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
