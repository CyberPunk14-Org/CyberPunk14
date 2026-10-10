using Content.Server._CyberPunk.Machines;
using Content.Shared._CyberPunk.Cyberspace;
using Content.Shared._CyberPunk.Machines;
using System.Linq;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.UserInterface;
using Robust.Server.GameStates;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// Using a node in cyberspace opens its machine's UI, as if the runner stood at the machine. A runner wears a
/// proxy of the ID their real body wears; a machine whose access they lack, or another runner's deck, opens once
/// they've stood at its node for <see cref="BreachTime"/>. The UI closes when they walk <see cref="NodeReach"/> from the node or jack out.
/// </summary>
/// <remarks>
/// The engine closes any UI whose user is on another map than its machine, unless the UI has no range. So while
/// a runner has a machine's UI open its range is taken away, and the range it had is checked here instead for
/// everyone else at it. The machine is sent to the runner's client, which can't otherwise see it.
/// </remarks>
public sealed partial class CyberspaceSystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;

    /// <summary>How far a runner may walk from a node before its machine's UI closes, in tiles.</summary>
    public const float NodeReach = 3.2f;

    /// <summary>How long a runner stands at a locked machine's node to breach it.</summary>
    public static readonly TimeSpan BreachTime = TimeSpan.FromSeconds(3);

    private sealed class RemoteUi
    {
        /// <summary>The range the UI had before a runner opened it.</summary>
        public float Range;

        /// <summary>The runners using it, the node each opened it from, and their sessions.</summary>
        public readonly Dictionary<EntityUid, (EntityUid Node, ICommonSession? Session)> Runners = new();
    }

    private readonly Dictionary<(EntityUid Machine, Enum Key), RemoteUi> _remoteUis = new();

    /// <summary>
    /// How long a machine stays sent to a runner's client after its UI closes, so the client hears that it closed
    /// before the machine leaves its view.
    /// </summary>
    private static readonly TimeSpan PvsLinger = TimeSpan.FromSeconds(1);

    private readonly List<(EntityUid Machine, ICommonSession Session, TimeSpan Until)> _lingering = new();

    private void InitializeNodes()
    {
        SubscribeLocalEvent<CyberNodeComponent, ActivateInWorldEvent>(OnNodeActivate);
        SubscribeLocalEvent<CyberAvatarComponent, CyberBreachDoAfterEvent>(OnBreached);
        SubscribeLocalEvent<BoundUserInterfaceMessageAttempt>(OnRemoteUiMessageAttempt);
    }

    private void OnNodeActivate(Entity<CyberNodeComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !TryComp<CyberAvatarComponent>(args.User, out var avatar))
            return;

        args.Handled = true;
        UseNode((args.User, avatar), ent);
    }

    private void OnBreached(Entity<CyberAvatarComponent> ent, ref CyberBreachDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !TryComp<CyberNodeComponent>(args.Target, out var node)
            || node.Machine is not { } machine)
        {
            return;
        }

        args.Handled = true;
        ent.Comp.Breached.Add(machine);
        if (node.Kind == CyberNodeKind.Firewall)
            SignalBreach(args.Target.Value);

        _popup.PopupEntity(Loc.GetString("cyberspace-breached", ("node", args.Target.Value)), ent, ent);
        UseNode(ent, (args.Target.Value, node));
    }

    /// <summary>
    /// Opens the UI of a node's machine for a runner, or starts breaching it. Tells them why not.
    /// </summary>
    private void UseNode(Entity<CyberAvatarComponent> avatar, Entity<CyberNodeComponent> node)
    {
        if (node.Comp.Kind == CyberNodeKind.Deck && node.Comp.Machine == avatar.Owner)
        {
            _ui.OpenUi(avatar.Owner, MachineTerminalUiKey.Key, avatar.Owner);
            return;
        }

        if (node.Comp.Kind == CyberNodeKind.Firewall && node.Comp.Machine is { } firewall)
        {
            if (MayPass(avatar, firewall))
                _popup.PopupEntity(Loc.GetString("cyberspace-firewall-open", ("node", node.Owner)), avatar, avatar);
            else
                StartBreach(avatar, node);

            return;
        }

        if (node.Comp.Machine is not { } machine
            || TerminatingOrDeleted(machine)
            || UiKeyOf(machine) is not { } key)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-node-no-ui", ("node", node.Owner)), avatar, avatar);
            return;
        }

        if (!_power.IsPowered(machine))
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-device-dead", ("device", node.Owner)), avatar, avatar);
            return;
        }

        // Another runner's deck is nobody else's to open.
        var locked = node.Comp.Kind == CyberNodeKind.Deck
                     || TryComp<AccessReaderComponent>(machine, out var reader) && !_access.IsAllowed(avatar, machine, reader);

        if (locked && !avatar.Comp.Breached.Contains(machine))
        {
            StartBreach(avatar, node);
            return;
        }

        OpenRemoteUi(avatar, node, machine, key);
    }

    private void StartBreach(EntityUid avatar, EntityUid node)
    {
        var doAfter = new DoAfterArgs(EntityManager, avatar, BreachTime, new CyberBreachDoAfterEvent(), avatar,
            target: node)
        {
            BreakOnMove = true,
            NeedHand = false,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("cyberspace-breaching", ("node", node)), avatar, avatar);
    }

    /// <summary>
    /// Whether a firewall lets a runner through: they breached it, or their ID passes its reader.
    /// </summary>
    private bool MayPass(Entity<CyberAvatarComponent> avatar, EntityUid firewall)
    {
        return avatar.Comp.Breached.Contains(firewall)
               || !TryComp<AccessReaderComponent>(firewall, out var reader)
               || _access.IsAllowed(avatar, firewall, reader);
    }

    /// <summary>
    /// Opens each firewall's pad to the runners it lets through, and shuts it to the rest.
    /// </summary>
    private void TendFirewalls(List<Entity<CyberAvatarComponent>> walking)
    {
        var query = EntityQueryEnumerator<CyberFirewallGateComponent, CyberNodeComponent>();
        while (query.MoveNext(out var uid, out var gate, out var node))
        {
            var passes = node.Machine is { } firewall
                ? walking.Where(w => MayPass(w, firewall)).Select(w => w.Owner).ToHashSet()
                : new HashSet<EntityUid>();

            if (passes.SetEquals(gate.Passes))
                continue;

            var changed = new HashSet<EntityUid>(passes);
            changed.SymmetricExceptWith(gate.Passes);
            gate.Passes = passes;
            Dirty(uid, gate);
            foreach (var avatar in changed)
            {
                if (!TerminatingOrDeleted(avatar))
                    _physics.RegenerateContacts(avatar);
            }
        }
    }

    /// <summary>
    /// The UI a machine opens when used, or its terminal if it's a computer.
    /// </summary>
    private Enum? UiKeyOf(EntityUid machine)
    {
        if (TryComp<ActivatableUIComponent>(machine, out var aui) && aui.Key is { } key && _ui.HasUi(machine, key))
            return key;

        return HasComp<WasmMachineComponent>(machine) && _ui.HasUi(machine, MachineTerminalUiKey.Key)
            ? MachineTerminalUiKey.Key
            : null;
    }

    private void OpenRemoteUi(EntityUid avatar, EntityUid node, EntityUid machine, Enum key)
    {
        if (!_ui.TryGetInterfaceData(machine, key, out var data))
            return;

        if (!_remoteUis.TryGetValue((machine, key), out var remote))
        {
            remote = new RemoteUi { Range = data.InteractionRange };
            _remoteUis[(machine, key)] = remote;
            _ui.SetUi(machine, key, new InterfaceData(data) { InteractionRange = 0f });
        }

        if (remote.Runners.TryGetValue(avatar, out var old) && old.Session != null)
            _pvsOverride.RemoveSessionOverride(machine, old.Session);

        var session = CompOrNull<ActorComponent>(avatar)?.PlayerSession;
        remote.Runners[avatar] = (node, session);
        if (session != null)
            _pvsOverride.AddSessionOverride(machine, session);

        _ui.OpenUi(machine, key, avatar);
    }

    /// <summary>
    /// Anyone else at a machine whose UI a runner has open must still be in its range to use it.
    /// </summary>
    private void OnRemoteUiMessageAttempt(ref BoundUserInterfaceMessageAttempt args)
    {
        if (args.Cancelled
            || !_remoteUis.TryGetValue((args.Target, args.UiKey), out var remote)
            || remote.Runners.ContainsKey(args.Actor))
        {
            return;
        }

        if (remote.Range > 0f && !_interaction.InRangeAndAccessible(args.Actor, args.Target, remote.Range))
            args.Cancel();
    }

    /// <summary>
    /// Closes the UIs runners have walked away from, and those of anyone else out of range, and gives a machine
    /// its range back once no runner has its UI open.
    /// </summary>
    private void TendRemoteUis()
    {
        ReleaseLingering();

        foreach (var ((machine, key), remote) in new List<KeyValuePair<(EntityUid, Enum), RemoteUi>>(_remoteUis))
        {
            if (TerminatingOrDeleted(machine))
            {
                _remoteUis.Remove((machine, key));
                continue;
            }

            foreach (var (avatar, (node, session)) in new List<KeyValuePair<EntityUid, (EntityUid, ICommonSession?)>>(remote.Runners))
            {
                var open = _ui.IsUiOpen(machine, key, avatar);
                if (open && AtNode(avatar, node))
                    continue;

                if (open)
                    _ui.CloseUi(machine, key, avatar);

                if (session != null)
                    _lingering.Add((machine, session, _timing.CurTime + PvsLinger));

                remote.Runners.Remove(avatar);
            }

            if (remote.Runners.Count == 0)
            {
                if (_ui.TryGetInterfaceData(machine, key, out var data))
                    _ui.SetUi(machine, key, new InterfaceData(data) { InteractionRange = remote.Range });

                _remoteUis.Remove((machine, key));
                continue;
            }

            if (remote.Range <= 0f)
                continue;

            foreach (var user in new List<EntityUid>(_ui.GetActors(machine, key)))
            {
                if (!remote.Runners.ContainsKey(user)
                    && !_interaction.InRangeAndAccessible(user, machine, remote.Range))
                {
                    _ui.CloseUi(machine, key, user);
                }
            }
        }
    }

    /// <summary>
    /// Stops sending machines to the clients of runners whose UIs closed a while ago, unless they've opened them
    /// again since.
    /// </summary>
    private void ReleaseLingering()
    {
        for (var i = _lingering.Count - 1; i >= 0; i--)
        {
            var (machine, session, until) = _lingering[i];
            if (_timing.CurTime < until)
                continue;

            _lingering.RemoveAt(i);
            if (TerminatingOrDeleted(machine) || Reopened(machine, session))
                continue;

            _pvsOverride.RemoveSessionOverride(machine, session);
        }
    }

    private bool Reopened(EntityUid machine, ICommonSession session)
    {
        foreach (var ((uid, _), remote) in _remoteUis)
        {
            if (uid != machine)
                continue;

            foreach (var (_, (_, other)) in remote.Runners)
            {
                if (other == session)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a runner is still jacked in and within reach of a node.
    /// </summary>
    private bool AtNode(EntityUid avatar, EntityUid node)
    {
        return TryComp<CyberAvatarComponent>(avatar, out var comp)
               && IsJackedIn(comp.Body, out var walked)
               && walked == avatar
               && !TerminatingOrDeleted(node)
               && _transform.InRange(avatar, node, NodeReach);
    }
}
