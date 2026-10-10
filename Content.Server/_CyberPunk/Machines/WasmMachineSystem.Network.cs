using System.Linq;
using Content.Server._CyberPunk.Cyberspace;
using Content.Server._CyberPunk.Network;
using Content.Server._CyberPunk.Wasm;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;
using Content.Shared.Power;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CyberPunk.Machines;

/// <summary>
/// The network between machines, after Switchboard's <c>sb_sim/src/network.rs</c>: data cable joins machines to
/// switches and routers, and everything joined that way is one local network. A network works when a router
/// with power is on it: the router gives each machine an address, joins the network to every other routed
/// network on the same map, and keeps the directory of hostnames the machines publish. Machines on a network
/// with no router have no address and can't send anything. Machines with a UI of their own get addresses too
/// (WasmMachineSystem.Devices.cs).
/// </summary>
/// <remarks>
/// The network is worked out again only when something changes: cable or a machine joins or leaves, a hub
/// gains or loses power, or a program changes its hostname. Packets sent in a tick are delivered after every
/// machine has run, to be read the next tick.
/// </remarks>
public sealed partial class WasmMachineSystem
{
    [Dependency] private NodeContainerSystem _nodes = default!;
    [Dependency] private NodeGroupSystem _nodeGroups = default!;
    [Dependency] private SharedMapSystem _map = default!;

    private static readonly IReadOnlyDictionary<string, uint> NoHosts = new Dictionary<string, uint>();

    private bool _networkDirty = true;

    /// <summary>The machine at each address, and the map whose backbone its network is on.</summary>
    private readonly Dictionary<uint, (EntityUid Machine, MapId Map)> _addresses = new();

    private readonly List<Packet> _sent = new();

    /// <summary>Machines with no cable of their own on a network, like decks, and where they join.</summary>
    private readonly Dictionary<EntityUid, VirtualHost> _virtualHosts = new();

    private void InitializeNetwork()
    {
        SubscribeLocalEvent<WasmMachineComponent, NodeGroupsRebuilt>(OnMachineNodesRebuilt);

        SubscribeLocalEvent<NetworkHubComponent, MapInitEvent>(OnHubMapInit);
        SubscribeLocalEvent<NetworkHubComponent, ComponentShutdown>(OnHubShutdown);
        SubscribeLocalEvent<NetworkHubComponent, PowerChangedEvent>(OnHubPowerChanged);
        SubscribeLocalEvent<NetworkHubComponent, NodeGroupsRebuilt>(OnHubNodesRebuilt);

        SubscribeLocalEvent<DataCableComponent, MapInitEvent>(OnCableMapInit);
        SubscribeLocalEvent<DataCableComponent, AnchorStateChangedEvent>(OnCableAnchorChanged);
    }

    /// <summary>
    /// Has the network worked out again before machines next run.
    /// </summary>
    public void RefreshNetwork()
    {
        _networkDirty = true;
    }

    /// <summary>
    /// Puts a machine with no cable on a network, like a runner's deck, or takes it off with null.
    /// </summary>
    public void SetVirtualHost(EntityUid machine, VirtualHost? host)
    {
        if (host is { } h)
            _virtualHosts[machine] = h;
        else
            _virtualHosts.Remove(machine);

        _networkDirty = true;
    }

    /// <summary>
    /// The address of a host on a practice network: 10.250.(lan + 1).host.
    /// </summary>
    public static uint PracticeAddress(int lan, byte host)
    {
        return 10u << 24 | 250u << 16 | (uint) (lan + 1) << 8 | host;
    }

    private void OnMachineNodesRebuilt(Entity<WasmMachineComponent> ent, ref NodeGroupsRebuilt args)
    {
        _networkDirty = true;
    }

    private void OnHubNodesRebuilt(Entity<NetworkHubComponent> ent, ref NodeGroupsRebuilt args)
    {
        _networkDirty = true;
    }

    private void OnHubMapInit(Entity<NetworkHubComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.Router && (ent.Comp.Subnet <= 0 || SubnetTaken(ent)))
            ent.Comp.Subnet = FreeSubnet();

        SetHubEnabled(ent, _power.IsPowered(ent));
        _networkDirty = true;
    }

    private void OnHubShutdown(Entity<NetworkHubComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Leases.Clear();
        _networkDirty = true;
    }

    private void OnHubPowerChanged(Entity<NetworkHubComponent> ent, ref PowerChangedEvent args)
    {
        SetHubEnabled(ent, args.Powered);
        _networkDirty = true;
    }

    private void SetHubEnabled(Entity<NetworkHubComponent> ent, bool enabled)
    {
        if (!_nodes.TryGetNode(ent.Owner, ent.Comp.Node, out DataHubNode? node) || node.Enabled == enabled)
            return;

        node.Enabled = enabled;
        _nodeGroups.QueueReflood(node);
    }

    private bool SubnetTaken(Entity<NetworkHubComponent> ent)
    {
        var query = EntityQueryEnumerator<NetworkHubComponent>();
        while (query.MoveNext(out var uid, out var hub))
        {
            if (uid != ent.Owner && hub.Router && hub.Subnet == ent.Comp.Subnet)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The lowest subnet no router has.
    /// </summary>
    private int FreeSubnet()
    {
        var taken = new HashSet<int>();
        var query = EntityQueryEnumerator<NetworkHubComponent>();
        while (query.MoveNext(out var hub))
        {
            if (hub.Router)
                taken.Add(hub.Subnet);
        }

        var subnet = 1;
        while (taken.Contains(subnet))
        {
            subnet++;
        }

        return subnet;
    }

    /// <summary>
    /// The first three parts of the addresses on a subnet: 10.(1 + (n - 1) / 254).(1 + (n - 1) % 254).
    /// </summary>
    public static uint SubnetBase(int subnet)
    {
        var n = (uint) Math.Max(subnet - 1, 0);
        return 10u << 24 | ((1 + n / 254) & 255) << 16 | (1 + n % 254) << 8;
    }

    private void OnCableMapInit(Entity<DataCableComponent> ent, ref MapInitEvent args)
    {
        RefloodHubsNear(ent);
    }

    private void OnCableAnchorChanged(Entity<DataCableComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            RefloodHubsNear(ent);
    }

    /// <summary>
    /// Has the switches and routers on a cable's tile and the four beside it look for cable again, as cable
    /// doesn't look for them.
    /// </summary>
    private void RefloodHubsNear(EntityUid cable)
    {
        var xform = Transform(cable);
        if (!xform.Anchored || xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var tile = _map.TileIndicesFor((gridUid, grid), xform.Coordinates);
        foreach (var (_, node) in NodeHelpers.GetCardinalNeighborNodes(GetEntityQuery<NodeContainerComponent>(),
                     (gridUid, grid),
                     tile,
                     _map))
        {
            if (node is DataHubNode)
                _nodeGroups.QueueReflood(node);
        }
    }

    /// <summary>
    /// The network a node is on, if it can connect at all.
    /// </summary>
    private object? NetworkOf(EntityUid uid, string nodeName)
    {
        if (!_nodes.TryGetNode(uid, nodeName, out Node? node))
            return null;

        if (node is DataHubNode { Enabled: false })
            return null;

        return node.NodeGroup;
    }

    /// <summary>
    /// Works out every machine's address, who it can reach, its neighbours and the hostnames it can look up,
    /// and tells each machine.
    /// </summary>
    private void RebuildNetwork()
    {
        _networkDirty = false;
        _addresses.Clear();

        // Each network's router: the first one on it, if there are several.
        var routers = new List<Entity<NetworkHubComponent>>();
        var hubs = EntityQueryEnumerator<NetworkHubComponent>();
        while (hubs.MoveNext(out var uid, out var hub))
        {
            if (hub.Router)
                routers.Add((uid, hub));
        }

        routers.Sort((a, b) => a.Owner.CompareTo(b.Owner));
        var served = new Dictionary<object, Entity<NetworkHubComponent>>();
        foreach (var router in routers)
        {
            if (NetworkOf(router, router.Comp.Node) is not { } network
                || router.Comp.Subnet <= 0
                || !served.TryAdd(network, router))
            {
                router.Comp.Leases.Clear();
            }
        }

        // The machines on each served network, and those on none, then the machines with a UI on its cable.
        var members = new Dictionary<object, List<EntityUid>>();
        var unconnected = new List<Entity<WasmMachineComponent>>();
        var machines = EntityQueryEnumerator<WasmMachineComponent>();
        while (machines.MoveNext(out var uid, out var machine))
        {
            if (machine.Vm == null || _virtualHosts.ContainsKey(uid))
                continue;

            if (NetworkOf(uid, machine.DataNode) is { } network && served.ContainsKey(network))
            {
                if (!members.TryGetValue(network, out var list))
                    members[network] = list = new List<EntityUid>();

                list.Add(uid);
            }
            else
            {
                unconnected.Add((uid, machine));
            }
        }

        FindDevices(served, members);

        // Decks join the network of the machine they came in through.
        foreach (var (machine, virtualHost) in _virtualHosts)
        {
            if (virtualHost.Beside is not { } device || !TryComp<WasmMachineComponent>(machine, out var comp) || comp.Vm == null)
                continue;

            if (members.FirstOrDefault(m => m.Value.Contains(device)).Value is { } list)
                list.Add(machine);
            else
                unconnected.Add((machine, comp));
        }

        // Addresses: a machine keeps the one it has while it stays, and a newcomer gets the lowest free one.
        var lans = new List<(MapId Map, List<(uint Address, EntityUid Machine)> Hosts)>();
        foreach (var (network, router) in served)
        {
            var list = members.GetValueOrDefault(network) ?? new List<EntityUid>();
            list.Sort();

            var leases = router.Comp.Leases;
            var here = list.ToHashSet();
            foreach (var gone in leases.Keys.Where(m => !here.Contains(m)).ToList())
            {
                leases.Remove(gone);
            }

            var used = leases.Values.ToHashSet();
            var map = Transform(router).MapID;
            var hosts = new List<(uint, EntityUid)>();
            foreach (var machine in list)
            {
                if (!leases.TryGetValue(machine, out var host))
                {
                    // Decks take the top of the range, .200 up.
                    host = (byte) (_virtualHosts.ContainsKey(machine) ? 200 : 2);
                    while (host < 255 && used.Contains(host))
                    {
                        host++;
                    }

                    // The network is full.
                    if (host == 255)
                    {
                        if (TryComp<WasmMachineComponent>(machine, out var full))
                            unconnected.Add((machine, full));

                        continue;
                    }

                    leases[machine] = host;
                    used.Add(host);
                }

                var address = SubnetBase(router.Comp.Subnet) | host;
                _addresses[address] = (machine, map);
                hosts.Add((address, machine));
            }

            lans.Add((map, hosts));
        }

        // Practice networks: each its own network, reaching nothing else, on a map of its own that no real map
        // shares.
        foreach (var group in _virtualHosts.Where(v => v.Value.Beside == null).GroupBy(v => v.Value.Lan))
        {
            var map = new MapId(-1 - group.Key);
            var hosts = new List<(uint, EntityUid)>();
            foreach (var (machine, virtualHost) in group.OrderBy(v => v.Value.Host))
            {
                if (CompOrNull<WasmMachineComponent>(machine)?.Vm == null)
                    continue;

                var address = PracticeAddress(group.Key, virtualHost.Host);
                _addresses[address] = (machine, map);
                hosts.Add((address, machine));
            }

            lans.Add((map, hosts));
        }

        // Every routed network on a map reaches every other, and its routers share one directory of hostnames.
        var reachable = new Dictionary<MapId, HashSet<uint>>();
        var directories = new Dictionary<MapId, Dictionary<string, uint>>();
        foreach (var (address, (machine, map)) in _addresses.OrderBy(a => a.Key))
        {
            if (!reachable.TryGetValue(map, out var set))
            {
                reachable[map] = set = new HashSet<uint>();
                directories[map] = new Dictionary<string, uint>();
            }

            set.Add(address);

            // Two machines with one name: the lower address has it. Machines with a UI have no names.
            var name = CompOrNull<WasmMachineComponent>(machine)?.Hostname ?? "";
            if (name.Length > 0)
                directories[map].TryAdd(name, address);
        }

        foreach (var (map, hosts) in lans)
        {
            foreach (var (address, machine) in hosts)
            {
                if (CompOrNull<WasmMachineComponent>(machine)?.Vm is not { } vm)
                    continue;

                var neighbours = hosts.Select(h => h.Address).Where(a => a != address).OrderBy(a => a).ToList();
                vm.SetNetwork(address, reachable[map], neighbours, directories[map]);
            }
        }

        foreach (var machine in unconnected)
        {
            machine.Comp.Vm!.SetNetwork(null, null, Array.Empty<uint>(), NoHosts);
        }

        var networksEv = new MachineNetworksRebuiltEvent(Topology(served, members));
        RaiseLocalEvent(ref networksEv);
    }

    /// <summary>
    /// What's on each served network and what's linked to what, for cyberspace: each switch or router is linked
    /// to every machine on the cable it joins and to the switches and routers that cable (or a rack) reaches.
    /// </summary>
    private List<MachineNetwork> Topology(Dictionary<object, Entity<NetworkHubComponent>> served,
        Dictionary<object, List<EntityUid>> members)
    {
        var networks = new Dictionary<object, MachineNetwork>();
        foreach (var (network, router) in served)
        {
            networks[network] = new MachineNetwork(router.Owner);
        }

        var addressOf = _addresses.ToDictionary(a => a.Value.Machine, a => a.Key);
        foreach (var (network, list) in members)
        {
            if (!networks.TryGetValue(network, out var net))
                continue;

            foreach (var machine in list)
            {
                // Decks have nodes of their own beside the machine they came in through, not pads.
                if (_virtualHosts.ContainsKey(machine))
                    continue;

                if (addressOf.TryGetValue(machine, out var address))
                    net.Hosts.Add((machine, address));
            }

            net.Hosts.Sort((a, b) => a.Address.CompareTo(b.Address));
        }

        var hubs = EntityQueryEnumerator<NetworkHubComponent>();
        while (hubs.MoveNext(out var uid, out var hub))
        {
            if (NetworkOf(uid, hub.Node) is { } network && networks.TryGetValue(network, out var net))
                net.Hubs.Add(uid);
        }

        foreach (var net in networks.Values)
        {
            net.Hubs.Sort();
            var hosts = net.Hosts.Select(h => h.Machine).ToHashSet();
            var links = new HashSet<(EntityUid, EntityUid)>();
            void Link(EntityUid a, EntityUid b)
            {
                if (a != b)
                    links.Add(a.CompareTo(b) < 0 ? (a, b) : (b, a));
            }

            foreach (var hub in net.Hubs)
            {
                if (!TryComp<NetworkHubComponent>(hub, out var hubComp)
                    || !_nodes.TryGetNode(hub, hubComp.Node, out DataHubNode? start))
                {
                    continue;
                }

                var seen = new HashSet<Node> { start };
                var queue = new Queue<Node>(start.ReachableNodes);
                seen.UnionWith(start.ReachableNodes);
                while (queue.TryDequeue(out var node))
                {
                    if (node is DataHubNode)
                    {
                        Link(hub, node.Owner);
                        continue;
                    }

                    if (hosts.Contains(node.Owner))
                    {
                        Link(hub, node.Owner);
                        continue;
                    }

                    if (!HasComp<DataCableComponent>(node.Owner))
                        continue;

                    foreach (var device in _cableDevices.GetValueOrDefault(node.Owner) ?? new List<EntityUid>())
                    {
                        if (hosts.Contains(device))
                            Link(hub, device);
                    }

                    foreach (var next in node.ReachableNodes)
                    {
                        if (seen.Add(next))
                            queue.Enqueue(next);
                    }
                }
            }

            // Hub-to-host links alone lose the cable sides of a firewall: hosts served only by that
            // firewall would each look like an isolated zone. Join the non-firewall members of each
            // adjoining cable segment too, without flooding through any hub into another segment.
            var seenCable = new HashSet<Node>();
            foreach (var firewall in net.Hubs.Where(h => HasComp<FirewallComponent>(h)))
            {
                var hub = Comp<NetworkHubComponent>(firewall);
                if (!_nodes.TryGetNode(firewall, hub.Node, out DataHubNode? start))
                    continue;

                foreach (var port in start.ReachableNodes)
                {
                    if (!HasComp<DataCableComponent>(port.Owner) || !seenCable.Add(port))
                        continue;

                    var side = new HashSet<EntityUid>();
                    var queue = new Queue<Node>();
                    queue.Enqueue(port);
                    while (queue.TryDequeue(out var cable))
                    {
                        foreach (var device in _cableDevices.GetValueOrDefault(cable.Owner) ?? new List<EntityUid>())
                        {
                            if (hosts.Contains(device))
                                side.Add(device);
                        }

                        foreach (var next in cable.ReachableNodes)
                        {
                            if (next is DataHubNode)
                            {
                                if (net.Hubs.Contains(next.Owner) && !HasComp<FirewallComponent>(next.Owner))
                                    side.Add(next.Owner);
                            }
                            else if (hosts.Contains(next.Owner))
                                side.Add(next.Owner);
                            else if (HasComp<DataCableComponent>(next.Owner) && seenCable.Add(next))
                                queue.Enqueue(next);
                        }
                    }

                    // Prefer a hub, preserving the existing links when this side has one. Otherwise a
                    // spanning star is enough to represent the cable, rather than a quadratic clique.
                    var ordered = side.OrderBy(m => net.Hubs.Contains(m) ? 0 : 1).ThenBy(m => m).ToList();
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        Link(ordered[0], ordered[i]);
                    }
                }
            }

            net.Links.AddRange(links.OrderBy(l => l.Item1).ThenBy(l => l.Item2));
        }

        return networks.Values.OrderBy(n => n.Router).ToList();
    }

    /// <summary>
    /// The machine with an address, if one has it.
    /// </summary>
    public EntityUid? MachineAt(uint address)
    {
        return _addresses.TryGetValue(address, out var at) ? at.Machine : null;
    }

    /// <summary>
    /// The address a machine has, if it's on a working network.
    /// </summary>
    public uint? AddressOf(EntityUid machine)
    {
        foreach (var (address, (uid, _)) in _addresses)
        {
            if (uid == machine)
                return address;
        }

        return null;
    }

    /// <summary>
    /// Delivers the packets sent this tick, to be read next tick: each goes to the machine with its address, if
    /// that machine is on the sender's backbone.
    /// </summary>
    private void DeliverPackets()
    {
        foreach (var packet in _sent)
        {
            if (!_addresses.TryGetValue(packet.From, out var from)
                || !_addresses.TryGetValue(packet.To, out var to)
                || from.Map != to.Map
                || !TryComp<WasmMachineComponent>(to.Machine, out var machine))
            {
                continue;
            }

            machine.Vm?.Deliver(packet);
        }

        _sent.Clear();
    }
}

/// <summary>
/// A served network as cyberspace sees it: its router, its switches and routers (the router among them), its
/// machines with their addresses, and the links between them, each pair lowest first.
/// </summary>
public sealed class MachineNetwork(EntityUid router)
{
    public readonly EntityUid Router = router;
    public readonly List<EntityUid> Hubs = new();
    public readonly List<(EntityUid Machine, uint Address)> Hosts = new();
    public readonly List<(EntityUid A, EntityUid B)> Links = new();
}

/// <summary>
/// Raised (broadcast) whenever the machine networks have been worked out again, with every served network.
/// </summary>
[ByRefEvent]
public readonly record struct MachineNetworksRebuiltEvent(List<MachineNetwork> Networks);

/// <summary>
/// Where a machine with no cable joins a network: the network of the machine it's beside, or, with none, a
/// practice network of its own, at a fixed host number.
/// </summary>
public readonly record struct VirtualHost(EntityUid? Beside, int Lan = 0, byte Host = 0);
