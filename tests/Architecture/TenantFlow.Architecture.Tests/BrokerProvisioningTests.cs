using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0012 criterion 7, user part (MW-A02, S3f): the deployed application's RabbitMQ user has no tags and rights only in
/// its own virtual host, and only the operator's provisioning task reaches the broker's management API. The provisioner
/// runs against RabbitMQ 4.3.6 in ProvisioningTests; these tests keep the files from drifting.
/// </summary>
public sealed partial class BrokerProvisioningTests
{
    private static readonly string Root = FindRoot();

    private static string Environment(string file) =>
        File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", file)).ReplaceLineEndings("\n");

    // The rules themselves are exercised against RabbitMQ 4.3.6 by ProvisioningTests in the database test run; this keeps
    // the provisioner from ever giving the user a tag or rights outside its virtual host.
    [Fact]
    public void The_application_user_gets_no_tags_and_is_checked_against_the_management_api()
    {
        var provisioner = File.ReadAllText(Path.Combine(Root, "src", "Hosts", "TenantFlow.Migrator", "Provisioning", "BrokerProvisioner.cs"));

        Assert.Contains("new { password = options.AppPassword, tags = string.Empty }", provisioner, StringComparison.Ordinal);
        Assert.DoesNotContain("administrator", provisioner, StringComparison.Ordinal);
        Assert.Contains("$\"api/permissions/{vhost}/{user}\"", provisioner, StringComparison.Ordinal);
        Assert.Contains("if (response.StatusCode != HttpStatusCode.Unauthorized)", provisioner, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_provisioning_task_reaches_the_management_api()
    {
        var network = Environment("network.tf");

        var https = Ingress().Matches(network)
            .Select(static m => m.Groups["body"].Value)
            .Where(static body => body.Contains("security_group_id            = aws_security_group.broker.id", StringComparison.Ordinal)
                && body.Contains("from_port                    = 443", StringComparison.Ordinal))
            .ToList();
        Assert.Single(https);
        Assert.Contains("referenced_security_group_id = aws_security_group.provisioning.id", https[0], StringComparison.Ordinal);
        Assert.Contains("security_group  = aws_security_group.provisioning.id", Environment("outputs.tf"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_worker_connects_to_the_virtual_host_the_user_was_given()
    {
        var services = Environment("services.tf");

        Assert.Contains("broker_vhost = \"tenantflow\"", services, StringComparison.Ordinal);
        Assert.Contains("{ name = \"Messaging__RabbitMq__VirtualHost\", value = local.broker_vhost }", services, StringComparison.Ordinal);
        Assert.Contains("{ name = \"Provisioning__Broker__VirtualHost\", value = local.broker_vhost }", Environment("provisioning.tf"), StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // Every ingress rule, with its body (the lines up to its closing brace).
    [GeneratedRegex(@"resource ""aws_vpc_security_group_ingress_rule"" ""[\w-]+"" \{\n(?<body>(?:(?!\}\n).*\n)*?)\}\n")]
    private static partial Regex Ingress();
}
