using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Connectors;

namespace DipTick.ViewModels;

/// <summary>
/// Connectors in the chat: which are on in the chat on screen, switched on or
/// off from the rail, <c>/connector</c> or Settings; each change a <c>diptick chat connectors</c> verb,
/// run in this process.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>The connectors on in the chat on screen as last read; null until read again.</summary>
    private IReadOnlyList<string>? _connectorsOn;

    /// <summary>The connectors added, as <c>connectors.json</c> has them now.</summary>
    public IReadOnlyList<Connector> Connectors => ConnectorFile.Load(Config).Connectors;

    /// <summary>A mark of the connectors file as it stands, for a screen to know when to draw them again.</summary>
    public string ConnectorsStamp
    {
        get
        {
            if (ConnectorFile.PathFor(Config) is not { } path || !File.Exists(path))
            {
                return "none";
            }

            var info = new FileInfo(path);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
    }

    /// <summary>The connectors on in the chat on screen (<c>chat connectors</c>); none for a chat nobody has spoken in.</summary>
    public IReadOnlyList<string> ConnectorsOn
    {
        get
        {
            lock (_lock)
            {
                if (_connectorsOn is { } known)
                {
                    return known;
                }
            }

            IReadOnlyList<string> on = [];
            if (_sessionId is { } id)
            {
                var (json, _, code) = Json(Private("chat", "connectors", $"--session={id}"));
                if (code == Controllers.Exit.Ok)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        on = [.. doc.RootElement.GetProperty("on").EnumerateArray().Select(n => n.GetString()).OfType<string>()];
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        on = [];
                    }
                }
            }

            lock (_lock)
            {
                _connectorsOn = on;
            }

            return on;
        }
    }

    /// <summary>A connector on or off in the chat on screen (<c>chat connectors add|remove</c>), making the chat first when nobody has spoken in it. Runs on a worker thread.</summary>
    public async Task SetConnectorAsync(string name, bool on)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (EnsureKept() is not { } id)
        {
            return;
        }

        await Task.Run(() => Run(false, Private("chat", "connectors", $"--session={id}", on ? "add" : "remove", name))).ConfigureAwait(false);
        lock (_lock)
        {
            _connectorsOn = null;
        }

        Changed?.Invoke();
    }

    /// <summary>/connectors: every connector and whether it is on here (<c>chat connectors</c>), or the list alone in a chat nobody has spoken in.</summary>
    private Task ConnectorsCommandAsync() =>
        _sessionId is { } id ? Verb(Private("chat", "connectors", $"--session={id}")) : Verb("connectors");

    /// <summary>/connector on|off &lt;name&gt;.</summary>
    private async Task ConnectorCommandAsync(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not [("on" or "off") and var word, var name])
        {
            await Say(Role.System, "/connector on <name> or /connector off <name>; /connectors lists them.").ConfigureAwait(false);
            return;
        }

        await SetConnectorAsync(name, word == "on").ConfigureAwait(false);
    }
}
