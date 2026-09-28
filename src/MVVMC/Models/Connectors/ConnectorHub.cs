using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DipTick.Models.Harness;
using DipTick.Models.Sandbox;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DipTick.Models.Connectors;

/// <summary>
/// The connections to connectors for this run: one a connector, opened on
/// first use, kept while its entry stays as it was, and closed when it fails, when its entry changes, or
/// when DipTick quits. A local connector's program is started by DipTick in a Job object
/// (<see cref="JobProcess"/>); a remote one is reached through DipTick's own client (no proxy, no cookies,
/// no redirect, public addresses only unless you allowed its network), with reconnection off so one call
/// is one request. Neither is offered sampling, elicitation or your folders: a connector can never use
/// your models, ask you anything, or list your files through DipTick.
/// </summary>
internal static class ConnectorHub
{
    /// <summary>The most tools one connector may offer; the rest are left out and said.</summary>
    public const int MostTools = 500;

    /// <summary>How long connecting may take before it is given up.</summary>
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Live> Open = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Opening = new(1, 1);

    private static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    /// <summary>A connector's tools, listed once a connection. <paramref name="interactive"/> lets a sign-in open the browser.</summary>
    /// <exception cref="ConnectorException">It could not be reached, or needs signing in.</exception>
    public static async Task<IReadOnlyList<McpClientTool>> ToolsAsync(MasterConfig config, Connector connector, bool interactive, Action<string>? say, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(connector);
        var live = await ConnectAsync(config, connector, interactive, say, cancel).ConfigureAwait(false);
        if (live.Tools is { } known)
        {
            return known;
        }

        try
        {
            var listed = await live.Client.ListToolsAsync(cancellationToken: cancel).ConfigureAwait(false);
            var tools = listed.Take(MostTools).ToList();
            live.Tools = tools;
            live.Dropped = Math.Max(0, listed.Count - MostTools);
            return tools;
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            await CloseAsync(connector.Name).ConfigureAwait(false);
            throw Wrap(connector, ex);
        }
    }

    /// <summary>How many tools a connector offered past <see cref="MostTools"/>, on the connection open now.</summary>
    public static int Dropped(string name) => Open.TryGetValue(name, out var live) ? live.Dropped : 0;

    /// <summary>One call of a connector's tool, on its connection.</summary>
    /// <exception cref="ConnectorException">It could not be reached, or the call failed on the way.</exception>
    public static async Task<CallToolResult> CallAsync(MasterConfig config, Connector connector, string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(connector);
        var live = await ConnectAsync(config, connector, interactive: false, say: null, cancel).ConfigureAwait(false);
        try
        {
            return await live.Client.CallToolAsync(tool, arguments, cancellationToken: cancel).ConfigureAwait(false);
        }
        catch (McpProtocolException ex)
        {
            // The server answered the call with an error: the connection is as good as it was.
            throw new ConnectorException($"{connector.Name} answered with an error: {SafeText.Line(ex.Message)}", ex);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            await CloseAsync(connector.Name).ConfigureAwait(false);
            throw Wrap(connector, ex);
        }
    }

    /// <summary>What a connection says of the server, while one is open: its name and version, and how many tools.</summary>
    public static string? Describe(string name) =>
        Open.TryGetValue(name, out var live) && live.Client.ServerInfo is { } info
            ? $"{SafeText.Line(info.Name)} {SafeText.Line(info.Version)}{(live.Tools is { } tools ? string.Create(CultureInfo.InvariantCulture, $", {tools.Count} tools") : string.Empty)}"
            : null;

    /// <summary>A connector's connection closed, and its program ended.</summary>
    public static async Task CloseAsync(string name)
    {
        if (Open.TryRemove(name, out var live))
        {
            await live.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every connection closed: DipTick is quitting.</summary>
    public static async Task CloseAllAsync()
    {
        foreach (var name in Open.Keys.ToList())
        {
            await CloseAsync(name).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why a local connector cannot run now, or null: it is off, has no yes, its program cannot be found,
    /// what would run is not what you said yes to, or its sandbox cannot hold it (a program the container
    /// cannot read, a folder it cannot be given).
    /// </summary>
    public static string? LocalProblem(Connector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (!connector.Local)
        {
            return null;
        }

        var (launch, problem) = Launch.For(connector, Environment.GetEnvironmentVariable);
        if (problem is not null)
        {
            return problem;
        }

        if (connector.Consent is null)
        {
            return $"it runs a program on this PC and waits for your yes: 'diptick connectors enable {connector.Name}'";
        }

        if (connector.Consent != ConnectorFile.ConsentHash(launch!.Program, connector))
        {
            return $"what it runs changed since your yes: 'diptick connectors enable {connector.Name}' shows it again";
        }

        return SandboxProblem(connector, launch.Program, settingsFolder: null);
    }

    /// <summary>
    /// Why a connector's sandbox cannot hold it, or null: the container cannot be made, its program is where
    /// the container cannot read it, or a folder its entry names cannot be given to it.
    /// </summary>
    public static string? SandboxProblem(Connector connector, string program, string? settingsFolder)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(program);
        if (!connector.Box.On)
        {
            return null;
        }

        var off = $"'diptick connectors sandbox {connector.Name} --off' runs it as you instead";
        if (AppContainer.For(AppContainer.ForConnector(connector.Name)).Problem is { } made)
        {
            return $"{made}; {off}";
        }

        if (CodeSandbox.Unreadable(Path.GetFileNameWithoutExtension(program), program, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } unreadable)
        {
            return $"its sandbox cannot start it: {unreadable}; or {off}";
        }

        foreach (var folder in connector.Box.Read.Concat(connector.Box.Write))
        {
            if (ConnectorPlace.FolderProblem(folder, settingsFolder) is { } bad)
            {
                return $"its sandbox cannot be given {folder}: {bad}";
            }
        }

        return null;
    }

    private static async Task<Live> ConnectAsync(MasterConfig config, Connector connector, bool interactive, Action<string>? say, CancellationToken cancel)
    {
        var shape = Shape(connector);
        if (Open.TryGetValue(connector.Name, out var known) && known.Shape == shape && known.Alive)
        {
            return known;
        }

        await Opening.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (Open.TryGetValue(connector.Name, out known))
            {
                if (known.Shape == shape && known.Alive)
                {
                    return known;
                }

                await CloseAsync(connector.Name).ConfigureAwait(false);
            }

            if (!connector.Enabled)
            {
                throw new ConnectorException($"{connector.Name} is off: 'diptick connectors enable {connector.Name}'");
            }

            var live = connector.Local
                ? await LocalAsync(config, connector, cancel).ConfigureAwait(false)
                : await RemoteAsync(config, connector, interactive, say, cancel).ConfigureAwait(false);
            live.Shape = shape;
            Open[connector.Name] = live;
            return live;
        }
        finally
        {
            Opening.Release();
        }
    }

    /// <summary>
    /// A local connector: its program started by DipTick in a Job object, spoken to over its pipes; in its
    /// own sandbox, with the network and folders its entry names, unless the entry says it runs as you.
    /// </summary>
    private static async Task<Live> LocalAsync(MasterConfig config, Connector connector, CancellationToken cancel)
    {
        if (LocalProblem(connector) is { } problem)
        {
            throw new ConnectorException($"{connector.Name}: {problem}");
        }

        var (launch, _) = Launch.For(connector, Environment.GetEnvironmentVariable);
        var settingsFolder = config.Path is { } settings ? Path.GetDirectoryName(Path.GetFullPath(settings)) : null;
        if (SandboxProblem(connector, launch!.Program, settingsFolder) is { } boxed)
        {
            throw new ConnectorException($"{connector.Name}: {boxed}");
        }

        var folder = ConnectorPlace.Home(connector.Name);
        Directory.CreateDirectory(folder);
        IReadOnlyDictionary<string, string> variables = launch.Variables;
        SandboxSpec? sandbox = null;
        if (connector.Box.On)
        {
            var (sid, why) = AppContainer.For(AppContainer.ForConnector(connector.Name));
            if (sid == 0)
            {
                throw new ConnectorException($"{connector.Name}: {why}");
            }

            ConnectorPlace.Prepare(connector, sid, settingsFolder);
            variables = ConnectorPlace.Environment(launch.Variables, connector);
            sandbox = new SandboxSpec(sid, ConnectorPlace.MostProcesses, ConnectorPlace.MostMemory, MostCpu: null, connector.Box.Capabilities);
        }

        var live = new Live();
        try
        {
            try
            {
                live.Process = launch.CommandLine is { } line
                    ? JobProcess.StartLine(launch.Program, line, variables, folder, sandbox: sandbox)
                    : JobProcess.Start(launch.Program, launch.Arguments, variables, folder, sandbox: sandbox);
            }
            catch (LaunchException ex)
            {
                throw new ConnectorException($"{connector.Name} could not be started: {ex.Message}", ex);
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(ConnectLimit);
                live.Client = await McpClient.CreateAsync(new StreamClientTransport(live.Process.Input, live.Process.Output), Options(), cancellationToken: limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is McpException or IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancel.IsCancellationRequested)
                {
                    throw;
                }

                var said = live.Process.ErrorLines;
                var why = said.Count > 0 ? $" It said: {string.Join(" / ", said.TakeLast(3))}" : string.Empty;
                var box = connector.Box.On
                    ? $" It ran {connector.Box.Words}: if it needs more, 'diptick connectors sandbox {connector.Name}' gives it, or --off runs it as you."
                    : string.Empty;
                throw new ConnectorException($"{connector.Name} started but did not speak MCP within {ConnectLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.{why}{box}", ex);
            }

            return live;
        }
        catch
        {
            await live.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A remote connector: HTTPS (plain http only to this PC), through DipTick's own client, its headers
    /// with their variables and saved values, and when no header signs it in, the SDK's sign-in with
    /// DipTick's browser, loopback answer and Credential Manager.
    /// </summary>
    private static async Task<Live> RemoteAsync(MasterConfig config, Connector connector, bool interactive, Action<string>? say, CancellationToken cancel)
    {
        var missing = new List<string>();
        var address = ConnectorFile.Expand(connector.Url ?? string.Empty, Environment.GetEnvironmentVariable, missing);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint))
        {
            throw new ConnectorException($"{connector.Name}'s address {SafeText.Line(address)} is not an address");
        }

        if (RemoteProblem(endpoint) is { } bad)
        {
            throw new ConnectorException($"{connector.Name}'s address {SafeText.Line(address)} is not one DipTick reaches: {bad}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in connector.Headers)
        {
            headers[name] = ConnectorFile.Expand(value, Environment.GetEnvironmentVariable, missing);
        }

        ClientOAuthOptions? oauth;
        try
        {
            foreach (var name in connector.SecretHeaders)
            {
                headers[name] = ConnectorSecrets.Header(connector.Name, name)
                    ?? throw new ConnectorException($"{connector.Name}'s {name} is not saved: 'diptick connectors secret {connector.Name} {name}'");
            }

            oauth = headers.ContainsKey("Authorization") ? null : OAuth(config, connector, interactive, say);
        }
        catch (Keys.KeyStoreException ex)
        {
            throw new ConnectorException($"{connector.Name}: {ex.Message}", ex);
        }

        if (missing.Count > 0)
        {
            throw new ConnectorException($"{connector.Name} names {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}, which {(missing.Count == 1 ? "is" : "are")} not set");
        }

        var allowLocal = connector.AllowPrivateNetwork || (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) || endpoint.IsLoopback;
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = connector.Name,
            TransportMode = connector.Type == ConnectorFile.Sse ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            AdditionalHeaders = headers,
            MaxReconnectionAttempts = 0,
            EnableStandaloneGetStream = false,
            ConnectionTimeout = ConnectLimit,
            OAuth = oauth,
        };
        var live = new Live();
        try
        {
            // The connection owns its HTTP client and its transport from the moment each is made, and lets them go in that order's reverse.
            live.Http = Research.SafeConnect.NewClient(allowLocal);
            live.Transport = new HttpClientTransport(options, live.Http, loggerFactory: null, ownsHttpClient: false);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(interactive ? LoopbackSignIn.Wait + ConnectLimit : ConnectLimit);
                live.Client = await McpClient.CreateAsync(live.Transport, Options(), cancellationToken: limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException or ConnectorException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancel.IsCancellationRequested)
                {
                    throw;
                }

                throw Wrap(connector, ex);
            }

            return live;
        }
        catch
        {
            await live.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Why DipTick does not reach an address, or null: https anywhere, http only to this PC.</summary>
    public static string? RemoteProblem(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return endpoint.UserInfo.Length > 0 ? "a user name or password in an address is never sent" : null;
        }

        return endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback ? null : "https only, or http to this PC";
    }

    /// <summary>
    /// The SDK's sign-in, with DipTick's parts: your client ID, else the service registers DipTick as a
    /// native app; the answer on 127.0.0.1 at the connector's port; tokens in Credential Manager; and the
    /// browser only when you asked to sign in.
    /// </summary>
    private static ClientOAuthOptions OAuth(MasterConfig config, Connector connector, bool interactive, Action<string>? say)
    {
        var port = connector.OAuth?.CallbackPort ?? LoopbackSignIn.FreePort();
        if (interactive && connector.OAuth?.CallbackPort is null)
        {
            // The port chosen now is kept, so a registration made with it stays valid for the next sign-in.
            KeepPort(config, connector, port);
        }

        return new ClientOAuthOptions
        {
            RedirectUri = LoopbackSignIn.RedirectFor(port),
            ClientId = connector.OAuth?.ClientId,
            ClientSecret = connector.OAuth?.ClientId is null ? null : ConnectorSecrets.ClientSecret(connector.Name),
            Scopes = connector.OAuth?.Scopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "DipTick", ApplicationType = "native" },
            TokenCache = new CredentialTokenCache(connector.Name),
            AuthorizationCallbackHandler = interactive
                ? (context, cancel) => LoopbackSignIn.WaitAsync(context, say ?? (_ => { }), cancel)
                : (_, _) => Task.FromException<AuthorizationResult?>(new ConnectorException($"{connector.Name} needs you to sign in: Settings > Connectors > Connect, or 'diptick connectors signin {connector.Name}'") { NeedsSignIn = true }),
        };
    }

    private static void KeepPort(MasterConfig config, Connector connector, int port)
    {
        var list = ConnectorFile.Load(config);
        if (list.Path is not { } path)
        {
            return;
        }

        var changed = list.Connectors.Select(c => c.Name == connector.Name ? c with { OAuth = (c.OAuth ?? new ConnectorOAuth(null, null, null)) with { CallbackPort = port } } : c).ToList();
        try
        {
            ConnectorFile.Write(path, changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sign-in still works this once; the next one may pick another port and register again.
        }
    }

    private static McpClientOptions Options() => new()
    {
        ClientInfo = new Implementation { Name = "DipTick", Version = Version },
        Capabilities = new ClientCapabilities(),
        InitializationTimeout = ConnectLimit,
    };

    /// <summary>A failure, in words: a sign-in needed, the address not reached, or what the SDK said.</summary>
    private static ConnectorException Wrap(Connector connector, Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is ConnectorException said)
            {
                return said;
            }
        }

        return ex switch
        {
            HttpRequestException http => new ConnectorException($"{connector.Name} could not be reached: {SafeText.Line(http.Message)}", ex),
            OperationCanceledException or TimeoutException => new ConnectorException(string.Create(CultureInfo.InvariantCulture, $"{connector.Name} did not answer within {ConnectLimit.TotalSeconds} seconds"), ex),
            System.Text.Json.JsonException => new ConnectorException($"{connector.Name} answered in a shape MCP does not give that answer: {SafeText.Line(ex.Message)}", ex),
            _ => new ConnectorException($"{connector.Name}: {SafeText.Line(ex.Message)}", ex),
        };
    }

    /// <summary>What a connection was opened for: a changed entry opens a new one.</summary>
    private static string Shape(Connector connector)
    {
        var text = new StringBuilder()
            .Append(connector.Type).Append('\0').Append(connector.Command).Append('\0').Append(string.Join('\0', connector.Args)).Append('\0')
            .Append(string.Join('\0', connector.Env.Select(e => e.Key + "=" + e.Value))).Append('\0').Append(connector.Url).Append('\0')
            .Append(string.Join('\0', connector.Headers.Select(h => h.Key + "=" + h.Value))).Append('\0').Append(string.Join('\0', connector.SecretHeaders)).Append('\0')
            .Append(connector.OAuth?.ClientId).Append('\0').Append(connector.OAuth?.Scopes).Append('\0').Append(connector.Enabled).Append(connector.Consent).Append(connector.AllowPrivateNetwork).Append('\0')
            .Append(connector.Local ? connector.Box.ConsentText() : string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// One connection: its client, what it was opened for, and what it runs or reaches through. It owns what
    /// is put in it from the moment it is put there, and lets it all go when it is disposed.
    /// </summary>
    private sealed class Live : IAsyncDisposable
    {
        private McpClient? _client;

        public McpClient Client
        {
            get => _client ?? throw new InvalidOperationException("the connection is not open");
            set => _client = value;
        }

        /// <summary>A remote connector's HTTP client: no proxy, no cookies, no redirects, public addresses only unless allowed.</summary>
        public HttpClient? Http { get; set; }

        /// <summary>A remote connector's transport, over <see cref="Http"/>.</summary>
        public HttpClientTransport? Transport { get; set; }

        /// <summary>A local connector's program.</summary>
        public JobProcess? Process { get; set; }

        public string Shape { get; set; } = string.Empty;

        public IReadOnlyList<McpClientTool>? Tools { get; set; }

        public int Dropped { get; set; }

        /// <summary>Whether it can still be used: its session has not ended, and a local connector's program still runs.</summary>
        public bool Alive => _client is { } client && !client.Completion.IsCompleted && (Process?.Running ?? true);

        public async ValueTask DisposeAsync()
        {
            if (_client is { } client)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is McpException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Closing a connection that already failed: nothing more to do than let it go.
                }
            }

            if (Transport is { } transport)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            Http?.Dispose();

            if (Process is { } process)
            {
                process.Kill();
                process.Dispose();
            }
        }
    }
}