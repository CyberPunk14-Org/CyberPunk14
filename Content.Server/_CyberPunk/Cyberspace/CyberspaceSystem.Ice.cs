using System.Linq;
using System.Numerics;
using Content.Server._CyberPunk.Machines;
using Content.Server._CyberPunk.Wasm;
using Content.Shared._CyberPunk.Cyberspace;
using Content.Shared.Access.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// ICE, after Switchboard's <c>sb_sim/src/cyberspace/ice.rs</c>: guards that walk a network's region, each run by
/// a program on one of the network's computers. A program asks for ICE with <c>ice_start</c> and it appears on its
/// computer's pad. Before every machine tick the program is shown what its ICE sees; after it, the ICE carries
/// out the program's orders: walk to a node or a tile, chase a runner, strike one in reach. ICE keeps to its
/// region unless it's engaging, and keeps behind firewalls while patrolling. It goes quietly when its program ends, and its program is halted when a runner derezzes it or its
/// computer drops off the network.
/// </summary>
public sealed partial class CyberspaceSystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    /// <summary>What one strike of ICE takes from a runner, or half that through a ward.</summary>
    public const int IceDamage = 25;

    /// <summary>How far ICE sees, along a clear path, in tiles.</summary>
    public const float IceSight = 9f;

    /// <summary>How fast ICE walks, in tiles a second.</summary>
    public const float IceSpeed = 5.5f;

    public static readonly TimeSpan IceCooldown = TimeSpan.FromSeconds(1);

    private static readonly EntProtoId IcePrototype = "CyberIce";

    /// <summary>What ICE that throws a runner out does to their head.</summary>
    private static readonly DamageSpecifier IceBurn = new() { DamageDict = { ["Heat"] = 15 } };

    /// <summary>The computers whose programs were shown ICE last tick.</summary>
    private readonly HashSet<EntityUid> _iceHosts = new();

    /// <summary>The most tiles ICE chasing a runner off its network looks over for a way to them.</summary>
    private const int IceChaseSearch = 40000;

    /// <summary>
    /// Where ICE walks: a region of a network, or a practice grid. Its nodes are in the order of its graph's pads,
    /// and its links are pairs of indices into them. Zones say which side of the firewalls each node is on, if
    /// there are any.
    /// </summary>
    private sealed record IceArea(
        int? Region,
        int? Practice,
        CyberRect Rect,
        List<EntityUid> Nodes,
        List<(int A, int B)> Links,
        EntityUid Home,
        List<int>? Zones = null)
    {
        /// <summary>The nodes on the same side of the firewalls as its home: where patrolling ICE goes.</summary>
        public IEnumerable<EntityUid> HomeSide()
        {
            if (Zones is not { } zones)
                return Nodes;

            var home = zones[Nodes.IndexOf(Home)];
            return Nodes.Where((_, i) => zones[i] == home);
        }
    }

    private void InitializeIce()
    {
        SubscribeLocalEvent<WasmMachineComponent, IceStartedEvent>(OnIceStarted);
        SubscribeLocalEvent<WasmMachineComponent, IceOrdersEvent>(OnIceOrders);
        SubscribeLocalEvent<IceComponent, ExaminedEvent>(OnIceExamined);
    }

    /// <summary>
    /// The area a computer's ICE guards, from its pad: its network's region, or the practice grid it serves.
    /// </summary>
    private IceArea? AreaOf(EntityUid computer)
    {
        for (var r = 0; r < _regions.Count; r++)
        {
            var region = _regions[r];
            if (!region.Nodes.TryGetValue(computer, out var home)
                || region.Graph is not { } graph
                || RegionRect(r) is not { } rect)
            {
                continue;
            }

            var nodes = region.Pads.Select(m => region.Nodes.GetValueOrDefault(m)).ToList();
            return new IceArea(r, null, rect, nodes, graph.Links, home, graph.Zones);
        }

        for (var slot = 0; slot < _sandboxes.Length; slot++)
        {
            if (_sandboxes[slot] is not { } sandbox || sandbox.Server != computer)
                continue;

            var home = sandbox.Nodes.First(n => CompOrNull<CyberNodeComponent>(n)?.Machine == computer);
            return new IceArea(null, slot, CyberLayout.Tiles(CyberLayout.PracticeCells(slot)), sandbox.Nodes,
                SandboxLinks, home);
        }

        return null;
    }

    /// <summary>
    /// The id ICE programs know a node by: its machine's, or its own if it stands for no machine.
    /// </summary>
    private uint NodeId(EntityUid node)
    {
        var machine = CompOrNull<CyberNodeComponent>(node)?.Machine ?? node;
        return (uint) GetNetEntity(machine).Id;
    }

    private void OnIceStarted(Entity<WasmMachineComponent> ent, ref IceStartedEvent args)
    {
        foreach (var pid in args.Pids)
        {
            if (AreaOf(ent) is not { } area)
            {
                _machines.Announce(ent.AsNullable(), "[ice: can't start: this computer isn't on a working network]\n");
                continue;
            }

            var ice = Spawn(IcePrototype, Transform(area.Home).Coordinates);
            var comp = EnsureComp<IceComponent>(ice);
            comp.Computer = ent;
            comp.Pid = pid;
            comp.Region = area.Region;
            comp.Practice = area.Practice;
            _machines.Announce(ent.AsNullable(), "[ice: in cyberspace, guarding this network]\n");
        }
    }

    /// <summary>
    /// Tells the program of every ICE on a firewall's network that it was breached, and where.
    /// </summary>
    private void SignalBreach(EntityUid firewallNode)
    {
        var pos = _transform.GetWorldPosition(firewallNode);
        var at = ((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
        var query = EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out _, out var ice))
        {
            if (AreaOf(ice.Computer) is { } area && area.Nodes.Contains(firewallNode))
                _machines.SignalBreach(ice.Computer, ice.Pid, at);
        }
    }

    private void OnIceOrders(Entity<WasmMachineComponent> ent, ref IceOrdersEvent args)
    {
        var mine = new Dictionary<uint, Entity<IceComponent>>();
        var query = EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out var uid, out var ice))
        {
            if (ice.Computer == ent.Owner)
                mine[ice.Pid] = (uid, ice);
        }

        foreach (var order in args.Orders)
        {
            if (!mine.TryGetValue(order.Pid, out var ice) || AreaOf(ent) is not { } area)
                continue;

            switch (order.Kind)
            {
                case IceOrderKind.Go:
                    var node = Walkable(ice, area).FirstOrDefault(n => n.Valid && NodeId(n) == (uint) order.A);
                    if (node.Valid)
                        ice.Comp.Target = new IceTarget(IceTargetKind.Node, node);
                    break;
                case IceOrderKind.Chase:
                    if (RunnerById(order.A) is { } runner)
                        ice.Comp.Target = new IceTarget(IceTargetKind.Runner, runner);
                    break;
                case IceOrderKind.Attack:
                    if (RunnerById(order.A) is { } victim)
                        IceStrike(ice, victim);
                    break;
                case IceOrderKind.GoTo:
                    if (area.Rect.Contains(order.A, order.B))
                        ice.Comp.Target = new IceTarget(IceTargetKind.Tile, X: order.A, Y: order.B);
                    break;
                case IceOrderKind.Mode:
                    if (ice.Comp.Mode != (IceMode) order.A)
                        ice.Comp.Route.Clear();

                    ice.Comp.Mode = (IceMode) order.A;
                    _appearance.SetData(ice, IceVisuals.Mode, ice.Comp.Mode);
                    break;
            }
        }
    }

    /// <summary>
    /// A jacked-in runner's virtual body, by the id programs know it by.
    /// </summary>
    private EntityUid? RunnerById(int id)
    {
        return TryGetEntity(new NetEntity(id), out var avatar)
               && TryComp<CyberAvatarComponent>(avatar, out var comp)
               && IsJackedIn(comp.Body, out var walked)
               && walked == avatar
            ? avatar
            : null;
    }

    /// <summary>
    /// ICE strikes a runner in reach, once a second at most. At none left they're thrown out, with a burnt head
    /// unless it was a practice grid.
    /// </summary>
    private void IceStrike(Entity<IceComponent> ice, EntityUid avatar)
    {
        var now = _timing.CurTime;
        if (now < ice.Comp.StrikeReadyAt
            || !TryComp<CyberAvatarComponent>(avatar, out var victim)
            || !_transform.InRange(ice.Owner, avatar, StrikeReach))
        {
            return;
        }

        ice.Comp.StrikeReadyAt = now + IceCooldown;
        var warded = now < victim.WardUntil;
        victim.Integrity = Math.Max(0, victim.Integrity - (warded ? IceDamage / 2 : IceDamage));
        if (victim.Integrity > 0)
        {
            var text = Loc.GetString(warded ? "cyberspace-ice-strikes-warded" : "cyberspace-ice-strikes",
                ("integrity", victim.Integrity));
            _popup.PopupEntity(text, avatar, avatar);
            return;
        }

        if (ice.Comp.Practice != null)
        {
            JackOut(victim.Body, Loc.GetString("cyberspace-ice-practice-out"), true);
            return;
        }

        _damageable.TryChangeDamage(victim.Body, IceBurn, ignoreResistances: true);
        JackOut(victim.Body, Loc.GetString("cyberspace-ice-dumpshock"), true);
    }

    /// <summary>
    /// A deck strikes ICE: at none left it derezzes, and its program is halted.
    /// </summary>
    private void StrikeIce(Entity<CyberAvatarComponent> deck, Entity<IceComponent> ice)
    {
        ice.Comp.Integrity = Math.Max(0, ice.Comp.Integrity - DeckDamage);
        if (ice.Comp.Integrity > 0)
        {
            _popup.PopupEntity(Loc.GetString("cyberspace-strike-ice", ("integrity", ice.Comp.Integrity)), deck, deck);
            return;
        }

        Derez(ice, $"its ICE was derezzed by {Name(deck)}");
        _popup.PopupEntity(Loc.GetString("cyberspace-ice-derezzed"), deck, deck);
    }

    private void Derez(Entity<IceComponent> ice, string why)
    {
        _machines.Halt(ice.Comp.Computer, ice.Comp.Pid, why);
        QueueDel(ice);
    }

    private void OnIceExamined(Entity<IceComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("cyberspace-ice-examined", ("integrity", ent.Comp.Integrity)));
    }

    /// <summary>
    /// Walks every ICE towards what its program last asked, takes away the ICE whose programs have ended or whose
    /// computers have left, and shows each program what its ICE sees.
    /// </summary>
    private void TendIce(float frameTime, List<Entity<CyberAvatarComponent>> walking)
    {
        var all = new List<Entity<IceComponent>>();
        var query = EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out var uid, out var ice))
        {
            all.Add((uid, ice));
        }

        var views = new Dictionary<EntityUid, Dictionary<uint, IceView>>();
        foreach (var ice in all)
        {
            if (TerminatingOrDeleted(ice.Comp.Computer) || !_machines.IsRunning(ice.Comp.Computer, ice.Comp.Pid))
            {
                QueueDel(ice);
                continue;
            }

            if (AreaOf(ice.Comp.Computer) is not { } area
                || area.Region != ice.Comp.Region
                || area.Practice != ice.Comp.Practice)
            {
                Derez(ice, "its computer dropped off the network");
                continue;
            }

            WalkIce(ice, area, frameTime);
            if (!views.TryGetValue(ice.Comp.Computer, out var mine))
                views[ice.Comp.Computer] = mine = new Dictionary<uint, IceView>();

            mine[ice.Comp.Pid] = ViewOf(ice, area, walking);
        }

        foreach (var gone in _iceHosts.Where(h => !views.ContainsKey(h)))
        {
            _machines.SetIceViews(gone, new Dictionary<uint, IceView>());
        }

        _iceHosts.Clear();
        foreach (var (computer, mine) in views)
        {
            _machines.SetIceViews(computer, mine);
            _iceHosts.Add(computer);
        }
    }

    /// <summary>
    /// What an ICE's program sees: where it is, the nodes of its area, and the runners in it that it can see. A
    /// trace alert is seen once.
    /// </summary>
    private IceView ViewOf(Entity<IceComponent> ice, IceArea area, List<Entity<CyberAvatarComponent>> walking)
    {
        var pos = _transform.GetWorldPosition(ice);
        var nodes = Walkable(ice, area).Where(n => n.Valid && !TerminatingOrDeleted(n)).ToList();

        int? NearestNode(Vector2 at, float within)
        {
            int? best = null;
            var bestDistance = within;
            for (var i = 0; i < area.Nodes.Count; i++)
            {
                var node = area.Nodes[i];
                if (!node.Valid || TerminatingOrDeleted(node))
                    continue;

                var d = (_transform.GetWorldPosition(node) - at).Length();
                if (d <= bestDistance)
                {
                    best = i;
                    bestDistance = d;
                }
            }

            return best;
        }

        var here = NearestNode(pos, PadReach);
        var neighbours = new List<uint>();
        if (here is { } h)
        {
            foreach (var (a, b) in area.Links)
            {
                var other = a == h ? b : b == h ? a : -1;
                if (other >= 0 && area.Nodes[other].Valid && !TerminatingOrDeleted(area.Nodes[other]))
                    neighbours.Add(NodeId(area.Nodes[other]));
            }
        }

        var reader = CompOrNull<AccessReaderComponent>(ice.Comp.Computer);
        var runners = new List<IceRunner>();
        foreach (var runner in walking)
        {
            var at = _transform.GetWorldPosition(runner);
            var (x, y) = ((int) MathF.Floor(at.X), (int) MathF.Floor(at.Y));
            var mayChase = area.Rect.Contains(x, y) || ice.Comp.Mode == IceMode.Engaging;
            if (!mayChase || !_interaction.InRangeUnobstructed(ice.Owner, runner.Owner, IceSight))
                continue;

            var nearest = NearestNode(at, float.MaxValue) is { } n ? NodeId(area.Nodes[n]) : 0;
            runners.Add(new IceRunner(GetNetEntity(runner).Id,
                (int) nearest,
                reader != null && _access.IsAllowed(runner, ice.Comp.Computer, reader),
                (at - pos).Length() <= StrikeReach,
                x,
                y,
                Name(runner)));
        }

        runners.Sort((a, b) => a.Id.CompareTo(b.Id));
        var alert = ice.Comp.Alert;
        ice.Comp.Alert = null;
        return new IceView(ice.Comp.Integrity,
            (int) MathF.Floor(pos.X),
            (int) MathF.Floor(pos.Y),
            here is { } i ? NodeId(area.Nodes[i]) : -1,
            nodes.Select(NodeId).ToList(),
            neighbours,
            runners,
            alert);
    }

    /// <summary>
    /// The nodes ICE may be sent to: all of its area's while it's searching or engaging, and only those on its own
    /// side of the firewalls while it patrols.
    /// </summary>
    private static IEnumerable<EntityUid> Walkable(Entity<IceComponent> ice, IceArea area)
    {
        return ice.Comp.Mode == IceMode.Patrolling ? area.HomeSide() : area.Nodes;
    }

    /// <summary>
    /// The tiles of the firewalls' pads in an area, which patrolling ICE doesn't cross.
    /// </summary>
    private HashSet<(int X, int Y)> FirewallTiles(IceArea area)
    {
        var tiles = new HashSet<(int X, int Y)>();
        foreach (var node in area.Nodes)
        {
            if (!TryComp<CyberNodeComponent>(node, out var comp) || comp.Kind != CyberNodeKind.Firewall)
                continue;

            var centre = _transform.GetWorldPosition(node);
            var (cx, cy) = ((int) MathF.Floor(centre.X), (int) MathF.Floor(centre.Y));
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    tiles.Add((cx + dx, cy + dy));
                }
            }
        }

        return tiles;
    }

    /// <summary>
    /// Walks ICE along the paths towards its target, the shortest way. It keeps to its area and doesn't cross
    /// firewalls while patrolling, except to get back to its own side; engaging, it follows a runner anywhere,
    /// and once it stops it comes back to its area. It gives up on a tile it has no way to; a node or runner it
    /// keeps, standing still.
    /// </summary>
    private void WalkIce(Entity<IceComponent> ice, IceArea area, float frameTime)
    {
        var pos = _transform.GetWorldPosition(ice);
        var from = ((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
        var engaging = ice.Comp.Mode == IceMode.Engaging;
        var away = !area.Rect.Contains(from.Item1, from.Item2);

        var target = ice.Comp.Target;
        Vector2? goal = target.Kind switch
        {
            _ when away && !engaging => _transform.GetWorldPosition(area.Home),
            IceTargetKind.Node when target.Entity is { } node && !TerminatingOrDeleted(node)
                => _transform.GetWorldPosition(node),
            IceTargetKind.Runner when target.Entity is { } runner && RunnerById(GetNetEntity(runner).Id) != null
                => _transform.GetWorldPosition(runner),
            IceTargetKind.Tile => new Vector2(target.X + 0.5f, target.Y + 0.5f),
            _ => null,
        };

        var goalTile = goal is { } g ? ((int) MathF.Floor(g.X), (int) MathF.Floor(g.Y)) : default;
        var chasingOut = target.Kind == IceTargetKind.Runner && !area.Rect.Contains(goalTile.Item1, goalTile.Item2);
        if (goal == null || chasingOut && !engaging && !away)
        {
            ice.Comp.Target = IceTarget.Idle;
            return;
        }

        var returning = away && !engaging;
        var arrive = target.Kind == IceTargetKind.Runner && !returning ? 0.6f * StrikeReach : 0.06f;
        if ((goal.Value - pos).Length() <= arrive)
        {
            if (target.Kind != IceTargetKind.Runner && !returning)
                ice.Comp.Target = IceTarget.Idle;

            return;
        }

        if (ice.Comp.RouteGoal != goalTile || ice.Comp.Route.Count == 0)
        {
            // Off its area it may go anywhere; on it, it keeps to it, and patrolling it keeps off the firewalls
            // unless that's the only way back to its side.
            var rect = engaging || away ? (CyberRect?) null : area.Rect;
            var path = ice.Comp.Mode == IceMode.Patrolling && !away
                ? TilePath(rect, from, goalTile, FirewallTiles(area))
                  ?? (target.Kind == IceTargetKind.Node ? TilePath(rect, from, goalTile) : null)
                : TilePath(rect, from, goalTile);

            if (path == null)
            {
                ice.Comp.Route.Clear();
                ice.Comp.RouteGoal = null;
                if (target.Kind == IceTargetKind.Tile)
                    ice.Comp.Target = IceTarget.Idle;

                return;
            }

            ice.Comp.Route = path;
            ice.Comp.RouteGoal = goalTile;
        }

        var step = IceSpeed * frameTime;
        var route = ice.Comp.Route;
        while (step > 0f)
        {
            var next = route.Count <= 1 ? goal.Value : new Vector2(route[0].X + 0.5f, route[0].Y + 0.5f);
            var to = next - pos;
            var length = to.Length();
            if (length > step)
            {
                pos += to / length * step;
                break;
            }

            pos = next;
            step -= length;
            if (route.Count <= 1)
                break;

            route.RemoveAt(0);
        }

        _transform.SetCoordinates(ice, new EntityCoordinates(_mapUid!.Value, pos));
    }

    /// <summary>
    /// The shortest way over walkable tiles, from one tile to another, without the first; null if there's none.
    /// It keeps inside a rectangle and off some tiles, if given; with no rectangle it looks over at most
    /// <see cref="IceChaseSearch"/> tiles.
    /// </summary>
    private List<(int X, int Y)>? TilePath(CyberRect? rect, (int X, int Y) from, (int X, int Y) to,
        HashSet<(int X, int Y)>? blocked = null)
    {
        if (from == to)
            return new List<(int X, int Y)> { to };

        var came = new Dictionary<(int X, int Y), (int X, int Y)> { [from] = from };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(from);
        while (queue.TryDequeue(out var at) && came.Count < IceChaseSearch)
        {
            foreach (var (dx, dy) in SpurDirections)
            {
                var n = (at.X + dx, at.Y + dy);
                if (came.ContainsKey(n)
                    || rect is { } r && !r.Contains(n.Item1, n.Item2)
                    || blocked != null && blocked.Contains(n)
                    || !CyberLayout.Walkable(FloorAt(n.Item1, n.Item2)))
                {
                    continue;
                }

                came[n] = at;
                if (n == to)
                {
                    var path = new List<(int X, int Y)>();
                    for (var back = to; back != from; back = came[back])
                    {
                        path.Add(back);
                    }

                    path.Reverse();
                    return path;
                }

                queue.Enqueue(n);
            }
        }

        return null;
    }

    /// <summary>
    /// The ICE a deck can see, for its programs.
    /// </summary>
    private void AddIceTargets(Entity<CyberAvatarComponent> deck, List<DeckTarget> targets)
    {
        var deckPos = _transform.GetWorldPosition(deck);
        var query = EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out var uid, out var ice))
        {
            if (TerminatingOrDeleted(uid) || !_interaction.InRangeUnobstructed(deck.Owner, uid, DeckSight))
                continue;

            var distance = (_transform.GetWorldPosition(uid) - deckPos).Length();
            targets.Add(new DeckTarget(GetNetEntity(uid).Id, true, "ICE", ice.Integrity, distance <= StrikeReach,
                (int) distance));
        }
    }

    /// <summary>
    /// Brings the ICE of a region that moved to its computer's pad there.
    /// </summary>
    private void MoveIce(int r)
    {
        var query = EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out var uid, out var ice))
        {
            if (ice.Region != r || !_regions[r].Nodes.TryGetValue(ice.Computer, out var home))
                continue;

            ice.Route.Clear();
            ice.RouteGoal = null;
            _transform.SetCoordinates(uid, Transform(home).Coordinates);
        }
    }
}
