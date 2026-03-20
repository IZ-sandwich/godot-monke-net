using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

[GlobalClass]
public partial class ServerInputReceiver : InternalServerComponent
{
    private readonly Dictionary<int, Dictionary<IServerSyncedEntity, IPackableElement>> _pendingInputs = [];
    private readonly Dictionary<IServerSyncedEntity, IPackableElement> _lastInputStored = []; // Used for re-running old inputs in case no new inputs are received

    public IPackableElement GetInputForEntityTick(IServerSyncedEntity serverEntity, int tick)
    {
        // TODO: use something else, not try/catch
        try
        {
            var input = _pendingInputs[tick][serverEntity];
            _lastInputStored[serverEntity] = input; // Mark this input as the last processed input, so we can re-use it if no inputs are received from the client
            return input;
        }
        catch
        {
            // Reuse last input
            if (_lastInputStored.TryGetValue(serverEntity, out IPackableElement input))
            {
                return input;
            }
            else
            {
                return null;
            }
        }
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is not PackedClientInputMessage inputCommand)
            return;

        // Find the ServerEntity target for this input command
        foreach (var entity in MonkeNetManager.Instance.EntitySpawner.Entities)
        {
            if (entity is IServerSyncedEntity serverEntity && clientId == serverEntity.Authority)
            {
                RegisterCommand(serverEntity, inputCommand);
            }
        }
    }

    private void RegisterCommand(IServerSyncedEntity serverEntity, PackedClientInputMessage inputCommand)
    {
        int offset = inputCommand.Inputs.Length - 1;
        foreach (IPackableElement input in inputCommand.Inputs)
        {
            int tick = inputCommand.Tick - (offset--);

            // Check if we have an entry for this tick
            if (!_pendingInputs.TryGetValue(tick, out Dictionary<IServerSyncedEntity, IPackableElement> value))
            {
                value = ([]);
                _pendingInputs.Add(tick, value);
            }

            value.TryAdd(serverEntity, input);
        }
    }

    public void DropOutdatedInputs(int currentTick)
    {
        foreach (int key in _pendingInputs.Keys)
        {
            if (key <= currentTick)
            {
                _pendingInputs.Remove(key);
            }
        }
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Input Receiver"))
        {
            ImGui.Text($"Input Queue {_pendingInputs.Count}");
        }
    }
}
