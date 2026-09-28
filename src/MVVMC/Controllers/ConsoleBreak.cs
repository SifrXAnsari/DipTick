namespace DipTick.Controllers;

/// <summary>
/// Ctrl+C and Ctrl+Break on the command line: DipTick exits with
/// STATUS_CONTROL_C_EXIT, 0xC000013A, which PowerShell's <c>$LASTEXITCODE</c> shows as
/// -1073741510, whatever the verb. A verb that stops cleanly (an answer, the window) says so with
/// <see cref="Cli.StopsCleanly"/>: the first Ctrl+C then asks it to stop through
/// <see cref="Cli.Stop"/>, it keeps what it has, and DipTick exits with the same code when it is
/// done; a second Ctrl+C ends DipTick at once. Any other verb ends at once on the first.
/// </summary>
internal sealed class ConsoleBreak : IDisposable
{
    /// <summary>STATUS_CONTROL_C_EXIT (ntstatus.h): "{Application Exit by CTRL+C}".</summary>
    public const int ExitCode = unchecked((int)0xC000013A);

    private readonly CancellationTokenSource _stop = new();
    private int _presses;
    private volatile bool _clean;

    public ConsoleBreak() => Console.CancelKeyPress += OnBreak;

    /// <summary>Cancelled by the first Ctrl+C when the verb stops cleanly.</summary>
    public CancellationToken Token => _stop.Token;

    /// <summary>True once Ctrl+C or Ctrl+Break was pressed: the run ends with <see cref="ExitCode"/>.</summary>
    public bool Pressed => Volatile.Read(ref _presses) > 0;

    /// <summary>The verb that runs stops cleanly at <see cref="Token"/>.</summary>
    public void StopsCleanly() => _clean = true;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnBreak;
        _stop.Dispose();
    }

    private void OnBreak(object? sender, ConsoleCancelEventArgs e)
    {
        var presses = Interlocked.Increment(ref _presses);
        if (_clean && presses == 1 && e.SpecialKey == ConsoleSpecialKey.ControlC)
        {
            // The verb stops and keeps what it has; DipTick exits when it is done.
            e.Cancel = true;
            _stop.Cancel();
            return;
        }

        // Ended here, with STATUS_CONTROL_C_EXIT rather than whatever the runtime would give.
        Environment.Exit(ExitCode);
    }
}
