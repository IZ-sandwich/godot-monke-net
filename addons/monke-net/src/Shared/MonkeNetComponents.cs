using Godot;

namespace MonkeNet.Shared;

public class MonkeNetComponents
{
    public static T GetComponent<T>(Node node) where T : MonkeNetNode
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T comp) { return comp; }
        }

        return null;
    }
}
