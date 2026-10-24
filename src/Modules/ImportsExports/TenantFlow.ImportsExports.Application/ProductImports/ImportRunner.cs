using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Csv;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductImports;

/// <summary>
/// Runs an import from its due row in the job's tenant (SPEC-0008 criteria 4 and 5, ADR-0024 decision 3). It checks the
/// header and the row count before anything is written, then hands batches of 500 rows to Catalog, which applies each
/// (job, batch) exactly once, and records a checkpoint after each. A run applies at most
/// <see cref="ImportLimits.BatchesPerRun"/> batches and runs again, so a lease is never held long; a crash resumes after
/// the last checkpoint. No transaction is open while the file is read or Catalog is called (AGENTS.md rule 3). A job that
/// ends is counted once (<see cref="ImportsExportsMetrics"/>). Before it starts and before every batch it asks whether the
/// requester may still have it run (<see cref="RequesterCheck"/>); if not, it ends with that code and the batches already
/// applied stay (SPEC-0009 criterion 11).
/// </summary>
public sealed class ImportRunner(IImportJobStore store, StoredFiles files, ICatalogImport catalog, RequesterCheck requester, ImportsExportsMetrics metrics, TimeProvider clock)
{
    public async Task<TimerDecision> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.FindAsync(jobId, cancellationToken);
        if (job is not { Status: JobStatus.Queued or JobStatus.Running })
        {
            return TimerDecision.Complete;
        }

        if (await RefusedAsync(job, cancellationToken))
        {
            return TimerDecision.Complete;
        }

        var now = clock.GetUtcNow();
        if (!await store.StartRunningAsync(job.Id, now, cancellationToken))
        {
            return TimerDecision.Complete;
        }

        // Pass 1: the file's shape, before anything is written.
        var (failure, columns) = await ScanAsync(job.FileId, cancellationToken);
        if (failure is not null || columns is null)
        {
            await EndAsync(job, JobStatus.Failed, failure ?? JobFailureCodes.InvalidHeader, cancellationToken);
            return TimerDecision.Complete;
        }

        // Pass 2: the batches after the checkpoint. The scan just read the file whole, so it is there and intact.
        var checkpoint = job.CheckpointRow;
        var batches = 0;
        await using (var content = await files.OpenAsync(job.FileId, cancellationToken) ?? throw new InvalidOperationException("The import's file is missing."))
        {
            var batch = new List<ProductImportRow>();
            var readErrors = new List<ImportRowError>();
            var row = 0;
            await foreach (var record in CsvReader.ReadAsync(content, cancellationToken))
            {
                if (row++ == 0 || row - 1 <= checkpoint)
                {
                    continue;
                }

                var number = row - 1;
                if (record.Length != columns.Count)
                {
                    readErrors.Add(new ImportRowError(number, "*", "column_count"));
                }
                else
                {
                    // Text cells an export neutralized are restored (SPEC-0008 criterion 8).
                    batch.Add(new ProductImportRow(number, CsvCells.Restore(record[columns["sku"]]), CsvCells.Restore(record[columns["name"]]), record[columns["price_amount"]],
                        record[columns["price_currency"]], record[columns["status"]]));
                }

                if (batch.Count + readErrors.Count == ImportLimits.BatchSize)
                {
                    if (await RefusedAsync(job, cancellationToken))
                    {
                        return TimerDecision.Complete;
                    }

                    checkpoint = await ApplyAsync(job.Id, checkpoint, number, batch, readErrors, cancellationToken);
                    if (++batches == ImportLimits.BatchesPerRun)
                    {
                        return TimerDecision.RunAgain(clock.GetUtcNow());
                    }
                }
            }

            if (batch.Count + readErrors.Count > 0)
            {
                if (await RefusedAsync(job, cancellationToken))
                {
                    return TimerDecision.Complete;
                }

                checkpoint = await ApplyAsync(job.Id, checkpoint, row - 1, batch, readErrors, cancellationToken);
            }
        }

        await EndAsync(job, JobStatus.Succeeded, null, cancellationToken);
        return TimerDecision.Complete;
    }

    /// <summary>
    /// Fails an upload that is still receiving when its hour passed (SPEC-0008 criterion 10). Only a job still receiving is
    /// ended, and its file deleted: one queued meanwhile goes on.
    /// </summary>
    public async Task<TimerDecision> AbandonAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(jobId, cancellationToken) is { Status: JobStatus.Receiving } job
            && await EndAsync(job, JobStatus.Failed, JobFailureCodes.UploadAbandoned, cancellationToken, onlyFrom: JobStatus.Receiving))
        {
            await files.DeleteAsync(job.FileId, cancellationToken);
        }

        return TimerDecision.Complete;
    }

    /// <summary>Ends a job whose run failed on every attempt allowed (<see cref="JobFailureCodes.RunFailed"/>).</summary>
    public async Task FailAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(jobId, cancellationToken) is { } job)
        {
            await EndAsync(job, JobStatus.Failed, JobFailureCodes.RunFailed, cancellationToken);
        }
    }

    // Ends the job, before anything more is written, when its requester may no longer have it run.
    private async Task<bool> RefusedAsync(ImportJob job, CancellationToken cancellationToken)
    {
        if (await requester.RefusalAsync(job.RequestedBy, cancellationToken) is not { } failureCode)
        {
            return false;
        }

        await EndAsync(job, JobStatus.Failed, failureCode, cancellationToken);
        return true;
    }

    // Ends the job if no other run did (the store also schedules its upload's expiry), and counts it.
    private async Task<bool> EndAsync(ImportJob job, JobStatus status, string? failureCode, CancellationToken cancellationToken, JobStatus? onlyFrom = null)
    {
        if (!await store.FinishAsync(job.Id, status, failureCode, clock.GetUtcNow(), cancellationToken, onlyFrom))
        {
            return false;
        }

        if (await store.FindAsync(job.Id, cancellationToken) is { CompletedAt: { } completedAt } ended)
        {
            metrics.RecordFinished(ImportsExportsMetrics.ImportJob, status.ToName(), completedAt - ended.CreatedAt, ended.FailedCount);
        }

        return true;
    }

    // Catalog applies the batch (exactly once per job and batch), then the checkpoint moves past it.
    private async Task<int> ApplyAsync(Guid jobId, int checkpoint, int lastRow, List<ProductImportRow> batch, List<ImportRowError> readErrors, CancellationToken cancellationToken)
    {
        var batchNumber = (checkpoint / ImportLimits.BatchSize) + 1;
        var result = await catalog.ApplyBatchAsync(jobId, batchNumber, batch, cancellationToken);
        if (!await store.CheckpointAsync(jobId, checkpoint, lastRow, result, readErrors, clock.GetUtcNow(), cancellationToken))
        {
            throw new InvalidOperationException("The import's checkpoint moved under this run; it resumes on its next run.");
        }

        batch.Clear();
        readErrors.Clear();
        return lastRow;
    }

    // The header's columns by name, or why the file cannot be imported: a header that is not exactly the import's columns,
    // too many rows, text that is not CSV (not UTF-8, an unclosed quote, a field or row over its limit), or a file that is
    // gone or no longer matches its checksum. Each is permanent: retrying would fail the same way.
    private async Task<(string? Failure, Dictionary<string, int>? Columns)> ScanAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var content = await files.OpenAsync(fileId, cancellationToken);
        if (content is null)
        {
            return (JobFailureCodes.FileMissing, null);
        }

        Dictionary<string, int>? columns = null;
        var rows = -1;
        try
        {
            await foreach (var record in CsvReader.ReadAsync(content, cancellationToken))
            {
                if (++rows == 0)
                {
                    var names = record.Select(static n => n.Trim().ToLowerInvariant()).ToList();
                    if (names.Count != ImportLimits.Columns.Count || names.Distinct().Count() != names.Count || !ImportLimits.Columns.All(names.Contains))
                    {
                        return (JobFailureCodes.InvalidHeader, null);
                    }

                    columns = names.Select(static (n, i) => (n, i)).ToDictionary(static p => p.n, static p => p.i);
                }
                else if (rows > ImportLimits.MaxRows)
                {
                    return (JobFailureCodes.TooManyRows, null);
                }
            }
        }
        catch (FormatException)
        {
            return (JobFailureCodes.FileUnreadable, null);
        }
        catch (StorageIntegrityException)
        {
            return (JobFailureCodes.FileCorrupt, null);
        }

        return (null, columns);
    }
}
