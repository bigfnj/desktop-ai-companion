using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private const string GuideRecipeCheck = "GUIDE_RECIPE_PARSES_PS51: the compiled watcher recipe parses in Windows PowerShell 5.1 and conditionally includes complete optional pairs";

    private static void RunGuideRecipeChecks(Action<bool, string> check, string guide)
    {
        MatchCollection blocks = Regex.Matches(guide, "<pre id=\"watcher-command\">(?<recipe>.*?)</pre>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (blocks.Count != 1)
        {
            check(false, GuideRecipeCheck + " (expected one watcher-command pre block)");
            return;
        }

        string recipe = WebUtility.HtmlDecode(blocks[0].Groups["recipe"].Value);
        (bool passed, string detail) = CheckGuideRecipeInWindowsPowerShell(recipe);
        check(passed, GuideRecipeCheck + (passed ? "" : " (" + detail + ")"));
    }

    private static (bool Passed, string Detail) CheckGuideRecipeInWindowsPowerShell(string recipe)
    {
        // Parse the actual embedded recipe, but execute only its argument construction. The setup, worker invocation and
        // exit statements are never executed; even Join-Path sees a synthetic child-only LOCALAPPDATA value.
        const string parser = """
            $ErrorActionPreference = 'Stop'
            try {
                if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
                    throw 'Windows PowerShell 5.1 is required'
                }
                $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd()))
                $cursorAssignment = '$cursor = ' + "''"
                $seenAssignment = '$seenIds = ' + "''"
                if ([regex]::Matches($source, [regex]::Escape($cursorAssignment)).Count -ne 1 -or
                    [regex]::Matches($source, [regex]::Escape($seenAssignment)).Count -ne 1) {
                    throw 'Expected one empty template value per optional argument'
                }
                $env:LOCALAPPDATA = 'C:\AgentBrokerGuideSynthetic'
                $cases = @(
                    @{ Cursor = ''; Seen = '' },
                    @{ Cursor = 'YWJj'; Seen = '' },
                    @{ Cursor = ''; Seen = '11111111-1111-4111-8111-111111111111' },
                    @{ Cursor = 'YWJj'; Seen = '11111111-1111-4111-8111-111111111111' },
                    @{ Cursor = ''; Seen = '11111111-1111-4111-8111-111111111111,22222222-2222-4222-8222-222222222222' },
                    @{ Cursor = 'YWJj'; Seen = '11111111-1111-4111-8111-111111111111,22222222-2222-4222-8222-222222222222' }
                )
                function Build-Arguments([string]$Construction, [string]$CursorValue, [string]$SeenValue) {
                    $cursor = $CursorValue
                    $seenIds = $SeenValue
                    $armRoot = 'C:\AgentBrokerGuideSynthetic\arm'
                    . ([scriptblock]::Create($Construction))
                    return $watchArgs
                }
                $checked = 0
                foreach ($case in $cases) {
                    $variant = $source.Replace($cursorAssignment, '$cursor = ' + "'" + $case.Cursor + "'")
                    $variant = $variant.Replace($seenAssignment, '$seenIds = ' + "'" + $case.Seen + "'")
                    $tokens = $null
                    $errors = $null
                    $null = [System.Management.Automation.Language.Parser]::ParseInput($variant, [ref]$tokens, [ref]$errors)
                    if ($errors.Count -ne 0) { throw ('Recipe parse failed: ' + $errors[0].ErrorId) }
                    $start = $variant.IndexOf('$watchArgs = @(', [StringComparison]::Ordinal)
                    $end = $variant.IndexOf('& $worker @watchArgs', [StringComparison]::Ordinal)
                    if ($start -lt 0 -or $end -le $start) { throw 'Argument construction boundaries are missing' }
                    $tail = $variant.Substring($end + '& $worker @watchArgs'.Length).Trim()
                    if ($tail -cne 'exit $LASTEXITCODE') { throw 'The final statement must propagate the worker exit code' }
                    $actual = @(Build-Arguments ($variant.Substring($start, $end - $start)) $case.Cursor $case.Seen)
                    if ($actual.Count -lt 1 -or $actual[0] -cne '--watch-inbox' -or ($actual.Count % 2) -ne 1) {
                        throw 'Watcher arguments are not complete flag/value pairs'
                    }
                    $pairs = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
                    for ($i = 1; $i -lt $actual.Count; $i += 2) {
                        if ($pairs.ContainsKey([string]$actual[$i]) -or [string]::IsNullOrEmpty([string]$actual[$i + 1])) {
                            throw 'Watcher arguments contain a duplicate flag or empty value'
                        }
                        $pairs.Add([string]$actual[$i], [string]$actual[$i + 1])
                    }
                    $expected = @('--data', '--broker-id', '--session-id', '--incarnation', '--arm-root', '--max-seconds', '--parent-pid', '--parent-start')
                    if ($case.Cursor) { $expected += '--cursor' }
                    if ($case.Seen) { $expected += '--seen-ids' }
                    if ($pairs.Count -ne $expected.Count -or @($expected | Where-Object { -not $pairs.ContainsKey($_) }).Count -ne 0) {
                        throw 'Optional watcher pairs were not included exactly when populated'
                    }
                    $duration = 0L
                    if (-not [long]::TryParse($pairs['--max-seconds'], [ref]$duration) -or $duration -lt 1 -or $duration -gt 31536000) {
                        throw 'The watcher recipe needs a finite lifetime in the supported range'
                    }
                    if ($pairs['--arm-root'] -cne 'C:\AgentBrokerGuideSynthetic\arm') {
                        throw 'The watcher recipe must use the separately chosen arm root'
                    }
                    if (($case.Cursor -and $pairs['--cursor'] -cne $case.Cursor) -or ($case.Seen -and $pairs['--seen-ids'] -cne $case.Seen)) {
                        throw 'Optional watcher values changed'
                    }
                    $checked++
                }
                [Console]::Out.WriteLine('GUIDE_PS51_OK variants=' + $checked)
                exit 0
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", parser })
            start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return (false, "the Windows PowerShell parser did not start");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(recipe)));
            process.StandardInput.Close();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return (false, "the Windows PowerShell parser timed out");
            }
            string text = output.GetAwaiter().GetResult().Trim();
            string diagnostic = error.GetAwaiter().GetResult().Trim();
            bool passed = process.ExitCode == 0 && text == "GUIDE_PS51_OK variants=6" && diagnostic.Length == 0;
            return (passed, diagnostic.Length > 0 ? diagnostic : "parser exit " + process.ExitCode);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, "the Windows PowerShell parser failed: " + error.GetType().Name);
        }
    }
}
