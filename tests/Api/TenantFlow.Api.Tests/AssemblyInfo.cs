// WebApplicationFactory finds a top-level-statement host by intercepting the next host built in
// the process. Test classes that build hosts in parallel can intercept each other's host and
// settings, which made a configuration test flaky. Host tests therefore run one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// SPEC-0010 criterion 5: every TenantFlow metric these tests record is checked against the label allow-list.
[assembly: AssemblyFixture(typeof(TenantFlow.Testing.MeterTagGuardFixture))]
