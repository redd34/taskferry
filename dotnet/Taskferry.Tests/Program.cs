using BenchmarkDotNet.Running;

// Tests share process-wide leak counters (live GCHandles / Rust buffers), so run them serially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

// `dotnet test` discovers the xUnit tests; running the executable runs the benchmarks.
BenchmarkSwitcher.FromAssembly(typeof(Taskferry.Tests.FfiBenchmarks).Assembly).Run(args);
