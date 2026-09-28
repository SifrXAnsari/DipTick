using DipTick.Models;
using DipTick.Models.Chat;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick settings [&lt;name&gt;]</c>: every setting as a person reads it, by section, with its value,
/// what it does, its default and how to change it. It changes nothing; <c>config set</c> stays the
/// one way to write.
/// </summary>
internal static class SettingsController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        if (args.Count > 2)
        {
            return cli.Fail("usage: diptick settings [<name>]", Exit.Usage);
        }

        var rows = ConfigController.Rows(cli);
        var shown = Entries(cli.Config).ToList();
        if (args.Count == 2)
        {
            // A former name (ai.model) is read as the name it has now (main.model), and said so.
            var name = ConfigController.Current(args[1], cli);
            var entry = shown.FirstOrDefault(e => string.Equals(e.Info.Name, name, StringComparison.OrdinalIgnoreCase));
            return entry.Info is null
                ? cli.Fail($"no setting '{args[1]}'. 'diptick settings' lists them.", Exit.Usage)
                : One(entry.Info, rows, cli);
        }

        var places = DataPlaces.For(cli.Config, cli.Config.Path);
        if (cli.WriteJson(new { config = cli.Config.Path, settings = shown.Select(e => Json(e.Info, rows)), data = places.Select(p => new { name = p.Label, path = p.Path }) }))
        {
            return Exit.Ok;
        }

        cli.Note(cli.Config.Path is null ? "No settings file in use; every value is a default. 'diptick setup' makes one." : $"From {cli.Config.Path}");
        var section = string.Empty;
        foreach (var (info, seat) in shown)
        {
            if (info.Section != section)
            {
                section = info.Section;
                cli.Data(section);
                var note = SettingsCatalogue.Sections.First(s => s.Name == section).Note;
                if (note.Length > 0)
                {
                    cli.Data($"  ({note})");
                }
            }

            if (seat is not null && info.Name.EndsWith(".name", StringComparison.Ordinal))
            {
                cli.Data($"  Seat {seat.Id}");
            }

            cli.Data(Row(info.Label, Value(info, rows), info.Name, indent: seat is null ? 2 : 4));

            // The key sits under the model: the two things a person changes together.
            var keys = seat is null
                ? info.Name == "main.model" ? Engines.KeysFor(cli.Config) : null
                : info.Name == $"{seat.Id}.model" ? Engines.KeysFor(seat) : null;
            if (keys is not null)
            {
                cli.Data(Row("Key", keys.Describe().Words, $"diptick key set {keys.Section}", indent: seat is null ? 2 : 4));
            }
        }

        // Where things are kept: not settings, but where to look (the window's Settings, Data).
        cli.Data("Data");
        foreach (var place in places)
        {
            // A path in full: a clipped one is no use to go and look at.
            cli.Data(SafeText.Line($"  {place.Label.PadRight(26)} {place.Path}"));
        }

        cli.Note("'diptick settings <name>' says more about one; 'diptick config set <name> <value>' changes it, or Settings in the window.");
        return Exit.Ok;
    }

    /// <summary>Every setting in the order shown: each section's own, and after the main model's, each seat's.</summary>
    internal static IEnumerable<(SettingInfo Info, SeatConfig? Seat)> Entries(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var (section, _) in SettingsCatalogue.Sections)
        {
            foreach (var info in SettingsCatalogue.All.Where(s => s.Section == section))
            {
                yield return (info, null);
            }

            if (section == "Models")
            {
                foreach (var seat in config.Seats)
                {
                    foreach (var template in SettingsCatalogue.SeatTemplate)
                    {
                        yield return (SettingsCatalogue.For(template, seat.Id), seat);
                    }
                }
            }
        }
    }

    /// <summary>A setting's value for a person: "(empty)", "set" for a password, "(default)" after a default.</summary>
    internal static string Value(SettingInfo info, IReadOnlyList<ConfigController.Row> rows)
    {
        ArgumentNullException.ThrowIfNull(info);
        var row = rows.FirstOrDefault(r => string.Equals(r.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        if (info.Kind == SettingKind.Password)
        {
            return row is { Value.Length: > 0 } ? "set" : "not set";
        }

        var value = row is null || row.Value.Length == 0 ? "(empty)" : row.Value;
        return row is { Layer: "default" } ? $"{value} · default" : value;
    }

    private static int One(SettingInfo info, IReadOnlyList<ConfigController.Row> rows, Cli cli)
    {
        var row = rows.FirstOrDefault(r => string.Equals(r.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        if (cli.WriteJson(Json(info, rows)))
        {
            return Exit.Ok;
        }

        cli.Data($"{info.Label} · {info.Name}");
        cli.Data($"  Value     {SafeText.Line(Value(info, rows))}{(row is null || row.Layer == "default" ? string.Empty : $"  [{row.Layer}]")}");
        cli.Data($"  Default   {(info.Default.Length == 0 ? "(empty)" : info.Default)}");
        cli.Data($"  Takes     {info.Takes}");
        cli.Data($"  About     {info.Help}");
        if (SettingsCatalogue.NeedsYes(info.Name))
        {
            cli.Data("  Yes       changing it needs your yes: it changes what agents may do or where the chat goes");
        }

        cli.Data(info.Kind == SettingKind.Password
            ? "  Change    diptick config password"
            : $"  Change    diptick config set {info.Name} <value>   (or unset, for the default)");
        return Exit.Ok;
    }

    private static object Json(SettingInfo info, IReadOnlyList<ConfigController.Row> rows)
    {
        var row = rows.FirstOrDefault(r => string.Equals(r.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        return new
        {
            name = info.Name,
            section = info.Section,
            label = info.Label,
            value = info.Kind == SettingKind.Password ? (row is { Value.Length: > 0 } ? "set" : "not set") : row?.Value ?? string.Empty,
            layer = row?.Layer ?? "default",
            @default = info.Default,
            kind = info.Kind.ToString().ToUpperInvariant(),
            takes = info.Takes,
            help = info.Help,
            needsYes = SettingsCatalogue.NeedsYes(info.Name),
        };
    }

    private static string Row(string label, string value, string name, int indent) =>
        SafeText.Line($"{new string(' ', indent)}{label.PadRight(28 - indent)} {Clip(value).PadRight(36)} {name}").TrimEnd();

    private static string Clip(string value) => value.Length <= 36 ? value : value[..35] + "…";
}
