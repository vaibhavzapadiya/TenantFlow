using TenantFlow.BuildingBlocks.Abstractions.Workflow;

namespace TenantFlow.ImportsExports.Application.Files;

/// <summary>
/// Deletes a file when it expires (SPEC-0008 criterion 10, ADR-0024 decision 4): an export 7 days after it succeeded, an
/// upload 7 days after its import ended. The job stores schedule the due row, with the file id as its subject, in the
/// transaction that ends the job; it runs in the file's tenant. Deleting is idempotent, so a run that repeats is harmless.
/// </summary>
public sealed class FileExpiry(StoredFiles files)
{
    public const string Kind = "imports_exports.expire_file";

    public async Task<TimerDecision> ExpireAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await files.DeleteAsync(fileId, cancellationToken);
        return TimerDecision.Complete;
    }
}
