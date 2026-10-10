using Robust.Shared.Physics.Events;

namespace Content.Shared._CyberPunk.Cyberspace;

/// <summary>
/// Lets the runners a firewall's pad passes walk through it, on the client as on the server, so walking
/// through is predicted.
/// </summary>
public sealed class CyberFirewallGateSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CyberFirewallGateComponent, PreventCollideEvent>(OnPreventCollide);
    }

    private void OnPreventCollide(Entity<CyberFirewallGateComponent> ent, ref PreventCollideEvent args)
    {
        if (ent.Comp.Passes.Contains(args.OtherEntity))
            args.Cancelled = true;
    }
}
