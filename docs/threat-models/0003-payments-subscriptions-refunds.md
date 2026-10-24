# Threat model: payments, webhooks, subscriptions and refunds

- Status: Draft (M9), written for SPEC-0009 criterion 1
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: the Payments module (create with idempotency keys, simulator and Stripe test-mode adapter, signed webhooks,
  reconciliation of unknown outcomes), the order payment and refund sagas in Orders, the Subscriptions module (plans,
  subscribe, renewals from Quartz-run due rows) and refunds (captured-amount lock, idempotency, reconciliation).
- Related: blueprint sections 7, 11, 12, 14; ADR-0021, [ADR-0022](../architecture/decisions/0022-payments-provider-port-and-order-payment-saga.md),
  [ADR-0023](../architecture/decisions/0023-subscriptions-renewal-scheduler-and-refunds.md);
  [SPEC-0006](../specs/0006-payment-workflow.md), [SPEC-0007](../specs/0007-subscriptions-and-refunds.md),
  [SPEC-0009](../specs/0009-security-and-privacy.md); threat model [0001](0001-tenant-boundary.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Payments (`payments.payments`): amount, currency, status, provider reference, provider payment-method token | Confidential, integrity-critical | Decides whether money is taken and whether an order is `Paid` |
| Refunds (`payments.refunds`): amount, status, kind, reason, requesting membership | Confidential, integrity-critical | Money leaving the tenant; the reason is free text written by a person |
| Idempotency keys (`payments.idempotency_keys`): request hash, stored first response | Confidential | A replayed response must reach only the caller who made the request |
| Webhook events (`payments.webhook_events`): provider event id, tenant, payment, outcome | Internal, integrity-critical | The dedupe record that stops a replay from acting twice |
| Plans and subscriptions (`subscriptions.*`) | Confidential | Recurring charges; who is billed and how often |
| Due-row timers (`workflow_timers` in Payments, Orders, Subscriptions) | Internal | Ids only; they decide when reconciliation and renewals run |
| Stripe secret key and webhook signing secrets (simulator and Stripe) | Secret | A key can move money; a signing secret can forge provider news |
| Outbox and broker messages (`payment.*` events) | Internal, integrity-critical | Drive the order sagas |

No card number, CVC or cardholder name is accepted or stored: the client sends a provider payment-method token only.
Personal data in scope: membership ids (pseudonymous) and whatever a person types into a refund or cancel reason.

## Actors and trust boundaries

Actors: customer (pays, subscribes); tenant administrator (plans, refunds, cancels paid orders); payment provider
(Stripe test mode or the simulator), which calls back anonymously; Worker (reconciliation, renewals, saga consumers);
anyone on the internet who can reach the webhook route; developer or operator with configuration access.

| Boundary | Crossed by | Control |
|---|---|---|
| P1 Internet → tenant payment routes | pay, refund, subscribe, read | JWT, active membership, named permission, owner rule (threat model 0001) |
| P2 Internet → webhook route | `POST /v1/payment-webhooks/{provider}` | Anonymous; HMAC signature over the raw body is the only authentication |
| P3 API and Worker → provider | create, status, refund calls | No transaction open; payment or refund id sent as the provider idempotency key |
| P4 Payments → Orders and Subscriptions | outbox events, `IOrderPayments`, `ISubscriptionCharges`, `IOrderRefunds` | Contracts only (ADR-0002); one tenant per message from the envelope |
| P5 Worker → tenant data | due-row timers, consumers | Timer row holds ids only; the work runs in the row's tenant under forced RLS |
| P6 Configuration → host | provider keys and secrets | User-secrets or environment (ADR-0008); startup validation |

## Entry points and data flows

1. Pay: token → membership with `orders.place`, owner of the order → idempotency key claimed (tenant, operation, key,
   request hash including the caller's membership) → amount and currency read from Orders → payment, key and timer
   commit → Orders marks the payment begun → provider call outside any transaction → outcome and outbox event commit.
2. Webhook: raw body and signature header → HMAC check and 5-minute timestamp tolerance → TenantFlow metadata gives the
   tenant and payment → tenant must exist and the stored provider reference must match → event id deduplicated →
   state table → outbox event on a change.
3. Reconciliation: Worker claims due timers with leases → asks the provider that took the payment or refund → applies
   the answer like a webhook; after five checks without an answer the item goes to review.
4. Order sagas: Orders consumes payment and refund events once each (`orders.processed_messages`) in the event's tenant.
5. Subscribe and renew: `subscriptions.subscribe` → subscription and renewal timer commit → Quartz runs the due-row
   job → Payments charges (subscription, period, attempt) once → the subscription advances by its state table.
6. Refund: `orders.manage` → key claimed → payment row locked, captured amount minus live refunds checked → refund and
   timer commit → provider call → outcome, event, order moves to `PartiallyRefunded` or `Refunded`.

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| PY-01 | Webhook spoofing | Mark an unpaid order paid | Post an unsigned or wrongly signed event to the anonymous route |
| PY-02 | Webhook replay | Re-apply an old outcome | Resend a captured signed request later or many times |
| PY-03 | Duplicate and out-of-order events | Move a paid payment back, or act twice | Late `processing` after `succeeded`, duplicate broker delivery, refund event before payment event |
| PY-04 | Cross-tenant webhook resolution | Change tenant B's payment with a valid event for A | Signed event whose metadata names another tenant or payment |
| PY-05 | Idempotency-key misuse | Read another caller's stored response, or block their request | Reuse a known key from another member or tenant |
| PY-06 | Double charge on timeout | Customer charged twice | Provider times out; client or Worker retries with a new provider key |
| PY-07 | Second charge per order or period | Charge twice | New key on a paid order; renewal job runs twice or loses its lease |
| PY-08 | Over-refund race | Refund more than was captured | Concurrent refunds, or a cancellation refund racing a partial refund |
| PY-09 | Unauthorised refund | Customer refunds their own order | Call the refund or paid-order cancel route without `orders.manage` |
| PY-10 | Cross-tenant or cross-customer access | Pay, read or cancel someone else's order or subscription | Change the route tenant or object id |
| PY-11 | Amount tampering | Pay less than the order or plan total | Send an amount in the body; amounts the provider rounds |
| PY-12 | Card data submitted | Make TenantFlow store card data (PCI scope) | Send a card number as the payment-method token |
| PY-13 | Wrong provider mode | Take real money in development, or fake money in production | Live Stripe key configured; simulator enabled outside development |
| PY-14 | Provider secret leakage | Move money or forge webhooks | Key or signing secret in a tracked file, log or image |
| PY-15 | Renewal after rights end | Charge a removed member or a suspended tenant (M7-R1) | Renewal timer fires after removal, suspension or cancellation |
| PY-16 | Personal data in refund reason | Keep personal data longer than needed | Free text up to 500 characters stored for the life of the tenant |
| PY-17 | Refund repudiation | Deny having issued a refund | Only the refund row records who and why; no append-only audit record yet |
| PY-18 | Webhook flood | Exhaust API capacity | Many requests to the anonymous route, each costing a read and an HMAC |
| PY-19 | Unbounded retention | Growing store of keys and events | `idempotency_keys` and `webhook_events` are never deleted today |
| PY-20 | Provider-side reversal unnoticed | Refund reversed after success goes unseen | Provider reports a reversal after `succeeded` |
| PY-21 | Due row in the wrong tenant | Renewal or reconciliation acts on another tenant | Timer tables sit outside RLS |

## Existing controls (end of M8, verified in code and tests)

- `WebhookSignature.Verify` checks HMAC-SHA256 over `timestamp.` plus the exact body bytes, compares in constant time,
  refuses a missing secret or header, and refuses a timestamp more than 5 minutes off. Event ids are unique per provider.
- `PaymentWebhookService` resolves the tenant only from TenantFlow's metadata in the verified event, answers an unknown
  tenant and an unknown payment with the same 404, and the store requires the stored provider reference to match.
  Payments, refunds, keys and webhook events have forced RLS.
- Idempotency keys are keyed by (tenant, operation, key); the request hash includes the caller's membership, so another
  member gets 422 and another tenant's use is a separate request. An unfinished key answers 409.
- A timeout or 5xx is `unknown`, never a failure; the provider receives the payment or refund id as its idempotency key
  and nothing is retried in the request. One payment per order, and one per (subscription, period, attempt), by unique index.
- Refunds lock the payment row and check captured minus non-failed refunds; at most one cancellation refund per payment,
  sent only after other refunds settle. The refund currency is the payment's; the amount is checked (> 0, scale ≤ 4).
- Order amounts come from Orders and plan amounts from the plan, never from the request; a zero total is refused.
- A token of 12 to 19 digits (also split by underscores) is refused and never echoed.
- The host refuses a Stripe key without `sk_test_` and refuses simulator payments unless explicitly enabled. Stripe
  secrets come from user-secrets or the environment (`StripeOptions`).
- Renewals of a removed member end without a charge; renewals of a suspended tenant wait; a charge that succeeds after
  its subscription ended is refunded once.
- The API's request body limit (64 KiB) applies to webhooks; nothing is read from the database before the signature verifies.

## Gaps and residual risk

| Gap | Residual risk | Handled by |
|---|---|---|
| No audit record for refunds and paid-order cancels | Repudiation relies on refund rows the application role can update | SPEC-0009 AC 2 (M9) |
| Refund reason is free text kept for the life of the tenant | Personal data typed by an admin stays indefinitely | SPEC-0009 AC 7; erasure deferred (M13 review) |
| `idempotency_keys` and `webhook_events` never deleted | Growth; stored responses kept longer than needed | SPEC-0009 AC 8 (keys 30 d, events 90 d) |
| No rate limit on the webhook route | Flood degrades the API for every tenant | M11 |
| Production secret storage and rotation not built | Secrets handled by hand outside development | M12 |
| Provider reversal after success only logged, untested, no alert | A reversal can go unnoticed | M10 |
| Timer tables outside RLS (ids only) | A bug could select another tenant's row id | Accepted (ADR-0023); work runs under RLS |
| Live Stripe test-mode run is manual | Fixture drift from Stripe's real shapes | Accepted until CI runs |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| PY-A01 | PY-01 | HMAC-SHA256 over raw body bytes, constant-time compare, missing secret fails closed | Claude | M6 | done | `PaymentWebhookTests.Forged_stale_tampered_or_malformed_webhooks_are_refused_and_change_nothing`, `PaymentWebhookTests.Without_a_configured_secret_every_webhook_is_refused`, `StripePaymentProviderTests.A_stripe_signed_event_is_read_and_a_wrong_or_missing_secret_refuses_it` |
| PY-A02 | PY-02 | 5-minute timestamp tolerance plus provider event id dedupe | Claude | M6 | done | `PaymentWebhookTests.Forged_stale_tampered_or_malformed_webhooks_are_refused_and_change_nothing`, `PaymentWebhookTests.Duplicate_and_out_of_order_webhooks_change_nothing_and_announce_nothing_twice` |
| PY-A03 | PY-03 | State tables ignore illegal transitions; message dedupe in Orders; early refund event retried | Claude | M7 | done | `PaymentWebhookTests.Duplicate_and_out_of_order_webhooks_change_nothing_and_announce_nothing_twice`, `PaymentStateMachineTests.A_settled_payment_ignores_every_later_answer_and_keeps_its_failure_code`, `OrderPaymentSagaTests.A_duplicate_has_one_effect_and_a_redelivery_after_a_crash_before_the_release_still_releases`, `OrderRefundSagaTests.A_refund_event_before_the_payment_event_is_retried_and_a_refunded_order_cannot_be_cancelled` |
| PY-A04 | PY-04 | Tenant from signed metadata, stored provider reference must match, same 404 for unknown tenant or payment, forced RLS | Claude | M6 | done | `PaymentWebhookTests.A_correctly_signed_webhook_cannot_act_on_another_tenants_payment_or_a_mismatched_one`, `PaymentWebhookTests.A_signed_event_without_tenantflow_metadata_is_acknowledged_and_changes_nothing`, `RowLevelSecurityTests.Row_level_security_is_forced_on_every_tenant_owned_table` |
| PY-A05 | PY-05 | Keys scoped to (tenant, operation, key); request hash includes the caller's membership; unfinished key is 409 | Claude | M7 | done | `PaymentEndpointTests.The_same_key_and_body_replay_the_first_response_without_charging_again`, `PaymentEndpointTests.A_repeat_while_the_first_request_is_still_charging_is_409_and_starts_nothing`, `RefundEndpointTests.A_repeated_refund_key_replays_another_body_is_refused_and_an_unfinished_one_is_in_progress`, `CreatePaymentCommandValidatorTests.Card_numbers_and_malformed_keys_are_refused_with_their_codes` |
| PY-A06 | PY-06 | Timeout is unknown, reconciled with the provider that took it; provider idempotency key is the payment id | Claude | M6 | done | `PaymentEndpointTests.A_timeout_or_server_error_records_an_unknown_outcome_shown_as_processing_with_no_event`, `PaymentReconciliationTests.A_timed_out_charge_is_settled_by_reconciliation_once_its_first_check_is_due_and_announced_once`, `PaymentReconciliationTests.A_payment_is_reconciled_by_the_provider_that_took_it_not_the_one_configured_now`, `StripePaymentProviderTests.Every_ambiguous_answer_is_an_unknown_outcome_and_nothing_is_sent_twice` |
| PY-A07 | PY-07 | Unique payment per order and per (subscription, period, attempt); conditional state updates | Claude | M7 | done | `PaymentEndpointTests.An_order_with_a_payment_is_not_charged_again_under_a_new_key_and_a_draft_cannot_be_paid`, `SubscriptionRenewalTests.A_renewal_that_runs_again_after_charging_or_twice_at_once_charges_once_and_advances_once`, `SubscriptionRenewalTests.One_attempt_holds_one_payment_and_a_stale_renewal_cannot_move_a_subscription_back` |
| PY-A08 | PY-08 | Payment row locked while captured minus live refunds is checked; one cancellation refund that waits for others | Claude | M7 | done | `RefundEndpointTests.Refunds_never_add_up_to_more_than_was_captured_even_when_sent_at_once`, `OrderRefundSagaTests.A_cancellation_waits_for_a_pending_refund_and_then_refunds_exactly_the_rest`, `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order` |
| PY-A09 | PY-09 | Refunds and paid-order cancels require `orders.manage`; a customer gets 403 | Claude | M7 | done | `RefundEndpointTests.An_order_manager_refunds_part_of_a_paid_order_with_an_audited_reason_and_a_customer_cannot`, `IsolationMatrixTests` |
| PY-A10 | PY-09 | Paid-order cancel route checks `orders.place`, the service enforces `orders.manage` | user | M12 | accepted | Accepted in ADR-0023 (M7-R13); the service check refuses a customer, reviewed with the production identity provider |
| PY-A11 | PY-10 | Owner and tenant checks, 404 for foreign ids, forced RLS on payments, refunds, plans and subscriptions | Claude | M7 | done | `PaymentEndpointTests.Another_tenants_or_another_customers_order_cannot_be_paid_or_read_and_nothing_is_stored`, `RefundEndpointTests.Another_tenant_can_neither_refund_nor_list_refunds_of_this_tenants_order`, `SubscriptionEndpointTests.Another_tenant_can_neither_see_nor_cancel_nor_subscribe_to_this_tenants_plans_and_subscriptions`, `SubscriptionEndpointTests.Only_the_subscriber_and_a_manager_see_or_cancel_a_subscription_and_cancel_follows_the_period_rules` |
| PY-A12 | PY-11 | Amount from Orders or the plan, zero total refused, amounts Stripe cannot charge exactly refused before sending | Claude | M7 | done | `PaymentEndpointTests.A_reserved_order_is_charged_once_for_its_own_amount_and_announces_the_outcome`, `PaymentEndpointTests.An_order_with_nothing_to_pay_is_refused_and_stays_cancellable`, `StripePaymentProviderTests.An_amount_stripe_cannot_charge_exactly_is_refused_before_anything_is_sent`, `StripePaymentProviderTests.Isk_and_ugx_are_whole_units_sent_in_hundredths_and_a_fraction_is_refused` |
| PY-A13 | PY-12 | Card-number-shaped tokens refused with 400, never echoed, nothing stored | Claude | M7 | done | `PaymentEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored`, `SubscriptionEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored`, `CreatePaymentCommandValidatorTests.Card_numbers_and_malformed_keys_are_refused_with_their_codes` |
| PY-A14 | PY-13 | Host refuses a non-test Stripe key and refuses simulator payments unless enabled | Claude | M6 | done | `ConfigurationValidationTests.Host_refuses_to_start_with_a_stripe_key_that_is_not_a_test_key_and_names_only_the_key`, `ConfigurationValidationTests.Host_refuses_to_take_payments_with_the_simulator_unless_it_is_switched_on` |
| PY-A15 | PY-13 | Live Stripe test-mode run stays a manual opt-in check; tests use recorded fixtures | user | M12 | accepted | No remote and no CI yet; fixture drift is a known risk until a live test-mode run is automated |
| PY-A16 | PY-14 | Secret scan over history and working tree, failing on a planted fake secret | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| PY-A17 | PY-14 | Production secret store and rotation for the Stripe key and signing secrets | user | M12 | planned | M12 cloud deployment |
| PY-A18 | PY-15 | Removed member's renewal ends without a charge; suspended tenant's renewals wait; late charge refunded once | Claude | M7 | done | `SubscriptionRenewalTests.A_removed_members_subscription_ends_at_its_next_renewal_without_a_charge`, `SubscriptionRenewalTests.Renewals_of_a_suspended_tenant_wait_and_charge_nothing`, `SubscriptionRenewalTests.A_charge_that_succeeds_after_its_subscription_ended_is_refunded_once` |
| PY-A19 | PY-16 | Classify refund and cancel reasons in the privacy and retention document | Claude | M9 | done | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule` |
| PY-A20 | PY-16 | Reason kept with the refund for the life of the tenant, at most 500 characters | user | M13 | accepted | Refunds are kept for the life of the tenant (SPEC-0009 Q5); on-request erasure and offboarding are deferred |
| PY-A21 | PY-17 | Append-only audit record for refund requested and paid order cancelled, via the outbox, no free text | Claude | M9 | done | `RefundEndpointTests.An_order_manager_refunds_part_of_a_paid_order_with_an_audited_reason_and_a_customer_cannot`, `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order` |
| PY-A22 | PY-17 | No hash-chained audit store; tamper evidence from privileges and an update trigger | user | M13 | accepted | SPEC-0009 decision 4; a hash chain is a revisit trigger |
| PY-A23 | PY-18 | Rate limit the anonymous webhook route and tenant payment routes | Claude | M11 | planned | M11 workload model |
| PY-A24 | PY-19 | Retention tasks delete finished idempotency keys after 30 days and webhook events after 90 days, per tenant | Claude | M9 | done | `RetentionTests.Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run` |
| PY-A25 | PY-20 | Test and alert on a provider reversal after a successful refund | Claude | M10 | done | `RefundEndpointTests.A_refund_failed_after_it_succeeded_is_counted_as_reversed`; the RefundReversed page alert and its promtool test in `deploy/compose/config/prometheus/rules/alerts.rules.test.yml` |
| PY-A26 | PY-21 | Timers act only in their own tenant; work runs under forced RLS | Claude | M7 | done | `PaymentReconciliationTests.A_timer_runs_in_its_own_tenant_only`, `WorkflowTimerTests.Scheduling_a_kind_and_subject_again_moves_the_timer_only_within_its_own_tenant`, `OrderPaymentSagaTests.A_message_acts_in_its_own_tenant_only_and_an_unknown_tenant_or_order_is_dead_lettered` |
| PY-A27 | PY-21 | Timer tables stay outside RLS, holding ids only | user | M12 | accepted | Accepted in ADR-0022 and ADR-0023; the claimed work runs in the row's tenant under RLS |

## Security tests and monitoring

- Tests: the classes cited above, including the signed duplicate, delayed, out-of-order, forged and stale webhooks from
  the simulator test helpers, the isolation matrix and the forced-RLS check. New payment routes or events must extend
  them and pass the `slice` skill's tenant isolation checklist and a manual security review.
- Monitoring today: webhook refusals answer 401 or 400 with stable codes; a dead due row is logged as an error and a
  provider reversal after a successful refund is logged as critical. Alerts on webhook refusal spikes, review counts and reversals arrive in M10.

## Review date and change triggers

Review at the M9 gate, then at M10 (alerting), M11 (rate limits and provider resilience) and M12 (live provider
account, secret store, production identity provider). Also review when a provider, payment method, currency rule,
webhook event type or refund kind is added; when a second payment attempt, plan changes or proration are proposed; or
when audit or retention rules change.
