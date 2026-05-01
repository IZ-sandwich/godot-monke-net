# Monke-Net Quickstart

Server-authoritative multiplayer addon for Godot 4 / C#. The server runs physics and holds authoritative state. Clients predict locally, send inputs, and reconcile against server snapshots.

---

## Repository Layout

```
addons/monke-net/   ← the addon (treat as read-only)
demo/               ← working example to copy from
templates/          ← bare-bones starting templates
```

---

## Core Concepts

### Three scenes per entity type

Every networked entity needs three Godot scenes registered in `MonkeNetConfig`:

| Scene | Spawned on | Purpose |
|---|---|---|
| **ServerScene** | Server (+ listen server) | Runs physics, receives input, produces state snapshots |
| **ClientAuthorityScene** | Owning client | Local prediction + reconciliation, has camera/input |
| **ClientDummyScene** | All other clients | Interpolates state received from snapshots |

Registration is done via the `EntitySpawnConfiguration` resource exported on the `MonkeNetConfig` node in your main scene (`demo/MainScene.tscn` → `MonkeNet/MonkeNetConfig`).

### Physics tick loop

MonkeNet disables Godot's automatic physics and steps it manually once per tick. **Do not use `_PhysicsProcess` for gameplay logic** — use the tick callbacks below instead. Jolt Physics is required.

---

## Key Files

### Addon internals (`addons/monke-net/src/`)

| File | What it does |
|---|---|
| `Shared/MonkeNetManager/MonkeNetManager.cs` | Singleton. Call `CreateServer`, `CreateClient`, or `CreateListenServer` here. |
| `Shared/Nodes/MonkeNetConfig.cs` | Config node in your scene. Holds entity spawn configs and the active `InputProducer`. |
| `Shared/Entities/EntitySpawner.cs` | Instantiates entity scenes, wires `NetworkBehaviour`, separates collision layers in listen-server mode. |
| `ServerSide/ServerManager.cs` | Drives the server tick loop, calls `OnProcessTick` on server entities, steps physics. |
| `ServerSide/InternalComponents/ServerEntityManager.cs` | Spawns/destroys entities server-side and syncs world state to connecting clients. Internal component — access via `ServerManager.Instance.SpawnEntity<T>()`. |
| `ServerSide/InternalComponents/ServerInputReceiver.cs` | Buffers per-entity inputs from clients; called each tick by `ServerManager`. |
| `ClientSide/ClientManager.cs` | Drives the client tick loop, skips `SpaceStep` in listen-server mode. |
| `ClientSide/InternalComponents/ClientInputManager.cs` | Calls `InputProducer.GenerateCurrentInput()` each tick, packs and sends inputs. |
| `ClientSide/InternalComponents/ClientPredictionManager.cs` | Calls `OnProcessTick` on predicted entities, detects mispredictions, rolls back and re-simulates. |
| `ClientSide/InternalComponents/ClientSnapshotInterpolator.cs` | Buffers snapshots and calls `HandleStateInterpolation` on interpolated entities. |

### Node base classes to extend

| Base class | Extend for |
|---|---|
| `ServerStateSyncronizer` | Server entity — implement `OnProcessTick` (apply input, move body) and `PackEntityState` (return current state). |
| `ClientPredictedEntity` | Authority client entity — implement `OnProcessTick`, `HasMisspredicted`, `HandleReconciliation`, `ResimulateTick`. |
| `ClientInterpolatedEntity` | Dummy client entity — implement `HandleStateInterpolation` (lerp between past/future states). |
| `InputProducerComponent` | Input reader — implement `GenerateCurrentInput()`, call `base._Ready()` to register with `MonkeNetConfig`. |

### Demo files to copy from (`demo/`)

| File | Start here when making |
|---|---|
| `demo/players/server_player/PlayerStateSyncronizer.cs` | Any server entity |
| `demo/players/local_player/LocalPlayerPrediction.cs` | Any predicted client entity |
| `demo/players/dummy_player/DummyStateInterpolation.cs` | Any interpolated client entity |
| `demo/players/local_player/PlayerInputProducer.cs` | Any input producer |
| `demo/NetworkMessages.cs` | Game-specific input/state message structs |
| `demo/MainScene.cs` | How to start server/client/listen-server |

---

## Network Messages

Two interfaces used for serialization (binary, no reflection at runtime):

- **`IPackableElement`** — input messages and entity state. Implement `WriteBytes` / `ReadBytes` / `GetCopy`.
- **`IEntityStateData`** — same as above but specifically for per-entity state inside snapshots (also carries `EntityId`).

All structs implementing these interfaces are auto-discovered by `MessageSerializer` at startup via reflection. Define them in your own namespace (see `demo/NetworkMessages.cs`).

---

## Adding a New Entity Type

1. **Define messages** in your `NetworkMessages.cs`:
   - An input struct (`IPackableElement`) if the entity accepts input
   - A state struct (`IEntityStateData`) for snapshot data

2. **Create three scenes** (copy from `demo/players/`):
   - `ServerFoo.tscn` — extend `ServerStateSyncronizer`, set `collision_layer = 32768, collision_mask = 1`
   - `LocalFoo.tscn` — extend `ClientPredictedEntity`, set `collision_layer = 2, collision_mask = 3`
   - `DummyFoo.tscn` — extend `ClientInterpolatedEntity`, set `collision_layer = 2, collision_mask = 3`

3. **Register** a new `EntitySpawnConfiguration` resource on `MonkeNetConfig` with an `EntityType` byte and the three scenes.

4. **Spawn** from server code:
   ```csharp
   ServerManager.Instance.SpawnEntity<Node3D>(entityTypeByte, clientId);
   ```

5. **Request** from client code (client asks server to spawn for them):
   ```csharp
   ClientManager.Instance.MakeEntityRequest(entityTypeByte);
   ```

---

## Listen Server

Call `MonkeNetManager.Instance.CreateListenServer(port)` instead of separate `CreateServer`/`CreateClient`. Internally this runs both managers in the same process with separate `SceneMultiplayer` instances so their ENet peers don't clobber each other.

**Collision layers are required** when running listen-server — server and client entities share the same Jolt physics space. The addon overrides collision layers automatically at spawn time (`EntitySpawner`), but bake the correct values into `.tscn` files as well because Jolt assigns a body's broad-phase layer at `AddChild` time from the initial node values:

| Layer | Value | Who |
|---|---|---|
| Environment | `1` | Static geometry (default) |
| ClientPlayers | `2` | LocalPlayer, DummyPlayer |
| ServerPlayers | `32768` (layer 16) | ServerPlayer (hidden, mask=1 only) |

---

## Shared Physics Utility

`PhysicsUtils.MoveAndSlide(CharacterBody3D)` — use this instead of calling `body.MoveAndSlide()` directly. It normalizes delta time to the fixed network tick rate so simulation is consistent regardless of frame rate.
