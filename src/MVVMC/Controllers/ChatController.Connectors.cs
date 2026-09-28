using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Connectors;

namespace DipTick.Controllers;

/// <summary>
/// <c>chat connectors</c>: which connectors are on in a chat. Agents in a chat
/// see only the tools of the connectors on in it, each call still gated as <c>mcp.call</c>.
/// </summary>
internal static partial class ChatController
{
    private const string ConnectorsUsage = "usage: diptick chat connectors [--session <id|last>] [add|remove <name>]";

    private static int Connectors(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var rest = words.Words();
        if (words.Problem is not null || rest.Count is not (0 or 2) || (rest.Count == 2 && rest[0] is not ("add" or "remove")))
        {
            return cli.Fail(ConnectorsUsage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var list = ConnectorFile.Load(cli.Config);
        var on = store.ChatConnectors(found.Id);
        if (rest.Count == 0)
        {
            if (cli.WriteJson(new
            {
                session = found.Id,
                on,
                connectors = list.Connectors.Select(c => new { name = c.Name, on = on.Contains(c.Name, StringComparer.Ordinal), enabled = c.Enabled, state = ConnectorsController.State(c) }),
            }))
            {
                return Exit.Ok;
            }

            if (list.Connectors.Count == 0)
            {
                cli.Note("No connectors added yet: 'diptick connectors' lists the known ones, and adds one.");
                return Exit.Ok;
            }

            foreach (var c in list.Connectors)
            {
                cli.Data(SafeText.Line($"  {(on.Contains(c.Name, StringComparer.Ordinal) ? "on " : "off")}  {c.Name,-16} {ConnectorsController.State(c)}"));
            }

            return Exit.Ok;
        }

        var name = rest[1];
        var adding = rest[0] == "add";
        if (list.Find(name) is not { } connector)
        {
            if (adding || !on.Contains(name, StringComparer.Ordinal))
            {
                return cli.Fail($"no connector {SafeText.Line(name)}; 'diptick connectors' lists them.", Exit.Usage);
            }

            connector = null;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would switch {name} {(adding ? "on" : "off")} in {found.Id}; nothing changed.");
            return Exit.Ok;
        }

        store.SetChatConnector(found.Id, name, adding);
        if (!cli.WriteJson(new { session = found.Id, connector = name, on = adding }))
        {
            cli.Note(adding
                ? connector is { Enabled: false } ? $"{name} is on in this chat, and off in Settings, so it offers nothing until 'diptick connectors enable {name}'." : $"{name} is on in this chat: agents here may call its tools, each through your permissions."
                : $"{name} is off in this chat.");
        }

        return Exit.Ok;
    }
}
