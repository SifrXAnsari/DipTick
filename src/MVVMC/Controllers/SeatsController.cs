using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick seats [list|check|add|remove]</c>: who sits at the table.
/// </summary>
/// <remarks>
/// Adding or removing a seat is the person's act only, with a yes: a new seat is a new place the
/// conversation goes. No agent can add one; nothing an agent writes reaches this verb.
/// </remarks>
internal static class SeatsController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var sub = args.Count > 1 ? args[1] : "list";
        return sub switch
        {
            "list" when args.Count <= 2 => List(cli),
            "check" when args.Count == 2 => Check(cli),
            "add" when args.Count >= 3 => Add(args, cli),
            "remove" when args.Count == 3 => Remove(args[2], cli),
            _ => cli.Fail("usage: diptick seats [list] | seats check | seats add <id> --provider <p> --model <m> [--key-variable <VAR>] [--endpoint <url>] [--name <name>] | seats remove <id>", Exit.Usage),
        };
    }

    private static int List(Cli cli)
    {
        var config = cli.Config;
        var rows = config.Seats.Select(s =>
        {
            var key = Engines.KeysFor(s)?.Describe();
            return new
            {
                id = s.Id,
                name = s.Name,
                provider = s.Provider,
                model = s.Model,
                endpoint = s.Endpoint.Length > 0 ? s.Endpoint : Engines.Known.GetValueOrDefault(s.Provider)?.ToString() ?? string.Empty,
                keyVariable = s.KeyVariable,
                keySet = key?.Usable ?? false,
                key = key?.Words ?? "no endpoint",
                lane = s.Lane,
                ready = s.Filled,
            };
        }).ToList();

        if (cli.WriteJson(new { seats = rows, mode = config.Chat.Mode, listed = config.Chat.Seats }))
        {
            return Exit.Ok;
        }

        if (rows.Count == 0)
        {
            cli.Note("No seats. 'diptick seats add <id> --provider ... --model ...' adds one.");
            return Exit.Ok;
        }

        foreach (var r in rows)
        {
            cli.Data(SafeText.Line($"  {r.id,-12} {r.name,-12} {r.provider,-17} {(r.model.Length == 0 ? "(no model)" : r.model),-24} key: {r.key} · lane {string.Join(",", r.lane)}"));
        }

        cli.Note($"mode: {config.Chat.Mode}");
        return Exit.Ok;
    }

    private static int Check(Cli cli)
    {
        var config = cli.Config;
        var seatIds = config.Chat.Seats;
        var problems = config.Problems.Where(p => p.Contains("chat.seats", StringComparison.Ordinal) || p.Contains("Seat '", StringComparison.Ordinal)
            || seatIds.Any(id => p.Contains($"'{id}.", StringComparison.Ordinal)) || p.Contains("one key, one host", StringComparison.Ordinal)).ToList();
        foreach (var seat in config.Seats.Where(s => !s.Filled))
        {
            problems.Add($"Seat '{seat.Id}' has no {(seat.Provider.Length == 0 ? "provider" : "model")}.");
        }

        foreach (var seat in config.Seats.Where(s => s.Filled))
        {
            if (Engines.KeysFor(seat)?.Describe() is { Usable: false } key)
            {
                problems.Add($"Seat '{seat.Id}': {key.Problem}");
            }
        }

        if (cli.WriteJson(new { ok = problems.Count == 0, problems }))
        {
            return problems.Count == 0 ? Exit.Ok : Exit.Failed;
        }

        if (problems.Count == 0)
        {
            cli.Note($"{config.Seats.Count} seat(s), all ready.");
            return Exit.Ok;
        }

        foreach (var p in problems)
        {
            cli.Err.WriteLine($"  {SafeText.Line(p)}");
        }

        return Exit.Failed;
    }

    private static int Add(IReadOnlyList<string> args, Cli cli)
    {
        var id = args[2];
        string? provider = null, model = null, keyVariable = null, endpoint = null, name = null;
        for (var i = 3; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--provider": provider = Next(); break;
                case "--model": model = Next(); break;
                case "--key-variable": keyVariable = Next(); break;
                case "--endpoint": endpoint = Next(); break;
                case "--name": name = Next(); break;
                default: return cli.Fail($"seats add does not take '{args[i]}'.", Exit.Usage);
            }
        }

        if (provider is null || model is null)
        {
            return cli.Fail("seats add needs --provider and --model.", Exit.Usage);
        }

        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        if (cli.Config.Chat.Seats.Contains(id))
        {
            return cli.Fail($"'{id}' already sits. Change it with 'diptick config set {id}.<key> <value>'.");
        }

        var seats = cli.Config.Chat.Seats.Append(id).ToList();
        var edits = new List<MasterConfigWriter.Edit>
        {
            new(id, "provider", JsonSerializer.Serialize(provider), provider),
            new(id, "model", JsonSerializer.Serialize(model), model),
        };
        if (keyVariable is not null)
        {
            edits.Add(new(id, "keyVariable", JsonSerializer.Serialize(keyVariable), keyVariable));
        }

        if (endpoint is not null)
        {
            edits.Add(new(id, "endpoint", JsonSerializer.Serialize(endpoint), endpoint));
        }

        if (name is not null)
        {
            edits.Add(new(id, "name", JsonSerializer.Serialize(name), name));
        }

        edits.Add(new("chat", "seats", "[" + string.Join(", ", seats.Select(s => JsonSerializer.Serialize(s))) + "]", string.Join(", ", seats)));

        // Prove it first, so a bad id or provider is refused before anyone is asked anything.
        if (MasterConfigWriter.Apply(path, edits, write: false) is { } why)
        {
            return cli.Fail(why);
        }

        var destination = endpoint ?? Engines.Known.GetValueOrDefault(provider)?.ToString() ?? provider;
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would seat '{id}': {provider} {model}, sending to {destination}; nothing written.");
            return Exit.Ok;
        }

        var answer = cli.Confirm($"Seat '{id}' ({provider} {model})? The conversation will also go to {destination}.");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, $"seating '{id}'");
        }

        if (MasterConfigWriter.Apply(path, edits, write: true) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"'{id}' sits: {provider} {model}.{(keyVariable is null ? string.Empty : $" Put its key in {keyVariable}.")}");
        return Exit.Ok;
    }

    private static int Remove(string id, Cli cli)
    {
        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change.", Exit.Usage);
        }

        if (!cli.Config.Chat.Seats.Contains(id))
        {
            return cli.Fail($"'{id}' does not sit.", Exit.Usage);
        }

        var seats = cli.Config.Chat.Seats.Where(s => s != id).ToList();
        var edit = new MasterConfigWriter.Edit("chat", "seats", "[" + string.Join(", ", seats.Select(s => JsonSerializer.Serialize(s))) + "]", seats.Count == 0 ? "(none)" : string.Join(", ", seats));
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would unseat '{id}'; its section stays; nothing written.");
            return Exit.Ok;
        }

        var answer = cli.Confirm($"Unseat '{id}'? Its section stays in the file, unused.");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, $"unseating '{id}'");
        }

        // The section stays and is then reported as unknown: exactly those notes are expected and
        // accepted; any other new problem still refuses the change.
        if (MasterConfigWriter.Apply(path, [edit], write: true, tolerate: p => p.StartsWith($"Unknown key '{id}", StringComparison.Ordinal)) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"'{id}' no longer sits. Its section is still in the file; remove it by hand if you like.");
        return Exit.Ok;
    }
}
