// These tests start API hosts. WebApplicationFactory intercepts the next host built in the process,
// so host tests run one at a time (.agents/rules/tests.md); each still uses its own database.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// SPEC-0010 criterion 5: every TenantFlow metric these tests record is checked against the label allow-list.
[assembly: AssemblyFixture(typeof(TenantFlow.Testing.MeterTagGuardFixture))]
