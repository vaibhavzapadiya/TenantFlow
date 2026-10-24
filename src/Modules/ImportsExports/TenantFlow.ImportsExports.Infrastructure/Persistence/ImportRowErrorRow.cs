namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>A row of an import that was not applied: where and why, never the value (SPEC-0008 criterion 4).</summary>
internal sealed class ImportRowErrorRow
{
    public Guid TenantId { get; set; }

    public Guid JobId { get; set; }

    public int Row { get; set; }

    public string Column { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}
