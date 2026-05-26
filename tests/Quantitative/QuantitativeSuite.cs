using GdUnit4;
using MonkeNet.Tests.Quantitative.Scenarios;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// Quantitative performance evaluation suite. Sweeps a scenario × network-condition
/// matrix and writes a timestamped + commit-stamped summary CSV under
/// <c>TestResults/Quantitative/summary.&lt;date&gt;.&lt;commit&gt;[+dirty].csv</c>.
///
/// <para>
/// This is one long-running gdUnit4 test that spawns many (server, client) Godot
/// process pairs in sequence. It exists as a single test case rather than one
/// per (scenario, condition) cell because each spawn is expensive and we want a
/// single CSV row per cell rather than scattering rows across test outputs.
/// </para>
///
/// <para>
/// To run a focused subset, comment out scenarios in <see cref="Scenarios"/>
/// below — the matrix is the source of truth for what gets run.
/// </para>
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class QuantitativeSuite : QuantitativeTestBase
{
    [BeforeTest] public void SetUp()    => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    /// <summary>The full scenario list. S5 spawns a second client (observer)
    /// via <see cref="IScenario.RequiresObserver"/>; the other scenarios use
    /// the single-client default.</summary>
    private static readonly IScenario[] Scenarios =
    {
        new S1_Idle(),
        new S2_LinearMotion(),
        new S3_ImpulseResponse(),
        new S4_PhysicsStack(),
        new S5_MultiClientSharedPhysics(),
        // S6_JitterStress removed: M1/M2 are measured only by S2 now, and
        // the isolated-jitter profile (50 ms latency, 50 ms jitter, 0 % loss)
        // moved to NetworkCondition.C_Jitter so it shows up in S2's matrix.
        new S7_MultiBodyChaos(),
        new S8_DegradationStress(),
    };

    [TestCase]
    public void RunFullMatrix()
    {
        // RunMatrix early-exits if GODOT_BIN isn't set, matching the rest of
        // the multi-process test suite's "no-op on dev machines without the
        // env var" convention.
        RunMatrix(Scenarios);
    }
}
