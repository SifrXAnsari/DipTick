namespace DipTick.Controllers;

/// <summary>
/// <c>diptick completion pwsh</c>: a PowerShell tab-completion script, to stdout. It completes
/// verbs, sub-verbs, capabilities and the global flags; it runs nothing but string matching.
/// </summary>
internal static class CompletionController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        if (args.Count != 2 || args[1] is not ("pwsh" or "powershell"))
        {
            return cli.Fail("usage: diptick completion pwsh", Exit.Usage);
        }

        // The docs' pages by id, so 'diptick docs <Tab>' offers each one.
        cli.Out.WriteLine(Script.Replace("__PAGES__", string.Join(' ', Docs.Pages.Select(p => p.Id)), StringComparison.Ordinal));
        return Exit.Ok;
    }

    private const string Script = """
            # DipTick tab completion for PowerShell. Load it with:
            #   diptick completion pwsh | Out-String | Invoke-Expression
            Register-ArgumentCompleter -Native -CommandName diptick, diptick.exe -ScriptBlock {
                param($word, $ast, $cursor)
                $verbs = @{
                    ''           = 'setup settings key models providers tools research connectors skills run image video transcribe init config login whoami policy audit doctor docs data completion tui window ui version help ask chat seats acts'
                    'window'     = '--session --private --wait'
                    'ui'         = '--session --private --wait'
                    'tui'        = '--session'
                    'config'     = 'list show get set unset check password'
                    'policy'     = 'show check try'
                    'audit'      = 'show verify accept protect export --all --since --who --what --verdict --format --private --status --archive'
                    'docs'       = 'search export __PAGES__'
                    'data'       = 'export'
                    'completion' = 'pwsh'
                    'seats'      = 'list check add remove'
                    'acts'       = 'check'
                    'key'        = 'status set clear'
                    'setup'      = '--full --quick'
                    'models'     = '--for --model unload'
                    'tools'      = 'list time cal calc net'
                    'research'   = 'status search fetch'
                    'connectors' = 'list add import enable disable test tools signin signout secret sandbox remove --network --read --write --no-folders --on --off'
                    'skills'     = 'list show add approve remove check --replace'
                    'run'        = '--lang --seconds --skill --arg'
                    'image'      = '--provider --size --session --private --out'
                    'video'      = '--seconds --resolution --portrait --session --private --out'
                    'transcribe' = '--out'
                    'chat'       = 'say pass retry seats mode brief cost research sources files save connectors skills list show new title pin export close delete --private --attach'
                    'help'       = 'setup settings key models providers tools research connectors skills run image video transcribe init config login whoami policy audit doctor docs data completion tui window ui version ask chat seats acts'
                }
                $caps = 'model.call fs.read fs.write fs.delete net.fetch net.probe web.search media.make mcp.call db.read db.write shell.run'
                $flags = '--dry-run --no-dry-run --yes --no-yes --interactive --no-interactive --no-input --output --json --no-json --verbosity --quiet --no-quiet --verbose --no-verbose --debug --no-debug --color --no-color --config --no-config --timeout --no-timeout --help --version'
                $words = @($ast.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() } | Where-Object { $_ -notlike '-*' -and $_ -ne $word })
                $pool = if ($word -like '-*') { $flags }
                    elseif ($words.Count -eq 0) { $verbs[''] }
                    elseif ($words.Count -eq 1 -and $verbs.ContainsKey($words[0])) { $verbs[$words[0]] }
                    elseif ($words.Count -eq 2 -and $words[0] -eq 'policy' -and $words[1] -in 'check', 'try') { $caps }
                    else { '' }
                $pool -split ' ' | Where-Object { $_ -and $_ -like "$word*" } | ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                }
            }
            """;
}
