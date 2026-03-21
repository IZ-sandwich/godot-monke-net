using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

/// <summary>
/// Input Producer Component can be linked with the ClientManager and will read and send inputs to the server each frame.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/gamepad_solid.png")]
public abstract partial class InputProducerComponent : ClientComponent
{
    [Export] private bool _current = true;

    public override void _Ready()
    {
        Current = true;
    }

    /// <summary>
    /// Return IPackableElement with input data.
    /// </summary>
    /// <returns></returns>
    public abstract IPackableElement GenerateCurrentInput();

    public bool Current
    {
        get { return _current; }
        set
        {
            if (_current) { MonkeNetConfig.Instance.InputProducer = this; }
            _current = value;
        }
    }
}
