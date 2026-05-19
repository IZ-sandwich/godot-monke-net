using System.Collections.Generic;
using System.Threading;
using GdUnit4;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-SL-01..02 — server stop / restart lifecycle in the multi-process harness.
///
/// Cross-process variant of the same-space <c>ServerLifecycleTests</c>. Where
/// the same-space tests use a <c>FakeNetworkBridge</c> to exercise the in-
/// process state-clearing logic (<c>_connectedPeers</c> after disconnect,
/// <c>EntitySpawner.ClearServerEntities</c>), these spin up REAL Godot child
/// processes for both server and client and validate the lifecycle at the OS
/// level: ENet UDP port release on shutdown, fresh-process boot on the same
/// port, no client/peer/entity state leaking between sessions.
///
/// Each major step prints a tagged line to the test output via Godot.GD.Print
/// so a reader scanning the per-process logs can follow the scenario without
/// running it. Lines look like:
///   [MP-SL-01] phase 1: server1 spawned on port=9123, orchPort=...
///   [MP-SL-01] phase 1: client1 connected, netId=2
///   [MP-SL-01] phase 1: client2 connected, netId=3
///   [MP-SL-01] phase 1: server1 reports 2 connected peers
///   [MP-SL-01] phase 2: tearing down session 1 (server1, client1, client2)
///   [MP-SL-01] phase 3: server2 spawned on port=9123 (SAME as session 1)
///   [MP-SL-01] phase 3: client3 connected to server2, netId=2
///   [MP-SL-01] phase 3: server2 reports 1 connected peer (no stale peers from session 1)
///
/// Artifact directory: <c>TestResults/ServerLifecycle/</c> — currently holds
/// only the per-process logs (CSV/SVG/MP4 aren't useful for an assertion-only
/// lifecycle test; the test's diagnostic surface is the printed phase lines).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessServerLifecycleTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "ServerLifecycle";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    // Settle wait between server shutdown and the next server spawn. The
    // previous server process needs to fully exit (releasing its ENet UDP
    // socket) before the replacement binds. TestProcess.Dispose() blocks
    // until the child exits, so this is belt-and-braces; a fraction of a
    // second on top guards against post-exit OS-level port-release lag.
    private const int RestartSettleMs = 250;

    // MP-SL-01 ──────────────────────────────────────────────────────────────────
    // Stop/restart with no stale peers. Session 1: server + 2 clients all
    // connected. Tear all three down. Session 2: spawn fresh server on the
    // SAME ENet port, connect a fresh client. The new server must report
    // exactly 1 connected peer — no ghosts of client1/client2 inherited
    // across the restart.
    [TestCase]
    public void MultiProcess_ServerStopRestart_NoStalePeersInNewSession()
    {
        if (Orch == null) return;

        int port = NextPort();
        var paths = ArtifactsFor("stop_restart_peers");
        using var steps = new StepLogger(paths, "stop_restart_peers", "MP-SL-01");
        steps.Log($"using shared ENet port {port}");

        // ── phase 1: session 1 — server1 + client1 + client2 ───────────────────
        steps.Log("phase 1: spawning server1");
        var server1 = Orch.Spawn("server", enetPort: port, label: "srv1");
        server1.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server1.RemoteLogPath;
        steps.Log($"phase 1: server1 spawned on port={port}, orchPort={server1.OrchPort}, pid={server1.RemotePid}");

        steps.Log("phase 1: spawning client1");
        var client1 = Orch.Spawn("client", enetPort: port, label: "c1");
        client1.WaitReady(networkReady: true, timeoutMs: 30_000);
        ClientLogPath = client1.RemoteLogPath;
        int client1Net = client1.NetworkId;
        AssertThat(client1Net).OverrideFailureMessage("client1 must have a non-zero ENet peer id").IsNotEqual(0);
        steps.Log($"phase 1: client1 connected, netId={client1Net}");

        steps.Log("phase 1: spawning client2");
        var client2 = Orch.Spawn("client", enetPort: port, label: "c2");
        client2.WaitReady(networkReady: true, timeoutMs: 30_000);
        int client2Net = client2.NetworkId;
        AssertThat(client2Net).OverrideFailureMessage("client2 must have a non-zero ENet peer id").IsNotEqual(0);
        AssertThat(client2Net).OverrideFailureMessage("client1 and client2 must have distinct peer ids").IsNotEqual(client1Net);
        steps.Log($"phase 1: client2 connected, netId={client2Net}");

        var session1Peers = ReadServerPeerCount(server1);
        steps.Log($"phase 1: server1 reports {session1Peers} connected peers (expected 2)");
        AssertThat(session1Peers).OverrideFailureMessage(
            $"server1 should see 2 connected peers (client1 netId={client1Net}, client2 netId={client2Net}); reported {session1Peers}")
            .IsEqual(2);

        // ── phase 2: tear down session 1 ─────────────────────────────────────
        steps.Log("phase 2: tearing down session 1 (client1, client2, server1)");
        // Dispose clients first so the server sees clean disconnects, then
        // the server itself. Order matches the user's StopServer protocol
        // documented in the same-space ServerLifecycleTests.
        client1.Dispose();
        client2.Dispose();
        server1.Dispose();
        Thread.Sleep(RestartSettleMs);
        steps.Log($"phase 2: session 1 torn down, waited {RestartSettleMs} ms for port release");

        // ── phase 3: session 2 — fresh server on SAME port, fresh client ─────
        steps.Log($"phase 3: spawning server2 on port={port} (SAME as session 1)");
        var server2 = Orch.Spawn("server", enetPort: port, label: "srv2");
        server2.WaitReady(networkReady: true, timeoutMs: 30_000);
        steps.Log($"phase 3: server2 spawned, orchPort={server2.OrchPort}, pid={server2.RemotePid}");

        int peersBeforeNewClient = ReadServerPeerCount(server2);
        steps.Log($"phase 3: server2 reports {peersBeforeNewClient} connected peers BEFORE any client connects (expected 0)");
        AssertThat(peersBeforeNewClient).OverrideFailureMessage(
            $"server2 must start with zero connected peers; reported {peersBeforeNewClient} (stale peers from session 1 leaked across restart)")
            .IsEqual(0);

        steps.Log("phase 3: spawning client3");
        var client3 = Orch.Spawn("client", enetPort: port, label: "c3");
        client3.WaitReady(networkReady: true, timeoutMs: 30_000);
        int client3Net = client3.NetworkId;
        steps.Log($"phase 3: client3 connected to server2, netId={client3Net}");

        int peersAfterNewClient = ReadServerPeerCount(server2);
        steps.Log($"phase 3: server2 reports {peersAfterNewClient} connected peers after client3 (expected 1)");
        AssertThat(peersAfterNewClient).OverrideFailureMessage(
            $"server2 should see exactly 1 connected peer (client3 netId={client3Net}); reported {peersAfterNewClient}")
            .IsEqual(1);

        // Copy server2/client3 logs to the artifact dir for diagnostic
        // archival; client1/client2/server1 logs were copied via the field
        // assignment above (and at this point those processes have exited).
        CopyProcessLog(paths.Directory, server2.RemoteLogPath, "stop_restart_peers.server2.log");
        CopyProcessLog(paths.Directory, client3.RemoteLogPath, "stop_restart_peers.client3.log");
        CopyProcessLogs(paths);
        steps.Log("complete");
    }

    // MP-SL-02 ──────────────────────────────────────────────────────────────────
    // Stop/restart with no stale entities. Session 1: server spawns a cube,
    // client receives the snapshot. Tear down. Session 2: fresh server reports
    // 0 entities, fresh client receives 0 entities. Exercises both the
    // server-side entity teardown path and the client's view of "nothing
    // here yet" on a fresh connect.
    [TestCase]
    public void MultiProcess_ServerStopRestart_EntitiesCleanInNewSession()
    {
        if (Orch == null) return;

        const byte EntityTypeCube = 4;
        int port = NextPort();
        var paths = ArtifactsFor("stop_restart_entities");
        using var steps = new StepLogger(paths, "stop_restart_entities", "MP-SL-02");
        steps.Log($"using shared ENet port {port}");

        // ── phase 1: session 1 — server spawns an entity, client sees it ────
        steps.Log("phase 1: spawning server1 + client1");
        var server1 = Orch.Spawn("server", enetPort: port, label: "srv1");
        server1.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server1.RemoteLogPath;
        var client1 = Orch.Spawn("client", enetPort: port, label: "c1");
        client1.WaitReady(networkReady: true, timeoutMs: 30_000);
        ClientLogPath = client1.RemoteLogPath;
        WaitForClockSync(server1, client1, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log($"phase 1: server1 + client1 ready; client1.netId={client1.NetworkId}");

        int cubeEid = SpawnEntity(server1, EntityTypeCube, authority: 0, 0f, 3f, 0f);
        steps.Log($"phase 1: server1 spawned cube eid={cubeEid}");
        WaitForClientEntity(client1, cubeEid, timeoutMs: 5_000);

        var server1Entities = QueryAllEntities(server1);
        var client1Entities = QueryAllEntities(client1);
        steps.Log($"phase 1: server1 reports {server1Entities.Count} entities, client1 reports {client1Entities.Count}");
        AssertThat(server1Entities.Count).OverrideFailureMessage(
            $"server1 should have 1 entity (the spawned cube), reported {server1Entities.Count}")
            .IsEqual(1);
        AssertThat(client1Entities.Count).OverrideFailureMessage(
            $"client1 should mirror server1's 1 entity, reported {client1Entities.Count}")
            .IsEqual(1);

        // ── phase 2: tear down session 1 ─────────────────────────────────────
        steps.Log("phase 2: tearing down session 1 (client1, server1)");
        client1.Dispose();
        server1.Dispose();
        Thread.Sleep(RestartSettleMs);
        steps.Log($"phase 2: session 1 torn down, waited {RestartSettleMs} ms for port release");

        // ── phase 3: session 2 — fresh server, fresh client, no entities ─────
        steps.Log($"phase 3: spawning server2 on port={port} (SAME as session 1)");
        var server2 = Orch.Spawn("server", enetPort: port, label: "srv2");
        server2.WaitReady(networkReady: true, timeoutMs: 30_000);

        int server2EntitiesBeforeClient = QueryAllEntities(server2).Count;
        steps.Log($"phase 3: server2 reports {server2EntitiesBeforeClient} entities BEFORE any client connects (expected 0)");
        AssertThat(server2EntitiesBeforeClient).OverrideFailureMessage(
            $"server2 must start with zero entities; reported {server2EntitiesBeforeClient} " +
            $"(EntitySpawner state leaked across stop/restart — same-process autoload bug)")
            .IsEqual(0);

        steps.Log("phase 3: spawning client2");
        var client2 = Orch.Spawn("client", enetPort: port, label: "c2");
        client2.WaitReady(networkReady: true, timeoutMs: 30_000);
        WaitForClockSync(server2, client2, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log($"phase 3: server2 + client2 ready; client2.netId={client2.NetworkId}");

        int server2EntitiesAfter = QueryAllEntities(server2).Count;
        int client2EntitiesAfter = QueryAllEntities(client2).Count;
        steps.Log($"phase 3: server2 reports {server2EntitiesAfter} entities, client2 reports {client2EntitiesAfter} (both expected 0)");
        AssertThat(server2EntitiesAfter).OverrideFailureMessage(
            $"server2 should still have 0 entities after client2 connected; reported {server2EntitiesAfter}")
            .IsEqual(0);
        AssertThat(client2EntitiesAfter).OverrideFailureMessage(
            $"client2 should observe 0 entities (no stale snapshots from session 1); reported {client2EntitiesAfter}")
            .IsEqual(0);

        CopyProcessLog(paths.Directory, server2.RemoteLogPath, "stop_restart_entities.server2.log");
        CopyProcessLog(paths.Directory, client2.RemoteLogPath, "stop_restart_entities.client2.log");
        CopyProcessLogs(paths);
        steps.Log("complete");
    }

    private static int ReadServerPeerCount(TestProcess server)
    {
        // Server-side entity-count query side-channel: get-network-id is the
        // simplest probe that confirms the server is alive AND lets us indirect
        // peer-count by walking through clock-state's wallMs as a heartbeat.
        // Cleaner: re-use get-all-entities (already exposed), and probe peer
        // count via a small wrapper around the server's connected-peers list.
        // The harness doesn't expose a "peer-count" command directly, but
        // ServerManager broadcasts ownership via Authority on each entity —
        // since this is a no-entity test that wouldn't help.
        //
        // What we actually have available: the harness's "ready" cmd reports
        // serverEntityCount / clientEntityCount, and clock-state reports
        // networkReady. There's no peer count. Add one quickly by sending
        // a small custom probe — but we don't want to extend the harness
        // for this single test. Instead, infer peer count from the live
        // EntitySpawner's snapshot: each connected peer that owns an entity
        // would be visible. For a no-entity scenario that's not enough.
        //
        // Compromise: re-use ServerManager.GetClientIds via a small probe.
        // The cleanest minimal addition is one new harness cmd "server-peer-count"
        // returning ServerManager.Instance.GetClientIds().Length.
        using var doc = server.Send(new { cmd = "server-peer-count" });
        return doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();
    }

    private static List<EntityHandle> QueryAllEntities(TestProcess process)
    {
        using var doc = process.Send(new { cmd = "get-all-entities" });
        var arr = doc.RootElement.GetProperty("data").GetProperty("entities");
        var result = new List<EntityHandle>();
        foreach (var el in arr.EnumerateArray())
        {
            result.Add(new EntityHandle
            {
                Id = el.GetProperty("id").GetInt32(),
                Type = el.GetProperty("type").GetByte(),
                Authority = el.GetProperty("authority").GetInt32(),
            });
        }
        return result;
    }

    private class EntityHandle { public int Id; public byte Type; public int Authority; }
}
