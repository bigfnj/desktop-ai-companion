# Shared by the actual process-fixture consumer and its focused readiness regression.
function Get-AgentBrokerFixtureStartupFailure([Diagnostics.Process]$Process,[string]$CaseName) {
    $nativeExit='running'; $diagnostic='not-read-running'
    if ($Process.HasExited) {
        $nativeExit=[string]$Process.ExitCode; $diagnostic='read-deadline'
        try {
            $buffer=[char[]]::new(256)
            $read=$Process.StandardError.ReadBlockAsync($buffer,0,$buffer.Length)
            if ($read.Wait(1000)) {
                $sample=[string]::new($buffer,0,$read.GetAwaiter().GetResult())
                if ($sample -cmatch '\AFIXTURE_READY_PUBLISH_FAILED (EPERM|EACCES|EBUSY|OTHER)(?:\r?\n|\z)') {
                    $diagnostic='FIXTURE_READY_PUBLISH_FAILED '+$Matches[1]
                } elseif ($sample.Length -eq 0) { $diagnostic='empty' }
                else { $diagnostic='unclassified' }
            }
        } catch { $diagnostic='read-failed' }
    }
    return ('Fixture failed to start: '+$CaseName+'; exit='+$nativeExit+'; stderr='+$diagnostic)
}
