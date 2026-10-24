// TenantFlow Migrator: applies module migrations as the owner role (ADR-0012, SPEC-0002 criterion 1). With the command
// "provision", it first creates what the migrations need, as the server's administrator (ProvisioningApplication).

using TenantFlow.Migrator;
using TenantFlow.Migrator.Provisioning;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return args is [ProvisioningApplication.Command, ..]
    ? await ProvisioningApplication.RunAsync(args[1..], cancellationToken: cancellation.Token)
    : await MigratorApplication.RunAsync(args, cancellationToken: cancellation.Token);
