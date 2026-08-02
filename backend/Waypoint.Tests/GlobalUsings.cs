global using Xunit;

// Multiple test classes each spin up their own in-process WebApplicationFactory<Program>
// host. xUnit's default class-level parallelism races those concurrent host-builds
// against ASP.NET Core's HostFactoryResolver (a process-global hook), which
// intermittently throws "the entry point exited without ever building an IHost" —
// nothing to do with the code under test. Serializing test classes avoids it; this
// suite is small enough that the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
