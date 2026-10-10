namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// What a node in cyberspace stands for.
/// </summary>
public enum CyberNodeKind : byte
{
    /// <summary>The backbone that joins every network, in the middle of the hub.</summary>
    Backbone,
    Router,
    Switch,
    Computer,
    DoorController,
    Camera,

    /// <summary>A machine with a UI of its own on the network, like a vending machine.</summary>
    Device,

    /// <summary>A network port in a wall, a way into cyberspace.</summary>
    AccessPoint,

    /// <summary>A runner's deck, on a spur beside the machine it came in through.</summary>
    Deck,

    /// <summary>A switch that shuts its pad to runners its reader doesn't pass.</summary>
    Firewall,
}

/// <summary>
/// A node in cyberspace: the pad of a machine on a network, or the backbone.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberNodeComponent : Component
{
    [ViewVariables]
    public CyberNodeKind Kind;

    /// <summary>
    /// The machine it stands for (a deck's is the runner's virtual body); none for the backbone and the parts of
    /// a practice grid that are only there to look at.
    /// </summary>
    [ViewVariables]
    public EntityUid? Machine;

    /// <summary>
    /// The region it's in; none for the backbone.
    /// </summary>
    [ViewVariables]
    public int? Region;
}

/// <summary>
/// The map that holds all of cyberspace.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberspaceMapComponent : Component;
