using System.Text;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick ask [--to &lt;seat&gt;|team|a,b] &lt;question&gt;</c>: one question, answered by the main
/// model, or by one or more seats in parallel, each through its own gate.
/// </summary>
/// <remarks>
/// With <c>--to</c>, every seat answers blind (none sees another's answer) and at once, and the
/// answers are shown attributed, in seat order. Each call is that seat's own <c>model.call</c>,
/// decided against its lane and recorded as <c>agent:&lt;seat&gt;</c>. A seat that fails does not stop
/// the others; the exit is 1 if any failed.
/// </remarks>
internal static class AskController
{
    private const int MaxQuestion = 256 * 1024;

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        // Words after a lone -- are the question, whatever they look like: "ask -- --to x" asks it.
        var parsed = new VerbArgs(args, 1, cli.LiteralFrom);
        var to = parsed.Option("--to");
        var fromStdin = parsed.Dash;
        var words = parsed.Words();
        if (parsed.Problem is { } problem)
        {
            return cli.Fail($"{problem} usage: diptick ask [--to <seat>|team|a,b] [--] <question...>  or  ... -  (stdin)", Exit.Usage);
        }

        string question;
        if (fromStdin)
        {
            var buffer = new char[MaxQuestion + 1];
            var read = Console.In.ReadBlock(buffer, 0, buffer.Length);
            if (read > MaxQuestion)
            {
                return cli.Fail("the question on stdin is longer than 256 KiB.", Exit.Usage);
            }

            question = new string(buffer, 0, read).Trim();
        }
        else
        {
            question = string.Join(' ', words).Trim();
        }

        if (question.Length == 0)
        {
            return cli.Fail("usage: diptick ask [--to <seat>|team|a,b] <question...>  or  ... -  (stdin)", Exit.Usage);
        }

        return to is null ? One(question, cli) : Seats(to, question, cli);
    }

    /// <summary>The main model, streamed as it answers.</summary>
    private static int One(string question, Cli cli)
    {
        var engine = Engines.Create(cli.Config);
        try
        {
            using var cancel = Cancel(cli);
            var answer = new StringBuilder();
            var transcript = new List<ChatLine> { new(Role.Person, question, DateTimeOffset.Now) };
            try
            {
                foreach (var piece in engine.AnswerAsync(transcript, Gate(cli, seat: null, prompts: null), cancel.Token).ToBlockingEnumerable(cancel.Token))
                {
                    answer.Append(piece);
                    if (!cli.Json && cli.Flags.Output != OutputFormat.None)
                    {
                        // What the model says is shown, never obeyed: no escape sequence or stray
                        // carriage return reaches the terminal. --json carries it exactly.
                        cli.Out.Write(SafeText.Block(piece));
                        cli.Out.Flush();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cli.Out.WriteLine();
                return cli.Fail(cli.Stop.IsCancellationRequested ? "stopped." : "stopped: the answer took longer than the timeout.");
            }
            catch (Exception ex) when (ex is ChatException or InvalidDataException)
            {
                if (answer.Length > 0)
                {
                    cli.Out.WriteLine();
                }

                return cli.Fail(ex.Message);
            }

            if (!cli.WriteJson(new { question, answer = answer.ToString(), engine = engine.Name }) && cli.Flags.Output != OutputFormat.None)
            {
                cli.Out.WriteLine();
            }

            return Exit.Ok;
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
        }
    }

    /// <summary>Several seats at once, blind, each through its own gate; shown attributed, in seat order.</summary>
    private static int Seats(string to, string question, Cli cli)
    {
        var config = cli.Config;
        var names = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<SeatConfig> seats;
        if (names is ["team"] or ["all"])
        {
            seats = [.. config.Seats];
        }
        else
        {
            seats = [];
            foreach (var name in names)
            {
                if (config.Seat(name) is not { } seat)
                {
                    return cli.Fail($"no seat '{name}'. 'diptick seats' lists them.", Exit.Usage);
                }

                if (!seats.Contains(seat))
                {
                    seats.Add(seat);
                }
            }
        }

        if (seats.Count == 0)
        {
            return cli.Fail("no seats are set up. 'diptick seats add <id> --provider ... --model ...'.", Exit.Usage);
        }

        var results = AllAsync(seats, question, cli).GetAwaiter().GetResult();

        if (!cli.WriteJson(new
        {
            question,
            answers = results.Select(r => new { seat = r.Seat.Id, name = r.Seat.Name, engine = r.Engine, answer = r.Answer, error = r.Error }),
        }) && cli.Flags.Output != OutputFormat.None)
        {
            foreach (var r in results)
            {
                cli.Out.WriteLine($"{SafeText.Line(r.Seat.Name)} · {SafeText.Line(r.Engine)}");
                cli.Out.WriteLine(r.Error is null ? Indent(SafeText.Block(r.Answer)) : $"  (no answer: {SafeText.Line(r.Error)})");
                cli.Out.WriteLine();
            }
        }

        foreach (var r in results.Where(r => r.Error is not null))
        {
            cli.Err.WriteLine($"diptick: {SafeText.Line(r.Seat.Name)}: {SafeText.Line(r.Error)}");
        }

        return results.All(r => r.Error is null) ? Exit.Ok : Exit.Failed;
    }

    private sealed record SeatResult(SeatConfig Seat, string Engine, string Answer, string? Error);

    /// <summary>
    /// Every seat starts before any is awaited; each awaits its own network. All are done before
    /// the semaphore and the cancellation source go out of scope.
    /// </summary>
    private static async Task<List<SeatResult>> AllAsync(List<SeatConfig> seats, string question, Cli cli)
    {
        using var cancel = Cancel(cli);
        using var prompts = new SemaphoreSlim(1, 1);
        return [.. await Task.WhenAll(seats.Select(seat => AnswerAsync(seat, question, cli, prompts, cancel.Token))).ConfigureAwait(false)];
    }

    private static async Task<SeatResult> AnswerAsync(SeatConfig seat, string question, Cli cli, SemaphoreSlim prompts, CancellationToken cancel)
    {
        var engine = Engines.Create(seat);
        var answer = new StringBuilder();
        try
        {
            var transcript = new List<ChatLine> { new(Role.Person, question, DateTimeOffset.Now) };
            await foreach (var piece in engine.AnswerAsync(transcript, Gate(cli, seat, prompts), cancel).ConfigureAwait(false))
            {
                answer.Append(piece);
            }

            return new SeatResult(seat, engine.Name, answer.ToString(), null);
        }
        catch (OperationCanceledException)
        {
            return new SeatResult(seat, engine.Name, answer.ToString(), cli.Stop.IsCancellationRequested ? "stopped" : "stopped: the answer took longer than the timeout");
        }
        catch (Exception ex) when (ex is ChatException or InvalidDataException)
        {
            return new SeatResult(seat, engine.Name, answer.ToString(), ex.Message);
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
        }
    }

    /// <summary>The answer's own stop: the timeout, and Ctrl+C or the screen's Esc through <see cref="Cli.Stop"/>.</summary>
    private static CancellationTokenSource Cancel(Cli cli)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(cli.Stop);
        if (cli.Flags.MainTimeoutSeconds > 0)
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(cli.Flags.MainTimeoutSeconds));
        }

        cli.StopsCleanly();
        return cancel;
    }

    /// <summary>
    /// One gate per seat. Questions to the person are asked one at a time even when the seats run
    /// at once, so two prompts never interleave on the screen.
    /// </summary>
    private static Gate Gate(Cli cli, SeatConfig? seat, SemaphoreSlim? prompts) =>
        new(cli.Config.Permissions, cli.Flags, AuditLog.ForThisUser(), cli.Who, cli.Err, question =>
        {
            if (!cli.Flags.Interactive)
            {
                return null;
            }

            prompts?.Wait();
            try
            {
                return ConsoleInput.Ask(question, cli.Err);
            }
            finally
            {
                prompts?.Release();
            }
        }, seat);

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "  " + line));
}
