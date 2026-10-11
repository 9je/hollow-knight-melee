// libmelee_core is one instance per process: tests that load it must not run in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
