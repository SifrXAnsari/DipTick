using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;
using DipTick.Models.Keys;
using DipTick.Models.Providers;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick providers</c>: every provider DipTick knows, from the catalogue compiled into it.
/// <c>list</c> shows them grouped by kind, those that work now first;
/// <c>show</c> says everything the catalogue holds on one; <c>check</c> lists its models with your key,
/// which costs nothing, through the gate as <c>model.call</c>.
/// </summary>
internal static class ProvidersController
{
    private const string Usage =
        "usage: diptick providers [list] [--kind local|lab|host|cloud|gateway] [--works] [--can vision|tools|images|video|search|pdf] [--find <words>]\n" +
        "       diptick providers show <id>\n" +
        "       diptick providers check <id> [--address <url> | --<part> <value> ...] [--key-variable <VAR>] [--workspace <wrkspc_...>]";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var sub = args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "list";
        var rest = args.Skip(args.Count > 1 && sub == args[1] ? 2 : 1).ToList();
        return sub switch
        {
            "list" => List(rest, cli),
            "show" when rest.Count == 1 => Show(rest[0], cli),
            "check" when rest.Count >= 1 => Check(rest[0], rest.Skip(1).ToList(), cli),
            _ => cli.Fail(Usage, Exit.Usage),
        };
    }

    private static int List(List<string> args, Cli cli)
    {
        ProviderKind? kind = null;
        var works = false;
        var can = Can.None;
        var find = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--kind":
                    var kindName = Next();
                    kind = Enum.GetValues<ProviderKind>().Where(k => k != ProviderKind.Other).Cast<ProviderKind?>().FirstOrDefault(k => ProviderCatalogue.KindName(k!.Value) == kindName);
                    if (kind is null)
                    {
                        return cli.Fail("--kind takes local, lab, host, cloud or gateway.", Exit.Usage);
                    }

                    break;
                case "--works":
                    works = true;
                    break;
                case "--can":
                    var ability = Next();
                    var found = ProviderCatalogue.Abilities.FirstOrDefault(a => a.Name == ability);
                    if (found.Name is null)
                    {
                        return cli.Fail($"--can takes {string.Join(", ", ProviderCatalogue.Abilities.Select(a => a.Name))}.", Exit.Usage);
                    }

                    can |= found.Can;
                    break;
                case "--find":
                    find = Next() ?? string.Empty;
                    if (find.Length == 0)
                    {
                        return cli.Fail("--find takes the words to look for.", Exit.Usage);
                    }

                    break;
                default:
                    return cli.Fail(Usage, Exit.Usage);
            }
        }

        var shown = ProviderCatalogue.Ordered
            .Where(p => kind is null || p.Kind == kind)
            .Where(p => !works || p.WorksNow || p.Makes != Can.None)
            .Where(p => (p.Can & can) == can)
            .Where(p => find.Length == 0 || Matches(p, find))
            .ToList();

        if (cli.WriteJson(new
        {
            @checked = ProviderCatalogue.Checked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            providers = shown.Select(Json),
        }))
        {
            return Exit.Ok;
        }

        if (shown.Count == 0)
        {
            cli.Note("No provider matches that.");
            return Exit.Ok;
        }

        var idWidth = shown.Max(p => p.Id.Length);
        var nameWidth = Math.Min(34, shown.Max(p => p.Name.Length));
        ProviderKind? group = null;
        foreach (var p in shown)
        {
            if (p.Kind != group)
            {
                group = p.Kind;
                cli.Data(string.Empty);
                cli.Data(ProviderCatalogue.KindWords(p.Kind));
            }

            var name = p.Name.Length <= nameWidth ? p.Name.PadRight(nameWidth) : p.Name[..(nameWidth - 1)] + "…";
            var needs = p.WorksNow ? ProviderCatalogue.NeedsWords(p)
                : p.Makes != Can.None ? $"no chat; {MakesWords(p)}"
                : "not yet: " + FirstClause(p.Needs);
            var abilities = ProviderCatalogue.CanWords(p.Can);
            cli.Data(SafeText.Line($"  {p.Id.PadRight(idWidth)}  {name}  {needs}{(abilities.Length > 0 ? $"  · {abilities}" : string.Empty)}").TrimEnd());
        }

        var working = shown.Count(p => p.WorksNow);
        cli.Note(string.Empty);
        cli.Note(string.Create(CultureInfo.InvariantCulture, $"{shown.Count} provider(s), {working} work now; checked {ProviderCatalogue.Checked:yyyy-MM-dd}. 'diptick providers show <id>' says all about one."));
        return Exit.Ok;
    }

    private static int Show(string id, Cli cli)
    {
        if (ProviderCatalogue.Find(id) is not { } p)
        {
            return cli.Fail($"'{SafeText.Line(id)}' is not a provider DipTick knows. 'diptick providers' lists them.", Exit.Usage);
        }

        if (cli.WriteJson(Json(p)))
        {
            return Exit.Ok;
        }

        var lines = new List<(string Label, string Value)>
        {
            ("Kind", ProviderCatalogue.KindWords(p.Kind)),
            ("Works now", p.WorksNow ? $"yes, {ShapeWords(p)}" : p.Makes != Can.None ? $"not for chat; {MakesWords(p)}" : $"not yet: {p.Needs}"),
            ("Address", p.Address?.AbsoluteUri ?? (p.Give.Length > 0 ? $"you give it: {p.Give}" : "none")),
            ("Needs", ProviderCatalogue.NeedsWords(p)),
            ("Key", KeyWords(p)),
            ("Models", ModelWords(p)),
            ("Can", ProviderCatalogue.CanWords(p.Can) is { Length: > 0 } can ? can : "its pages say nothing"),
            ("Keeps", p.OnThisPc && p.Keeps is null ? "nothing leaves this PC" : string.Join(" ", new[] { p.KeepsNote, p.Keeps?.AbsoluteUri ?? string.Empty }.Where(t => t.Length > 0))),
        };
        if (p.Note.Length > 0)
        {
            lines.Add(("Note", p.Note));
        }

        lines.Add(("Checked", p.Checked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (p.Sources.Count > 0 ? ", from:" : string.Empty)));
        cli.Data(SafeText.Line($"{p.Name}  ({p.Id})"));
        cli.Data(SafeText.Line($"  {p.About}."));
        var width = lines.Max(l => l.Label.Length);
        foreach (var (label, value) in lines)
        {
            cli.Data(SafeText.Line($"  {label.PadRight(width)}  {value}"));
        }

        foreach (var source in p.Sources)
        {
            cli.Data(SafeText.Line($"  {new string(' ', width)}    {source.AbsoluteUri}"));
        }

        if (p.WorksNow)
        {
            cli.Note(string.Empty);
            cli.Note($"Use it: 'diptick setup', or Settings, Models, Add a model; 'diptick providers check {p.Id}' lists its models first.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Lists a provider's models with your key, as setup's check does and as <c>diptick models</c> does
    /// for a section: it costs nothing, and it is still a <c>model.call</c> through the gate, recorded.
    /// A provider with no list says so; one whose maker publishes its IDs lists them with nothing sent.
    /// </summary>
    private static int Check(string id, List<string> args, Cli cli)
    {
        if (ProviderCatalogue.Find(id) is not { } p)
        {
            return cli.Fail($"'{SafeText.Line(id)}' is not a provider DipTick knows. 'diptick providers' lists them.", Exit.Usage);
        }

        if (!p.WorksNow)
        {
            return cli.Fail(p.Makes != Can.None
                ? $"{p.Name} does not chat, so it has no models to list here: {p.Needs}"
                : $"DipTick cannot talk to {p.Name} yet: {p.Needs}");
        }

        string? address = null;
        var keyVariable = string.Empty;
        var workspace = string.Empty;
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            var arg = args[i];
            if (arg == "--address")
            {
                address = Next();
            }
            else if (arg == "--key-variable")
            {
                keyVariable = Next() ?? string.Empty;
                if (keyVariable.Length == 0 || !keyVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    return cli.Fail("--key-variable takes a variable's name, not its value.", Exit.Usage);
                }
            }
            else if (arg == "--workspace")
            {
                workspace = Next() ?? string.Empty;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && p.Parts.Contains(arg[2..]) && Next() is { } value)
            {
                parts[arg[2..]] = value;
            }
            else
            {
                return cli.Fail($"providers check {p.Id} does not take '{SafeText.Line(arg)}'. {PartsHint(p)}", Exit.Usage);
            }
        }

        if (WhereTo(p, address, parts, out var why) is not { } endpoint)
        {
            return cli.Fail(why, Exit.Usage);
        }

        if (Engines.EndpointProblem(p.Id, endpoint.AbsoluteUri) is { } wrongPlace)
        {
            return cli.Fail($"the address {wrongPlace}.", Exit.Usage);
        }

        var keys = Engines.KeysFor(p.Id, p.Id, endpoint, keyVariable, WindowsKeyStore.Instance, Environment.GetEnvironmentVariable);
        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cli.Stop);
        if (cli.Flags.NetTimeoutSeconds > 0)
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(cli.Flags.NetTimeoutSeconds));
        }

        cli.StopsCleanly();
        try
        {
            if (p.Models == ModelSource.Typed)
            {
                return NoList(p, keys, gate, cli, cancel.Token);
            }

            var models = ModelLister.Instance.ListAsync(new ModelQuery(p.Id, p.Id, endpoint, keys, workspace), gate, cancel.Token).GetAwaiter().GetResult();
            return Listed(p, endpoint, models, cli);
        }
        catch (ChatException ex)
        {
            return cli.Fail(SafeText.Line(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return cli.Fail(cli.Stop.IsCancellationRequested
                ? "stopped."
                : $"{endpoint.Host} did not answer within {cli.Flags.NetTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }
    }

    /// <summary>
    /// A provider with no list of models: nothing to ask it, so nothing is sent; for a Google sign-in,
    /// the sign-in itself is proved (a token fetched from Google, through the gate), since that is
    /// what setting it up can go wrong on.
    /// </summary>
    private static int NoList(Provider p, KeyFinder keys, Gate gate, Cli cli, CancellationToken cancel)
    {
        var signedIn = false;
        if (p.GoogleSignIn)
        {
            if (!gate.Request("providers check", Capability.ModelCall, "https://oauth2.googleapis.com/token"))
            {
                return cli.Fail("the harness did not allow the sign-in (model.call). 'diptick audit' says why.");
            }

            _ = keys.TakeAsync(cancel).GetAwaiter().GetResult();
            signedIn = true;
        }

        var page = p.ModelsPage is { } link ? $" {link} lists them." : string.Empty;
        if (!cli.WriteJson(new { provider = p.Id, signedIn, models = Array.Empty<string>(), listed = false }))
        {
            cli.Note((signedIn ? "Signed in to Google Cloud. " : string.Empty) + $"{p.Name} has no list of models to ask; you type the model's name.{page}");
        }

        return Exit.Ok;
    }

    private static int Listed(Provider p, Uri endpoint, IReadOnlyList<ModelInfo> models, Cli cli)
    {
        if (cli.WriteJson(new
        {
            provider = p.Id,
            address = endpoint.AbsoluteUri,
            listed = p.Models != ModelSource.Fixed,
            models = models.Select(m => new { id = m.Id, name = m.Name, created = m.Created, context = m.Context }),
        }))
        {
            return Exit.Ok;
        }

        if (models.Count == 0)
        {
            cli.Note($"{p.Name} offers no models yet.");
            return Exit.Ok;
        }

        var width = Math.Min(60, models.Max(m => m.Id.Length));
        foreach (var m in models)
        {
            var when = m.Created is { } created ? created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
            cli.Data(SafeText.Line($"  {m.Id.PadRight(width)}  {when,-10}  {m.Name}").TrimEnd());
        }

        cli.Note(p.Models == ModelSource.Fixed
            ? $"{models.Count.ToString(CultureInfo.InvariantCulture)} model(s), as {p.Name}'s maker publishes them; {p.Name} has no list to ask, so nothing was sent."
            : $"{models.Count.ToString(CultureInfo.InvariantCulture)} model(s) from {endpoint.Host}, which cost nothing to list.");
        return Exit.Ok;
    }

    /// <summary>Where a check goes: the provider's own address, the one given, or one built from its parts.</summary>
    private static Uri? WhereTo(Provider p, string? address, Dictionary<string, string> parts, out string why)
    {
        why = string.Empty;
        if (address is not null)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var given) || ProviderCatalogue.AddressProblem(given) is { } bad)
            {
                why = $"--address must be a full address, https or http to this PC: {(Uri.TryCreate(address, UriKind.Absolute, out var u) ? ProviderCatalogue.AddressProblem(u) : "not an address")}.";
                return null;
            }

            return given;
        }

        if (p.Address is { } own)
        {
            return own;
        }

        if (p.Parts.Count == 0)
        {
            why = $"{p.Name} has no address of its own: give it with --address. {PartsHint(p)}";
            return null;
        }

        if (p.Parts.FirstOrDefault(n => !parts.ContainsKey(n)) is { } missing)
        {
            why = $"{p.Name} needs {Part(missing)}. {PartsHint(p)}";
            return null;
        }

        var composed = ProviderCatalogue.Compose(p, parts, out var problem);
        why = problem;
        return composed;
    }

    private static string Part(string name) => ProviderCatalogue.Part(name)?.Noun ?? name;

    private static string PartsHint(Provider p) => p.Parts.Count == 0
        ? p.Given || p.Address is null ? "It takes --address <url>." : "It takes --address <url> for a different address."
        : $"It takes {string.Join(" ", p.Parts.Select(n => $"--{n} <{ProviderCatalogue.Part(n)?.Example ?? n}>"))}, or --address <url>.";

    private static bool Matches(Provider p, string words) =>
        p.Id.Contains(words, StringComparison.OrdinalIgnoreCase)
        || p.Name.Contains(words, StringComparison.OrdinalIgnoreCase)
        || p.About.Contains(words, StringComparison.OrdinalIgnoreCase)
        || ProviderCatalogue.KindWords(p.Kind).Contains(words, StringComparison.OrdinalIgnoreCase);

    private static string FirstClause(string text)
    {
        var cut = text.IndexOfAny(['.', ';', ':']);
        return cut > 0 ? text[..cut] : text;
    }

    private static string ShapeWords(Provider p) => p.Shape switch
    {
        ProviderShape.Anthropic => p.Flavor switch
        {
            AnthropicFlavor.Vertex => "through Anthropic's Vertex package and your Google Cloud sign-in",
            _ => "through Anthropic's own SDK",
        },
        ProviderShape.Gemini => "through Google's generateContent",
        ProviderShape.OpenInference => "through KServe's Open Inference Protocol: one prompt, one text back, no tools",
        _ => "through the OpenAI chat shape",
    };

    /// <summary>What DipTick makes through a provider's own API, and the setting that names it.</summary>
    private static string MakesWords(Provider p)
    {
        var what = new List<string>();
        if (p.Makes.HasFlag(Can.Images))
        {
            what.Add($"makes images (media.images {p.Id})");
        }

        if (p.Makes.HasFlag(Can.Video))
        {
            what.Add($"video (media.video {p.Id})");
        }

        if (p.Makes.HasFlag(Can.Speech))
        {
            what.Add($"turns speech into text (media.speech {p.Id})");
        }

        return string.Join(", ", what);
    }

    private static string KeyWords(Provider p)
    {
        var how = p.Key switch
        {
            KeyScheme.None => "none",
            KeyScheme.Optional => "only if you set one" + (p.KeyHow.Length > 0 ? $" ({p.KeyHow})" : string.Empty),
            KeyScheme.GoogleCloud => p.KeyHow,
            KeyScheme.KeyHeader => "a key, sent as Authorization: Key",
            KeyScheme.ApiKeyHeader => "a key, sent in an api-key header",
            KeyScheme.XApiKey => "a key, sent by Anthropic's SDK (x-api-key)",
            _ => "a key, sent as Authorization: Bearer",
        };
        if (p.Key is KeyScheme.Bearer or KeyScheme.KeyHeader or KeyScheme.ApiKeyHeader or KeyScheme.XApiKey && p.KeyHow.Length > 0)
        {
            how += $"; {p.KeyHow}";
        }

        if (p.KeyPage is { } page && p.Key != KeyScheme.None)
        {
            how += $"; get one at {page.AbsoluteUri}";
        }

        if (p.KeyVariable.Length > 0 && p.Key != KeyScheme.None)
        {
            how += $" (its usual variable: {p.KeyVariable})";
        }

        return how;
    }

    private static string ModelWords(Provider p) => p.Models switch
    {
        ModelSource.List => $"its own list ('diptick providers check {p.Id}')",
        ModelSource.Try => $"its own list, if it serves one; its docs do not say ('diptick providers check {p.Id}')",
        ModelSource.Fixed => $"as its maker publishes them: {string.Join(", ", p.ModelIds)}",
        _ => "no list: you type the name" + (p.ModelsPage is { } page ? $" ({page.AbsoluteUri} lists them)" : string.Empty),
    };

    private static object Json(Provider p) => new
    {
        id = p.Id,
        name = p.Name,
        kind = ProviderCatalogue.KindName(p.Kind),
        about = p.About,
        worksNow = p.WorksNow,
        shape = p.Shape switch { ProviderShape.OpenAi => "openai", ProviderShape.Anthropic => "anthropic", ProviderShape.Gemini => "gemini", ProviderShape.OpenInference => "openinference", _ => "none" },
        makes = ProviderCatalogue.Abilities.Where(a => p.Makes.HasFlag(a.Can)).Select(a => a.Name),
        needs = p.WorksNow ? ProviderCatalogue.NeedsWords(p) : p.Needs,
        address = p.Address?.AbsoluteUri,
        give = p.Give.Length > 0 ? p.Give : null,
        parts = p.Parts,
        key = p.Key switch
        {
            KeyScheme.None => "none",
            KeyScheme.Optional => "optional",
            KeyScheme.KeyHeader => "key",
            KeyScheme.ApiKeyHeader => "api-key",
            KeyScheme.XApiKey => "x-api-key",
            KeyScheme.GoogleCloud => "google-cloud",
            _ => "bearer",
        },
        keyPage = p.KeyPage?.AbsoluteUri,
        keyVariable = p.KeyVariable.Length > 0 ? p.KeyVariable : null,
        models = p.Models switch { ModelSource.List => "list", ModelSource.Try => "try", ModelSource.Fixed => "fixed", _ => "typed" },
        modelIds = p.ModelIds,
        modelsPage = p.ModelsPage?.AbsoluteUri,
        can = ProviderCatalogue.Abilities.Where(a => p.Can.HasFlag(a.Can)).Select(a => a.Name),
        keeps = p.Keeps?.AbsoluteUri,
        keepsNote = p.KeepsNote.Length > 0 ? p.KeepsNote : null,
        note = p.Note.Length > 0 ? p.Note : null,
        @checked = p.Checked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        sources = p.Sources.Select(s => s.AbsoluteUri),
    };
}
