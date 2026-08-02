global using Xunit;

// xUnit's default class-level parallelism is deliberately left ON. Concurrent
// WebApplicationFactory<Program> hosts used to fail intermittently here, but the cause
// was this app's own Serilog bootstrap logger being frozen twice ("The logger is already
// frozen"), not a framework limitation — fixed at the source in Program.cs
// (UseSerilog(preserveStaticLogger: true)). Do not reintroduce
// [assembly: CollectionBehavior(DisableTestParallelization = true)]: it would hide the
// next regression of that kind instead of surfacing it.
