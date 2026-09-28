namespace DipTick.Models.Chat.Seam;

/// <summary>
/// One agent at a chat's table: the name it is addressed by in this chat, the
/// set-up agent it is (<c>main</c>, or a seat's section), and the model it answers with here. The
/// same set-up agent can sit twice with two models, under two names.
/// </summary>
internal sealed record TableSeat(string Name, string Profile, string Model);

/// <summary>A chat's table: who sits at it, and what each is made from.</summary>
internal static class Table
{
    /// <summary>At most four agents at a table: with four, a blind step and a review still fit in one round of eight turns.</summary>
    public const int Most = MasterConfig.MaxSeats;

    /// <summary>The name the model that answers you sits under, as it is kept in the store.</summary>
    public const string Main = Store.ChatStores.MainSeat;

    /// <summary>
    /// The table a new chat starts with: the model that answers you, when one is set up, then the
    /// agents <c>chat.seats</c> names, at most <see cref="Most"/> in all.
    /// </summary>
    public static IReadOnlyList<TableSeat> Starting(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var seats = new List<TableSeat>();
        if (config.MainProvider.Length > 0)
        {
            seats.Add(new TableSeat(Main, Main, config.MainModel));
        }

        seats.AddRange(config.Seats.Select(s => new TableSeat(s.Id, s.Id, s.Model)));
        return [.. seats.Take(Most)];
    }

    /// <summary>
    /// What a seat's engine is made from: its set-up agent's settings with this chat's model and
    /// name; null when the set-up agent is no longer in the settings.
    /// </summary>
    public static SeatConfig? Resolve(MasterConfig config, TableSeat seat)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(seat);
        var agent = IsMain(seat.Profile) ? config.MainAgent : config.Seats.FirstOrDefault(s => s.Id == seat.Profile);
        return agent is null ? null : agent with { Name = seat.Name, Model = seat.Model.Length > 0 ? seat.Model : agent.Model };
    }

    /// <summary>Whether a set-up agent's name is the model that answers you, under its name now or its former one.</summary>
    public static bool IsMain(string profile) => Store.ChatStores.IsMainSeat(profile);

    /// <summary>A chat's table as the store keeps it.</summary>
    public static IReadOnlyList<TableSeat> From(IReadOnlyList<Store.SeatRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return [.. records.Where(r => r.Seated).Select(r => new TableSeat(r.Seat, r.Agent, r.Model))];
    }

    /// <summary>
    /// What the store keeps of a seat from a line on: its name, the set-up agent it is, its model, and
    /// where its words go; never a key. Its host is empty while it has no address; it is remote unless
    /// its address is this PC's own.
    /// </summary>
    public static Store.SeatRecord Record(MasterConfig config, TableSeat seat, bool seated = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(seat);
        var agent = Resolve(config, seat);
        var endpoint = agent is null || agent.Provider.Length == 0 ? null : Engines.Endpoint(agent.Provider, agent.Endpoint);
        var host = endpoint is null ? string.Empty : Keys.ApiKeys.HostOf(endpoint);
        return new Store.SeatRecord(
            seat.Name, seat.Name, agent?.Provider ?? string.Empty, seat.Model.Length > 0 ? seat.Model : agent?.Model ?? string.Empty,
            host, endpoint is not null && !endpoint.IsLoopback, agent?.Lane ?? ["model.call"], agent?.PriceIn, agent?.PriceOut, seat.Profile, seated);
    }

    /// <summary>
    /// A name for a set-up agent sitting again with another model: its model's own name made an id
    /// (lower case, letters, digits and hyphens, at most 12), else the agent's with a number, never one
    /// already at the table.
    /// </summary>
    public static string NameFor(string profile, string model, IReadOnlyCollection<TableSeat> table)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(table);
        var taken = table.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(profile))
        {
            return profile;
        }

        var fromModel = new string([.. Addressing.Lower(model).Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]).Trim('-');
        fromModel = fromModel.Length > 12 ? fromModel[..12].TrimEnd('-') : fromModel;
        if (fromModel.Length > 0 && char.IsAsciiLetter(fromModel[0]) && !taken.Contains(fromModel) && !Addressing.Reserved.Contains(fromModel))
        {
            return fromModel;
        }

        for (var n = 2; ; n++)
        {
            var numbered = $"{profile[..Math.Min(profile.Length, 10)]}{n}";
            if (!taken.Contains(numbered))
            {
                return numbered;
            }
        }
    }
}
