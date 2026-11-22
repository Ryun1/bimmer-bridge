// EdiabasNet binds its static register table to the most recently constructed instance
// (EdiabasNet.cs:3224-3227), so two instances interpreting at once in one process corrupt
// each other. Tests that run SGBD jobs must therefore run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
