// Entity spawning no longer requires a C# subclass.
//
// In the Godot editor, add an EntitySpawnConfiguration resource to the
// MonkeNetConfig.EntitySpawnConfiguration array for each entity type:
//
//   EntityType           — byte matching your EntityType enum value
//   ServerScene          — scene with ServerStateSyncronizer (+ NetworkBehaviour) child
//   ClientAuthorityScene — scene shown to the owning client (prediction)
//   ClientDummyScene     — scene shown to all other clients (interpolation)
//
// Example setup for three entity types (Player=0, Prop=1, Vehicle=2):
//
//   [EntitySpawnConfiguration]
//     EntityType=0, ServerScene=ServerPlayer.tscn,
//     ClientAuthorityScene=LocalPlayer.tscn, ClientDummyScene=DummyPlayer.tscn
//
//   [EntitySpawnConfiguration]
//     EntityType=1, ServerScene=ServerProp.tscn,
//     ClientAuthorityScene=ClientProp.tscn, ClientDummyScene=ClientProp.tscn
//     (same scene for both — all clients simulate props locally)
//
//   [EntitySpawnConfiguration]
//     EntityType=2, ServerScene=ServerVehicle.tscn,
//     ClientAuthorityScene=LocalVehicle.tscn, ClientDummyScene=DummyVehicle.tscn
//
// To spawn an entity from the server:
//   ServerManager.Instance.SpawnEntity<Node3D>(entityType, authorityClientId);
//
// To request a spawn from a client:
//   ClientManager.Instance.MakeEntityRequest(entityType);
//
// EntityType enum (define once alongside your NetworkMessages):
// public enum EntityType : byte { Player = 0, Prop = 1, Vehicle = 2 }
