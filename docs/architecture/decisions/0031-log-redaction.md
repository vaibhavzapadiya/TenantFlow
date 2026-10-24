# ADR-0031: Log redaction for exception messages and scopes

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (option chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: ADR-0007 (follow-up), ADR-0025; threat model 0008 (DP-A03); SPEC-0009 decision 16; SPEC-0010
  decision 3

## Context
ADR-0007 deferred redaction; SPEC-0009 moved it to M10. Faults are logged with their exception, and the OpenTelemetry
logging provider exports the message, stack and scopes to Loki. TenantFlow's own messages are source-generated with
named, reviewed placeholders, but a third-party exception message (Npgsql, a provider SDK, a JSON parser) can quote
input values, and a scope can hold whatever a caller put in it. Threat model 0008 asks for a decision plus a test that
a planted sensitive value never reaches an exported log record.

## Decision
- An OpenTelemetry log processor in `TenantFlow.BuildingBlocks.Observability`, registered for every host, which:
  keeps the exception type and stack trace; keeps the message of exceptions defined in TenantFlow assemblies; replaces
  any other exception message with `[redacted]`; and exports only scope keys on an allow-list (for example `TraceId`,
  `RequestPath`, `MessageId`, `Queue`, `Module`, `Job`).
- The JSON console logger used locally is not changed: it stays a developer aid and is not shipped anywhere.
- A test plants a value in a third-party exception message and in an unlisted scope key and asserts the exported
  record does not contain it.

Out of scope: classifying individual log properties with attributes, and redaction in the Collector.

## Options considered
- **Allow-list processor in process (chosen).** No new package; one place; works whatever the backend. Loses the text of
  third-party exceptions, which sometimes explains a fault; the type and stack usually suffice, and the console log
  still has it locally.
- **`Microsoft.Extensions.Compliance.Redaction` with data classification.** Fine-grained and standard, but needs
  classification attributes on every logged property and a new package family; heavier than the risk today.
- **Collector `redaction` or `transform` processor.** Keeps the application unchanged, but values have already left the
  process, and the rule lives in a different repository area from the code that emits the data.
- **No redaction, test only.** Cheapest; leaves third-party messages unbounded.

## Consequences
- Easier: a reviewer checks one allow-list instead of every log call.
- Harder: diagnosing some third-party faults needs the local console or a reproduction.
- Follow-up: new scope keys are added to the allow-list deliberately, like metric labels.

## Validation
The planted-value test (SPEC-0010 criterion 7), proven to fail without the processor.

## Revisit triggers
A fault that could not be diagnosed because a message was redacted; a need to classify data per property; M12's
managed logging offering redaction that replaces this.
