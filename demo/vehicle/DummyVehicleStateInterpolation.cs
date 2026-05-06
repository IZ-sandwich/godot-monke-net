using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class DummyVehicleStateInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _parent;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;
        _parent.Position = pastState.Position.Lerp(futureState.Position, interpolationFactor);

        // Rotation is shipped as Euler (Vector3) but Vector3.Lerp on Euler angles
        // glitches when any axis crosses ±π (a smoothly rotating vehicle wraps from
        // +π to −π in the Quaternion→Euler conversion). Slerp on quaternions takes
        // the geometrically shortest path and avoids the wrap-around flip.
        var pastQ = Quaternion.FromEuler(pastState.Rotation);
        var futureQ = Quaternion.FromEuler(futureState.Rotation);
        _parent.Quaternion = pastQ.Slerp(futureQ, interpolationFactor);
    }
}
