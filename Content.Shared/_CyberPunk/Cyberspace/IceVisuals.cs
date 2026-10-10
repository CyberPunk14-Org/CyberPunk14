using Robust.Shared.Serialization;

namespace Content.Shared._CyberPunk.Cyberspace;

[Serializable, NetSerializable]
public enum IceVisuals : byte
{
    Mode,
}

/// <summary>
/// How alert ICE shows itself to be, in its colour. Its program sets it; it's only for show.
/// </summary>
[Serializable, NetSerializable]
public enum IceMode : byte
{
    Patrolling,
    Searching,
    Engaging,
}
