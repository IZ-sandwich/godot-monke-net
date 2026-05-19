param(
    # Optional test selector within the multi-process suite. Pass a class name
    # (e.g. "MultiProcessClockSyncTests") or class+method
    # (e.g. "MultiProcessClockSyncTests.ConvergesUnderJitter") to narrow the run.
    # Matched as a substring against the test's FullyQualifiedName. When omitted,
    # runs every test under MonkeNet.Tests.MultiProcess.
    [Parameter(Position=0)]
    [string]$Test,

    # Print usage and exit. -h is recognised as a short alias.
    [Alias('h')]
    [switch]$Help
)

if ($Help) {
    Write-Host @'
Usage: run-multiprocess-tests.ps1 [<TestSelector>] [-Help]

Runs the MonkeNet.Tests.MultiProcess suite -- multi-process integration tests
that spawn separate Godot child processes (one per server/client) for true
OS-level isolation. Slower than the inner-loop suite (~30s-2min per test) and
excluded from run-tests.ps1; use this script to run them on demand.

Arguments:
  TestSelector   Optional substring matched against each test's
                 FullyQualifiedName via dotnet test's --filter, AND'ed with
                 the MonkeNet.Tests.MultiProcess class-path filter so it can
                 only ever match tests in the multi-process suite. When
                 omitted, runs every multi-process test.

Options:
  -Help, -h      Print this message and exit.

Outputs:
  test-output-multiprocess.log    stdout from `dotnet test`
  test-error-multiprocess.log     stderr from `dotnet test`
  exit code                       propagated from `dotnet test`

Examples:
  run-multiprocess-tests.ps1
      Run every multi-process test.

  run-multiprocess-tests.ps1 OffsetPush
      Run every test whose name contains "OffsetPush" -- primary +
      baseline offset-push tests.

  run-multiprocess-tests.ps1 MultiProcessSleepCoherenceTests
      Run every test inside MultiProcessSleepCoherenceTests.

  run-multiprocess-tests.ps1 MultiProcess_RigidPlayer_OffsetPushesCube_BaselineNoDriftNoTeleport
      Run a single multi-process test by exact method name.

  run-multiprocess-tests.ps1 MultiProcessMispredictTests.MultiProcess_RigidPlayer_RunsIntoTowerWhileJumping_MispredictionsStayUnderBudget
      Run a specific method via class.method.
'@
    exit 0
}

$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

# Runs the MonkeNet.Tests.MultiProcess suite — multi-process integration tests
# that spawn separate Godot child processes (one per server/client) for true
# OS-level isolation. These are slower and excluded from run-tests.ps1's fast
# inner-loop suite; this script runs them on demand.
#
# Each test spawns 2-3 Godot processes which take a few seconds each to come up,
# so the wall-clock budget is generous (5 minutes vs. run-tests.ps1's 2 minutes).
if ($Test) {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess&FullyQualifiedName~$Test"
} else {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess"
}

$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $filter" -RedirectStandardOutput "test-output-multiprocess.log" -RedirectStandardError "test-error-multiprocess.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(300000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output-multiprocess.log"
exit $proc.ExitCode
