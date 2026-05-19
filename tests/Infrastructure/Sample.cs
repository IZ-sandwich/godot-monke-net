using System.Collections.Generic;
using Godot;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// A snapshot of harness state captured at a specific physics tick: the
/// `mispredictionsCount` reported by the client's prediction manager plus a
/// per-entity readout (position, velocity, rotation, visual pose, sleep flags).
///
/// Lifted from a per-test inner type so multiple multi-process tests, plot
/// writers, and the shared CSV/SVG infrastructure can refer to a single shape.
/// </summary>
public class Sample
{
    public int Tick;
    public int MispredictionsCount;
    public List<EntityState> Entities;
}

/// <summary>
/// Per-entity slice of a <see cref="Sample"/>. Optional fields default to zero /
/// identity so tests that don't care about (say) angular velocity don't have to
/// initialise it.
/// </summary>
public class EntityState
{
    public int Id;
    public int Type;
    public int Authority;
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 VisualPosition;
    public Quaternion VisualRotation = Quaternion.Identity;
    public bool Sleeping;
    public bool CanSleep;
}
