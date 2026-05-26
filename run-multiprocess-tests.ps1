param(
    # Optional test selector within the multi-process suite. Pass a class name
    # (e.g. "MultiProcessClockSyncTests") or class+method
    # (e.g. "MultiProcessClockSyncTests.ConvergesUnderJitter") to narrow the run.
    # Matched as a substring against the test's FullyQualifiedName. When omitted,
    # runs every test under MonkeNet.Tests.MultiProcess.
    [Parameter(Position=0)]
    [string]$Test,

    # Run inside a transient working-tree copy so multiple invocations can run
    # in parallel without colliding on (a) the shared `.godot/mono/temp/bin/Debug`
    # build output dir, (b) gdUnit4's per-assembly named pipe (the pipe name is
    # derived from the test dll filename - same dll path = same pipe = collision
    # - so two parallel runs MUST run from different paths), and (c) the
    # `TestResults/` artifact dir. The copy includes BOTH tracked and untracked
    # files (mirrors the full working tree), so uncommitted changes flow into
    # the run. The copy is removed on exit; any artefacts under TestResults/
    # are copied back to the main checkout under a per-run subdir first.
    #
    # Why a directory copy instead of `git worktree`: tests/Infrastructure/Artifacts/
    # and other recently-added subdirs are not yet committed in this repo, so
    # `git worktree add` would produce a worktree without those files and the
    # test build would fail with CS0246 ("ArtifactPaths could not be found").
    # A mirror copy captures everything on disk, tracked or not.
    [switch]$Worktree,

    # Pause each Quantitative cell right before scenario.Setup so you can attach
    # dotnet-trace by PID. See run-tests.ps1's -AttachProfiler help.
    [switch]$AttachProfiler,

    # Print usage and exit. -h is recognised as a short alias.
    [Alias('h')]
    [switch]$Help
)

if ($Help) {
    Write-Host @'
Usage: run-multiprocess-tests.ps1 [<TestSelector>] [-Worktree] [-Help]

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
  -Worktree      Run the tests inside a transient working-tree copy so multiple
                 invocations can run in parallel. The copy lives in $env:TEMP,
                 has its own .godot/ build output, its own TestResults/ artifact
                 dir, and its own gdUnit4 pipe (because gdUnit4 derives the pipe
                 name from the test dll filename, which lives at a unique path
                 inside each copy). Uncommitted/untracked files ARE carried over
                 (the copy mirrors the working tree, not just the git index).
                 Artefacts are merged back into tests/TestResults/ (per-test-class
                 subdirs); two parallel runs of the SAME test class will
                 overwrite each other ("latest writer wins").
  -Help, -h      Print this message and exit.

Outputs:
  test-output-multiprocess.log    stdout from `dotnet test`
  test-error-multiprocess.log     stderr from `dotnet test`
  exit code                       propagated from `dotnet test`

Examples:
  run-multiprocess-tests.ps1
      Run every multi-process test in this checkout.

  run-multiprocess-tests.ps1 -Worktree OffsetPush
      Run "OffsetPush" tests inside a transient worktree. Safe to run in
      parallel with another invocation in another terminal.

  run-multiprocess-tests.ps1 MultiProcessSleepCoherenceTests
      Run every test inside MultiProcessSleepCoherenceTests (in-place).
'@
    exit 0
}

$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

# Forward -AttachProfiler. Set unconditionally so a prior session's value
# can't leak into a follow-up run. When ON, also set MONKENET_TEST_PROFILE_DIR
# to a fresh per-PID temp dir so the watcher and the harness agree on where
# the per-cell handshake files live; the worktree script picks the env var
# up and dispatches to tools/Watch-AttachProfiler.ps1.
$env:MONKENET_TEST_PROFILE = if ($AttachProfiler) { "1" } else { "0" }
if ($AttachProfiler) {
    $profileDir = Join-Path $env:TEMP "monke-net-profile-$PID"
    if (Test-Path $profileDir) { Remove-Item -Recurse -Force $profileDir }
    New-Item -ItemType Directory -Path $profileDir | Out-Null
    $env:MONKENET_TEST_PROFILE_DIR = $profileDir
    Write-Host "[run-multiprocess-tests] AttachProfiler ON - comm dir=$profileDir"
    Write-Host "[run-multiprocess-tests] traces will land in: $(Join-Path $PSScriptRoot 'tests\TestResults\profile-traces')"
} else {
    Remove-Item Env:MONKENET_TEST_PROFILE_DIR -ErrorAction SilentlyContinue
}

# Build the test filter once, regardless of worktree mode.
if ($Test) {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess&FullyQualifiedName~$Test"
} else {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess"
}

# ?? Worktree path (working-tree copy, not git worktree) ???????????????????
# Delegate the working-tree mirror + per-PID assembly rename + project.godot
# rewrite + run + artefact copy-back to the shared helper. See
# tools/Invoke-TestInWorktree.ps1 for the why.
if ($Worktree) {
    & (Join-Path $PSScriptRoot "tools\Invoke-TestInWorktree.ps1") `
        -Filter $filter `
        -StdoutLog "test-output-multiprocess.log" `
        -StderrLog "test-error-multiprocess.log" `
        -TimeoutMs 300000 `
        -Scenario "mp"
    exit $LASTEXITCODE
}

# ?? In-place path (default) ???????????????????????????????????????????????
# Original behavior preserved for single-runner workflows. Cannot be run in
# parallel with another in-place invocation - they'll collide on the gdUnit4
# pipe and the shared bin/ dir. Use -Worktree for parallel runs.
$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $filter" -RedirectStandardOutput "test-output-multiprocess.log" -RedirectStandardError "test-error-multiprocess.log" -NoNewWindow -PassThru

if ($AttachProfiler) {
    # Same handshake-watcher as the worktree path.
    & (Join-Path $PSScriptRoot "tools\Watch-AttachProfiler.ps1") `
        -Process     $proc `
        -CommDir     $env:MONKENET_TEST_PROFILE_DIR `
        -TraceOutDir (Join-Path $PSScriptRoot "tests\TestResults\profile-traces") `
        -TimeoutMs   300000
    if (-not $proc.HasExited) {
        if (-not $proc.WaitForExit(5000)) { $proc.Kill($true); $exit = 1 }
        else { $exit = $proc.ExitCode }
    } else { $exit = $proc.ExitCode }
    Get-Content "test-output-multiprocess.log"
    exit $exit
}

if (-not $proc.WaitForExit(300000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output-multiprocess.log"
exit $proc.ExitCode
