using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._CyberPunk.Machines;
using Content.Server.Mind;
using Content.Server.Power.EntitySystems;
using Content.Shared._CyberPunk.Cyberspace;
using Content.Shared._CyberPunk.Machines;
using Content.Shared.Actions;
using Content.Shared.Administration.Systems;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Ghost.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// Jacking in and out, after Switchboard's <c>sb_sim/src/cyberspace/runners.rs</c>. A cyberdeck used on a
/// networked machine jacks its holder in: plugged in when they're beside it, or through the air when it's in
/// sight within <see cref="HackRange"/>. Their mind visits a virtual body on the cyberspace map, a computer of
/// kind Deck, which joins the machine's network with a node on a spur beside the machine's pad. Their real
/// body stays where it was, helpless.
/// </summary>
/// <remarks>
/// A runner is thrown out, with <see cref="Dumpshock"/> before they can jack in again, when the deck leaves
/// their hands, the machine drops off the network or out of range or sight, the path dissolves under them, or
/// their body goes down. If their virtual body dies, they're thrown out and their real body collapses. Jacking
/// out by choice costs nothing.
/// </remarks>
public sealed partial class CyberspaceSystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private RejuvenateSystem _rejuvenate = default!;
    [Dependency] private SharedStaminaSystem _stamina = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private WasmMachineSystem _machines = default!;

    /// <summary>How far a runner's body may be from the machine they're plugged into, in tiles.</summary>
    public const float JackRange = 2.5f;

    /// <summary>How far a deck reaches a machine through the air, in sight, in tiles.</summary>
    public const float HackRange = 8f;

    /// <summary>How long a runner thrown out of cyberspace must wait to jack in again.</summary>
    public static readonly TimeSpan Dumpshock = TimeSpan.FromSeconds(10);

    private static readonly EntProtoId AvatarPrototype = "CyberAvatar";
    private static readonly EntProtoId JackOutAction = "ActionCyberJackOut";
    private static readonly EntProtoId OpenDeckAction = "ActionCyberOpenDeck";

    private void InitializeRunners()
    {
        SubscribeLocalEvent<CyberdeckComponent, AfterInteractEvent>(OnDeckAfterInteract);
        SubscribeLocalEvent<CyberdeckComponent, UseInHandEvent>(OnDeckUseInHand);
        SubscribeLocalEvent<CyberAvatarComponent, CyberJackOutActionEvent>(OnJackOutAction);
        SubscribeLocalEvent<CyberAvatarComponent, CyberOpenDeckActionEvent>(OnOpenDeckAction);
        SubscribeLocalEvent<CyberAvatarComponent, MindUnvisitedMessage>(OnAvatarUnvisited);
        SubscribeLocalEvent<CyberAvatarComponent, MobStateChangedEvent>(OnAvatarMobStateChanged);
        SubscribeLocalEvent<CyberAvatarComponent, GhostAttemptEvent>(OnAvatarGhostAttempt);
        SubscribeLocalEvent<NetrunnerComponent, ExaminedEvent>(OnRunnerExamined);
    }

    private void OnDeckAfterInteract(Entity<CyberdeckComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } target)
            return;

        // Only networked machines: anything with a pad, or anything that would have one on a working network.
        if (NodeOf(target) == null && !HasComp<WasmMachineComponent>(target) && !_machines.IsDevice(target))
            return;

        args.Handled = true;
        TryJackIn(args.User, ent, target, args.CanReach);
    }

    private void OnDeckUseInHand(Entity<CyberdeckComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        TryPractise(args.User, ent);
    }

    /// <summary>
    /// Jacks someone in through a networked machine with the deck in their hands: plugged in when they can
    /// reach it, or through the air when it's in sight within <see cref="HackRange"/>. Tells them why not.
    /// </summary>
    public bool TryJackIn(EntityUid user, EntityUid deck, EntityUid target, bool canReach)
    {
        var remote = !canReach;
        if (remote && !_examine.InRangeUnOccluded(user, target, HackRange))
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-beyond-deck", ("device", target)), user, user);
            return false;
        }

        if (!CanJackIn(user, out var why) || !CanJackInto(target, out why))
        {
            _popup.PopupEntity(why, user, user);
            return false;
        }

        var runner = EnsureComp<NetrunnerComponent>(user);
        var avatar = EnsureAvatar((user, runner));
        if (AttachSpur(avatar, target) is not { } at)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-no-signal", ("device", target)), user, user);
            return false;
        }

        _machines.SetVirtualHost(avatar, new VirtualHost(target));
        if (!Enter((user, runner), avatar, at, new JackIn(deck, target, remote, null)))
        {
            DetachSpur(avatar);
            _machines.SetVirtualHost(avatar, null);
            return false;
        }

        var self = remote ? "cyberspace-jack-in-remote" : "cyberspace-jack-in";
        _popup.PopupEntity(Loc.GetString(self, ("device", target)), avatar, avatar);
        _popup.PopupEntity(Loc.GetString(self + "-others", ("user", user), ("device", target)),
            user,
            Filter.PvsExcept(user),
            true);

        return true;
    }

    /// <summary>
    /// Jacks someone into a practice grid of their own with the deck in their hands. Tells them why not.
    /// </summary>
    public bool TryPractise(EntityUid user, EntityUid deck)
    {
        if (!CanJackIn(user, out var why))
        {
            _popup.PopupEntity(why, user, user);
            return false;
        }

        var slot = Array.FindIndex(_sandboxes, s => s == null);
        if (slot < 0)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-practice-full"), user, user);
            return false;
        }

        var runner = EnsureComp<NetrunnerComponent>(user);
        var avatar = EnsureAvatar((user, runner));
        if (BuildSandbox(slot, user) is not { } at)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-practice-failed"), user, user);
            return false;
        }

        _machines.SetVirtualHost(avatar, new VirtualHost(null, slot, 3));
        if (!Enter((user, runner), avatar, at, new JackIn(deck, null, false, slot)))
        {
            TeardownSandbox(slot);
            _machines.SetVirtualHost(avatar, null);
            return false;
        }

        _popup.PopupEntity(Loc.GetString("cyberspace-jack-in-practice"), avatar, avatar);
        _popup.PopupEntity(Loc.GetString("cyberspace-jack-in-practice-others", ("user", user)),
            user,
            Filter.PvsExcept(user),
            true);

        return true;
    }

    /// <summary>
    /// Whether someone is jacked in, and their virtual body.
    /// </summary>
    public bool IsJackedIn(EntityUid user, [NotNullWhen(true)] out EntityUid? avatar)
    {
        avatar = null;
        if (!TryComp<NetrunnerComponent>(user, out var runner) || runner.JackedIn == null)
            return false;

        avatar = runner.Avatar;
        return avatar != null;
    }

    /// <summary>
    /// A practice grid's training server, if the grid is up.
    /// </summary>
    public EntityUid? PracticeServer(int slot)
    {
        return _sandboxes[slot]?.Server;
    }

    /// <summary>
    /// Whether someone may jack in at all: they aren't already, and their head has stopped ringing.
    /// </summary>
    private bool CanJackIn(EntityUid user, out string why)
    {
        why = "";
        if (TryComp<NetrunnerComponent>(user, out var runner))
        {
            if (runner.JackedIn != null)
            {
                why = Loc.GetString("cyberspace-already-jacked-in");
                return false;
            }

            var left = runner.DumpshockUntil - _timing.CurTime;
            if (left > TimeSpan.Zero)
            {
                why = Loc.GetString("cyberspace-dumpshocked", ("seconds", (int) Math.Ceiling(left.TotalSeconds)));
                return false;
            }
        }

        if (!_mind.TryGetMind(user, out _, out _))
        {
            why = Loc.GetString("cyberspace-no-mind");
            return false;
        }

        if (_mapUid == null)
            EnsureCreated();

        return true;
    }

    /// <summary>
    /// Whether a machine can take a deck: it works and it's on a working network.
    /// </summary>
    private bool CanJackInto(EntityUid device, out string why)
    {
        why = "";
        if (!_power.IsPowered(device))
        {
            why = Loc.GetString("cyberspace-device-dead", ("device", device));
            return false;
        }

        if (NodeOf(device) == null)
        {
            why = Loc.GetString("cyberspace-no-signal", ("device", device));
            return false;
        }

        return true;
    }

    /// <summary>
    /// A runner's virtual body, made the first time they jack in.
    /// </summary>
    private EntityUid EnsureAvatar(Entity<NetrunnerComponent> runner)
    {
        if (runner.Comp.Avatar is { } existing && !TerminatingOrDeleted(existing))
            return existing;

        // Made on cyberspace's map so it boots like any machine, then kept out of the way until it's walked.
        var avatar = Spawn(AvatarPrototype, new EntityCoordinates(_mapUid!.Value, 0.5f, 0.5f));
        _meta.SetEntityName(avatar, Name(runner));
        TakeShape(avatar, runner);
        // Its mind only visits, so it would always read as catatonic.
        RemComp<MindExaminableComponent>(avatar);
        var comp = EnsureComp<CyberAvatarComponent>(avatar);
        comp.Body = runner;
        _actions.AddAction(avatar, ref comp.JackOutAction, JackOutAction);
        _actions.AddAction(avatar, ref comp.OpenDeckAction, OpenDeckAction);

        if (TryComp<WasmMachineComponent>(avatar, out var machine))
            _machines.SetRunning((avatar, machine), false);

        _transform.DetachEntity(avatar, Transform(avatar));
        runner.Comp.Avatar = avatar;
        return avatar;
    }

    /// <summary>
    /// Puts a runner's mind into their virtual body, whole again, standing at a spot in cyberspace.
    /// </summary>
    private bool Enter(Entity<NetrunnerComponent> runner, EntityUid avatar, EntityCoordinates at, JackIn how)
    {
        if (!_mind.TryGetMind(runner, out var mindId, out var mind))
            return false;

        _rejuvenate.PerformRejuvenate(avatar);
        if (TryComp<CyberAvatarComponent>(avatar, out var comp))
        {
            comp.Integrity = MaxIntegrity;
            comp.StrikeReadyAt = comp.WardUntil = comp.WardReadyAt = TimeSpan.Zero;
        }

        _transform.SetCoordinates(avatar, at);
        _transform.AttachToGridOrMap(avatar);
        if (TryComp<WasmMachineComponent>(avatar, out var machine))
            _machines.SetRunning((avatar, machine), true);

        runner.Comp.JackedIn = how;
        _mind.Visit(mindId, avatar, mind);
        return true;
    }

    /// <summary>
    /// Brings a runner back to their body. With <paramref name="shock"/>, they can't jack in again for a while,
    /// unless they were practising.
    /// </summary>
    public void JackOut(Entity<NetrunnerComponent?> runner, string why, bool shock)
    {
        if (!Resolve(runner, ref runner.Comp, false) || runner.Comp.JackedIn is not { } how)
            return;

        runner.Comp.JackedIn = null;
        if (runner.Comp.Avatar is { } avatar && !TerminatingOrDeleted(avatar))
        {
            if (TryComp<CyberAvatarComponent>(avatar, out var avatarComp))
                avatarComp.Breached.Clear();

            DetachSpur(avatar);
            _machines.SetVirtualHost(avatar, null);
            _machines.SetDeckView(avatar, null);
            if (TryComp<WasmMachineComponent>(avatar, out var machine))
                _machines.SetRunning((avatar, machine), false);

            _ui.CloseUis(avatar);
            if (TryComp<VisitingMindComponent>(avatar, out var visiting) && visiting.MindId is { } mindId)
                _mind.UnVisit(mindId);

            _transform.DetachEntity(avatar, Transform(avatar));
        }

        if (how.Practice is { } slot)
            TeardownSandbox(slot);
        else if (shock)
            runner.Comp.DumpshockUntil = _timing.CurTime + Dumpshock;

        _popup.PopupEntity(why, runner, runner, shock ? PopupType.MediumCaution : PopupType.Small);
    }

    private void OnJackOutAction(Entity<CyberAvatarComponent> ent, ref CyberJackOutActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        JackOut(ent.Comp.Body, Loc.GetString("cyberspace-jack-out"), false);
    }

    private void OnOpenDeckAction(Entity<CyberAvatarComponent> ent, ref CyberOpenDeckActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        _ui.OpenUi(ent.Owner, MachineTerminalUiKey.Key, ent.Owner);
    }

    private void OnAvatarUnvisited(Entity<CyberAvatarComponent> ent, ref MindUnvisitedMessage args)
    {
        // Their mind left some other way, like ghosting.
        JackOut(ent.Comp.Body, Loc.GetString("cyberspace-lost-connection"), false);
    }

    private void OnAvatarMobStateChanged(Entity<CyberAvatarComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead || !IsJackedIn(ent.Comp.Body, out var avatar) || avatar != ent.Owner)
            return;

        JackOut(ent.Comp.Body, Loc.GetString("cyberspace-avatar-died"), true);
        if (TryComp<StaminaComponent>(ent.Comp.Body, out var stamina))
            _stamina.TakeStaminaDamage(ent.Comp.Body, stamina.CritThreshold, stamina, ignoreResist: true);
    }

    private void OnAvatarGhostAttempt(Entity<CyberAvatarComponent> ent, ref GhostAttemptEvent args)
    {
        // Ghosting from here would leave the ghost wherever the avatar is put away, so they go back to their body
        // first and ghost from there. Succumbing in crit ghosts too, and kills the avatar.
        args.Cancelled = true;
        if (_mobState.IsCritical(ent))
            _mobState.ChangeMobState(ent, MobState.Dead);
        else
            JackOut(ent.Comp.Body, Loc.GetString("cyberspace-lost-connection"), false);
    }

    private void OnRunnerExamined(Entity<NetrunnerComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.JackedIn != null && args.IsInDetailsRange)
            args.PushMarkup(Loc.GetString("cyberspace-examined-jacked-in", ("ent", ent.Owner)));
    }

    /// <summary>
    /// Keeps every runner connected, or throws them out.
    /// </summary>
    private void TendRunners()
    {
        var query = EntityQueryEnumerator<NetrunnerComponent>();
        var thrown = new List<(EntityUid, string)>();
        while (query.MoveNext(out var uid, out var runner))
        {
            if (runner.JackedIn is not { } how)
                continue;

            if (Disconnected(uid, runner, how) is { } why)
                thrown.Add((uid, why));
        }

        foreach (var (uid, why) in thrown)
        {
            JackOut(uid, why, true);
        }
    }

    /// <summary>
    /// Why a runner has lost their connection, or null while they have it.
    /// </summary>
    private string? Disconnected(EntityUid body, NetrunnerComponent runner, JackIn how)
    {
        if (runner.Avatar is not { } avatar || TerminatingOrDeleted(avatar) || Transform(avatar).MapUid != _mapUid)
            return Loc.GetString("cyberspace-lost-connection");

        if (!_hands.IsHolding(body, how.Deck))
            return Loc.GetString("cyberspace-deck-torn");

        if (_mobState.IsIncapacitated(body) || _standing.IsDown(body))
            return Loc.GetString("cyberspace-body-down");

        if (how.Device is { } device)
        {
            var near = TerminatingOrDeleted(device) || NodeOf(device) == null
                ? false
                : how.Remote
                    ? _examine.InRangeUnOccluded(body, device, HackRange)
                    : _transform.InRange(body, device, JackRange);

            if (!near)
                return Loc.GetString(how.Remote ? "cyberspace-lost-remote" : "cyberspace-lost-device");
        }
        else if (how.Practice is { } slot && _sandboxes[slot] == null)
        {
            return Loc.GetString("cyberspace-lost-device");
        }

        var pos = Transform(avatar).LocalPosition;
        if (!CyberLayout.Walkable(FloorAt((int) Math.Floor(pos.X), (int) Math.Floor(pos.Y))))
            return Loc.GetString("cyberspace-path-dissolves");

        return null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        TendRunners();
        var walking = WalkingRunners();
        TendIce(frameTime, walking);
        TendDecks(walking);
        TendFirewalls(walking);
        MirrorIds();
        TendRemoteUis();
    }
}
