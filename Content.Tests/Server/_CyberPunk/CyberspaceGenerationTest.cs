using System.Collections.Generic;
using System.Linq;
using Content.Server._CyberPunk.Cyberspace;
using Content.Server._CyberPunk.Procgen;
using NUnit.Framework;

#nullable enable

namespace Content.Tests.Server._CyberPunk;

/// <summary>
/// The shape of cyberspace and the regions generated from networks, ported from Switchboard's procgen tests.
/// </summary>
[TestFixture]
[TestOf(typeof(CyberRegionGenerator))]
public sealed class CyberspaceGenerationTest
{
    private static readonly RegionShape Shape = RegionShape.For(9, 2);

    private static readonly int Width = Shape.Width * CyberLayout.Cell;

    /// <summary>A router, two switches and hosts under each.</summary>
    private static RegionGraph Graph()
    {
        var hosts = Shape.HostSlots;
        var switches = Shape.SwitchSlots;
        return new RegionGraph
        {
            Pads =
            {
                (Shape.RouterSlot, PadKind.Router),
                (switches[0], PadKind.Switch),
                (switches[1], PadKind.Switch),
                (hosts[0], PadKind.Host),
                (hosts[1], PadKind.Host),
                (hosts[5], PadKind.Host),
                (hosts[3], PadKind.Host),
            },
            Links = { (0, 1), (0, 2), (1, 2), (1, 3), (1, 5), (2, 4), (2, 6) },
            Gate = 0,
        };
    }

    private static bool Walkable(CyberFloor[] tiles, int i)
    {
        return CyberLayout.Walkable(tiles[i]);
    }

    /// <summary>Which tiles the tile <paramref name="start"/> reaches.</summary>
    private static bool[] Flood(CyberFloor[] tiles, int width, int start)
    {
        var height = tiles.Length / width;
        var seen = new bool[tiles.Length];
        var queue = new Queue<int>();
        queue.Enqueue(start);
        seen[start] = true;
        while (queue.TryDequeue(out var i))
        {
            var (x, y) = (i % width, i / width);
            foreach (var (dx, dy) in WfcWave.Directions)
            {
                var (nx, ny) = (x + dx, y + dy);
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    continue;

                var n = ny * width + nx;
                if (!seen[n] && Walkable(tiles, n))
                {
                    seen[n] = true;
                    queue.Enqueue(n);
                }
            }
        }

        return seen;
    }

    private static int Centre((int X, int Y) slot)
    {
        var (x, y) = (slot.X * CyberLayout.Cell + CyberLayout.Cell / 2, slot.Y * CyberLayout.Cell + CyberLayout.Cell / 2);
        return y * Width + x;
    }

    [Test]
    public void LinkedPadsAreJoinedAndUnlinkedOnesAreNot()
    {
        var graph = Graph();

        // An unplugged host: a pad with no links.
        graph.Pads.Add((Shape.HostSlots[4], PadKind.Host));
        var tiles = CyberRegionGenerator.Generate(7, Shape, graph);
        var reached = Flood(tiles, Width, Centre(graph.Pads[0].Slot));
        for (var i = 0; i < graph.Pads.Count; i++)
        {
            var slot = graph.Pads[i].Slot;
            Assert.That(reached[Centre(slot)], Is.EqualTo(i != 7), $"pad {i}");
            Assert.That(tiles[Centre(slot)], Is.EqualTo(CyberFloor.Node));
        }

        // The gate opens the router's pad onto the top edge.
        var top = (Shape.Height * CyberLayout.Cell - 1) * Width + Shape.RouterSlot.X * CyberLayout.Cell + 2;
        Assert.That(Walkable(tiles, top));

        // Nothing else reaches the edge.
        var height = Shape.Height * CyberLayout.Cell;
        var edge = Enumerable.Range(0, Width)
            .Concat(Enumerable.Range(0, height).Select(y => y * Width))
            .Concat(Enumerable.Range(0, height).Select(y => y * Width + Width - 1));

        Assert.That(edge.All(i => !Walkable(tiles, i)));
    }

    [Test]
    public void SameGraphSameTilesAndStylesVary()
    {
        var graph = Graph();
        var first = CyberRegionGenerator.Generate(3, Shape, graph);
        var second = CyberRegionGenerator.Generate(3, Shape, graph);
        Assert.That(second, Is.EqualTo(first));

        var sizes = new HashSet<int>();
        for (var seed = 0UL; seed < 20; seed++)
        {
            var tiles = CyberRegionGenerator.Generate(seed, Shape, graph);
            Assert.That(tiles.Any(CyberLayout.Walkable));
            sizes.Add(tiles.Count(t => t == CyberFloor.Data));
        }

        Assert.That(sizes, Has.Count.GreaterThan(1), "the WFC pass should pick different styles");
    }

    [Test]
    public void RemovingALinkRemovesItsCorridor()
    {
        var graph = Graph();
        var cut = Graph();
        cut.Links.Remove((2, 6));
        var tiles = CyberRegionGenerator.Generate(1, Shape, cut);
        var reached = Flood(tiles, Width, Centre(graph.Pads[0].Slot));
        Assert.That(reached[Centre(graph.Pads[6].Slot)], Is.False);
        Assert.That(reached[Centre(graph.Pads[4].Slot)]);
    }

    /// <summary>
    /// A full subnet fits in one region, and every host on it is joined to the router.
    /// </summary>
    [Test]
    public void ARegionHoldsAFullSubnet()
    {
        var shape = RegionShape.For(253, 4);
        Assert.That(shape.Fits(253, 4));
        Assert.That(shape.HostSlots.Distinct().Count(), Is.EqualTo(shape.HostSlots.Count));

        var graph = new RegionGraph { Pads = { (shape.RouterSlot, PadKind.Router) }, Gate = 0 };
        for (var i = 0; i < 4; i++)
        {
            graph.Pads.Add((shape.SwitchSlots[i], PadKind.Switch));
            graph.Links.Add((0, i + 1));
        }

        for (var i = 0; i < 253; i++)
        {
            graph.Pads.Add((shape.HostSlots[i], PadKind.Host));
            graph.Links.Add((1 + i % 4, graph.Pads.Count - 1));
        }

        var width = shape.Width * CyberLayout.Cell;
        var tiles = CyberRegionGenerator.Generate(5, shape, graph);
        int At((int X, int Y) slot) => (slot.Y * CyberLayout.Cell + 2) * width + slot.X * CyberLayout.Cell + 2;
        var reached = Flood(tiles, width, At(shape.RouterSlot));
        foreach (var (slot, _) in graph.Pads)
        {
            Assert.That(reached[At(slot)], $"slot {slot}");
        }
    }

    /// <summary>
    /// Hosts sharing a cable behind a firewall stay connected even with its entire pad blocked.
    /// Each cable side may contain more hosts than the firewall has entrances.
    /// </summary>
    [TestCase(2, 6)]
    [TestCase(3, 6)]
    [TestCase(4, 6)]
    public void FirewallSidesStayConnected(int sides, int hostsPerSide)
    {
        var shape = RegionShape.For(hostsPerSide * sides, 1);
        var graph = new RegionGraph
        {
            Pads = { (shape.RouterSlot, PadKind.Router), (shape.SwitchSlots[0], PadKind.Switch) },
            Links = { (0, 1) },
            Zones = new List<int> { 0, -1 },
            Gate = 0,
        };
        for (var side = 0; side < sides; side++)
        {
            var first = graph.Pads.Count;
            for (var host = 0; host < hostsPerSide; host++)
            {
                var pad = graph.Pads.Count;
                graph.Pads.Add((shape.HostSlots[side * hostsPerSide + host], PadKind.Host));
                graph.Zones.Add(side);
                graph.Links.Add((1, pad));
                if (side == 0)
                    graph.Links.Add((0, pad));
                else if (pad != first)
                    graph.Links.Add((first, pad));
            }
        }

        var width = shape.Width * CyberLayout.Cell;
        int At(int pad) => (graph.Pads[pad].Slot.Y * CyberLayout.Cell + 2) * width
                          + graph.Pads[pad].Slot.X * CyberLayout.Cell + 2;
        for (var seed = 0UL; seed < 20; seed++)
        {
            Assert.That(CyberRegionGenerator.TryLayout(seed, shape, graph, out var layout), Is.True,
                $"layout for {sides} sides, seed {seed}");
            var original = graph;
            graph = layout;
            var tiles = CyberRegionGenerator.Generate(seed, shape, graph);
            var open = Flood(tiles, width, At(0));
            Assert.That(Enumerable.Range(0, graph.Pads.Count).All(p => open[At(p)]),
                $"all pads reachable with firewall open, {sides} sides, seed {seed}");

            // The physical gate covers the central 3x3 tiles, not the entire cell.
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                tiles[At(1) + dy * width + dx] = CyberFloor.Void;

            for (var side = 0; side < sides; side++)
            {
                var reached = Flood(tiles, width, At(2 + side * hostsPerSide));
                for (var pad = 0; pad < graph.Pads.Count; pad++)
                {
                    if (pad == 1)
                        continue;

                    Assert.That(reached[At(pad)], Is.EqualTo(graph.Zones[pad] == side),
                        $"side {side}, pad {pad}, {sides} sides, seed {seed}");
                }
            }
            graph = original;
        }
    }

    /// <summary>
    /// Regions go along the street nearest the hub first, each touching the street and none overlapping, and a
    /// region given back leaves a gap for the next that fits.
    /// </summary>
    [Test]
    public void RegionsLineTheStreet()
    {
        var layout = new CyberLayout();
        var shapes = new[] { RegionShape.Smallest, RegionShape.For(253, 1), RegionShape.For(30, 3), RegionShape.Smallest, RegionShape.For(80, 2), RegionShape.Smallest };
        var placed = shapes.Select(layout.Place).ToList();

        var all = placed.Append(CyberLayout.HubCells).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                Assert.That(Apart(all[i], all[j]), $"{all[i]} overlaps {all[j]}");
            }
        }

        foreach (var cells in placed)
        {
            Assert.That(CyberLayout.Above(cells) ? cells.Y == 1 : cells.Y + cells.H == 0, $"{cells} is off the street");
            Assert.That(cells.X >= layout.StreetWest && cells.X + cells.W - 1 <= layout.StreetEast);
        }

        // The first four take the four spots beside the hub.
        Assert.That(placed.Take(4).Select(c => (c.X > 0, CyberLayout.Above(c))).Distinct().Count(), Is.EqualTo(4));

        layout.Free(placed[0]);
        Assert.That(layout.Place(RegionShape.Smallest), Is.EqualTo(placed[0]), "a gap is filled again");
    }

    [Test]
    public void PracticeRegionsAreCutOff()
    {
        var layout = new CyberLayout();
        var network = layout.Place(RegionShape.For(253, 8));
        for (var s = 0; s < 8; s++)
        {
            var cells = CyberLayout.PracticeCells(s);
            Assert.That(Apart(Grow(cells), network) && Apart(Grow(cells), CyberLayout.HubCells));
            for (var t = s + 1; t < 8; t++)
            {
                Assert.That(Apart(Grow(cells), CyberLayout.PracticeCells(t)), $"{s} touches {t}");
            }
        }
    }

    /// <summary>A rectangle with a cell more all round.</summary>
    private static CyberRect Grow(CyberRect r)
    {
        return new CyberRect(r.X - 1, r.Y - 1, r.W + 2, r.H + 2);
    }

    private static bool Apart(CyberRect a, CyberRect b)
    {
        return a.X + a.W <= b.X || b.X + b.W <= a.X || a.Y + a.H <= b.Y || b.Y + b.H <= a.Y;
    }

    /// <summary>
    /// The solver keeps to its sockets: tiles that only fit themselves fill the grid with one of them.
    /// </summary>
    [Test]
    public void WfcNeighboursRespectSockets()
    {
        var rules = new WfcRules(new[] { 1, 1 }, (a, _, b) => a == b);
        for (var seed = 0UL; seed < 20; seed++)
        {
            var rng = new CyberRng(seed);
            var grid = new WfcWave(6, 6, rules.All()).Solve(rules, ref rng);
            Assert.That(grid, Is.Not.Null);
            Assert.That(grid!.All(t => t == grid[0]));
        }
    }

    [Test]
    public void WfcImpossibleCellsFail()
    {
        var rules = new WfcRules(new[] { 1, 1 }, (a, _, b) => a == b);
        var wave = new WfcWave(2, 1, rules.All());
        wave.Set(0, 0, WfcSet.Single(0));
        wave.Set(1, 0, WfcSet.Single(1));
        var rng = new CyberRng(0);
        Assert.That(wave.Solve(rules, ref rng), Is.Null);
    }
}
