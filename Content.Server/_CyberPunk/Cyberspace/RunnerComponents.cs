namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// A netrunner's deck: used on a networked machine it jacks its holder into cyberspace, and used in the hand it
/// opens a practice grid.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberdeckComponent : Component;

/// <summary>
/// A network port in the wall, the usual way into cyberspace. On data cable it joins the network like a machine
/// with a UI.
/// </summary>
[RegisterComponent]
public sealed partial class AccessPointComponent : Component;

/// <summary>
/// Someone who has jacked in: their virtual body, whether they're in it now and how, and their dumpshock.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class NetrunnerComponent : Component
{
    /// <summary>
    /// Their virtual body, made on their first jack-in and kept, with its deck's disk, between runs.
    /// </summary>
    [ViewVariables]
    public EntityUid? Avatar;

    /// <summary>
    /// When they can jack in again after being thrown out.
    /// </summary>
    [ViewVariables]
    public TimeSpan DumpshockUntil;

    /// <summary>
    /// How they're jacked in, while they are.
    /// </summary>
    [ViewVariables]
    public JackIn? JackedIn;

    /// <summary>
    /// The colour their virtual body shows in, picked at random on their first jack-in.
    /// </summary>
    [ViewVariables]
    public Color? Tint;
}

/// <summary>
/// How a runner is jacked in: the deck in their hands, and the machine they came in through (plugged in beside
/// it, or through the air) or the practice grid they raised.
/// </summary>
public sealed record JackIn(EntityUid Deck, EntityUid? Device, bool Remote, int? Practice);

/// <summary>
/// A runner's virtual body in cyberspace. It's a computer, of kind Deck.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberAvatarComponent : Component
{
    /// <summary>
    /// The body whose mind walks it.
    /// </summary>
    [ViewVariables]
    public EntityUid Body;

    [ViewVariables]
    public EntityUid? JackOutAction;

    [ViewVariables]
    public EntityUid? OpenDeckAction;

    /// <summary>
    /// The machines they've breached this run.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Breached = new();

    /// <summary>
    /// When their deck can strike again.
    /// </summary>
    [ViewVariables]
    public TimeSpan StrikeReadyAt;

    /// <summary>
    /// When their ward comes down.
    /// </summary>
    [ViewVariables]
    public TimeSpan WardUntil;

    /// <summary>
    /// When they can raise a ward again.
    /// </summary>
    [ViewVariables]
    public TimeSpan WardReadyAt;
}

/// <summary>
/// A program held in a runner's virtual hand: used, it runs on their deck, or at whatever it's used on.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberProgramComponent : Component
{
    /// <summary>
    /// The file on their deck it runs, or the name of a program that comes with the deck.
    /// </summary>
    [ViewVariables]
    public string File = "";
}

/// <summary>
/// A runner's virtual ID. It holds no access of its own: it opens whatever the ID their real body wears opens.
/// </summary>
[RegisterComponent, Access(typeof(CyberspaceSystem))]
public sealed partial class CyberProxyIdComponent : Component;
