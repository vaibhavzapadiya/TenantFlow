using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductExports;

/// <summary>
/// Runs an export from its due row in the job's tenant (SPEC-0008 criterion 7, ADR-0024 decision 3). It streams the
/// tenant's products from Catalog into the job's file in storage, then records the file and the job's success in one
/// transaction. Storage makes the file visible only once it is complete, so a crash leaves the previous content or none;
/// the next run starts from the first product and overwrites it. No transaction is open while Catalog is read or the file
/// is written (AGENTS.md rule 3). A job that ends is counted once (<see cref="ImportsExportsMetrics"/>); the store schedules the
/// file's expiry with its success. Before it writes it asks whether the requester may still have it run
/// (<see cref="RequesterCheck"/>); if not, it ends with that code (SPEC-0009 criterion 11).
/// </summary>
public sealed class ExportRunner(IExportJobStore store, StoredFiles files, ICatalogExport catalog, RequesterCheck requester, ImportsExportsMetrics metrics, TimeProvider clock)
{
    public async Task<TimerDecision> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.FindAsync(jobId, cancellationToken);
        if (job is not { Status: JobStatus.Queued or JobStatus.Running } || !await store.StartRunningAsync(job.Id, clock.GetUtcNow(), cancellationToken))
        {
            return TimerDecision.Complete;
        }

        if (await requester.RefusalAsync(job.RequestedBy, cancellationToken) is { } refusal)
        {
            await EndFailedAsync(job, refusal, cancellationToken);
            return TimerDecision.Complete;
        }

        await using var csv = new ProductCsvStream(catalog, ExportLimits.MaxRows, ExportLimits.PageSize);
        StoredObject written;
        try
        {
            written = await files.PutAsync(job.FileId, csv, ExportLimits.MaxBytes, cancellationToken);
        }
        catch (TooManyRowsException)
        {
            await EndFailedAsync(job, JobFailureCodes.TooManyRows, cancellationToken);
            return TimerDecision.Complete;
        }
        catch (ObjectTooLargeException)
        {
            await EndFailedAsync(job, JobFailureCodes.FileTooLarge, cancellationToken);
            return TimerDecision.Complete;
        }

        var now = clock.GetUtcNow();
        if (await store.CompleteAsync(job.Id, written, csv.Rows, now + ExportLimits.Retention, now, cancellationToken))
        {
            metrics.RecordFinished(ImportsExportsMetrics.ExportJob, JobStatusNames.Succeeded, now - job.CreatedAt);
        }

        return TimerDecision.Complete;
    }

    /// <summary>Ends a job whose run failed on every attempt allowed (<see cref="JobFailureCodes.RunFailed"/>).</summary>
    public async Task FailAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(jobId, cancellationToken) is { } job)
        {
            await EndFailedAsync(job, JobFailureCodes.RunFailed, cancellationToken);
        }
    }

    // Ends the job as failed if no other run ended it, and counts it.
    private async Task EndFailedAsync(ExportJob job, string failureCode, CancellationToken cancellationToken)
    {
        var failedAt = clock.GetUtcNow();
        if (await store.FailAsync(job.Id, failureCode, failedAt, cancellationToken))
        {
            metrics.RecordFinished(ImportsExportsMetrics.ExportJob, JobStatusNames.Failed, failedAt - job.CreatedAt);
        }
    }
}
