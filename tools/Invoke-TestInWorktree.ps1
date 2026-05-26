# Shared helper for run-tests.ps1 / run-multiprocess-tests.ps1's -Worktree mode.
#
# Mirrors the current working tree to a temp dir, restores NuGet, gives the
# build a per-PID assembly name, rewrites tests/project.godot so Godot's C#
# script binding follows the renamed dll, runs `dotnet test` with the supplied
# filter, copies TestResults back, and removes the copy.
#
# Why this exists: two `dotnet test` invocations of the same project collide on
# (a) the shared `.godot/mono/temp/bin/Debug` build output dir, (b) gdUnit4's
# named pipe (derived from Path.GetFileNameWithoutExtension(AssemblyPath) - same
# basename = same pipe = "All pipe instances are busy"), and (c) the
# `TestResults/` artifact dir. The worktree-copy plus per-PID dll filename
# eliminates all three collisions while keeping uncommitted/untracked files
# in the run (a `git worktree` would only ship committed state, and
# tests/Infrastructure/Artifacts/ and other dirs are currently untracked).
#
# This script is dot-sourced/invoked from the top-level test runners and is not
# intended to be called directly. It exits with the test process's exit code.

param(
    # Test filter string passed verbatim to `dotnet test --filter`.
    [Parameter(Mandatory)]
    [string]$Filter,

    # Filename for the captured stdout log (placed in the worktree, copied back).
    [string]$StdoutLog = "test-output.log",
    # Filename for the captured stderr log (placed in the worktree, copied back).
    [string]$StderrLog = "test-error.log",

    # Wall-clock budget for the inner `dotnet test`, in milliseconds.
    [int]$TimeoutMs = 300000,

    # Short human-readable scenario name printed in the wrapper's progress lines
    # so two parallel terminals are distinguishable at a glance ("inner-loop",
    # "multi-process", ...).
    [string]$Scenario = "tests"
)

$ErrorActionPreference = "Stop"

$repoRoot = (git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { Write-Error "not a git repo"; exit 1 }
$repoRoot = (Resolve-Path $repoRoot).Path

# Capture git state from the SOURCE TREE before mirroring. The worktree copy
# excludes .git (it doesn't need the history; the inner build only reads
# source files), so a `git rev-parse HEAD` inside the worktree would return
# nothing. Anything that needs the originating commit hash - the quantitative
# suite's run-folder name in particular - reads these env vars instead of
# shelling out to git. The vars are inherited by every child process the
# worktree spawns, so the inner dotnet test + spawned Godot harnesses all
# see the right values.
$srcCommit = ((git -C $repoRoot rev-parse HEAD) 2>$null).Trim()
$srcBranch = ((git -C $repoRoot rev-parse --abbrev-ref HEAD) 2>$null).Trim()
$srcDirtyList = ((git -C $repoRoot status --porcelain) 2>$null).Trim()
$env:MONKENET_RUN_COMMIT      = if ($srcCommit) { $srcCommit } else { "unknown" }
$env:MONKENET_RUN_BRANCH      = if ($srcBranch) { $srcBranch } else { "unknown" }
$env:MONKENET_RUN_DIRTY_LIST  = $srcDirtyList
$env:MONKENET_RUN_DIRTY       = if ([string]::IsNullOrWhiteSpace($srcDirtyList)) { "false" } else { "true" }

$wtName = "monke-net-$Scenario-$PID-$(Get-Date -Format yyyyMMdd-HHmmss)"
$wtPath = Join-Path $env:TEMP $wtName
Write-Host "[$Scenario] mirroring working tree to $wtPath"

# robocopy /MIR mirrors the source dir. Excludes:
#   .git       - copy carries the index/refs that the inner run doesn't read
#   .godot     - Godot project cache + Mono build output; per-run only
#   bin, obj   - .NET build output; per-run only
#   TestResults, parallel-*.log, test-output*.log, test-error*.log
#              - previous-run output; the new run produces fresh ones
# Exit codes 0-7 are success (8+ means actual error in robocopy land).
robocopy $repoRoot $wtPath /MIR /MT:8 /R:0 /W:0 /NFL /NDL /NJH /NJS /NP `
    /XD ".git" ".godot" "bin" "obj" "TestResults" ".claude" `
    /XF "parallel-*.log" "test-output-multiprocess.log" "test-error-multiprocess.log" "test-output.log" "test-error.log" | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed with code $LASTEXITCODE"; exit 1 }

try {
    Push-Location $wtPath

    # Restore NuGet assets first. The copy excludes obj/ (which contains
    # project.assets.json) so a vanilla `dotnet test` would fail with NETSDK1004
    # when BuildMonkeNetForChildProcesses MSBuilds the sibling monke-net.csproj.
    Write-Host "[$Scenario] restoring NuGet assets in $wtPath"
    dotnet restore tests/MonkeNetTests.csproj 2>&1 | Out-Null
    dotnet restore monke-net.csproj 2>&1 | Out-Null

    # Unique dll filename per parallel invocation. The gdUnit4 runner derives
    # its named-pipe from Path.GetFileNameWithoutExtension(AssemblyPath) - same
    # filename = same pipe regardless of path, so two parallel runs writing
    # MonkeNetTests.dll would still collide on the pipe "gdunit4-MonkeNetTests".
    # The per-run suffix is the wrapper PID, guaranteed unique among
    # concurrent invocations on this host.
    $asmName = "MonkeNetTests-$PID"
    Write-Host "[$Scenario] running with AssemblyName=$asmName"

    # Godot's C# script binding resolves classes (like GdUnit4TestRunnerScene)
    # via `project/assembly_name` in project.godot. If we rename the dll via
    # -p:AssemblyName= but leave assembly_name="MonkeNetTests" in the .godot
    # project file, Godot can't instantiate test runner scripts and the test
    # execution stage logs "Cannot instantiate C# script ... GdUnit4TestRunnerScene.cs"
    # then aborts with "No test matches the given testcase filter". Rewriting
    # BOTH assembly_name and config/name to match the renamed dll keeps Godot
    # and the build in sync.
    $projGodot = Join-Path $wtPath "tests\project.godot"
    if (Test-Path $projGodot) {
        (Get-Content $projGodot) `
            -replace 'project/assembly_name="MonkeNetTests"', "project/assembly_name=`"$asmName`"" `
            -replace 'config/name="MonkeNetTests"', "config/name=`"$asmName`"" `
            | Set-Content $projGodot
    }

    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $Filter -p:AssemblyName=$asmName" `
        -RedirectStandardOutput $StdoutLog `
        -RedirectStandardError $StderrLog `
        -NoNewWindow -PassThru

    if ($env:MONKENET_TEST_PROFILE -eq "1" -and $env:MONKENET_TEST_PROFILE_DIR) {
        # AttachProfiler mode: dispatch to the polling watcher which auto-spawns
        # dotnet-trace per cell. The watcher returns once $proc exits; we then
        # read $proc.ExitCode below as usual.
        Write-Host "[$Scenario] attach-profiler watcher polling $env:MONKENET_TEST_PROFILE_DIR"
        & (Join-Path $repoRoot "tools\Watch-AttachProfiler.ps1") `
            -Process     $proc `
            -CommDir     $env:MONKENET_TEST_PROFILE_DIR `
            -TraceOutDir (Join-Path $repoRoot "tests\TestResults\profile-traces") `
            -TimeoutMs   $TimeoutMs
        if (-not $proc.HasExited) {
            if (-not $proc.WaitForExit(5000)) { $proc.Kill($true); $exitCode = 1 }
            else { $exitCode = $proc.ExitCode }
        } else {
            $exitCode = $proc.ExitCode
        }
    }
    elseif (-not $proc.WaitForExit($TimeoutMs)) {
        Write-Host "[$Scenario] timeout reached - killing test process"
        $proc.Kill($true)
        $exitCode = 1
    } else {
        $exitCode = $proc.ExitCode
    }

    Get-Content $StdoutLog

    # Copy artefacts back to the main checkout, merging the worktree's
    # tests/TestResults/* directly into the main tests/TestResults/. No
    # per-run subdir - each test class writes to its own subdir (e.g.
    # ClockSync/, RampMotionPlots/) so different tests don't conflict, and if
    # the same test runs in two parallel terminals the later writer wins
    # ("latest" semantics). Robocopy /E mirrors children without removing
    # files that other parallel runs may have just written.
    $srcResults = Join-Path $wtPath "tests\TestResults"
    if (Test-Path $srcResults) {
        $dstResults = Join-Path $repoRoot "tests\TestResults"
        New-Item -ItemType Directory -Force -Path $dstResults | Out-Null
        Write-Host "[$Scenario] merging artefacts: $srcResults -> $dstResults"
        robocopy $srcResults $dstResults /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    }
    # Copy stdout/stderr to their standard filenames at the repo root,
    # overwriting any previous run's capture. With different tests in parallel
    # the wrapper's stdout/stderr are distinct streams but they all land at
    # the same filename - latest writer wins, matching the "no subfolders" model.
    foreach ($log in @($StdoutLog, $StderrLog)) {
        if (Test-Path (Join-Path $wtPath $log)) {
            Copy-Item (Join-Path $wtPath $log) (Join-Path $repoRoot $log) -Force
        }
    }
}
finally {
    Pop-Location
    Write-Host "[$Scenario] removing tree copy $wtPath"
    if (Test-Path $wtPath) { Remove-Item -Recurse -Force $wtPath -ErrorAction SilentlyContinue }
}

exit $exitCode
