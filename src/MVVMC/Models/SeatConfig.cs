namespace DipTick.Models;

/// <summary>
/// One agent's seat at the table: its own provider, model, key and lane. Nothing is
/// inherited from the <c>main</c> section or from another seat.
/// </summary>
/// <param name="Id">The seat's id, fixed: it keys the audit log, the store and the lane.</param>
/// <param name="Name">Shown on screen and matched by @; defaults to the id.</param>
/// <param name="Lane">The capabilities this agent may ask the harness for at all; it only narrows the permissions.</param>
/// <param name="Ask">Capabilities that need the person's yes for this agent even where the permissions allow them.</param>
internal sealed record SeatConfig(
    string Id,
    string Name,
    string Provider,
    string Model,
    string Endpoint,
    string KeyVariable,
    int TimeoutSeconds,
    int MaxAnswerTokens,
    int ContextTokens,
    decimal? PriceIn,
    decimal? PriceOut,
    string Color,
    IReadOnlyList<string> Lane,
    IReadOnlyList<string> Ask,
    string Brief,
    string Effort,
    string Workspace)
{
    /// <summary>Whether the seat has what it needs to answer: a provider and a model.</summary>
    public bool Filled => Provider.Length > 0 && Model.Length > 0;

    /// <summary>
    /// Whether it is offered tools (<c>&lt;id&gt;.tools</c>): "auto" as its provider
    /// and model say, "on" always, "off" never.
    /// </summary>
    public string Tools { get; init; } = "auto";
}

/// <summary>The <c>chat</c> section: who sits at the table, how a round runs, whether sessions are kept, and when a paste goes to a file.</summary>
/// <param name="Seats">The agents a new chat seats beside the model that answers you.</param>
/// <param name="Mode">The mode a new chat starts in.</param>
/// <param name="Save">Keep sessions in the store so they can be brought back; false keeps a screen's session in memory only.</param>
/// <param name="PasteLines">A paste of this many lines or more is kept in a file (<see cref="Chat.Pastes"/>); 0 never.</param>
/// <param name="Limits">How long a round may run.</param>
internal sealed record ChatSettings(IReadOnlyList<string> Seats, string Mode, bool Save, int PasteLines = Chat.Pastes.DefaultLines, RoundLimits? Limits = null)
{
    /// <summary>The round's limits, or the defaults.</summary>
    public RoundLimits Round => Limits ?? RoundLimits.Default;
}

/// <summary>
/// A round's hard stops: turns, checked when planning, under a ceiling of 8
/// compiled in; seconds, before each turn and on a timer; answer tokens, before each turn (a turn's
/// cap lowered to what is left) and while streaming; and how much of any one answer is quoted to
/// another agent.
/// </summary>
internal sealed record RoundLimits(int MaxAiTurns, int MaxRoundSeconds, int MaxRoundTokens, int MaxQuotedChars)
{
    /// <summary>No round has more agent turns than this, whatever the settings say.</summary>
    public const int TurnCeiling = 8;

    public static RoundLimits Default { get; } = new(4, 300, 16000, 16000);
}

/// <summary>
/// The <c>money</c> section: spending never blocks unless the person sets a cap.
/// Cost is shown where a price is known; each cap is off (null) until set, in the person's currency.
/// </summary>
internal sealed record MoneySettings(bool ShowCost, string Currency, decimal? CapChat, decimal? CapDay, decimal? CapMonth)
{
    public static MoneySettings Default { get; } = new(true, "USD", null, null, null);
}
