using FluentValidation;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Application.Plans.Commands;
using TenantFlow.Subscriptions.Application.Plans.Dtos;
using TenantFlow.Subscriptions.Domain.Plans;

namespace TenantFlow.Subscriptions.Application.Plans.Services;

/// <summary>
/// Plans of the tenant in the <see cref="ITenantContext"/> (SPEC-0007 criterion 1). The routes check the permissions:
/// <c>subscriptions.manage</c> to create, <c>subscriptions.subscribe</c> to list.
/// </summary>
public sealed class PlanService(ITenantContext context, ISubscriptionStore store, IValidator<CreatePlanCommand> validator, TimeProvider clock)
{
    /// <summary>A tenant sells a handful of plans; the list is capped instead of paged.</summary>
    public const int MaxListed = 100;

    public async Task<PlanDetails> CreateAsync(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.GetUtcNow();
        var plan = Plan.Create(Guid.CreateVersion7(now), context.TenantId, command.Name, command.Amount, command.Currency, command.Interval, command.TrialDays, now);
        await store.AddPlanAsync(plan, cancellationToken);
        return PlanDetails.From(plan);
    }

    public async Task<IReadOnlyList<PlanDetails>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await store.ListPlansAsync(MaxListed, cancellationToken)).Select(PlanDetails.From)];
}
