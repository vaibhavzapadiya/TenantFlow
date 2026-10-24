using Amazon;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.ImportsExports.Application.ProductExports;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Contracts;
using TenantFlow.ImportsExports.Infrastructure.Observability;
using TenantFlow.ImportsExports.Infrastructure.Persistence;
using TenantFlow.ImportsExports.Infrastructure.Storage;

namespace TenantFlow.ImportsExports.Infrastructure;

/// <summary>The ImportsExports module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class ImportsExportsModule
{
    /// <summary>
    /// Use cases, persistence and the storage adapter <c>Storage:Provider</c> names (ADR-0024 and its amendment 1): the
    /// filesystem at <c>Storage:Root</c>, which the API and the Worker must share and which must be switched on with
    /// <c>Storage:Filesystem:Enabled</c>, or S3 at <c>Storage:S3:Bucket</c> in <c>Storage:S3:Region</c> with the task role's
    /// credentials. The host refuses to start without the keys its choice needs. The host must also register the
    /// IdentityAccess module (tenant context) and the Catalog module (imports and exports), which ImportsExports reaches
    /// through their contracts only.
    /// </summary>
    public static IServiceCollection AddImportsExportsModule(this IServiceCollection services, string connectionString)
    {
        services.AddImportsExportsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        // The messages name the keys, never a value (ADR-0008).
        services.AddOptions<StorageOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
            {
                var section = configuration.GetSection(StorageOptions.SectionName);
                options.Provider = section["Provider"];
                options.Root = section["Root"];
                options.FileSystemEnabled = string.Equals(section["Filesystem:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
                options.Bucket = section["S3:Bucket"];
                options.Region = section["S3:Region"];
            })
            .Validate(static o => o.Provider is StorageOptions.FileSystemProvider or StorageOptions.S3Provider, "Storage:Provider must be filesystem or s3.")
            .Validate(static o => o.Provider != StorageOptions.FileSystemProvider || !string.IsNullOrWhiteSpace(o.Root),
                "Storage:Root is required: the folder the API and the Worker share for uploads and exports.")
            .Validate(static o => o.Provider != StorageOptions.FileSystemProvider || o.FileSystemEnabled,
                "Storage:Filesystem:Enabled must be true to keep files on this host's disk; deployed environments set Storage:Provider to s3.")
            .Validate(static o => o.Provider != StorageOptions.S3Provider || !string.IsNullOrWhiteSpace(o.Bucket), "Storage:S3:Bucket is required when Storage:Provider is s3.")
            .Validate(static o => o.Provider != StorageOptions.S3Provider || !string.IsNullOrWhiteSpace(o.Region), "Storage:S3:Region is required when Storage:Provider is s3.")
            .ValidateOnStart();
        // Created only when S3 is chosen; the SDK's default chain finds the task role's credentials. Tests may register their own.
        services.TryAddSingleton<IAmazonS3>(static services =>
            new AmazonS3Client(RegionEndpoint.GetBySystemName(services.GetRequiredService<IOptions<StorageOptions>>().Value.Region)));
        services.AddSingleton<IObjectStorage>(static services =>
            services.GetRequiredService<IOptions<StorageOptions>>().Value.Provider == StorageOptions.S3Provider
                ? ActivatorUtilities.CreateInstance<S3ObjectStorage>(services)
                : ActivatorUtilities.CreateInstance<FileSystemObjectStorage>(services));
        services.AddScoped<IFileStore, FileStore>();
        services.AddScoped<StoredFiles>();
        services.AddScoped<FileExpiry>();
        services.TryAddSingleton<ImportsExportsMetrics>();
        services.AddScoped<DueJobMetrics>();
        services.AddScoped<IImportJobStore, ImportJobStore>();
        services.AddScoped<ImportService>();
        services.AddScoped<RequesterCheck>();
        // The read-only job view of support grants (ADR-0027).
        services.AddScoped<ISupportJobView, SupportJobView>();
        services.AddScoped<ImportRunner>();
        services.AddScoped<IExportJobStore, ExportJobStore>();
        services.AddScoped<ExportService>();
        services.AddScoped<ExportRunner>();
        return services;
    }

    /// <summary>The module's due rows, for the Worker's timer runner (ADR-0023, ADR-0024 decision 3).</summary>
    public static IEnumerable<KeyValuePair<string, TimerHandler>> TimerHandlers =>
    [
        new(ImportLimits.RunKind, EndingOnLastAttempt(
            static (services, timer, cancellationToken) => services.GetRequiredService<ImportRunner>().RunAsync(timer.SubjectId, cancellationToken),
            static (services, jobId, cancellationToken) => services.GetRequiredService<ImportRunner>().FailAsync(jobId, cancellationToken))),
        new(ImportLimits.AbandonKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<ImportRunner>().AbandonAsync(timer.SubjectId, cancellationToken)),
        new(ExportLimits.RunKind, EndingOnLastAttempt(
            static (services, timer, cancellationToken) => services.GetRequiredService<ExportRunner>().RunAsync(timer.SubjectId, cancellationToken),
            static (services, jobId, cancellationToken) => services.GetRequiredService<ExportRunner>().FailAsync(jobId, cancellationToken))),
        new(FileExpiry.Kind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<FileExpiry>().ExpireAsync(timer.SubjectId, cancellationToken)),
    ];

    // A job run that fails on the last attempt the timer runner allows ends its job as failed (run_failed), so the job frees
    // its place instead of staying active forever; the failure still propagates, so the due row is marked dead and logged.
    private static TimerHandler EndingOnLastAttempt(TimerHandler run, Func<IServiceProvider, Guid, CancellationToken, Task> fail) =>
        async (services, timer, cancellationToken) =>
        {
            try
            {
                return await run(services, timer, cancellationToken);
            }
            catch (Exception) when (timer.Failures + 1 >= WorkflowTimerRunner.MaxFailures && !cancellationToken.IsCancellationRequested)
            {
                await fail(services, timer.SubjectId, cancellationToken);
                throw;
            }
        };

    /// <summary>The name of the task that deletes partial writes a crash left in storage (SPEC-0009 criterion 12).</summary>
    public const string StorageSweepTaskName = "imports_exports.storage_sweep";

    /// <summary>Every hour, at five past.</summary>
    public const string StorageSweepCronExpression = "0 5 * * * ?";

    /// <summary>A temporary file this old is left over from a crash: a write takes seconds.</summary>
    public static readonly TimeSpan TemporaryFileAge = TimeSpan.FromHours(1);

    /// <summary>The module's recurring tasks, for the Worker's scheduler (SPEC-0008 criterion 11, SPEC-0009 criterion 12).</summary>
    public static IEnumerable<ScheduledTask> ScheduledTasks =>
    [
        new(DueJobMetrics.TaskName, DueJobMetrics.CronExpression, static (services, cancellationToken) =>
            services.GetRequiredService<DueJobMetrics>().RefreshAsync(cancellationToken)),
        new(StorageSweepTaskName, StorageSweepCronExpression, static (services, cancellationToken) =>
            services.GetRequiredService<IObjectStorage>().DeleteTemporaryFilesAsync(
                services.GetRequiredService<TimeProvider>().GetUtcNow() - TemporaryFileAge, cancellationToken)),
    ];
}
