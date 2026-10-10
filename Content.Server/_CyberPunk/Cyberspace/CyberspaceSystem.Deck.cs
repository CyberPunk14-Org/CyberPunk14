using System.Linq;
using Content.Server._CyberPunk.Machines;
using Content.Server._CyberPunk.Wasm;
using Content.Shared.Access.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// What a runner's deck does in cyberspace, after Switchboard's <c>sb_sim/src/cyberspace/deck.rs</c>: its
/// programs see the runners around it and strike them, raise wards, put programs in the runner's hands and push
/// files onto computers (the <c>deck_</c> kernel functions). A held program runs on the deck when used, at
/// whatever it's used on.
/// </summary>
public sealed partial class CyberspaceSystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;

    /// <summary>A runner's integrity when they jack in.</summary>
    public const int MaxIntegrity = 100;

    /// <summary>What one strike of a deck takes, or half that through a ward.</summary>
    public const int DeckDamage = 25;

    /// <summary>How close a deck must be to strike, in tiles.</summary>
    public const float StrikeReach = 1.25f;

    /// <summary>How far a deck sees, along a clear path, in tiles.</summary>
    public const float DeckSight = 9f;

    /// <summary>How close a runner stands to a computer's node to be at it, in tiles.</summary>
    public const float PadReach = 1.6f;

    public static readonly TimeSpan StrikeCooldown = TimeSpan.FromSeconds(2.0 / 3.0);
    public static readonly TimeSpan WardTime = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WardCooldown = TimeSpan.FromSeconds(6);

    private static readonly EntProtoId ProgramPrototype = "CyberProgram";

    private void InitializeDeck()
    {
        SubscribeLocalEvent<CyberAvatarComponent, DeckOrdersEvent>(OnDeckOrders);
        SubscribeLocalEvent<CyberProgramComponent, UseInHandEvent>(OnProgramUseInHand);
        SubscribeLocalEvent<CyberProgramComponent, AfterInteractEvent>(OnProgramAfterInteract);
        SubscribeLocalEvent<CyberProgramComponent, DroppedEvent>(OnProgramDropped);
    }

    /// <summary>
    /// The virtual bodies of every runner jacked in now.
    /// </summary>
    private List<Entity<CyberAvatarComponent>> WalkingRunners()
    {
        var walking = new List<Entity<CyberAvatarComponent>>();
        var query = EntityQueryEnumerator<NetrunnerComponent>();
        while (query.MoveNext(out var body, out _))
        {
            if (IsJackedIn(body, out var avatar) && TryComp<CyberAvatarComponent>(avatar, out var comp))
                walking.Add((avatar.Value, comp));
        }

        return walking;
    }

    /// <summary>
    /// Shows every jacked-in deck what it can see, for its programs this tick.
    /// </summary>
    private void TendDecks(List<Entity<CyberAvatarComponent>> walking)
    {
        var now = _timing.CurTime;
        foreach (var deck in walking)
        {
            var targets = new List<DeckTarget>();
            AddIceTargets(deck, targets);
            foreach (var other in walking)
            {
                if (other.Owner == deck.Owner || !_interaction.InRangeUnobstructed(deck.Owner, other.Owner, DeckSight))
                    continue;

                var distance = (_transform.GetWorldPosition(other) - _transform.GetWorldPosition(deck)).Length();
                targets.Add(new DeckTarget(GetNetEntity(other).Id,
                    false,
                    Name(other),
                    other.Comp.Integrity,
                    distance <= StrikeReach,
                    (int) distance));
            }

            targets.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));
            _machines.SetDeckView(deck.Owner,
                new DeckView(deck.Comp.Integrity,
                    now < deck.Comp.WardUntil,
                    now >= deck.Comp.StrikeReadyAt,
                    now >= deck.Comp.WardReadyAt,
                    targets));
        }
    }

    private void OnDeckOrders(Entity<CyberAvatarComponent> ent, ref DeckOrdersEvent args)
    {
        if (!IsJackedIn(ent.Comp.Body, out var walked) || walked != ent.Owner)
            return;

        var orders = args.Orders;
        if (orders.Ward)
            RaiseWard(ent);

        if (orders.Strike is { } target)
            Strike(ent, target);

        if (orders.Hold is { } file)
            Hold(ent, file);

        if (orders.Push is { } push)
            Push(ent, push);
    }

    private void RaiseWard(Entity<CyberAvatarComponent> ent)
    {
        var now = _timing.CurTime;
        if (now < ent.Comp.WardReadyAt)
            return;

        ent.Comp.WardUntil = now + WardTime;
        ent.Comp.WardReadyAt = now + WardCooldown;
        _popup.PopupEntity(Loc.GetString("cyberspace-ward-up"), ent, ent);
    }

    /// <summary>
    /// Strikes ICE or another runner in reach, by the id their deck's programs know them by. Runners can strike
    /// anyone.
    /// </summary>
    private void Strike(Entity<CyberAvatarComponent> ent, int id)
    {
        var now = _timing.CurTime;
        if (now < ent.Comp.StrikeReadyAt
            || !TryGetEntity(new NetEntity(id), out var target)
            || target == ent.Owner
            || !_transform.InRange(ent.Owner, target.Value, StrikeReach))
        {
            return;
        }

        if (TryComp<IceComponent>(target, out var ice))
        {
            ent.Comp.StrikeReadyAt = now + StrikeCooldown;
            StrikeIce(ent, (target.Value, ice));
            return;
        }

        if (!TryComp<CyberAvatarComponent>(target, out var victim)
            || !IsJackedIn(victim.Body, out var walked)
            || walked != target)
        {
            return;
        }

        ent.Comp.StrikeReadyAt = now + StrikeCooldown;
        var damage = now < victim.WardUntil ? DeckDamage / 2 : DeckDamage;
        victim.Integrity = Math.Max(0, victim.Integrity - damage);
        if (victim.Integrity == 0)
        {
            JackOut(victim.Body, Loc.GetString("cyberspace-cut-down"), true);
            _popup.PopupEntity(Loc.GetString("cyberspace-strike-cut-out", ("target", target.Value)), ent, ent);
            return;
        }

        _popup.PopupEntity(Loc.GetString("cyberspace-strike", ("target", target.Value), ("integrity", victim.Integrity)), ent, ent);
        _popup.PopupEntity(Loc.GetString("cyberspace-struck", ("striker", ent.Owner), ("integrity", victim.Integrity)),
            target.Value,
            target.Value);
    }

    /// <summary>
    /// Puts a program in a free hand of a runner's virtual body, the active one first.
    /// </summary>
    private void Hold(Entity<CyberAvatarComponent> ent, string file)
    {
        if (!HasComp<HandsComponent>(ent))
        {
            _machines.Announce(ent.Owner, "[hold: this deck has no hands]\n");
            return;
        }

        var program = Spawn(ProgramPrototype, Transform(ent).Coordinates);
        _meta.SetEntityName(program, file);
        Comp<CyberProgramComponent>(program).File = file;
        if (!_hands.TryPickupAnyHand(ent, program, checkActionBlocker: false, animate: false))
        {
            Del(program);
            _machines.Announce(ent.Owner, "[hold: both hands are full; drop something]\n");
            return;
        }

        _machines.Announce(ent.Owner, $"[hold: {file} is in your hand]\n");
    }

    /// <summary>
    /// Copies a file from a runner's deck onto a computer, if the runner may: it's nobody's, their body's ID
    /// opens it, or they stand at it with its lock breached.
    /// </summary>
    private void Push(Entity<CyberAvatarComponent> ent, DeckPush push)
    {
        var here = ComputerHere(ent);
        var target = push.Address == 0
            ? here
            : _machines.MachineAt(push.Address) is { } machine && (machine == here || push.Reachable)
                ? machine
                : null;

        var name = push.File[(push.File.LastIndexOf('/') + 1)..];
        string? refused = null;
        if (target is not { } to || !TryComp<WasmMachineComponent>(to, out var computer) || computer.Kind != DeviceKind.Computer)
        {
            refused = push.Address == 0
                ? "stand at a computer's pad, or give its address"
                : "no computer there that your deck reaches";
        }
        else if (TryComp<AccessReaderComponent>(to, out var reader)
                 && !(to == here && ent.Comp.Breached.Contains(to))
                 && !_access.IsAllowed(ent, to, reader))
        {
            refused = to == here
                ? "its lock isn't breached yet; wait at its pad"
                : "its card reader wants its owner's card";
        }
        else
        {
            refused = _machines.WriteFile((to, computer), name, push.Data) switch
            {
                DiskError.None => null,
                DiskError.Full => "its disk is full",
                _ => $"it can't take a file called {name}",
            };
        }

        _machines.Announce(ent.Owner, refused != null
            ? $"[push: refused, {refused}]\n"
            : push.Address == 0
                ? $"[push: {name} copied]\n"
                : $"[push: {name} copied to {MachineIo.FormatAddress(push.Address)}]\n");
    }

    /// <summary>
    /// The computer whose node a runner stands at, the nearest if there are several.
    /// </summary>
    private EntityUid? ComputerHere(EntityUid avatar)
    {
        var at = _transform.GetWorldPosition(avatar);
        return _lookup.GetEntitiesInRange<CyberNodeComponent>(Transform(avatar).Coordinates, PadReach)
            .Where(node => node.Comp.Kind == CyberNodeKind.Computer && node.Comp.Machine != null)
            .OrderBy(node => (_transform.GetWorldPosition(node) - at).LengthSquared())
            .Select(node => node.Comp.Machine)
            .FirstOrDefault();
    }

    private void OnProgramUseInHand(Entity<CyberProgramComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        RunHeld(args.User, ent, null);
    }

    private void OnProgramAfterInteract(Entity<CyberProgramComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled
            || args.Target is not { } target
            || !HasComp<CyberAvatarComponent>(target) && !HasComp<IceComponent>(target))
        {
            return;
        }

        args.Handled = true;
        RunHeld(args.User, ent, target);
    }

    private void OnProgramDropped(Entity<CyberProgramComponent> ent, ref DroppedEvent args)
    {
        _popup.PopupEntity(Loc.GetString("cyberspace-program-dropped", ("file", ent.Comp.File)), args.User, args.User);
        QueueDel(ent);
    }

    /// <summary>
    /// Runs a held program on the runner's deck, at a target if it was used on one. Only one runs at a time.
    /// </summary>
    private void RunHeld(EntityUid user, Entity<CyberProgramComponent> program, EntityUid? target)
    {
        if (!HasComp<CyberAvatarComponent>(user))
            return;

        var file = program.Comp.File;
        var args = target is { } at ? GetNetEntity(at).Id.ToString() : "";
        if (_machines.Launch(user, file, args) is { } why)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-program-wont-run", ("file", file), ("why", why)), user, user);
            return;
        }

        var self = target == null
            ? Loc.GetString("cyberspace-program-run", ("file", file))
            : Loc.GetString("cyberspace-program-run-at", ("file", file), ("target", target));
        var others = target == null
            ? Loc.GetString("cyberspace-program-run-others", ("user", user), ("file", file))
            : Loc.GetString("cyberspace-program-run-at-others", ("user", user), ("file", file), ("target", target));
        _popup.PopupEntity(self, user, user);
        _popup.PopupEntity(others, user, Filter.PvsExcept(user), true);
    }
}
