using DipTick.Models;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick doctor</c>: every check that can be made without changing anything. A failure
/// exits 1; a warning is said and does not fail. What cannot be checked says so rather than
/// passing quietly.
/// </summary>
internal static class DoctorController
{
    private enum Result
    {
        Ok,
        Warn,
        Fail,
        Skip,
    }

    public static int Run(Cli cli)
    {
        var checks = new List<(string Name, Result Result, string Detail)>();
        var config = cli.Config;

        checks.Add(config.Path is null
            ? ("config", Result.Warn, $"no {MasterConfig.FileName} in use; every value is a default. 'diptick init' writes one.")
            : config.Problems.Count == 0
                ? config.Notices.Count == 0 ? ("config", Result.Ok, config.Path) : ("config", Result.Warn, config.Notices[0])
                : config.Problems.All(p => p.StartsWith("Unknown key", StringComparison.Ordinal))
                    // An unknown key changes nothing (a seat's left-over section, a newer file's key).
                    ? ("config", Result.Warn, $"{config.Problems.Count} unknown key(s), ignored: {config.Problems[0]}")
                    : ("config", Result.Fail, $"{config.Problems.Count} problem(s): {config.Problems.First(p => !p.StartsWith("Unknown key", StringComparison.Ordinal))}"));

        checks.Add(config.UserPasswordHash.Length > 0
            ? ("password", Result.Ok, "set")
            : ("password", Result.Warn, "not set; 'diptick ui' opens for anyone at this keyboard. 'diptick config password' sets one."));

        var log = AuditLog.ForThisUser();
        var broken = log.Verify();
        checks.Add(broken is null
            ? ("audit", Result.Ok, log.Path)
            : ("audit", Result.Fail, $"{log.Path}: {broken}"));

        var p = config.Permissions;
        // Running code asks every time whatever the lists say, so only * is worth a warning.
        checks.Add(p.Allow.Contains("*")
            ? ("policy", Result.Warn, "the allow list names *: agents may do everything without asking, except run code, which always asks.")
            : ("policy", Result.Ok, $"{p.Allow.Count} allow, {p.Ask.Count} ask, {p.Deny.Count} deny; everything else denied"));

        checks.Add(("database", Result.Skip, $"{DatabaseKinds.Name(config.DbKind)} at {config.DbConnection}: no tool uses a database, so there is nothing to reach."));

        var mainKey = Models.Chat.Engines.KeysFor(config)?.Describe();
        checks.Add(config.MainProvider.Length == 0
            ? ("main", Result.Warn, "no model set up (main.provider); nothing can answer. 'diptick setup' sets one up.")
            : mainKey is { Usable: false }
                ? ("main", Result.Fail, $"{config.MainProvider}: {mainKey.Problem}")
                : config.MainModel.Length == 0
                    ? ("main", Result.Warn, $"{config.MainProvider}: no model set (main.model).")
                    : ("main", Result.Ok, $"{config.MainProvider} {config.MainModel}, key: {mainKey?.Words ?? "none"} (not called: doctor spends nothing)"));

        foreach (var seat in config.Seats)
        {
            var name = $"seat {seat.Id}";
            var seatKey = Models.Chat.Engines.KeysFor(seat)?.Describe();
            checks.Add(!seat.Filled
                ? (name, Result.Fail, $"no {(seat.Provider.Length == 0 ? "provider" : "model")}")
                : seatKey is { Usable: false }
                    ? (name, Result.Fail, $"{seat.Provider} {seat.Model}: {seatKey.Problem}")
                    : !seat.Lane.Any(r => Models.Harness.Capabilities.Matches(r, Models.Harness.Capability.ModelCall))
                        ? (name, Result.Warn, $"{seat.Provider} {seat.Model}: its lane has no model.call, so it cannot answer")
                        : (name, Result.Ok, $"{seat.Provider} {seat.Model}, key: {seatKey?.Words ?? "none"}, lane {string.Join(",", seat.Lane)}"));
        }

        var failed = checks.Any(c => c.Result == Result.Fail);
        if (cli.WriteJson(new { ok = !failed, checks = checks.Select(c => new { name = c.Name, result = c.Result.ToString().ToUpperInvariant(), detail = c.Detail }) }))
        {
            return failed ? Exit.Failed : Exit.Ok;
        }

        foreach (var (name, result, detail) in checks)
        {
            var mark = result switch { Result.Ok => "ok  ", Result.Warn => "warn", Result.Fail => "FAIL", _ => "skip" };
            cli.Data($"  {mark}  {name,-9} {detail}");
        }

        return failed ? Exit.Failed : Exit.Ok;
    }
}
