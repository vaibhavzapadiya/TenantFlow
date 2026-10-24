namespace TenantFlow.ImportsExports.Domain.Files;

/// <summary>What a stored file is for; part of its storage key.</summary>
public static class FileKinds
{
    /// <summary>A CSV file a manager uploaded for an import.</summary>
    public const string ImportUpload = "import_upload";

    /// <summary>A CSV file an export produced.</summary>
    public const string Export = "export";

    public static readonly IReadOnlyList<string> All = [ImportUpload, Export];
}
