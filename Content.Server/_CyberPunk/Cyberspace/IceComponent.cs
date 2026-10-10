using Content.Shared._CyberPunk.Cyberspace;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// ICE: a guard walking the paths of a network's region of cyberspace. It has no mind of its own; a program on one
/// of the network's computers sees what it sees and gives it its orders (the <c>ice_</c> kernel functions).
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class IceComponent : Component
{
    /// <summary>The computer whose program runs it.</summary>
    [ViewVariables]
    public EntityUid Computer;

    /// <summary>The program running it, by pid.</summary>
    [ViewVariables]
    public uint Pid;

    /// <summary>The region it guards, or the practice grid.</summary>
    [ViewVariables]
    public int? Region;

    [ViewVariables]
    public int? Practice;

    /// <summary>What runners' strikes have left of it: at none it derezzes.</summary>
    [ViewVariables]
    public int Integrity = CyberspaceSystem.MaxIntegrity;

    /// <summary>When it can strike again.</summary>
    [ViewVariables]
    public TimeSpan StrikeReadyAt;

    [ViewVariables]
    public IceTarget Target = IceTarget.Idle;

    [ViewVariables]
    public IceMode Mode;

    /// <summary>Where a completed trace says an intruder is, until its program next sees it.</summary>
    [ViewVariables]
    public (int X, int Y)? Alert;

    /// <summary>The tiles it's walking along, and the tile they lead to.</summary>
    public List<(int X, int Y)> Route = new();

    public (int X, int Y)? RouteGoal;
}

/// <summary>
/// Where ICE is going: nowhere, to a node's pad (by its machine), after a runner, or to a tile.
/// </summary>
public readonly record struct IceTarget(IceTargetKind Kind, EntityUid? Entity = null, int X = 0, int Y = 0)
{
    public static readonly IceTarget Idle = new(IceTargetKind.Idle);
}

public enum IceTargetKind : byte
{
    Idle,
    Node,
    Runner,
    Tile,
}

/// <summary>
/// A firewall: a switch that stands between a router and the machines behind it. In cyberspace its pad is shut
/// like a locked door to runners whose ID doesn't pass its reader, until they breach it.
/// </summary>
[RegisterComponent]
public sealed partial class FirewallComponent : Component;
