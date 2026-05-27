using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalRigidPlayerPrediction : ClientPredictedEntity
{
    // 20 cm hard-reconcile threshold (0.04 m²), matching LocalVehiclePrediction.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Catches walking-into-wall
    // mismatches where position holds but momentum is wrong on one side.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Rotation divergence threshold (degrees). See LocalRigidPropPrediction for rationale.
    // Rigid players are upright capsules so rotation rarely drifts in normal play, but
    // a tipped / knocked-over player needs the same correction path as a tumbling cube.
    [Export] private float _maxRotationDeviationDegrees = 5f;
    [Export] private PredictionRigidbody3D _predictionRb;

    // Vertical offset above the vehicle while riding. Mirrors LocalPlayerPrediction and
    // PlayerStateSyncronizer so the predicted and authoritative anchor agree.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    // Squared interaction range for finding a vehicle to claim via the Interact key.
    // Matches the demo's Vehicle OwnershipPolicy.MaxRequesterDistance (4 m) so the
    // client-side proximity check and the server-side policy approval agree.
    private const float InteractRangeSquared = 16f;

    // Tracks last tick's Interact bit so we can edge-trigger claim/release on the
    // F-key rising edge only — holding F shouldn't repeatedly fire request messages.
    private bool _lastInteract;

    // Visual layer bit assigned to the local player's own knight rig so the
    // first-person Camera3D (cull_mask omits this layer) doesn't draw the body
    // it's inside. Bit 1 = layer 2; matches LocalRigidPlayer.tscn's cull_mask.
    private const uint OwnPlayerVisualLayer = 1u << 1;

    // Knight visual cached at spawn. The rigid body has lock_rotation=true so the
    // body itself never carries yaw — and the camera yaw lives on a separate
    // RotationHelperY (camera-only), so nothing else would rotate the visible
    // knight. We drive its Y rotation from input.CameraYaw every tick on every
    // client, which makes observers see the knight turn to match the owner's
    // first-person look direction. (Predicted/cached input flows through for
    // remote players via GameSnapshotMessage.Inputs — see ClientPredictionManager.)
    private Node3D _knightRig;

    // While riding, the body is set to Freeze=Kinematic so its position is fully
    // derived from the vehicle pose (vehicle.GlobalPosition + RideOffset) instead of
    // integrated by Jolt. This drives the rider's client/server deviation to zero —
    // both sides compute the same anchor formula. Tracking the entity id we last
    // rode lets us read that vehicle's LinearVelocity at dismount and inherit it
    // when the body unfreezes, so jumping off a moving vehicle preserves momentum.
    private int _lastRiddenVehicleEntityId;

    // Latest AdvancePhysics result for this entity, kept between OnProcessTick (pre-step)
    // and OnPostPhysicsTick (post-step). LogPostPhysics consumes preVel + the impulse
    // AdvancePhysics queued to compute the residual velocity attributable to Jolt's
    // contact-resolution step. Cleared back to default when AdvancePhysics is skipped
    // (rider-anchored / no-input ticks) so a stale impulse isn't subtracted from an
    // anchor-step body that didn't run input physics.
    private RigidPlayerPhysics.AdvanceResult _lastAdvanceResult;
    private bool _hasLastAdvanceResult;

    public override void OnEntitySpawned()
    {
        base.OnEntitySpawned();

        _knightRig = GetParent()?.GetNodeOrNull<Node3D>("KnightRig");

        // Leave SceneTreeFTI on (Inherit → project default = ON). The rider
        // body's transform is rewritten every physics tick by AnchorBodyToVehicle
        // while frozen-kinematic; FTI's pump captures prev = current at iteration
        // prepare and the post-_PhysicsProcess write becomes the new current,
        // so FTI lerps smoothly between consecutive anchor poses. The camera
        // (a child of this body) then renders the world from the SAME interpolated
        // pose the vehicle mesh is rendered at — visual stays at constant offset
        // from the camera. AnchorBodyToVehicle no longer calls
        // ResetPhysicsInterpolation, which previously collapsed prev=curr each
        // tick and broke that lerp.

        // Only the locally-owned knight rig moves to layer 2. Remote players' rigs
        // stay on the default layer 1 so the local first-person camera (which omits
        // layer 2 via cull_mask) still renders them. The harness observer camera
        // uses the default cull_mask and so renders both layers regardless.
        if (Authority != ClientManager.Instance?.GetNetworkId()) return;
        if (_knightRig == null) return;
        SetVisualLayerRecursive(_knightRig, OwnPlayerVisualLayer);
    }

    private static void SetVisualLayerRecursive(Node node, uint layer)
    {
        if (node is VisualInstance3D vi) vi.Layers = layer;
        foreach (Node child in node.GetChildren())
            SetVisualLayerRecursive(child, layer);
    }

    // Re-anchor the rider AFTER SpaceStep so the FTI lerp window of the rider
    // (and its child Camera3D) matches the vehicle's. OnProcessTick runs
    // BEFORE SpaceStep; vehicle.GlobalPosition there is still the previous
    // tick's post-step pose, so anchoring then leaves the rider one tick
    // behind the vehicle visual when both are FTI-lerped — the first-person
    // camera then renders the world from a viewpoint that lags the vehicle
    // mesh by 1 tick, perceived as oscillation. This post-step pass re-reads
    // vehicle.GlobalPosition AFTER its just-integrated pose is on the scene
    // tree, so the rider's curr-tick anchor pose lines up with the vehicle's.
    //
    // Dismount-with-velocity-inheritance also fires here (not in OnProcessTick)
    // so the vehicle's post-step LinearVelocity is what's inherited — the same
    // value the server's UpdateRideFreezeState(post-step) reads and packs into
    // the snapshot stream. Without this matched timing, the client and server
    // inherit different vehicle velocities (off by one tick of integration)
    // and the player's post-dismount free-fall trajectory diverges by ~0.5 m/s
    // on Y, tripping RIDER-MISPREDICT-TRIP a couple of seconds later.
    public override void OnPostPhysicsTick(int tick, IPackableElement input)
    {
        TryAnchorToOwnedVehicle();
        UpdateRideFreezeState(isPostStep: true);

        // Post-step diagnostic — runs only on live ticks (resim path doesn't invoke
        // OnPostPhysicsTick). If the immediately-preceding OnProcessTick ran
        // RigidPlayerPhysics.AdvancePhysics, _lastAdvanceResult holds the preVel and
        // queued impulse; LogPostPhysics computes the residual velocity Jolt applied
        // during the step and dumps the post-step contact list. Skipped when the
        // body was anchored or no input arrived (those ticks don't run AdvancePhysics
        // and have no "expected post" to subtract from).
        var body = _predictionRb?.Body;
        if (_hasLastAdvanceResult && body != null && !body.Freeze)
        {
            RigidPlayerPhysics.LogPostPhysics(body, _lastAdvanceResult.PreVel, _lastAdvanceResult.QueuedImpulse, phase: "live");
        }
        _hasLastAdvanceResult = false;

        // T1 contact-upgrade. Runs on BOTH the locally-owned player AND any
        // observer-side player simulations. The driver's upgrade is the
        // obvious case (we touch a cube → flip it to Resim so our contact
        // stays crisp). The observer's upgrade matters too: the observer is
        // locally simulating the remote player's contacts, and if a touched
        // cube stays in Interpolate the always-blend path yanks it toward
        // server pose every snapshot, perturbing the observer's player-vs-
        // cube contact resolution and tripping player rollbacks. Running the
        // upgrade on both sides keeps cubes synchronised with whichever
        // process is locally simulating contact. The observer's player will
        // mispredict more (it always does — stale-input prediction), but the
        // CUBE side stays stable instead of being a spurious source of
        // additional rollbacks.
        //
        // Same contact-query plumbing the diagnostic LogPostPhysics uses; for
        // each body we touch, walk up the scene tree to the owning
        // ClientEntities NetworkBehaviour, fetch its ClientPredictedEntity
        // component, and request a hysteresis upgrade. The upgrade takes
        // effect on this same tick (RequestResimUpgrade bumps the counter
        // immediately) so any misprediction routed THIS tick uses Resim
        // semantics — required so the player doesn't briefly push through a
        // body still blending toward its snapshot pose.
        if (body == null) return;
        var contactBodies = RigidPlayerPhysics.QueryContactBodies(body);
        if (contactBodies.Count == 0) return;
        foreach (var cb in contactBodies)
        {
            var cpe = FindOwningPredictedEntity(cb);
            if (cpe != null && cpe != this)
                cpe.RequestResimUpgrade();
        }
    }

    // Resolve a contact body back to its owning ClientPredictedEntity, if any.
    // Demo entities are rooted at their RigidBody3D (the body IS the entity
    // root, e.g. LocalCube is the body itself with the NetworkBehaviour as a
    // child Node) — so a "walk up to find NetworkBehaviour" pass misses
    // entirely because the NetworkBehaviour is BELOW the body, not above it.
    // Use EntitySpawner.GetEntityRoot for the comparison: for every
    // ClientEntity, ask the spawner for its root Node3D, and walk up from the
    // collider checking ancestry against each root. The first match wins.
    // Floors / offline geometry don't have an entity root in ClientEntities,
    // so they return null and the upgrade silently no-ops.
    internal static ClientPredictedEntity FindOwningPredictedEntity(Node collider)
    {
        if (collider == null) return null;
        var spawner = EntitySpawner.Instance;
        if (spawner == null) return null;
        for (int i = 0; i < spawner.ClientEntities.Count; i++)
        {
            var entity = spawner.ClientEntities[i];
            var root = spawner.GetEntityRoot(entity);
            if (root == null) continue;
            // Match either the body == root OR root is an ancestor of the body
            // (covers entities whose collider is a nested CollisionShape3D child
            // rather than the root itself).
            Node cursor = collider;
            while (cursor != null)
            {
                if (cursor == root)
                    return entity.GetComponent<ClientPredictedEntity>();
                cursor = cursor.GetParent();
            }
        }
        return null;
    }

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // ClientPredictionManager.Predict() now routes per-entity input to every
        // predicted entity: the locally-driven entity gets fresh local input; every
        // other entity gets the input the server last reported for it (forwarded
        // via GameSnapshotMessage.Inputs). So this script can apply RigidPlayerPhysics
        // for every player it predicts — local OR remote — and the resulting motion
        // tracks the server's because both sides apply the same input.
        //
        // The interact rising-edge handler is the one thing that must stay gated by
        // local-ownership: only the player actually pressing F should fire the
        // RequestAuthority RPC. A remote player's input might also have the Interact
        // bit set (that's how they trigger their own claim), but processing it here
        // would fire a duplicate request from this client.
        bool ownedByMe = Authority == ClientManager.Instance?.GetNetworkId();

        if (input is CharacterInputMessage cmd)
        {
            if (ownedByMe)
            {
                bool interactNow = SharedPlayerMovement.ReadInput(cmd.Keys, InputFlags.Interact);
                if (interactNow && !_lastInteract)
                    HandleInteractEdge();
                _lastInteract = interactNow;
            }

            ApplyVisualYaw(cmd.CameraYaw);
            // Mount-only here. Dismount-with-velocity-inheritance fires from
            // OnPostPhysicsTick so it reads the vehicle's just-integrated velocity.
            UpdateRideFreezeState(isPostStep: false);
            if (TryAnchorToOwnedVehicle())
            {
                _hasLastAdvanceResult = false; // anchored — no input physics this tick
                return;
            }
            _lastAdvanceResult = RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd, phase: "live");
            _hasLastAdvanceResult = true;
            return;
        }

        // No input yet for this entity (first few ticks before any snapshot has
        // delivered an input for it). Still try to anchor — riding state is driven
        // by Authority, not input — but skip the physics advance.
        UpdateRideFreezeState(isPostStep: false);
        TryAnchorToOwnedVehicle();
        _hasLastAdvanceResult = false;
    }

    // Temporary per-render-frame trace of the rider body. Paired with
    // PredictionVisualSmoothing3D's [SMOOTH-FRAME] trace on the vehicle, the
    // combined log lets us reconstruct what the rider's camera sees: rider
    // body Y/Z (== camera origin) vs vehicle visual Y/Z (what's drawn), per
    // render frame.
    //
    // posFti is the body's FTI-interpolated pose — what Camera3D (a child
    // of this body) actually renders the world from. If FTI is on for this
    // body AND for the vehicle body, posFti for the rider and visFti for
    // the vehicle should lerp in lockstep, so the vehicle stays at a
    // constant offset from the camera each render frame (no oscillation).
    // If posFti == pos (FTI off / not tracked), the camera renders the
    // world un-interpolated and any FTI-lerped object oscillates against it.
    // Cached lookup of the deepest rendered node in the player's mesh chain
    // (a MeshInstance3D under KnightRig/Rig/Skeleton3D). The smoother
    // controls KnightRig.GlobalPosition; the actual on-screen pixels come
    // from the MeshInstance3D's interpolated transform after FTI walks the
    // KnightRig → Skeleton3D → MeshInstance3D chain. Logging that node's
    // GetGlobalTransformInterpolated() exposes the value the renderer
    // actually submits to the RenderingServer (via fti_update_servers_xform
    // in scene_tree_fti.cpp:585), so any discrepancy between
    // KnightRig.GlobalPosition (what the smoother controls) and what's
    // visible in the MP4 shows up as a difference between visFti (the
    // smoother's target) and meshFti (the rendered transform).
    private MeshInstance3D _deepestMesh;

    private MeshInstance3D FindDeepestMesh(Node n)
    {
        if (n is MeshInstance3D m) return m;
        foreach (Node child in n.GetChildren())
        {
            var f = FindDeepestMesh(child);
            if (f != null) return f;
        }
        return null;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        var body = _predictionRb?.Body;
        if (body == null) return;
        ulong physFrame = Engine.GetPhysicsFrames();
        Vector3 pos = body.GlobalPosition;
        Vector3 posFti = body.GetGlobalTransformInterpolated().Origin;
        bool ftiSame = pos.IsEqualApprox(posFti);
        double interpFrac = Engine.GetPhysicsInterpolationFraction();

        // RENDERED transform — read the deepest mesh node's interpolated
        // global position. This is what gets uploaded to the rendering
        // server and shown in the MP4. If it differs from KnightRig's
        // visFti (read by the smoother), something downstream in the
        // KnightRig → Skeleton → MeshInstance chain is offsetting the
        // visible pixels in a way the smoother doesn't know about.
        if (_deepestMesh == null && _knightRig != null) _deepestMesh = FindDeepestMesh(_knightRig);
        if (_deepestMesh != null)
        {
            Vector3 meshPos = _deepestMesh.GlobalPosition;
            Vector3 meshFti = _deepestMesh.GetGlobalTransformInterpolated().Origin;
            Vector3 knightRigFti = _knightRig != null ? _knightRig.GetGlobalTransformInterpolated().Origin : Vector3.Zero;
            int clientTick = ClientManager.Instance?
                .GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?
                .GetCurrentTick() ?? -1;
            MonkeLogger.Debug($"[RIDER-RENDERED] eid={EntityId} pf={physFrame} clientTick={clientTick} pif={interpFrac:F3} bodyFti=({posFti.X:F5},{posFti.Y:F5},{posFti.Z:F5}) knightRigFti=({knightRigFti.X:F5},{knightRigFti.Y:F5},{knightRigFti.Z:F5}) meshNode={_deepestMesh.Name} meshFti=({meshFti.X:F5},{meshFti.Y:F5},{meshFti.Z:F5}) meshRaw=({meshPos.X:F5},{meshPos.Y:F5},{meshPos.Z:F5})");
        }
        // Capture the on-screen render-position of the actual mesh, not just
        // the KnightRig parent. The Running_A animation drives the skeleton
        // bones (no root_motion_track is set on the AnimationTree, so the
        // animation's root translation is APPLIED to bone positions, which
        // moves the rendered mesh independently from KnightRig.GlobalPosition).
        // Logging Skeleton3D.GlobalPosition and the Skeleton's root-bone-0
        // global pose tells us where pixels actually land in the MP4 — the
        // value the smoother controls is KnightRig.GlobalPosition, but the
        // user's eye reads the bones.
        var skeleton = _knightRig?.GetNodeOrNull<Skeleton3D>("Rig/Skeleton3D");
        if (skeleton != null)
        {
            Vector3 skelPos = skeleton.GlobalPosition;
            Vector3 skelPosFti = skeleton.GetGlobalTransformInterpolated().Origin;
            // Bone 0 = root bone in glTF imports. Its local pose is animated;
            // its world pose = skeleton.global * bone-pose-transform.
            // Sample several bones to see which one drives mesh motion.
            // glTF imports typically: bone 0 = armature root (identity),
            // bone 1 = hips (animated for running stride).
            Transform3D bone0Pose = skeleton.GetBoneGlobalPose(0);
            Vector3 bone0World = (skeleton.GlobalTransform * bone0Pose).Origin;
            Transform3D bone1Pose = skeleton.GetBoneGlobalPose(1);
            Vector3 bone1World = (skeleton.GlobalTransform * bone1Pose).Origin;
            int clientTick = ClientManager.Instance?
                .GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?
                .GetCurrentTick() ?? -1;
            MonkeLogger.Debug($"[RIDER-SKELETON] eid={EntityId} pf={physFrame} clientTick={clientTick} body=({pos.X:F4},{pos.Y:F4},{pos.Z:F4}) knightRig=({_knightRig.GlobalPosition.X:F4},{_knightRig.GlobalPosition.Y:F4},{_knightRig.GlobalPosition.Z:F4}) skel=({skelPos.X:F4},{skelPos.Y:F4},{skelPos.Z:F4}) skelFti=({skelPosFti.X:F4},{skelPosFti.Y:F4},{skelPosFti.Z:F4}) bone0World=({bone0World.X:F4},{bone0World.Y:F4},{bone0World.Z:F4}) bone1World=({bone1World.X:F4},{bone1World.Y:F4},{bone1World.Z:F4}) knightRigYaw={_knightRig.Rotation.Y:F4}");
        }
        MonkeLogger.Debug($"[RIDER-FRAME] eid={EntityId} pf={physFrame} pif={interpFrac:F3} pos=({pos.X:F5},{pos.Y:F5},{pos.Z:F5}) posFti=({posFti.X:F5},{posFti.Y:F5},{posFti.Z:F5}) ftiSame={ftiSame} frozen={body.Freeze} interpMode={body.PhysicsInterpolationMode}");
    }

    // F-key rising-edge: if we own a vehicle, release it; otherwise look for the
    // closest unowned vehicle within range and request authority over it. The server
    // is the final arbiter via OwnershipPolicy — local code just sends the request.
    private void HandleInteractEdge()
    {
        var clientMgr = ClientManager.Instance;
        if (clientMgr == null) return;
        int myId = clientMgr.GetNetworkId();

        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction && entity.Authority == myId)
            {
                clientMgr.ReleaseAuthority(entity.EntityId);
                return;
            }
        }

        var body = _predictionRb?.Body;
        if (body == null) return;
        Vector3 myPos = body.GlobalPosition;
        LocalVehiclePrediction closest = null;
        float closestDistSq = InteractRangeSquared;
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction vp && vp.Authority == 0)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(vp);
                if (root == null) continue;
                float dSq = root.GlobalPosition.DistanceSquaredTo(myPos);
                if (dSq < closestDistSq) { closestDistSq = dSq; closest = vp; }
            }
        }
        if (closest != null)
            clientMgr.RequestAuthority(closest.EntityId);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

    public override RigidbodyState GetSnapshotState()
    {
        var state = _predictionRb.SnapshotState();
        // Mirror ServerRigidPlayerStateSyncronizer.PackEntityState: while the body is
        // anchored to a vehicle (Freeze=Kinematic), Jolt derives a LinearVelocity from
        // the per-tick transform delta the anchor writes. That derived value is garbage
        // for prediction purposes — the body's visible motion is the anchor formula, not
        // the integrator. Recording it into _predictedStates would feed bogus velocities
        // into the resim path AND into the per-snapshot HasMisspredicted comparison if
        // the freeze guard there ever changed. Returning zero matches what the server
        // ships in its snapshot and what the predict-loop should reason about.
        var body = _predictionRb.Body;
        bool anchored = body != null && body.Freeze
                        && body.FreezeMode == RigidBody3D.FreezeModeEnum.Kinematic;
        if (anchored)
        {
            state.LinearVelocity = Vector3.Zero;
            state.AngularVelocity = Vector3.Zero;
        }
        return state;
    }

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override Vector3 ExtractAuthoritativeVelocity(IEntityStateData state) =>
        ((EntityStateMessage)state).Velocity;

    public override Quaternion ExtractAuthoritativeRotation(IEntityStateData state) =>
        ((EntityStateMessage)state).Rotation;

    public override string DescribeMispredictTrigger(IEntityStateData authoritativeState, RigidbodyState savedState)
    {
        // Match this entity's HasMisspredicted thresholds — particularly the
        // tighter _maxVelocityDeviationSquared (0.25 ≈ 0.5 m/s) that's looser
        // than the prop / vehicle default (1.0 ≈ 1.0 m/s). Without this
        // override the base classifier would compare against the looser
        // default and report "below-thresholds" any time the player's
        // tighter velocity check actually fired — a silent classification
        // bug that surfaced in the tower_run analysis at tick 594.
        Vector3 authPos = ExtractAuthoritativePosition(authoritativeState);
        Vector3 authVel = ExtractAuthoritativeVelocity(authoritativeState);
        Quaternion authRot = ExtractAuthoritativeRotation(authoritativeState);
        bool posOver = (authPos - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared;
        bool velOver = (authVel - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared;
        bool rotOver = authRot.AngleTo(savedState.Rotation) > Mathf.DegToRad(5f);
        return MispredictTriggerString.Format(posOver, velOver, rotOver);
    }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        // While anchored to a vehicle the body is Freeze=Kinematic and its pose is
        // fully derived from vehicle.GlobalPosition + RideOffset every tick. Any
        // pos/vel/rot delta against the snapshot is just the vehicle's cross-process
        // Jolt drift bleeding through the anchor formula — reconciling would just
        // write the server's pose only for the next anchor tick to overwrite it.
        if (_predictionRb?.Body?.Freeze == true) return false;

        EntityStateMessage state = (EntityStateMessage)receivedState;
        float posDeltaSq = (state.Position - savedState.Position).LengthSquared();
        float velDeltaSq = (state.Velocity - savedState.LinearVelocity).LengthSquared();
        float rotDelta = state.Rotation.AngleTo(savedState.Rotation);

        // Remote (non-locally-owned) players are predicted using the LAST input
        // the server forwarded for them via GameSnapshotMessage.Inputs[]. That
        // cache is stale by up to N ticks (snapshot interval). During those
        // stale ticks the observer applies an input that the server is no
        // longer applying, so the predicted velocity drifts away from the
        // authoritative value by ~accel × stale_ticks × dt — for our players
        // that's up to ~2.5 m/s (50 m/s² × 3 ticks × 0.0167 s) on every input
        // change. The 0.5 m/s velocity threshold catches every direction
        // change as a misprediction even though the position diff is < 2 cm.
        //
        // Loosen the threshold for non-local entities so the next hard reconcile
        // path handles small drift instead of firing a full rollback+resim on
        // every snapshot. Local players keep the tight threshold so their
        // prediction is still snapped on real divergence (wall-hits, etc.).
        bool ownedByMe = Authority == ClientManager.Instance?.GetNetworkId();
        // Remote (non-locally-owned) players are predicted using the LAST input
        // the server forwarded for them via GameSnapshotMessage.Inputs[]. That
        // cache lags the server's actual input by snapshot RTT/2 + processing
        // delay (≈ 5 ticks at LAN latency). During those stale ticks the
        // observer applies an input the server is no longer applying, so its
        // predicted velocity drifts from the authoritative value by ~accel ×
        // stale_ticks × dt — and during continuous direction changes (e.g.
        // walking in a circle, sustained yaw rotation, square + jumps) the
        // velocity diff stays around 1–3 m/s indefinitely.
        //
        // The tight 0.5 m/s velocity threshold catches every such diff as a
        // misprediction, causing constant rollback+resim cycles (observed as
        // 50+ mispredicts in 20 s of normal walking on the observer side).
        // The fix:
        //   - For OWNED players: keep the tight thresholds — deterministic
        //     prediction from local input should match the server's state.
        //   - For REMOTE players: skip the velocity check entirely and loosen
        //     the position check. Position is the meaningful signal anyway
        //     (collisions, walls, fall-through) and the stale-input velocity
        //     drift naturally converges as the cached input refreshes each
        //     snapshot — the next reconcile when the drift crosses the
        //     position threshold pulls everything back into lockstep.
        float posThresholdSq;
        float velThresholdSq;
        bool checkVel;
        if (ownedByMe)
        {
            posThresholdSq = _maxDeviationAllowedSquared;
            velThresholdSq = _maxVelocityDeviationSquared;
            checkVel = true;
        }
        else
        {
            // 1.0 m position threshold for remotes — covers ~12 ticks of
            // stale-input drift at 5 m/s max run speed plus the larger
            // transient at the instant of a direction change (when the
            // server has just applied a new input that has not yet reached
            // the cache). Real collision divergence still produces > 1 m
            // diffs in practice (e.g. server hits a wall the client missed).
            posThresholdSq = _maxDeviationAllowedSquared * 25f;
            velThresholdSq = 0f; // unused
            checkVel = false;
        }

        bool tripPos = posDeltaSq > posThresholdSq;
        bool tripVel = checkVel && velDeltaSq > velThresholdSq;
        bool tripRot = rotDelta > Mathf.DegToRad(_maxRotationDeviationDegrees);
        if (tripPos || tripVel || tripRot)
        {
            MonkeLogger.Debug($"[RIDER-MISPREDICT-TRIP] tick={tick} eid={EntityId} ownedByMe={ownedByMe} tripPos={tripPos} tripVel={tripVel} tripRot={tripRot} posΔ²={posDeltaSq:F6}/{posThresholdSq:F6} velΔ²={velDeltaSq:F6}/{velThresholdSq:F6} rotΔ={rotDelta:F6}rad/{Mathf.DegToRad(_maxRotationDeviationDegrees):F6}rad savedVel=({savedState.LinearVelocity.X:F3},{savedState.LinearVelocity.Y:F3},{savedState.LinearVelocity.Z:F3}) authVel=({state.Velocity.X:F3},{state.Velocity.Y:F3},{state.Velocity.Z:F3})");
            return true;
        }
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalRigidPlayer eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input)
    {
        // Rollback resim receives per-entity input from ClientPredictionManager —
        // local input for the locally-driven player, cached server-supplied input
        // for remote players. Apply physics for both so rollback resim of remote
        // entities tracks the server's resim. The phase tag on the produced
        // [PHYS-RIGIDPLAYER] log line distinguishes these calls from live-tick ones
        // so a misprediction-triggered burst of resims doesn't visually merge with
        // the live tick that recorded the predicted state in the first place.
        if (input is CharacterInputMessage cmd)
        {
            ApplyVisualYaw(cmd.CameraYaw);
            if (TryAnchorToOwnedVehicle()) return;
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd, phase: "resim");
            return;
        }
        TryAnchorToOwnedVehicle();
    }

    // Sets the knight visual's local Y rotation to the camera yaw. Driven from
    // input.CameraYaw rather than the body's transform because the rigid body
    // has lock_rotation=true (it never carries yaw), and the rotation lives on
    // a camera-only RotationHelperY which observers don't share. Applied on the
    // live OnProcessTick path for every entity, so each client renders both
    // its own player and observed remote players turned to face the same
    // direction the owner's camera looks. Resim re-applies it for symmetry —
    // the latest live tick's value is what the renderer ends up reading.
    private void ApplyVisualYaw(float yaw)
    {
        if (_knightRig == null) return;
        // +PI offset: the Knight.glb's Rig has its forward along +Z, while a yaw of 0
        // corresponds to camera/movement forward of -Z (Godot convention). Without the
        // half-turn the rig renders facing the opposite direction of travel.
        var r = _knightRig.Rotation;
        r.Y = yaw + Mathf.Pi;
        _knightRig.Rotation = r;
    }

    public override void RestoreBodyState(RigidbodyState state) => _predictionRb.Reconcile(state);

    // While the local client owns a vehicle, skip player physics and pin the rigid body
    // on top of the vehicle so WASD drives the vehicle alone. Without this, the rigid
    // player and the vehicle both consume the same CharacterInputMessage every tick —
    // the player accelerates forward via RigidPlayerPhysics while the vehicle accelerates
    // forward via VehiclePhysics, the two collide and shove each other around, and
    // because their positions diverge cross-process Jolt nondeterminism reconciles
    // continuously. Visible as the vehicle being unsteerable / unresponsive.
    //
    // Critical: only anchor to a vehicle THIS client owns. Under the unified-prediction
    // model every client instantiates LocalVehiclePrediction for every vehicle (owned or
    // not), so a blanket "any vehicle exists" check would anchor the player to a
    // server-owned vehicle they haven't claimed — zeroing the player's velocity every
    // tick and making them un-walkable.
    // Live-path-only ride-state manager. Detects ride entry/exit and toggles
    // Body.Freeze accordingly. On dismount, copies the vehicle's current linear
    // velocity onto the player so they inherit the vehicle's momentum.
    //
    // Kept out of TryAnchorToOwnedVehicle so the resim path (which also calls
    // TryAnchorToOwnedVehicle) doesn't accidentally toggle freeze state based on
    // current Authority while replaying historical ticks. Freeze is owned by
    // the live tick; resim only writes pose.
    //
    // isPostStep gates which transition fires this call:
    //   - false (from OnProcessTick): handle MOUNT only. Freezing the body before
    //     this tick's SpaceStep keeps it pinned at the anchor pose for the integration.
    //   - true (from OnPostPhysicsTick): handle DISMOUNT only. Reading the vehicle's
    //     LinearVelocity here gets its post-step value for this tick — matching what
    //     ServerRigidPlayerStateSyncronizer.UpdateRideFreezeState(post-step) reads
    //     server-side. Both inherit the same vehicle velocity, so the player's
    //     post-dismount trajectory agrees on both processes and the chronic
    //     ~0.5 m/s Y-axis gap that previously tripped RIDER-MISPREDICT-TRIP a few
    //     seconds later goes away.
    private void UpdateRideFreezeState(bool isPostStep)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        var ridden = FindRiddenVehicle();
        bool ridingNow = ridden != null;

        if (!isPostStep && ridingNow && !body.Freeze)
        {
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            body.Freeze = true;
            _lastRiddenVehicleEntityId = ridden.EntityId;
            return;
        }

        if (isPostStep && !ridingNow && body.Freeze)
        {
            // Inherit the vehicle's linear velocity on dismount so jumping off a
            // moving vehicle preserves momentum. Look up the vehicle we were last
            // anchored to — it still exists post-release (only its Authority went
            // back to 0). Angular velocity is intentionally zeroed: an upright
            // capsule doesn't want to inherit the vehicle's spin.
            Vector3 inheritedVel = Vector3.Zero;
            if (_lastRiddenVehicleEntityId != 0)
            {
                foreach (var entity in EntitySpawner.Instance.ClientEntities)
                {
                    if (entity.EntityId == _lastRiddenVehicleEntityId && entity is LocalVehiclePrediction lvp)
                    {
                        inheritedVel = lvp.GetSnapshotState().LinearVelocity;
                        break;
                    }
                }
            }
            body.Freeze = false;
            body.LinearVelocity = inheritedVel;
            body.AngularVelocity = Vector3.Zero;
            _lastRiddenVehicleEntityId = 0;
        }
    }

    private LocalVehiclePrediction FindRiddenVehicle()
    {
        int playerOwner = this.Authority;
        if (playerOwner == 0) return null;
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction vp && vp.Authority == playerOwner)
                return vp;
        }
        return null;
    }

    private bool TryAnchorToOwnedVehicle()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        // Match on THIS player's Authority (the client that owns this rigid player),
        // not on the local client's networkId. This makes the anchor run on every
        // client for every player — drivers anchor themselves, observers anchor any
        // remote rider they're watching — so the rider trajectory stays locked to
        // the vehicle across the whole session, not just on the driver's screen.
        var vehiclePred = FindRiddenVehicle();
        if (vehiclePred == null) return false;
        var vehicleRoot = EntitySpawner.Instance.GetEntityRoot(vehiclePred);
        if (vehicleRoot == null) return false;
        ServerRigidPlayerStateSyncronizer.AnchorBodyToVehicle(body, vehicleRoot.GlobalPosition + RideOffset);
        return true;
    }
}
