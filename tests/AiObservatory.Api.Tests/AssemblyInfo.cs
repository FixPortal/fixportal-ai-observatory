// WebApplicationFactory<Program> hosts are built via HostFactoryResolver's process-wide
// DiagnosticListener interception. Running multiple factories' StartServer() calls
// concurrently (the collection-fixture WAF tests vs. the ad-hoc per-test factories in
// DevSeedEndpointTests/StartupGuardsTests) raced and cross-applied ConfigureWebHost
// config between unrelated host builds — observed as a spurious "DB_CONNECTION
// configuration is missing" on factories that DID set it. Serializing the whole
// assembly avoids the race; this is a known WebApplicationFactory constraint, not a bug
// in the harness itself.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
