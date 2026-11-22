// EdiabasNet instances in one process must take turns; tests that run SGBD jobs run serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
