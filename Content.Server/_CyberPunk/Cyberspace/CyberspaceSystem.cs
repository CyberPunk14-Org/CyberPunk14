using System.Linq;
using System.Numerics;
using Content.Server._CyberPunk.Machines;
using Content.Server._CyberPunk.Procgen;
using Content.Server._CyberPunk.Wasm;
using Content.Shared.GameTicking;
using Content.Shared.Gravity;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// Cyberspace, after Switchboard's <c>sb_sim/src/cyberspace.rs</c>: one map, made when the first network comes
/// up, holding a region for each routed network with a pad for every machine on it and a corridor for every
/// link, the bus between the regions and the hub with the backbone in the middle. A region is generated again
/// whenever its network changes: pull out a machine and its pad and corridors are gone.
/// </summary>
/// <remarks>
/// <para>
/// Unlike Switchboard's fixed grid of regions, each region is as big as its network needs and sits along a
/// street that grows as networks come up (<see cref="CyberLayout"/>). A network keeps its region, by its router,
/// while the router exists. When it outgrows the region, the region is made again somewhere it fits, and the
/// runners in it are moved along with it. Machines keep their slots while unplugged, so plugging one back in
/// puts it where it was.
/// </para>
/// <para>
/// Tiles don't stop anyone walking, so the tiles that can't be walked on next to ones that can are walled off
/// by invisible barriers: one entity per <see cref="Chunk"/>-tile chunk, with a box for each run of such tiles.
/// </para>
/// </remarks>
public sealed partial class CyberspaceSystem : EntitySystem
{
    [Dependency] private FixtureSystem _fixtures = default!;
    [Dependency] private ITileDefinitionManager _tileDefs = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private MetaDataSystem _meta = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    /// <summary>Practice regions, sealed off from everything.</summary>
    public const int Sandboxes = 8;

    /// <summary>Width of a chunk of barriers, in tiles.</summary>
    public const int Chunk = 16;

    private const int BarrierLayer = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable
                                            | CollisionGroup.HighImpassable | CollisionGroup.LowImpassable
                                            | CollisionGroup.BulletImpassable | CollisionGroup.InteractImpassable);

    private static readonly EntProtoId Barrier = "CyberspaceBarrier";

    private static readonly Dictionary<CyberFloor, string> TileIds = new()
    {
        [CyberFloor.Static] = "CyberStatic",
        [CyberFloor.Data] = "CyberData",
        [CyberFloor.Node] = "CyberNode",
        [CyberFloor.Bus] = "CyberBus",
    };

    private static readonly Dictionary<CyberNodeKind, EntProtoId> NodePrototypes = new()
    {
        [CyberNodeKind.Backbone] = "CyberNodeBackbone",
        [CyberNodeKind.Router] = "CyberNodeRouter",
        [CyberNodeKind.Switch] = "CyberNodeSwitch",
        [CyberNodeKind.Computer] = "CyberNodeComputer",
        [CyberNodeKind.DoorController] = "CyberNodeDoorController",
        [CyberNodeKind.Camera] = "CyberNodeCamera",
        [CyberNodeKind.Device] = "CyberNodeDevice",
        [CyberNodeKind.AccessPoint] = "CyberNodeAccessPoint",
        [CyberNodeKind.Deck] = "CyberNodeDeck",
        [CyberNodeKind.Firewall] = "CyberNodeFirewall",
    };

    private sealed class Region
    {
        /// <summary>The router of the network it belongs to.</summary>
        public EntityUid? Router;

        /// <summary>Its cells, while it has a place.</summary>
        public CyberRect? Cells;

        public RegionShape Shape = RegionShape.Smallest;

        /// <summary>Each machine's slot, kept while it's away.</summary>
        public readonly Dictionary<EntityUid, (int X, int Y)> Slots = new();

        public RegionGraph? Graph;

        /// <summary>The machine on each pad, in the order of the graph's pads.</summary>
        public List<EntityUid> Pads = new();

        /// <summary>Each machine's node.</summary>
        public Dictionary<EntityUid, EntityUid> Nodes = new();
    }

    private EntityUid? _mapUid;
    private CyberLayout? _layout;
    private readonly Dictionary<(int X, int Y), CyberFloor> _tiles = new();
    private ulong _seed;
    private readonly List<Region> _regions = new();

    /// <summary>The cells of street with bus laid on them so far.</summary>
    private (int West, int East) _street;

    private readonly Dictionary<(int, int), EntityUid> _barriers = new();
    private readonly HashSet<(int, int)> _dirtyChunks = new();

    /// <summary>The map holding cyberspace, once it's made.</summary>
    public EntityUid? MapUid => _mapUid;

    public override void Initialize()
    {
        base.Initialize();
        // Decks see where everyone is before their programs run.
        UpdatesBefore.Add(typeof(WasmMachineSystem));
        SubscribeLocalEvent<MachineNetworksRebuiltEvent>(OnNetworksRebuilt);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        InitializeAvatars();
        InitializeRunners();
        InitializeNodes();
        InitializeDeck();
        InitializeIce();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        if (_mapUid is { } map && !TerminatingOrDeleted(map))
            QueueDel(map);

        Reset();
    }

    private void Reset()
    {
        _mapUid = null;
        _layout = null;
        _tiles.Clear();
        _regions.Clear();
        _barriers.Clear();
        _dirtyChunks.Clear();
        _spurs.Clear();
        Array.Clear(_sandboxes);
    }

    /// <summary>
    /// The tile of cyberspace at a position, void where there's nothing.
    /// </summary>
    public CyberFloor FloorAt(int x, int y)
    {
        return _tiles.GetValueOrDefault((x, y));
    }

    /// <summary>
    /// The region a network has, by its router, if it has one.
    /// </summary>
    public int? RegionOf(EntityUid router)
    {
        var r = _regions.FindIndex(region => region.Router == router);
        return r < 0 ? null : r;
    }

    /// <summary>
    /// A region's tiles, if it has a place.
    /// </summary>
    public CyberRect? RegionRect(int region)
    {
        return _regions[region].Cells is { } cells ? CyberLayout.Tiles(cells) : null;
    }

    /// <summary>
    /// The node of a machine in cyberspace, if it has a pad.
    /// </summary>
    public EntityUid? NodeOf(EntityUid machine)
    {
        foreach (var region in _regions)
        {
            if (region.Nodes.TryGetValue(machine, out var node))
                return node;
        }

        // A deck's node is on its spur, not a pad.
        return _spurs.TryGetValue(machine, out var spur) ? spur.Node : null;
    }

    private void OnNetworksRebuilt(ref MachineNetworksRebuiltEvent ev)
    {
        if (_mapUid is { } existing && TerminatingOrDeleted(existing))
            Reset();

        if (_mapUid == null)
        {
            if (ev.Networks.Count == 0)
                return;

            Create();
        }

        Rebuild(ev.Networks);
        NameDecks();
    }

    /// <summary>
    /// Makes cyberspace if there's none yet, as for a practice grid before any network is up.
    /// </summary>
    private void EnsureCreated()
    {
        if (_mapUid is { } existing && TerminatingOrDeleted(existing))
            Reset();

        if (_mapUid == null)
            Create();
    }

    /// <summary>
    /// Makes the cyberspace map: the hub with the backbone in its middle, and no street yet.
    /// </summary>
    private void Create()
    {
        _layout = new CyberLayout();
        _seed = (ulong) (uint) _random.Next() << 32 | (uint) _random.Next();

        var mapUid = _map.CreateMap(out _);
        _mapUid = mapUid;
        _meta.SetEntityName(mapUid, "cyberspace");
        EnsureComp<CyberspaceMapComponent>(mapUid);
        EnsureComp<MapGridComponent>(mapUid);

        var gravity = EnsureComp<GravityComponent>(mapUid);
        gravity.Enabled = true;
        gravity.Inherent = true;
        Dirty(mapUid, gravity);

        var light = EnsureComp<MapLightComponent>(mapUid);
        light.AmbientLightColor = Color.FromHex("#C8D2FF");
        Dirty(mapUid, light);

        var hub = CyberLayout.Tiles(CyberLayout.HubCells);
        Paint(hub, Enumerable.Repeat(CyberFloor.Bus, hub.W * hub.H).ToArray());
        _street = (-CyberLayout.HubHalf, CyberLayout.HubHalf);
        FlushBarriers();

        var backbone = Spawn(NodePrototypes[CyberNodeKind.Backbone],
            new EntityCoordinates(mapUid, CyberLayout.Cell / 2 + 0.5f, CyberLayout.Cell / 2 + 0.5f));
        var node = EnsureComp<CyberNodeComponent>(backbone);
        node.Kind = CyberNodeKind.Backbone;
        _meta.SetEntityName(backbone, "city backbone");
    }

    /// <summary>
    /// Lays bus along the street as far as the layout says it now runs.
    /// </summary>
    private void ExtendStreet()
    {
        var (west, east) = (_layout!.StreetWest, _layout.StreetEast);
        if (west < _street.West)
            PaintBus(west, _street.West - 1);
        if (east > _street.East)
            PaintBus(_street.East + 1, east);

        _street = (Math.Min(west, _street.West), Math.Max(east, _street.East));
    }

    private void PaintBus(int fromCell, int toCell)
    {
        var rect = CyberLayout.Tiles(new CyberRect(fromCell, 0, toCell - fromCell + 1, 1));
        Paint(rect, Enumerable.Repeat(CyberFloor.Bus, rect.W * rect.H).ToArray());
    }

    /// <summary>
    /// Generates again every region whose network changed.
    /// </summary>
    private void Rebuild(List<MachineNetwork> networks)
    {
        if (_layout == null)
            return;

        // A network whose router is gone gives its region back.
        foreach (var region in _regions)
        {
            if (region.Router is { } router && TerminatingOrDeleted(router))
            {
                region.Router = null;
                region.Slots.Clear();
            }
        }

        // A network new to cyberspace takes a region nobody has, or a new one.
        var byRegion = new Dictionary<int, MachineNetwork>();
        foreach (var network in networks)
        {
            var r = RegionOf(network.Router);
            if (r == null)
            {
                r = _regions.FindIndex(region => region.Router == null && region.Cells == null);
                if (r < 0)
                {
                    _regions.Add(new Region());
                    r = _regions.Count - 1;
                }

                _regions[r.Value].Router = network.Router;
            }

            byRegion[r.Value] = network;
        }

        for (var r = 0; r < _regions.Count; r++)
        {
            RebuildRegion(r, byRegion.GetValueOrDefault(r));
        }

        FlushBarriers();
    }

    private void RebuildRegion(int r, MachineNetwork? network)
    {
        var region = _regions[r];
        if (network == null)
        {
            if (region.Cells is { } gone)
            {
                ClearTiles(CyberLayout.Tiles(gone));
                _layout!.Free(gone);
                region.Cells = null;
                region.Graph = null;
                region.Pads.Clear();
                region.Router = null;
                region.Slots.Clear();
                SyncNodes(r, null, new List<(EntityUid, (int X, int Y), PadKind)>());
                RestampSpurs(r);
            }

            return;
        }

        var switches = network.Hubs.Where(h => h != network.Router).ToList();
        var hosts = network.Hosts.Select(h => h.Machine).ToList();

        // A network that has outgrown its region, or has none yet, gets one big enough.
        CyberRect? moved = null;
        if (region.Cells == null || !region.Shape.Fits(hosts.Count, switches.Count))
        {
            // The old place is cleared once the new one is painted, so no chunk of the grid empties and fills
            // again in between.
            moved = region.Cells;
            if (moved is { } old)
                _layout!.Free(old);

            region.Shape = RegionShape.For(hosts.Count, switches.Count);
            region.Cells = _layout!.Place(region.Shape);
            region.Graph = null;
            ExtendStreet();
        }

        var shape = region.Shape;
        var pads = new List<(EntityUid Machine, (int X, int Y) Slot, PadKind Kind)>();
        var links = network.Links;
        var present = new HashSet<EntityUid>(switches.Concat(hosts)) { network.Router };
        var kept = region.Slots.Where(s => !present.Contains(s.Key)).Select(s => s.Value).ToHashSet();
        var held = new HashSet<(int, int)>();

        // Pads on slots: the router at the top, switches, then hosts near their switch. Machines keep
        // their slots.
        held.Add(shape.RouterSlot);
        pads.Add((network.Router, shape.RouterSlot, PadKind.Router));

        var switchSlots = shape.SwitchSlots;
        foreach (var hub in switches)
        {
            var slot = KeptSlot(region, hub, switchSlots, held) ?? FreeSlot(switchSlots, held, kept, null);
            if (slot is not { } s)
                continue;

            held.Add(s);
            pads.Add((hub, s, PadKind.Switch));
        }

        var hostSlots = shape.HostSlots;
        foreach (var host in hosts)
        {
            var hardware = FirstHardware(host, links, network.Hubs);
            (int, int)? near = null;
            foreach (var pad in pads)
            {
                if (pad.Machine == hardware)
                    near = pad.Slot;
            }

            var slot = KeptSlot(region, host, hostSlots, held) ?? FreeSlot(hostSlots, held, kept, near);
            if (slot is not { } s)
                continue;

            held.Add(s);
            pads.Add((host, s, PadKind.Host));
        }

        foreach (var (machine, slot, _) in pads)
        {
            region.Slots[machine] = slot;
        }

        var index = new Dictionary<EntityUid, int>();
        for (var i = 0; i < pads.Count; i++)
        {
            index[pads[i].Machine] = i;
        }

        var padLinks = new SortedSet<(int, int)>();
        foreach (var (a, b) in links)
        {
            if (index.TryGetValue(a, out var ia) && index.TryGetValue(b, out var ib))
                padLinks.Add((Math.Min(ia, ib), Math.Max(ia, ib)));
        }

        var graph = new RegionGraph
        {
            Pads = pads.Select(p => (p.Slot, p.Kind)).ToList(),
            Links = padLinks.ToList(),
            Gate = 0,
            Zones = Zones(pads.Select(p => HasComp<FirewallComponent>(p.Machine)).ToList(), padLinks),
        };

        var machines = pads.Select(p => p.Machine).ToList();
        if (region.Graph is { } current && current.Same(graph) && region.Pads.SequenceEqual(machines))
        {
            SyncNodes(r, network, pads);
            return;
        }

        region.Graph = graph;
        region.Pads = machines;
        var seed = new CyberRng(_seed ^ unchecked((ulong) r * 0x9E3779B97F4A7C15)).NextU64();
        PaintRegion(region.Cells!.Value, CyberRegionGenerator.Generate(seed, shape, graph));
        SyncNodes(r, network, pads);
        RestampSpurs(r);

        if (moved is { } from)
        {
            ClearOutside(CyberLayout.Tiles(from), CyberLayout.Tiles(region.Cells!.Value));
            MoveRunners(r, CyberLayout.Tiles(from));
            MoveIce(r);
        }
    }

    /// <summary>
    /// Which side of the firewalls each pad is on: pads linked without a firewall between them share a side, and
    /// a firewall's is -1. Null if there are no firewalls.
    /// </summary>
    private static List<int>? Zones(List<bool> firewalls, IEnumerable<(int A, int B)> links)
    {
        if (!firewalls.Contains(true))
            return null;

        var zones = Enumerable.Range(0, firewalls.Count).ToList();
        int Find(int i) => zones[i] == i ? i : zones[i] = Find(zones[i]);

        foreach (var (a, b) in links)
        {
            if (!firewalls[a] && !firewalls[b])
                zones[Find(a)] = Find(b);
        }

        var result = new List<int>(firewalls.Count);
        for (var i = 0; i < firewalls.Count; i++)
        {
            result.Add(firewalls[i] ? -1 : Find(i));
        }

        return result;
    }

    /// <summary>
    /// Paints a region's generated tiles into its cells, flipped if it's above the street.
    /// </summary>
    private void PaintRegion(CyberRect cells, CyberFloor[] tiles)
    {
        var rect = CyberLayout.Tiles(cells);
        if (CyberLayout.Above(cells))
        {
            var flipped = new CyberFloor[tiles.Length];
            for (var y = 0; y < rect.H; y++)
            {
                Array.Copy(tiles, y * rect.W, flipped, (rect.H - 1 - y) * rect.W, rect.W);
            }

            tiles = flipped;
        }

        Paint(rect, tiles);
    }

    /// <summary>
    /// The tile at the middle of a slot in a region's cells.
    /// </summary>
    private static (int X, int Y) SlotCentre(CyberRect cells, (int X, int Y) slot)
    {
        var rect = CyberLayout.Tiles(cells);
        var (lx, ly) = (slot.X * CyberLayout.Cell + CyberLayout.Cell / 2, slot.Y * CyberLayout.Cell + CyberLayout.Cell / 2);
        return (rect.X + lx, CyberLayout.Above(cells) ? rect.Y + rect.H - 1 - ly : rect.Y + ly);
    }

    private void ClearTiles(CyberRect rect)
    {
        Paint(rect, new CyberFloor[rect.W * rect.H]);
    }

    /// <summary>
    /// Clears the tiles of a rectangle that aren't in another.
    /// </summary>
    private void ClearOutside(CyberRect rect, CyberRect keep)
    {
        var tiles = new CyberFloor[rect.W * rect.H];
        for (var y = 0; y < rect.H; y++)
        {
            for (var x = 0; x < rect.W; x++)
            {
                if (keep.Contains(rect.X + x, rect.Y + y))
                    tiles[y * rect.W + x] = FloorAt(rect.X + x, rect.Y + y);
            }
        }

        Paint(rect, tiles);
    }

    /// <summary>
    /// Brings the runners left where a region was to where it is now: to their own deck's node if it's here,
    /// otherwise to the router's.
    /// </summary>
    private void MoveRunners(int r, CyberRect from)
    {
        var region = _regions[r];
        if (region.Router is not { } router || !region.Nodes.TryGetValue(router, out var routerNode))
            return;

        var query = EntityQueryEnumerator<CyberAvatarComponent, TransformComponent>();
        while (query.MoveNext(out var avatar, out _, out var xform))
        {
            if (xform.MapUid != _mapUid)
                continue;

            var pos = _transform.GetWorldPosition(xform);
            if (!from.Contains((int) Math.Floor(pos.X), (int) Math.Floor(pos.Y)))
                continue;

            var to = _spurs.TryGetValue(avatar, out var spur) && spur.Region == r ? spur.Node : routerNode;
            _transform.SetCoordinates(avatar, Transform(to).Coordinates);
        }
    }

    /// <summary>
    /// The slot a machine had, if it's still one of these and nothing here holds it.
    /// </summary>
    private static (int, int)? KeptSlot(Region region, EntityUid machine, List<(int X, int Y)> options,
        HashSet<(int, int)> held)
    {
        return region.Slots.TryGetValue(machine, out var slot) && options.Contains(slot) && !held.Contains(slot)
            ? slot
            : null;
    }

    /// <summary>
    /// A free slot nearest <paramref name="near"/>: not held by anything here, nor kept for anything away
    /// unless every slot is.
    /// </summary>
    private static (int, int)? FreeSlot(List<(int X, int Y)> options, HashSet<(int, int)> held,
        HashSet<(int, int)> kept, (int X, int Y)? near)
    {
        int Distance((int X, int Y) s) => near is { } n ? Math.Abs(n.X - s.X) + Math.Abs(n.Y - s.Y) : 0;

        (int, int)? Pick(bool skipKept)
        {
            (int, int)? best = null;
            var bestDistance = int.MaxValue;
            foreach (var s in options)
            {
                if (held.Contains(s) || skipKept && kept.Contains(s))
                    continue;

                var d = Distance(s);
                if (d < bestDistance)
                {
                    best = s;
                    bestDistance = d;
                }
            }

            return best;
        }

        return Pick(true) ?? Pick(false);
    }

    /// <summary>
    /// The switch or router a host is plugged into first: the lowest one.
    /// </summary>
    private static EntityUid? FirstHardware(EntityUid host, List<(EntityUid A, EntityUid B)> links,
        List<EntityUid> hubs)
    {
        EntityUid? first = null;
        foreach (var (a, b) in links)
        {
            var other = a == host ? b : b == host ? a : EntityUid.Invalid;
            if (!hubs.Contains(other))
                continue;

            if (first == null || other.CompareTo(first.Value) < 0)
                first = other;
        }

        return first;
    }

    /// <summary>
    /// Puts a node on every pad of a region, and takes away those of machines no longer there.
    /// </summary>
    private void SyncNodes(int r, MachineNetwork? network,
        List<(EntityUid Machine, (int X, int Y) Slot, PadKind Kind)> pads)
    {
        var region = _regions[r];
        var addresses = network?.Hosts.ToDictionary(h => h.Machine, h => h.Address)
                        ?? new Dictionary<EntityUid, uint>();

        foreach (var (machine, node) in region.Nodes)
        {
            if (!pads.Any(p => p.Machine == machine))
                QueueDel(node);
        }

        // Routers, switches and firewalls go by the lowest address on their network: "router 10.4".
        var lowest = addresses.Count > 0 ? addresses.Values.Min() : (uint?) null;
        var nodes = new Dictionary<EntityUid, EntityUid>();
        foreach (var (machine, slot, pad) in pads)
        {
            var kind = pad switch
            {
                PadKind.Router => CyberNodeKind.Router,
                PadKind.Switch => HasComp<FirewallComponent>(machine) ? CyberNodeKind.Firewall : CyberNodeKind.Switch,
                _ => HostKind(machine),
            };

            var label = (kind, addresses.TryGetValue(machine, out var address), lowest) switch
            {
                (CyberNodeKind.Router or CyberNodeKind.Switch or CyberNodeKind.Firewall, _, { } low) => $"{KindName(kind)} 10.{(low >> 16) & 255}",
                (_, true, _) => $"{KindName(kind)} {MachineIo.FormatAddress(address)}",
                _ => KindName(kind),
            };

            var (x, y) = SlotCentre(region.Cells!.Value, slot);
            var at = new EntityCoordinates(_mapUid!.Value, x + 0.5f, y + 0.5f);

            // A node that changes kind is made again; one that stays is moved and renamed.
            if (region.Nodes.TryGetValue(machine, out var node)
                && !TerminatingOrDeleted(node)
                && TryComp<CyberNodeComponent>(node, out var comp)
                && comp.Kind == kind)
            {
                if (Transform(node).Coordinates != at)
                    _transform.SetCoordinates(node, at);
            }
            else
            {
                if (region.Nodes.TryGetValue(machine, out var stale))
                    QueueDel(stale);

                node = Spawn(NodePrototypes[kind], at);
                comp = EnsureComp<CyberNodeComponent>(node);
                comp.Kind = kind;
                comp.Machine = machine;
                comp.Region = r;
            }

            if (MetaData(node).EntityName != label)
                _meta.SetEntityName(node, label);

            nodes[machine] = node;
        }

        region.Nodes = nodes;
    }

    private CyberNodeKind HostKind(EntityUid machine)
    {
        if (HasComp<AccessPointComponent>(machine))
            return CyberNodeKind.AccessPoint;

        if (!TryComp<WasmMachineComponent>(machine, out var comp))
            return CyberNodeKind.Device;

        return comp.Kind switch
        {
            DeviceKind.DoorController => CyberNodeKind.DoorController,
            DeviceKind.Camera => CyberNodeKind.Camera,
            _ => CyberNodeKind.Computer,
        };
    }

    private static string KindName(CyberNodeKind kind)
    {
        return kind switch
        {
            CyberNodeKind.Backbone => "the city backbone",
            CyberNodeKind.Router => "router",
            CyberNodeKind.Switch => "switch",
            CyberNodeKind.Firewall => "firewall",
            CyberNodeKind.Computer => "computer",
            CyberNodeKind.DoorController => "door controller",
            CyberNodeKind.Camera => "camera",
            CyberNodeKind.AccessPoint => "access point",
            CyberNodeKind.Deck => "deck",
            _ => "device",
        };
    }

    /// <summary>
    /// Rewrites a rectangle of cyberspace's tiles, on the map too, and has the barriers around it laid again.
    /// </summary>
    private void Paint(CyberRect rect, CyberFloor[] tiles)
    {
        if (_mapUid is not { } mapUid || !TryComp<MapGridComponent>(mapUid, out var grid))
            return;

        // Tiles laid go in before tiles cleared, in batches of their own: the explosion system's edge map counts a
        // batch that does both at once twice over, and a chunk that empties and fills again before a client catches
        // up is sent to it as deleted twice, which the engine can't serialise.
        var cleared = new List<(Vector2i, Tile)>();
        var laid = new List<(Vector2i, Tile)>();
        for (var y = 0; y < rect.H; y++)
        {
            for (var x = 0; x < rect.W; x++)
            {
                var floor = tiles[y * rect.W + x];
                var at = (rect.X + x, rect.Y + y);
                if (FloorAt(at.Item1, at.Item2) == floor)
                    continue;

                if (floor == CyberFloor.Void)
                    _tiles.Remove(at);
                else
                    _tiles[at] = floor;

                var tile = TileIds.TryGetValue(floor, out var id) ? new Tile(_tileDefs[id].TileId) : Tile.Empty;
                (tile.IsEmpty ? cleared : laid).Add((new Vector2i(at.Item1, at.Item2), tile));
            }
        }

        if (laid.Count > 0)
            _map.SetTiles(mapUid, grid, laid);
        if (cleared.Count > 0)
            _map.SetTiles(mapUid, grid, cleared);

        // The barriers on the tiles just round it may change too.
        for (var cy = ChunkOf(rect.Y - 1); cy <= ChunkOf(rect.Y + rect.H); cy++)
        {
            for (var cx = ChunkOf(rect.X - 1); cx <= ChunkOf(rect.X + rect.W); cx++)
            {
                _dirtyChunks.Add((cx, cy));
            }
        }
    }

    private static int ChunkOf(int tile)
    {
        return (int) Math.Floor(tile / (float) Chunk);
    }

    /// <summary>
    /// Lays the barriers again in every chunk whose tiles changed: one entity per chunk, with a box over each
    /// run, along a row, of tiles that can't be walked on beside tiles that can.
    /// </summary>
    private void FlushBarriers()
    {
        if (_mapUid is not { } mapUid)
            return;

        foreach (var chunk in _dirtyChunks)
        {
            if (_barriers.Remove(chunk, out var old))
                QueueDel(old);

            var (cx, cy) = chunk;
            var origin = new Vector2(cx * Chunk + Chunk / 2f, cy * Chunk + Chunk / 2f);
            var runs = new List<(int Y, int From, int To)>();
            for (var y = cy * Chunk; y < (cy + 1) * Chunk; y++)
            {
                int? start = null;
                for (var x = cx * Chunk; x <= (cx + 1) * Chunk; x++)
                {
                    var blocks = x < (cx + 1) * Chunk && NeedsBarrier(x, y);
                    if (blocks && start == null)
                        start = x;
                    else if (!blocks && start is { } from)
                    {
                        runs.Add((y, from, x));
                        start = null;
                    }
                }
            }

            if (runs.Count == 0)
                continue;

            var barrier = Spawn(Barrier, new EntityCoordinates(mapUid, origin));
            for (var i = 0; i < runs.Count; i++)
            {
                var (y, from, to) = runs[i];
                var shape = new PolygonShape();
                var centre = new Vector2((from + to) / 2f, y + 0.5f) - origin;
                shape.SetAsBox((to - from) / 2f, 0.5f, centre, 0f);
                _fixtures.TryCreateFixture(barrier, shape, $"barrier{i}", hard: true, collisionLayer: BarrierLayer,
                    updates: false);
            }

            _fixtures.FixtureUpdate(barrier);
            _barriers[chunk] = barrier;
        }

        _dirtyChunks.Clear();
    }

    /// <summary>
    /// Whether a tile needs a barrier: it can't be walked on, and one of the eight round it can.
    /// </summary>
    private bool NeedsBarrier(int x, int y)
    {
        if (CyberLayout.Walkable(FloorAt(x, y)))
            return false;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dy != 0) && CyberLayout.Walkable(FloorAt(x + dx, y + dy)))
                    return true;
            }
        }

        return false;
    }
}
