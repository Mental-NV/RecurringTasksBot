// Configuration tests manipulate process-wide environment variables;
// the suite stays serial so they cannot race file-loading tests.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
