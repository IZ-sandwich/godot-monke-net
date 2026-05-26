# Watches the test process while polling for per-cell handshake files dropped
# by QuantitativeTestBase.ScriptedProfilerPause. For each cell, attaches
# dotnet-trace to the reported client PID and signals the harness to resume.
#
# Why a separate script: both run-tests.ps1 (direct) and Invoke-TestInWorktree.ps1
# (worktree-isolated) need the same polling logic; keeping it in one file means
# there's no duplicated workflow to drift apart.
#
# The script blocks until the test process exits, then waits for any in-flight
# dotnet-trace processes to finish writing their `.nettrace` files. It does NOT
# call WaitForExit on $Process itself for the main wait - callers read
# $Process.ExitCode after this returns.

param(
    # The dotnet-test wrapper process to watch. Treated as the "lifetime"
    # of the test run; polling stops when it exits.
    [Parameter(Mandatory)] [System.Diagnostics.Process]$Process,

    # Directory the harness writes its handshake files into (next.txt, go).
    # Created if missing. Must match $env:MONKENET_TEST_PROFILE_DIR seen by
    # the harness process.
    [Parameter(Mandatory)] [string]$CommDir,

    # Where final `.nettrace` files land. One file per (scenario, condition)
    # cell encountered.
    [Parameter(Mandatory)] [string]$TraceOutDir,

    # Hard upper bound on the watch loop, in milliseconds. Stops the script
    # if the test process never exits.
    [int]$TimeoutMs = 600000,

    # `dotnet-trace --duration` value per cell, in seconds. MUST finish before
    # the test cell ends, otherwise the runtime "rundown" step (which queries
    # the live process for JIT'd method names) sees a dead PID and stacks
    # render as "?!?" in Speedscope. S7 cells are ~20 s wall, so 18 s leaves
    # a small margin and still captures the entire scenario.Run window
    # (settle is ~4 s, Run is ~15 s).
    [int]$TraceDurationSec = 18,

    # How long to wait for each dotnet-trace process to finalise its
    # `.nettrace` file after the watched test exits.
    [int]$TraceWaitTimeoutMs = 90000
)

$ErrorActionPreference = "Continue"

New-Item -ItemType Directory -Force -Path $CommDir     | Out-Null
New-Item -ItemType Directory -Force -Path $TraceOutDir | Out-Null

# Track the spawned dotnet-trace processes so we can wait on their `.nettrace`
# finalisation after the test exits.
$traceProcs = New-Object System.Collections.ArrayList

$deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
$nextFile = Join-Path $CommDir "next.txt"
$goFile   = Join-Path $CommDir "go"

while (-not $Process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    if (Test-Path $nextFile) {
        # Handshake format (one value per line):
        #   line 0 = client pid
        #   line 1 = server pid
        #   line 2 = scenario id
        #   line 3 = condition id
        try {
            $lines       = Get-Content $nextFile -ErrorAction Stop
            $clientPid   = ($lines[0] -as [int])
            $serverPid   = ($lines[1] -as [int])
            $scenarioId  = $lines[2]
            $conditionId = $lines[3]
        } catch {
            Write-Host "[profile-watch] failed to read $nextFile : $_"
            $clientPid = 0; $serverPid = 0
        }

        foreach ($side in @(
            @{ Role = "client"; Pid = $clientPid },
            @{ Role = "server"; Pid = $serverPid }
        )) {
            if ($side.Pid -le 0) {
                Write-Host "[profile-watch] skipping $($side.Role) - no PID supplied"
                continue
            }
            $tracePath = Join-Path $TraceOutDir "$scenarioId.$conditionId.$($side.Role).nettrace"
            Write-Host "[profile-watch] attaching dotnet-trace to $($side.Role) PID=$($side.Pid) -> $tracePath"
            try {
                # Explicit providers so the symbol-rundown step gets the JIT
                # method-load events; the default `dotnet-common` profile
                # didn't on this Godot Mono build (every frame showed up as
                # "?!?" in Speedscope). Providers:
                #   Microsoft-DotNETCore-SampleProfiler - periodic stack samples
                #     (the actual CPU sampling).
                #   Microsoft-Windows-DotNETRuntime - JIT/Loader keywords
                #     (0x1B = GC|Loader|JitTracing|Method) at Verbose level
                #     so MethodLoadVerbose events fire for AOT'd / preJIT'd
                #     methods too, not just freshly-JIT'd ones.
                $trace = Start-Process -FilePath "dotnet-trace" `
                    -ArgumentList @(
                        "collect",
                        "--process-id", "$($side.Pid)",
                        "--duration",   "00:00:$TraceDurationSec",
                        "--providers",  "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,Microsoft-Windows-DotNETRuntime:0x1B:5",
                        "-o",           $tracePath
                    ) `
                    -NoNewWindow -PassThru
                [void]$traceProcs.Add($trace)
            } catch {
                Write-Host "[profile-watch] dotnet-trace failed to start for $($side.Role): $_"
            }
        }

        # One pre-roll for BOTH trace sessions to actually start emitting
        # samples before the harness releases. Without this, the early Setup
        # frames are missed by either or both.
        Start-Sleep -Seconds 2

        # Always signal the harness, even on attach failure - better to let
        # the test run than hang waiting for an attach that won't come.
        New-Item -ItemType File -Path $goFile -Force | Out-Null
        Remove-Item -Force $nextFile -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 250
}

# Drain dotnet-trace processes. They finish on their own (--duration), but
# can lag a few seconds writing the final .nettrace. Kill anything stuck
# rather than leaving an orphan tracing the now-dead client PID forever.
foreach ($t in $traceProcs) {
    if ($null -eq $t) { continue }
    try {
        if (-not $t.HasExited) { [void]$t.WaitForExit($TraceWaitTimeoutMs) }
        if (-not $t.HasExited) {
            Write-Host "[profile-watch] dotnet-trace PID=$($t.Id) did not finish within ${TraceWaitTimeoutMs}ms; killing"
            $t.Kill($true)
        }
    } catch { /* best effort */ }
}

Write-Host "[profile-watch] trace files written under: $TraceOutDir"
