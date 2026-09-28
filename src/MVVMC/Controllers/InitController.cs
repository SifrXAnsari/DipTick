using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick init [--force]</c>: writes a starter <c>diptick.master.json</c> at <c>--config</c>'s
/// path or the per-user folder. The starter is the one shipped inside diptick.exe, so it always
/// matches this build.
/// </summary>
internal static class InitController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var force = false;
        foreach (var arg in args.Skip(1))
        {
            if (arg is "--force" or "-f")
            {
                force = true;
            }
            else if (arg == "--no-force")
            {
                force = false;
            }
            else
            {
                return cli.Fail($"init does not take '{arg}'. Usage: diptick init [--force]", Exit.Usage);
            }
        }

        // The per-user file, or the one --config names. Never the current folder: DipTick does not
        // read a config from there (MasterConfig.Find), so it does not write one there either.
        var path = cli.ChosenConfig is { } chosen && !chosen.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(chosen)
            : MasterConfig.UserConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var exists = File.Exists(path);
        if (exists && !force)
        {
            return cli.Fail($"{path} is already there. --force overwrites it.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note(exists ? $"[dry run] would overwrite {path}; nothing written." : $"[dry run] would write {path}; nothing written.");
            return Exit.Ok;
        }

        if (exists)
        {
            var answer = cli.Confirm($"Overwrite {path}? Its settings and password hash are lost.");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"overwriting {path}");
            }
        }

        if (StarterConfig.Write(path) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"Wrote {path}. Next: 'diptick setup'.");
        return Exit.Ok;
    }
}
