using System.Linq;
using Content.Server._CyberPunk.Machines;
using Content.Server._CyberPunk.Wasm;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// Spurs and practice grids, after Switchboard's <c>spur.rs</c> and the practice networks in
/// <c>runners.rs</c>.
/// </summary>
/// <remarks>
/// A runner who comes in through a machine has a node of their own, a child of the machine, on a small spur
/// stamped onto the void beside the machine's pad, where they arrive. The region isn't generated again for it,
/// as that would move the paths under everyone else. When it is generated again, the spurs are laid again,
/// where they were if there's room.
/// </remarks>
public sealed partial class CyberspaceSystem
{
    /// <summary>Directions a spur is tried in, for each length: down, right, left, up.</summary>
    private static readonly (int X, int Y)[] SpurDirections = { (0, -1), (1, 0), (-1, 0), (0, 1) };

    private static readonly EntProtoId TrainingServerPrototype = "CyberTrainingServer";

    /// <summary>A practice grid's links: the router to the switch, and the switch to the way in and the server.</summary>
    private static readonly List<(int A, int B)> SandboxLinks = [(0, 1), (1, 2), (1, 3)];

    private sealed class Spur
    {
        public EntityUid Device;
        public EntityUid Node;
        public int Region;
        public (int X, int Y) Centre;
        public List<(int X, int Y)> Tiles = new();
    }

    private sealed class Sandbox
    {
        public EntityUid Body;
        public EntityUid Server;
        public readonly List<EntityUid> Nodes = new();
    }

    /// <summary>Each deck's spur, by the runner's virtual body.</summary>
    private readonly Dictionary<EntityUid, Spur> _spurs = new();

    private readonly Sandbox?[] _sandboxes = new Sandbox?[Sandboxes];

    /// <summary>
    /// The region and the tile at the middle of a machine's pad, if it has one.
    /// </summary>
    private bool PadOf(EntityUid machine, out int region, out (int X, int Y) centre)
    {
        region = 0;
        centre = default;
        for (var r = 0; r < _regions.Count; r++)
        {
            if (_regions[r].Cells is not { } cells
                || !_regions[r].Nodes.ContainsKey(machine)
                || !_regions[r].Slots.TryGetValue(machine, out var slot))
            {
                continue;
            }

            region = r;
            centre = SlotCentre(cells, slot);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gives a deck a node on a spur beside the pad of the machine it came in through. Returns where its
    /// runner arrives, or null if the machine has no pad.
    /// </summary>
    public EntityCoordinates? AttachSpur(EntityUid avatar, EntityUid device)
    {
        DetachSpur(avatar);
        if (_mapUid is not { } mapUid || !PadOf(device, out var r, out var from))
            return null;

        var spur = new Spur { Device = device, Region = r };
        LaySpur(spur, from, null);
        spur.Node = SpawnNode(CyberNodeKind.Deck, spur.Centre, "deck", avatar, r);
        _spurs[avatar] = spur;
        FlushBarriers();
        return new EntityCoordinates(mapUid, spur.Centre.X + 0.5f, spur.Centre.Y + 0.5f);
    }

    /// <summary>
    /// Takes a deck's spur and node away.
    /// </summary>
    public void DetachSpur(EntityUid avatar)
    {
        if (!_spurs.Remove(avatar, out var spur))
            return;

        QueueDel(spur.Node);
        foreach (var (x, y) in spur.Tiles)
        {
            SetFloor(x, y, CyberFloor.Void);
        }

        FlushBarriers();
    }

    /// <summary>
    /// Stamps a spur out from a pad: the first that fits of lengths 4, 5 and 6 in each direction, onto void
    /// only, inside the region and touching no other path, trying first where it was. With no room, its node
    /// shares the pad.
    /// </summary>
    private void LaySpur(Spur spur, (int X, int Y) from, (int X, int Y)? prefer)
    {
        var rect = RegionRect(spur.Region)!.Value;
        var candidates = new List<((int X, int Y) Centre, List<(int X, int Y)> Data, List<(int X, int Y)> Pad)>();
        foreach (var length in new[] { 4, 5, 6 })
        {
            foreach (var (dx, dy) in SpurDirections)
            {
                var centre = (from.X + dx * length, from.Y + dy * length);
                var data = new List<(int, int)>();
                for (var i = 2; i <= length - 2; i++)
                {
                    data.Add((from.X + dx * i, from.Y + dy * i));
                }

                var pad = new List<(int, int)>();
                for (var py = -1; py <= 1; py++)
                {
                    for (var px = -1; px <= 1; px++)
                    {
                        pad.Add((centre.Item1 + px, centre.Item2 + py));
                    }
                }

                candidates.Add((centre, data, pad));
            }
        }

        if (prefer is { } old)
            candidates = candidates.OrderBy(c => c.Centre == old ? 0 : 1).ToList();

        spur.Tiles = new List<(int X, int Y)>();
        spur.Centre = from;
        foreach (var (centre, data, pad) in candidates)
        {
            var tiles = data.Concat(pad).ToHashSet();
            if (!tiles.All(t => rect.Contains(t.X, t.Y) && !CyberLayout.Walkable(FloorAt(t.X, t.Y))))
                continue;

            // It joins nothing but its machine's pad, so it's never a way round a firewall.
            var joins = tiles.SelectMany(t => SpurDirections.Select(d => (X: t.X + d.X, Y: t.Y + d.Y)))
                .Where(n => !tiles.Contains(n) && (Math.Abs(n.X - from.X) > 1 || Math.Abs(n.Y - from.Y) > 1));
            if (joins.Any(n => CyberLayout.Walkable(FloorAt(n.X, n.Y))))
                continue;

            foreach (var (x, y) in data)
            {
                SetFloor(x, y, CyberFloor.Data);
            }

            foreach (var (x, y) in pad)
            {
                SetFloor(x, y, CyberFloor.Node);
            }

            spur.Tiles = data.Concat(pad).ToList();
            spur.Centre = centre;
            return;
        }
    }

    /// <summary>
    /// Lays the spurs of a region again after it was generated again: where they were if there's room, and
    /// gone with their machine.
    /// </summary>
    private void RestampSpurs(int r)
    {
        foreach (var (avatar, spur) in _spurs.Where(s => s.Value.Region == r).OrderBy(s => s.Key).ToList())
        {
            if (!PadOf(spur.Device, out var region, out var from) || region != r)
            {
                QueueDel(spur.Node);
                _spurs.Remove(avatar);
                continue;
            }

            LaySpur(spur, from, spur.Centre);
            _transform.SetCoordinates(spur.Node, new EntityCoordinates(_mapUid!.Value, spur.Centre.X + 0.5f, spur.Centre.Y + 0.5f));
        }
    }

    /// <summary>
    /// Names each deck's node after its address on the network.
    /// </summary>
    private void NameDecks()
    {
        foreach (var (avatar, spur) in _spurs)
        {
            var name = _machines.AddressOf(avatar) is { } address
                ? $"deck {MachineIo.FormatAddress(address)}"
                : "deck";

            if (MetaData(spur.Node).EntityName != name)
                _meta.SetEntityName(spur.Node, name);
        }
    }

    /// <summary>
    /// Raises a practice grid in a slot: a router, a switch, a way in and a training server, laid out like any
    /// region but joined to nothing. Returns where the way in is.
    /// </summary>
    private EntityCoordinates? BuildSandbox(int slot, EntityUid body)
    {
        if (_mapUid is not { } mapUid)
            return null;

        var shape = RegionShape.Smallest;
        var cells = CyberLayout.PracticeCells(slot);
        var hosts = shape.HostSlots;
        var graph = new RegionGraph
        {
            Pads =
            {
                (shape.RouterSlot, PadKind.Router),
                (shape.SwitchSlots[0], PadKind.Switch),
                (hosts[0], PadKind.Host),
                (hosts[1], PadKind.Host),
            },
            Links = SandboxLinks,
        };

        var seed = _seed ^ (0x5A4DB0C5UL + (ulong) slot);
        PaintRegion(cells, CyberRegionGenerator.Generate(seed, shape, graph));
        FlushBarriers();

        var at = (int i) => SlotCentre(cells, graph.Pads[i].Slot);
        var (sx, sy) = at(3);
        var server = Spawn(TrainingServerPrototype, new EntityCoordinates(mapUid, sx + 0.5f, sy + 0.5f));
        _machines.SetVirtualHost(server, new VirtualHost(null, slot, 2));

        var sandbox = new Sandbox { Body = body, Server = server };
        sandbox.Nodes.Add(SpawnNode(CyberNodeKind.Router, at(0), "practice router", null, null));
        sandbox.Nodes.Add(SpawnNode(CyberNodeKind.Switch, at(1), "practice switch", null, null));
        sandbox.Nodes.Add(SpawnNode(CyberNodeKind.AccessPoint, at(2), "practice way in", null, null));
        sandbox.Nodes.Add(SpawnNode(CyberNodeKind.Computer, at(3), "training server", server, null));
        _sandboxes[slot] = sandbox;

        var (x, y) = at(2);
        return new EntityCoordinates(mapUid, x + 0.5f, y + 0.5f);
    }

    /// <summary>
    /// Takes a practice grid down: its machines, nodes and tiles.
    /// </summary>
    private void TeardownSandbox(int slot)
    {
        if (_sandboxes[slot] is not { } sandbox)
            return;

        _sandboxes[slot] = null;
        _machines.SetVirtualHost(sandbox.Server, null);
        QueueDel(sandbox.Server);
        foreach (var node in sandbox.Nodes)
        {
            QueueDel(node);
        }

        ClearTiles(CyberLayout.Tiles(CyberLayout.PracticeCells(slot)));
        FlushBarriers();
    }

    /// <summary>
    /// Puts a node in cyberspace.
    /// </summary>
    private EntityUid SpawnNode(CyberNodeKind kind, (int X, int Y) tile, string label, EntityUid? machine, int? region)
    {
        var node = Spawn(NodePrototypes[kind], new EntityCoordinates(_mapUid!.Value, tile.X + 0.5f, tile.Y + 0.5f));
        var comp = EnsureComp<CyberNodeComponent>(node);
        comp.Kind = kind;
        comp.Machine = machine;
        comp.Region = region;
        _meta.SetEntityName(node, label);
        return node;
    }

    private void SetFloor(int x, int y, CyberFloor floor)
    {
        Paint(new CyberRect(x, y, 1, 1), new[] { floor });
    }
}
