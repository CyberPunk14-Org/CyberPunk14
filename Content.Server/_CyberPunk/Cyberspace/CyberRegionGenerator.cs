using System.Linq;
using Content.Server._CyberPunk.Procgen;

namespace Content.Server._CyberPunk.Cyberspace;

/// <summary>
/// A region's network: its pads, which pads are linked, and whether its router opens onto the bus.
/// </summary>
public sealed record RegionGraph
{
    public List<((int X, int Y) Slot, PadKind Kind)> Pads = new();

    /// <summary>Pairs of indices into <see cref="Pads"/>.</summary>
    public List<(int A, int B)> Links = new();

    /// <summary>The pad (a router) whose top side opens onto the bus, if any.</summary>
    public int? Gate;

    /// <summary>
    /// Which side of the firewalls each pad is on, a firewall's own being -1. Corridors on different sides
    /// never share a cell, so the only way from one side to the other is across a firewall's pad. Null for no
    /// firewalls.
    /// </summary>
    public List<int>? Zones;

    public bool Same(RegionGraph other)
    {
        return Pads.SequenceEqual(other.Pads)
               && Links.SequenceEqual(other.Links)
               && Gate == other.Gate
               && (Zones ?? []).SequenceEqual(other.Zones ?? []);
    }
}

/// <summary>
/// Generates a region's tiles from its network, after Switchboard's <c>generate_region</c>: each link is routed
/// as a corridor from pad to pad through the cells between, which fixes which sides of each cell a path leaves
/// by. A Wave Function Collapse pass then turns every cell into a pad, a corridor (straight, bend, junction) in
/// a thin or wide style, or the void or static between. Sockets make corridors keep their style from cell to
/// cell, changing only through an adapter. The same network and seed always give the same tiles.
/// </summary>
public static class CyberRegionGenerator
{
    /// <summary>Tries before the WFC pass falls back to wide corridors everywhere.</summary>
    private const int Attempts = 10;

    /// <summary>What a side of a cell joins to.</summary>
    private enum Socket : byte
    {
        /// <summary>Nothing: the side of void or static, or of a path that doesn't leave this way.</summary>
        Shut,

        /// <summary>A one-tile path.</summary>
        Thin,

        /// <summary>A three-tile path.</summary>
        Wide,
    }

    private enum PieceKind : byte
    {
        Void,
        Static,
        Corridor,
        Pad,
    }

    /// <summary>One WFC tile for a cell.</summary>
    private readonly record struct Piece(PieceKind Kind, Socket[] Sockets, int Weight);

    private static readonly Socket[] Shut = { Socket.Shut, Socket.Shut, Socket.Shut, Socket.Shut };

    private static readonly List<Piece> Pieces = MakePieces();

    private static readonly WfcRules Rules = new(Pieces.Select(p => p.Weight).ToArray(),
        (a, direction, b) => Pieces[a].Sockets[direction] == Pieces[b].Sockets[WfcWave.Opposite(direction)]);

    private static List<Piece> MakePieces()
    {
        var pieces = new List<Piece>
        {
            new(PieceKind.Void, Shut, 8),
            new(PieceKind.Static, Shut, 2),
        };

        for (var code = 0; code < 81; code++)
        {
            var sockets = new Socket[4];
            var c = code;
            for (var side = 0; side < 4; side++)
            {
                sockets[side] = (Socket) (c % 3);
                c /= 3;
            }

            var thin = sockets.Count(s => s == Socket.Thin);
            var wide = sockets.Count(s => s == Socket.Wide);

            // Corridors keep one style; changing style takes an adapter.
            var weight = (thin, wide) switch
            {
                (0, 0) => 0,
                (_, 0) or (0, _) => 6,
                _ => 1,
            };

            if (weight > 0)
                pieces.Add(new Piece(PieceKind.Corridor, sockets, weight));

            pieces.Add(new Piece(PieceKind.Pad, sockets, 1));
        }

        return pieces;
    }

    /// <summary>
    /// Routes every link as a corridor of cells from pad to pad, around other pads and the corridors on the other
    /// side of a firewall. Returns the sides each cell's paths leave by.
    /// </summary>
    /// <remarks>
    /// With firewalls, one side's corridor can cut off a link on the other, so links through firewalls go first
    /// and, if a link still can't be routed, they're tried again in other orders.
    /// </remarks>
    private static Dictionary<(int, int), bool[]> Route(int width, int height, RegionGraph graph, ref CyberRng rng,
        out int bestMissed)
    {
        var order = Enumerable.Range(0, graph.Links.Count)
            .OrderBy(l => graph.Zones is { } z && (z[graph.Links[l].A] < 0 || z[graph.Links[l].B] < 0) ? 0 : 1)
            .ToList();

        Dictionary<(int, int), bool[]>? best = null;
        bestMissed = int.MaxValue;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var sides = Route(width, height, graph, order, out var missed);
            if (missed < bestMissed)
                (best, bestMissed) = (sides, missed);

            if (missed == 0)
                break;

            rng.Shuffle(order);
        }

        return best!;
    }

    private static Dictionary<(int, int), bool[]> Route(int width, int height, RegionGraph graph, List<int> order,
        out int missed)
    {
        missed = 0;
        var sides = new Dictionary<(int, int), bool[]>();
        bool[] SidesOf((int, int) cell)
        {
            if (!sides.TryGetValue(cell, out var s))
                sides[cell] = s = new bool[4];

            return s;
        }

        var pads = graph.Pads.Select(p => p.Slot).ToHashSet();
        foreach (var slot in pads)
        {
            SidesOf(slot);
        }

        // Ordinary pads on the same side may be walked through, just like their corridors. Firewall pads
        // are deliberately not owned by a side: they may only be the endpoints of a route.
        var sideOf = new Dictionary<(int, int), int>();
        if (graph.Zones is { } padZones)
        {
            for (var i = 0; i < graph.Pads.Count; i++)
            {
                if (padZones[i] >= 0)
                    sideOf[graph.Pads[i].Slot] = padZones[i];
            }
        }

        foreach (var link in order)
        {
            var (a, b) = graph.Links[link];
            var (from, to) = (graph.Pads[a].Slot, graph.Pads[b].Slot);
            if (from == to)
                continue;

            // A link between two firewalls is a side of its own.
            var zone = graph.Zones is { } zones
                ? zones[a] >= 0 ? zones[a] : zones[b] >= 0 ? zones[b] : -2 - link
                : 0;

            // Prefer reusing this side's corridors over carving more approaches to a firewall.
            // A sequence number makes equal-cost paths deterministic.
            var came = new Dictionary<(int, int), (int, int)>();
            var costs = new Dictionary<(int, int), int> { [from] = 0 };
            var queue = new PriorityQueue<(int X, int Y), (int Cost, int Order)>();
            var sequence = 0;
            queue.Enqueue(from, (0, sequence++));
            var found = false;
            while (queue.TryDequeue(out var at, out var priority))
            {
                if (priority.Cost != costs[at])
                    continue;

                if (at == to)
                {
                    found = true;
                    break;
                }

                foreach (var (dx, dy) in WfcWave.Directions)
                {
                    var n = (at.X + dx, at.Y + dy);
                    if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= width || n.Item2 >= height)
                        continue;

                    var owned = sideOf.TryGetValue(n, out var side);
                    if (n == from || owned && side != zone)
                        continue;

                    if (pads.Contains(n) && n != to && (!owned || side != zone))
                        continue;

                    var cost = priority.Cost + (owned ? 0 : 1);
                    if (costs.TryGetValue(n, out var previous) && previous <= cost)
                        continue;

                    costs[n] = cost;
                    came[n] = at;
                    queue.Enqueue(n, (cost, sequence++));
                }
            }

            if (!found)
            {
                missed++;
                continue;
            }

            var back = to;
            while (back != from)
            {
                var prev = came[back];
                var d = Array.FindIndex(WfcWave.Directions, dir => prev.Item1 + dir.X == back.Item1 && prev.Item2 + dir.Y == back.Item2);
                SidesOf(prev)[d] = true;
                SidesOf(back)[WfcWave.Opposite(d)] = true;
                if (!pads.Contains(back))
                    sideOf[back] = zone;

                back = prev;
            }
        }

        return sides;
    }

    /// <summary>
    /// Keeps the current slots if they can be routed. Otherwise tries other slot assignments: retrying
    /// corridor order alone cannot fix pads that fence off another cable side.
    /// </summary>
    public static bool TryLayout(ulong seed, RegionShape shape, RegionGraph graph, out RegionGraph layout)
    {
        layout = graph;
        var routeRng = new CyberRng(seed);
        Route(shape.Width, shape.Height, layout, ref routeRng, out var missed);
        if (missed == 0)
            return true;

        var rng = new CyberRng(seed ^ 0xD1B54A32D192ED03);
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var hosts = shape.HostSlots;
            var switches = shape.SwitchSlots;
            rng.Shuffle(hosts);
            rng.Shuffle(switches);
            var pads = graph.Pads.ToList();
            var hostGroups = Enumerable.Range(0, pads.Count).Where(i => pads[i].Kind == PadKind.Host)
                .GroupBy(i => graph.Zones?[i] ?? 0).ToList();
            rng.Shuffle(hostGroups);
            foreach (var group in hostGroups)
            {
                var anchor = hosts[0];
                var nearest = hosts.OrderBy(s => Math.Abs(s.X - anchor.X) + Math.Abs(s.Y - anchor.Y)).ToList();
                var index = 0;
                foreach (var pad in group)
                {
                    var slot = nearest[index++];
                    pads[pad] = (slot, pads[pad].Kind);
                    hosts.Remove(slot);
                }
            }

            var hub = 0;
            for (var i = 0; i < pads.Count; i++)
            {
                if (pads[i].Kind == PadKind.Switch)
                    pads[i] = (switches[hub++], pads[i].Kind);
            }

            layout = graph with { Pads = pads };
            routeRng = new CyberRng(seed);
            Route(shape.Width, shape.Height, layout, ref routeRng, out missed);
            if (missed == 0)
                return true;
        }

        layout = graph;
        return false;
    }

    /// <summary>
    /// Generates a region's tiles from its network, row by row from the bottom, the shape's width in cells
    /// across.
    /// </summary>
    public static CyberFloor[] Generate(ulong seed, RegionShape shape, RegionGraph graph)
    {
        var (w, h) = (shape.Width, shape.Height);
        var rng = new CyberRng(seed);
        var sides = Route(w, h, graph, ref rng, out _);
        if (graph.Gate is { } gate)
        {
            var cell = graph.Pads[gate].Slot;
            if (!sides.TryGetValue(cell, out var s))
                sides[cell] = s = new bool[4];

            s[0] = true;
        }

        var pads = graph.Pads.Select(p => p.Slot).ToHashSet();
        bool IsPad((int, int) cell) => pads.Contains(cell);
        (int, int)? gateCell = graph.Gate is { } g ? graph.Pads[g].Slot : null;

        bool[] Want((int, int) cell) => sides.TryGetValue(cell, out var s) ? s : new bool[4];

        WfcSet Options((int, int) cell)
        {
            var want = Want(cell);
            var pad = IsPad(cell);
            var none = want.All(open => !open);
            var set = new WfcSet();
            for (var i = 0; i < Pieces.Count; i++)
            {
                var piece = Pieces[i];
                var matches = Enumerable.Range(0, 4).All(d => (piece.Sockets[d] != Socket.Shut) == want[d]);
                var ok = piece.Kind switch
                {
                    PieceKind.Void or PieceKind.Static => !pad && none,
                    PieceKind.Corridor => !pad && matches,
                    // The bus is wide.
                    _ => pad && matches && (gateCell != cell || piece.Sockets[0] == Socket.Wide),
                };

                if (ok)
                    set.Insert(i);
            }

            return set;
        }

        int[]? solved = null;
        for (var attempt = 0UL; attempt < Attempts; attempt++)
        {
            var wave = new WfcWave(w, h, Rules.All());
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    wave.Set(x, y, Options((x, y)));
                }
            }

            var fork = rng.Fork(attempt);
            solved = wave.Solve(Rules, ref fork);
            if (solved != null)
                break;
        }

        // If the pass ever paints itself into a corner, every path is wide.
        var cells = new Piece[w * h];
        for (var i = 0; i < cells.Length; i++)
        {
            if (solved != null)
            {
                cells[i] = Pieces[solved[i]];
                continue;
            }

            var cell = (i % w, i / w);
            var want = Want(cell);
            var s = want.Select(open => open ? Socket.Wide : Socket.Shut).ToArray();
            cells[i] = IsPad(cell) ? new Piece(PieceKind.Pad, s, 1)
                : want.All(open => !open) ? Pieces[0]
                : new Piece(PieceKind.Corridor, s, 1);
        }

        var (tw, th) = (w * CyberLayout.Cell, h * CyberLayout.Cell);
        var tiles = new CyberFloor[tw * th];
        for (var i = 0; i < cells.Length; i++)
        {
            var (cx, cy) = (i % w, i / w);
            for (var ly = 0; ly < CyberLayout.Cell; ly++)
            {
                for (var lx = 0; lx < CyberLayout.Cell; lx++)
                {
                    tiles[(cy * CyberLayout.Cell + ly) * tw + cx * CyberLayout.Cell + lx] = Paint(cells[i], lx, ly);
                }
            }
        }

        return tiles;
    }

    /// <summary>The tile at (lx, ly) inside a cell holding a piece.</summary>
    private static CyberFloor Paint(Piece piece, int lx, int ly)
    {
        switch (piece.Kind)
        {
            case PieceKind.Void:
                return CyberFloor.Void;
            case PieceKind.Static:
                // Speckled: static here and there, void between.
                return (lx * 7 + ly * 3) % 4 == 0 ? CyberFloor.Static : CyberFloor.Void;
        }

        var floor = piece.Kind == PieceKind.Pad ? CyberFloor.Node : CyberFloor.Data;
        const int mid = CyberLayout.Cell / 2;
        var wideCentre = piece.Kind == PieceKind.Pad || piece.Sockets.Contains(Socket.Wide);
        var half = wideCentre ? 1 : 0;
        bool Near(int v, int r) => Math.Abs(v - mid) <= r;
        if (Near(lx, half) && Near(ly, half))
            return floor;

        // Arms out to each open side, as wide as their socket.
        for (var d = 0; d < 4; d++)
        {
            var socket = piece.Sockets[d];
            if (socket == Socket.Shut)
                continue;

            var width = socket == Socket.Thin ? 0 : 1;
            var on = d switch
            {
                0 => ly > mid && Near(lx, width),
                1 => lx > mid && Near(ly, width),
                2 => ly < mid && Near(lx, width),
                _ => lx < mid && Near(ly, width),
            };

            if (on)
                return CyberFloor.Data;
        }

        return CyberFloor.Void;
    }
}
