using Robust.Shared.GameStates;

namespace Content.Shared._CyberPunk.Cyberspace;

/// <summary>
/// A firewall's pad in cyberspace, shut like a locked door to every runner but those it lets through: their ID
/// passes the firewall's reader, or they breached it.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CyberFirewallGateComponent : Component
{
    /// <summary>The runners' virtual bodies it lets through.</summary>
    [AutoNetworkedField]
    public HashSet<EntityUid> Passes = new();
}
