using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using ModelContextProtocol.AspNetCore;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AgentBroker.Worker;

public sealed class WorkerApplication : IAsyncDisposable
{
    private readonly BrokerIdentity identity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] localToken = RandomNumberGenerator.GetBytes(32);
    private Task? parentWatch;
    private LocalControlServer? control;
    private readonly McpActivityTracker mcpActivity;
    private readonly CancellationTokenRegistration mcpStarted, mcpStopping;
    private int disposed;
    public WebApplication Application { get; }
    public BrokerRuntime Runtime { get; }
    public WorkerOptions Options { get; }
    public string LocalAccessToken => Convert.ToHexString(localToken);
    public McpStatusView McpStatus => mcpActivity.Snapshot();
    // Read-only LAN observations for status: network category, this worker's rule, inbound evidence.
    public LanProfileMonitor LanProfile { get; } = new(new WindowsLanProfileProbe());

    private WorkerApplication(WebApplication application, BrokerRuntime runtime,
        BrokerIdentity identity, WorkerOptions options, McpActivityTracker mcpActivity)
    {
        Application = application; Runtime = runtime; this.identity = identity; Options = options;
        this.mcpActivity = mcpActivity;
        mcpStarted = application.Lifetime.ApplicationStarted.Register(static state => ((McpActivityTracker)state!).SetReady(true), mcpActivity);
        mcpStopping = application.Lifetime.ApplicationStopping.Register(static state => ((McpActivityTracker)state!).SetReady(false), mcpActivity);
    }

    public static async Task<WorkerApplication> CreateAsync(WorkerOptions options,
        IReadOnlyList<ILocalAgentAdapter>? adapters = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); options.Validate();
        var store = new BrokerStore(Path.Combine(options.DataDirectory, "store"),
            new BrokerLimits(DefaultMessageLifetimeMinutes: options.DefaultMessageLifetimeMinutes));
        BrokerIdentity? identity = null;
        IPeerFederation? federation = null;
        IReadOnlyList<ILocalAgentAdapter>? nativeAdapters = null;
        BrokerRuntime? runtime = null;
        WebApplication? app = null;
        WorkerApplication? worker = null;
        try
        {
            identity = BrokerIdentity.LoadOrCreate(options.DataDirectory, store.BrokerId);
            federation = new PeerFederation(store, identity, options, enableDiscovery: options.EnableBrokerDiscovery);
            nativeAdapters = adapters ?? CreateNativeAdapters(options);
            runtime = new BrokerRuntime(store, federation, nativeAdapters, new SessionPreferences(options.DataDirectory));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
            builder.Logging.ClearProviders(); // Neither message text nor credentials enter request logs.
            builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxRequestBodySize = 128 * 1024;
                server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                server.Limits.MaxConcurrentConnections = 128;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                server.Listen(IPAddress.Loopback, options.LoopbackPort);
                if (options.EnableLan)
                    server.Listen(options.LanListenAddress, options.LanPort, listener => listener.UseHttps(https =>
                    {
                        https.ServerCertificate = identity.Certificate;
                        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                        // Unknown certificates can reach only identity/pairing routes. Every data route
                        // checks the current paired identity and exact pin again for each request.
                        https.ClientCertificateValidation = (certificate, _, _) => certificate is not null &&
                            certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                            certificate.NotAfter.ToUniversalTime() >= DateTime.UtcNow;
                    }));
            });
            builder.Services.AddSingleton(runtime);
            builder.Services.AddHttpContextAccessor();
            var mcpActivity = new McpActivityTracker();
            var mcpFilters = new McpActivityFilters(mcpActivity, store.BrokerId);
            // The instructions card reaches HTTP clients directly and Codex through the verbatim stdio bridge.
            builder.Services.AddMcpServer(server => server.ServerInstructions = BrokerInstructions.Text).WithHttpTransport(transport =>
                transport.SessionMode = HttpServerSessionMode.Stateless).WithTools<BrokerTools>()
                .WithMessageFilters(filters => filters.AddIncomingFilter(mcpFilters.Incoming()).AddOutgoingFilter(mcpFilters.Outgoing()))
                .WithRequestFilters(filters => filters.AddListToolsFilter(mcpFilters.ListTools()).AddCallToolFilter(mcpFilters.CallTool()));
            app = builder.Build();
            worker = new WorkerApplication(app, runtime, identity, options, mcpActivity);
            runtime.FatalError += app.Lifetime.StopApplication;
            worker.control = new LocalControlServer(worker);
            worker.ConfigureRoutes();
            return worker;
        }
        catch
        {
            if (worker is not null) await CleanupAsync(() => worker.DisposeAsync().AsTask());
            else
            {
                if (app is not null) await CleanupAsync(() => app.DisposeAsync().AsTask());
                if (runtime is not null) await CleanupAsync(() => runtime.DisposeAsync().AsTask());
                else if (nativeAdapters is not null)
                    foreach (var adapter in nativeAdapters) await CleanupAsync(() => adapter.DisposeAsync().AsTask());
                if (federation is not null) await CleanupAsync(() => federation.DisposeAsync().AsTask());
                identity?.Dispose(); store.Dispose();
            }
            throw;
        }

        static async Task CleanupAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception) { Console.Error.WriteLine("agentbroker: startup_cleanup_failed"); }
        }
    }

    private static IReadOnlyList<ILocalAgentAdapter> CreateNativeAdapters(WorkerOptions options)
    {
        var adapters = new List<ILocalAgentAdapter>();
        if (options.EnableCodexNativeDiscovery) adapters.Add(new CodexNativeAdapter(options.EnableCodexPeerMessages,
            options.EnableCodexUpdateTolerantDelivery));
        if (options.EnableClaudeNativeDiscovery) adapters.Add(new ClaudeNativeAdapter(options.EnableClaudePeerCredentials));
        return adapters;
    }

    private void ConfigureRoutes()
    {
        Application.Use(async (context, next) =>
        {
            // Before any refusal: a request that arrived over the LAN listener proves reachability even when
            // it is then refused, which is what separates "observed reachable" from "rule configured".
            if (context.Request.IsHttps) LanProfile.ObserveInbound(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
            // Agent connections are native clients. Browsers and DNS aliases are not implicit local authority.
            if (context.Request.Headers.ContainsKey("Origin"))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!context.Request.IsHttps)
            {
                if (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
                    context.Request.Host.Host != "127.0.0.1")
                { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                if (!Options.AllowUnauthenticatedLoopback && !ValidLocalToken(context.Request.Headers.Authorization.ToString()))
                { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                context.Items["agentbroker.identity"] = Runtime.Store.BrokerId;
            }
            else if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                var peer = Runtime.Federation.AuthenticatePeer(context);
                if (peer is null) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                context.Items["agentbroker.identity"] = peer.Value;
            }
            try { await next(); }
            catch (BrokerException e)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = e.Code }, context.RequestAborted);
            }
        });
        Runtime.Federation.MapEndpoints(Application);
        Application.MapMcp("/mcp");
    }

    private bool ValidLocalToken(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length != 7 + localToken.Length * 2) return false;
        try
        {
            var supplied = Convert.FromHexString(authorization[7..]);
            try { return CryptographicOperations.FixedTimeEquals(supplied, localToken); }
            finally { CryptographicOperations.ZeroMemory(supplied); }
        }
        catch (FormatException) { return false; }
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        await Application.StartAsync(token);
        await Runtime.Federation.StartAsync(lifetime.Token);
        control!.Start();
        Runtime.Start(lifetime.Token);
        if (Options.ParentPid > 0) parentWatch = WatchParentAsync(lifetime.Token);
    }

    private async Task WatchParentAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool owned;
                try
                {
                    using var parent = Process.GetProcessById(Options.ParentPid);
                    owned = !parent.HasExited && parent.StartTime.ToUniversalTime().ToFileTimeUtc() == Options.ParentCreationFileTime;
                }
                catch (ArgumentException) { owned = false; }
                catch (InvalidOperationException) { owned = false; }
                catch (System.ComponentModel.Win32Exception) { owned = false; }
                if (!owned) { Application.Lifetime.StopApplication(); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        mcpActivity.SetReady(false);
        lifetime.Cancel();
        try
        {
            if (control is not null) await CleanAsync(() => control.DisposeAsync().AsTask());
            await CleanAsync(() => Runtime.DisposeAsync().AsTask());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CleanAsync(() => Application.StopAsync(deadline.Token));
            if (parentWatch is not null) await CleanAsync(() => parentWatch);
            await CleanAsync(() => Runtime.Federation.DisposeAsync().AsTask());
            await CleanAsync(() => Application.DisposeAsync().AsTask());
        }
        finally
        {
            Runtime.Store.Dispose(); identity.Dispose();
            mcpStarted.Dispose(); mcpStopping.Dispose();
            CryptographicOperations.ZeroMemory(localToken); lifetime.Dispose();
        }
        static async Task CleanAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception) { Console.Error.WriteLine("agentbroker: shutdown_cleanup_failed"); }
        }
    }
}
