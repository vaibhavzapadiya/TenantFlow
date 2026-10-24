namespace TenantFlow.Migrator.Provisioning;

/// <summary>
/// What <c>provision</c> sets up, bound from <c>Provisioning</c> (environment variables such as
/// <c>Provisioning__Database__Password</c>). Passwords come from the environment or a secret store, never a file.
/// </summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public DatabaseProvisioningOptions Database { get; } = new();

    public BrokerProvisioningOptions Broker { get; } = new();
}

/// <summary>The PostgreSQL server, the administrator that provisions it, and the passwords the roles get.</summary>
public sealed class DatabaseProvisioningOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 5432;

    /// <summary>TenantFlow's database; the roles get their rights on it.</summary>
    public string? Database { get; set; }

    /// <summary>The administrator: the Compose superuser locally, the RDS master user in AWS.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>An Npgsql <c>SslMode</c>: <c>VerifyFull</c> in AWS, <c>Disable</c> on the local network.</summary>
    public string SslMode { get; set; } = "VerifyFull";

    /// <summary>The certificate authority the server is verified against (the RDS bundle in the image).</summary>
    public string? RootCertificate { get; set; }

    public string? OwnerPassword { get; set; }

    public string? AppPassword { get; set; }

    /// <summary>When set, Keycloak's own role and database are provisioned too (deployed environments, ADR-0035).</summary>
    public string? KeycloakPassword { get; set; }
}

/// <summary>
/// The RabbitMQ management API and the application user to create (MW-A02). Skipped when <see cref="ManagementUrl"/>
/// is not set: the local broker's default user is the application's.
/// </summary>
public sealed class BrokerProvisioningOptions
{
    public string? ManagementUrl { get; set; }

    public string? OperatorUsername { get; set; }

    public string? OperatorPassword { get; set; }

    public string? AppUsername { get; set; }

    public string? AppPassword { get; set; }

    public string? VirtualHost { get; set; }
}
