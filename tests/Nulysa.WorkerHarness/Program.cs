using Microsoft.Extensions.Hosting;
using Nulysa;

using var host = FoundationHost.BuildWorker(args);
await host.RunAsync();
