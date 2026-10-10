using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._CyberPunk.Cyberspace;
using Content.Server._CyberPunk.Machines;
using Content.Server._CyberPunk.Wasm;
using Content.Server.Body.Components;
using Content.Shared._CyberPunk.Cyberspace;
using Content.Shared._CyberPunk.Machines;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Power.EntitySystems;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CyberPunk;

/// <summary>
/// A routed network gets a region of cyberspace, with a node on a pad for every machine on it, paths between
/// the pads of linked machines and out to the bus, and barriers along the paths. Unplug a machine and its pad
/// goes; plug it back in and it comes back where it was.
/// </summary>
[TestFixture]
public sealed class CyberspaceTest : GameTest
{
    private IEntityManager _entMan = default!;
    private Entity<MapGridComponent> _grid;

    [Test]
    public async Task NetworksGetRegionsOfCyberspace()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var machines = _entMan.System<WasmMachineSystem>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var lookup = _entMan.System<EntityLookupSystem>();

        EntityUid a = default, b = default, vending = default, router = default, gap = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 2; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                var cable = Place("CableData", x, 0);
                if (x == 4)
                    gap = cable;
            }

            a = Place("ComputerProgrammable", 0, 0);
            vending = Place("VendingMachineCola", 2, 0);
            b = Place("ComputerProgrammable", 5, 0);
            router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { a, b, vending, router })
            {
                power.SetNeedsPower(ent, false);
            }
        });

        await Pair.RunTicksSync(30);

        Vector2i bTile = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.MapUid, Is.Not.Null, "the first network makes cyberspace");
            Assert.That(cyberspace.RegionOf(router), Is.Not.Null, "the network has a region");

            var routerNode = cyberspace.NodeOf(router);
            Assert.That(routerNode, Is.Not.Null);
            var subnet = (machines.AddressOf(a)!.Value >> 16) & 255;
            Assert.That(Name(routerNode!.Value), Is.EqualTo($"router 10.{subnet}"));

            foreach (var machine in new[] { a, b, vending })
            {
                var node = cyberspace.NodeOf(machine);
                Assert.That(node, Is.Not.Null, $"{machine} has a node");
                Assert.That(Reaches(cyberspace, Tile(routerNode.Value), Tile(node!.Value)), $"{machine}'s pad is joined to the router's");
                Assert.That(cyberspace.FloorAt(Tile(node.Value).X, Tile(node.Value).Y), Is.EqualTo(CyberFloor.Node));
            }

            Assert.That(Name(cyberspace.NodeOf(a)!.Value), Does.StartWith("computer 10."));
            Assert.That(Name(cyberspace.NodeOf(vending)!.Value), Does.StartWith("device 10."));

            // The router's pad opens onto the bus, and the bus leads to the backbone.
            var backbone = _entMan.AllEntities<CyberNodeComponent>()
                .Single(n => n.Comp.Kind == CyberNodeKind.Backbone);
            Assert.That(Reaches(cyberspace, Tile(routerNode.Value), Tile(backbone)));

            // Every tile beside a path that can't be walked on is walled off.
            var mapId = _entMan.GetComponent<TransformComponent>(cyberspace.MapUid!.Value).MapID;
            var rect = cyberspace.RegionRect(cyberspace.RegionOf(router)!.Value)!.Value;
            var checkedOne = false;
            for (var y = rect.Y; y < rect.Y + rect.H; y++)
            {
                for (var x = rect.X; x < rect.X + rect.W; x++)
                {
                    if (CyberLayout.Walkable(cyberspace.FloorAt(x, y)) || !CyberLayout.Walkable(cyberspace.FloorAt(x + 1, y)))
                        continue;

                    var box = Box2.CenteredAround(new Vector2(x + 0.5f, y + 0.5f), new Vector2(0.5f, 0.5f));
                    var hit = lookup.GetEntitiesIntersecting(mapId, box)
                        .Any(e => _entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "CyberspaceBarrier");
                    Assert.That(hit, $"a barrier at {x}, {y}");
                    checkedOne = true;
                }
            }

            Assert.That(checkedOne);

            // Unplug B.
            bTile = Tile(cyberspace.NodeOf(b)!.Value);
            _entMan.DeleteEntity(gap);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(b), Is.Null, "an unplugged machine has no pad");
            Assert.That(cyberspace.FloorAt(bTile.X, bTile.Y), Is.Not.EqualTo(CyberFloor.Node));
            Place("CableData", 4, 0);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(b), Is.Not.Null);
            Assert.That(Tile(cyberspace.NodeOf(b)!.Value), Is.EqualTo(bTile), "plugged back in, it's where it was");

            // Without a powered router the region empties.
            power.SetNeedsPower(router, true);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(router), Is.Null);
            Assert.That(cyberspace.NodeOf(a), Is.Null);
        });
    }

    /// <summary>
    /// A cyberdeck jacks its holder in at an access point: their mind goes into a virtual body on a spur beside
    /// the access point's pad, which joins the network as a deck at .200 up. Jacking out by choice costs
    /// nothing; losing the deck throws them out with dumpshock. In the hand, the deck raises a practice grid.
    /// </summary>
    [Test]
    public async Task DecksJackRunnersIn()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var machines = _entMan.System<WasmMachineSystem>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();

        EntityUid computer = default, accessPoint = default, runner = default, deck = default, other = default, otherDeck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            computer = Place("ComputerProgrammable", 0, 0);
            accessPoint = Place("AccessPoint", 3, 0);
            var router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { computer, accessPoint, router })
            {
                power.SetNeedsPower(ent, false);
            }

            (runner, deck) = Runner(minds, hands, godmode, 3, 1);
            (other, otherDeck) = Runner(minds, hands, godmode, 5, 2);
        });

        await Pair.RunTicksSync(30);

        EntityUid avatar = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(accessPoint), Is.Not.Null, "the access point has a pad");
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            avatar = a!.Value;

            var mind = minds.GetMind(runner)!.Value;
            Assert.That(_entMan.GetComponent<MindComponent>(mind).VisitingEntity, Is.EqualTo(avatar));
            Assert.That(_entMan.GetComponent<TransformComponent>(avatar).MapUid, Is.EqualTo(cyberspace.MapUid));

            // The runner stands on their deck's own pad, on a spur off the access point's.
            var at = Tile(avatar);
            Assert.That(cyberspace.FloorAt(at.X, at.Y), Is.EqualTo(CyberFloor.Node));
            Assert.That(Reaches(cyberspace, at, Tile(cyberspace.NodeOf(accessPoint)!.Value)));
            Assert.That(at, Is.Not.EqualTo(Tile(cyberspace.NodeOf(accessPoint)!.Value)));
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var address = machines.AddressOf(avatar);
            Assert.That(address, Is.Not.Null, "the deck is on the network");
            Assert.That(address!.Value & 0xFF, Is.EqualTo(200));
            Assert.That(address.Value & 0xFFFFFF00, Is.EqualTo(machines.AddressOf(computer)!.Value & 0xFFFFFF00));
            Assert.That(Name(cyberspace.NodeOf(avatar)!.Value), Does.StartWith("deck 10."));

            // Jacking out by choice: no dumpshock, and the deck leaves the network.
            cyberspace.JackOut(runner, "", false);
            Assert.That(cyberspace.IsJackedIn(runner, out _), Is.False);
            Assert.That(_entMan.GetComponent<MindComponent>(minds.GetMind(runner)!.Value).VisitingEntity, Is.Null);
            Assert.That(cyberspace.NodeOf(avatar), Is.Null);
        });

        // The virtual body lets go of their mind at the end of the tick.
        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            // Back in, then the deck leaves their hands.
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var again) && again == avatar, "the same virtual body");
            hands.TryDrop(runner, deck, checkActionBlocker: false);
        });

        await Pair.RunTicksSync(5);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.IsJackedIn(runner, out _), Is.False, "losing the deck throws them out");
            hands.TryPickupAnyHand(runner, deck, checkActionBlocker: false);
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true), Is.False, "dumpshock");

            // Practice, from the deck in the hand.
            Assert.That(cyberspace.TryPractise(other, otherDeck));
            Assert.That(cyberspace.IsJackedIn(other, out var practising));
            var at = Tile(practising!.Value);
            Assert.That(cyberspace.FloorAt(at.X, at.Y), Is.EqualTo(CyberFloor.Node));
            Assert.That(Reaches(cyberspace, at, Tile(cyberspace.NodeOf(accessPoint)!.Value)), Is.False, "practice is cut off");
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.IsJackedIn(other, out var practising));
            var trainingServer = cyberspace.PracticeServer(0);
            Assert.That(trainingServer, Is.Not.Null);
            Assert.That(machines.AddressOf(trainingServer!.Value), Is.EqualTo(WasmMachineSystem.PracticeAddress(0, 2)));
            Assert.That(machines.AddressOf(practising!.Value), Is.EqualTo(WasmMachineSystem.PracticeAddress(0, 3)));

            cyberspace.JackOut(other, "", false);
            Assert.That(cyberspace.PracticeServer(0), Is.Null, "the grid comes down");
        });
    }

    /// <summary>
    /// A virtual body takes its runner's shape: their species' body with its hands and markings. It wears a
    /// runner's uniform, not their clothes, and a proxy ID that opens what their body's ID opens, whichever that
    /// is now. It's an avatar, though: it doesn't breathe, and it bleeds ghostlight.
    /// </summary>
    [Test]
    public async Task AvatarsLookLikeTheirRunners()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var inventory = _entMan.System<InventorySystem>();
        var visualBody = _entMan.System<SharedVisualBodySystem>();
        var access = _entMan.System<AccessReaderSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(_grid, Vector2i.Zero, new Tile(1));

            var (runner, deck) = Runner(minds, hands, godmode, 0, 0, "MobReptilian");
            Assert.That(inventory.TryEquip(runner, Place("ClothingUniformJumpsuitColorGrey", 0, 0), "jumpsuit", force: true));
            Assert.That(inventory.TryEquip(runner, Place("ClothingHeadsetGrey", 0, 0), "ears", force: true));
            Assert.That(inventory.TryEquip(runner, Place("CaptainIDCard", 0, 0), "id", force: true));

            Assert.That(cyberspace.TryPractise(runner, deck));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            var avatar = a!.Value;

            Assert.That(hands.GetHandCount(avatar), Is.EqualTo(2));
            Assert.That(_entMan.GetComponent<HumanoidProfileComponent>(avatar).Species.Id, Is.EqualTo("CyberAvatar"));
            Assert.That(_entMan.HasComponent<RespiratorComponent>(avatar), Is.False);
            var blood = _entMan.GetComponent<BloodstreamComponent>(avatar).BloodReferenceSolution;
            Assert.That(blood.Contents.Select(r => r.Reagent.Prototype), Is.EqualTo(new[] { "Ghostlight" }));

            Assert.That(Markings(visualBody, avatar), Is.EquivalentTo(Markings(visualBody, runner)));
            Assert.That(Markings(visualBody, avatar), Does.Contain("LizardTailSmooth"));

            Assert.That(inventory.TryGetSlotEntity(avatar, "jumpsuit", out var jumpsuit));
            Assert.That(_entMan.GetComponent<MetaDataComponent>(jumpsuit!.Value).EntityPrototype!.ID, Is.EqualTo("ClothingUniformCyberAvatar"));
            Assert.That(inventory.TryGetSlotEntity(avatar, "ears", out _), Is.False, "none of their clothes");
            Assert.That(_entMan.GetComponent<CyberAvatarLookComponent>(avatar).Tint, Is.Not.EqualTo(Color.White));

            Assert.That(inventory.TryGetSlotEntity(avatar, "id", out var proxy));
            Assert.That(_entMan.HasComponent<CyberProxyIdComponent>(proxy), "a proxy ID");
            Assert.That(inventory.TryUnequip(avatar, "id"), Is.False, "it doesn't come off");
            Assert.That(access.FindAccessTags(avatar), Does.Contain(new ProtoId<AccessLevelPrototype>("Captain")));

            Assert.That(inventory.TryUnequip(runner, "id"));
            Assert.That(access.FindAccessTags(avatar), Does.Not.Contain(new ProtoId<AccessLevelPrototype>("Captain")),
                "it shows the ID their body wears now");

            cyberspace.JackOut(runner, "", false);
        });
    }

    private List<string> Markings(SharedVisualBodySystem visualBody, EntityUid body)
    {
        Assert.That(visualBody.TryGatherMarkingsData(body, null, out _, out _, out var applied));
        return applied!.Values.SelectMany(layers => layers.Values).SelectMany(m => m).Select(m => m.MarkingId.Id).ToList();
    }

    /// <summary>
    /// A runner uses a computer's node and its terminal opens, and stays open though the computer is on another
    /// map. A locked computer, or another runner's deck, opens once they've stood at its node long enough to
    /// breach it. Jacking out closes
    /// them, and the computer gets its range back.
    /// </summary>
    [Test]
    public async Task RunnersOpenMachinesFromTheirNodes()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var interaction = _entMan.System<SharedInteractionSystem>();
        var ui = _entMan.System<SharedUserInterfaceSystem>();
        var access = _entMan.System<AccessReaderSystem>();

        EntityUid computer = default, locked = default, accessPoint = default, runner = default, deck = default;
        EntityUid rival = default, rivalDeck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            computer = Place("ComputerProgrammable", 0, 0);
            locked = Place("ComputerProgrammable", 5, 0);
            accessPoint = Place("AccessPoint", 3, 0);
            var router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { computer, locked, accessPoint, router })
            {
                power.SetNeedsPower(ent, false);
            }

            var reader = _entMan.EnsureComponent<AccessReaderComponent>(locked);
            access.TryAddAccess((locked, reader), "Captain");

            (runner, deck) = Runner(minds, hands, godmode, 3, 1);
            (rival, rivalDeck) = Runner(minds, hands, godmode, 4, 1);
        });

        await Pair.RunTicksSync(30);

        EntityUid avatar = default, rivalAvatar = default;
        float range = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            avatar = a!.Value;
            Assert.That(ui.TryGetInterfaceData(computer, MachineTerminalUiKey.Key, out var data));
            range = data!.InteractionRange;

            Stand(avatar, cyberspace.NodeOf(computer)!.Value);
            interaction.InteractionActivate(avatar, cyberspace.NodeOf(computer)!.Value);
            Assert.That(ui.IsUiOpen(computer, MachineTerminalUiKey.Key, avatar));
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(ui.IsUiOpen(computer, MachineTerminalUiKey.Key, avatar), "it stays open across maps");

            Stand(avatar, cyberspace.NodeOf(locked)!.Value);
            interaction.InteractionActivate(avatar, cyberspace.NodeOf(locked)!.Value);
            Assert.That(ui.IsUiOpen(locked, MachineTerminalUiKey.Key, avatar), Is.False, "it's locked");

            // Another runner's deck is always locked.
            Assert.That(cyberspace.TryJackIn(rival, rivalDeck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(rival, out var r));
            rivalAvatar = r!.Value;
            Stand(rivalAvatar, cyberspace.NodeOf(avatar)!.Value);
            interaction.InteractionActivate(rivalAvatar, cyberspace.NodeOf(avatar)!.Value);
            Assert.That(ui.IsUiOpen(avatar, MachineTerminalUiKey.Key, rivalAvatar), Is.False, "another runner's deck is locked");
        });

        await Pair.RunTicksSync(250);
        await server.WaitAssertion(() =>
        {
            Assert.That(ui.IsUiOpen(locked, MachineTerminalUiKey.Key, avatar), "breached, it opens");
            Assert.That(ui.IsUiOpen(avatar, MachineTerminalUiKey.Key, rivalAvatar), "breached, the deck opens");

            cyberspace.JackOut(rival, "", false);

            cyberspace.JackOut(runner, "", false);
        });

        await Pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            foreach (var machine in new[] { computer, locked })
            {
                Assert.That(ui.IsUiOpen(machine, MachineTerminalUiKey.Key, avatar), Is.False);
                Assert.That(ui.TryGetInterfaceData(machine, MachineTerminalUiKey.Key, out var data));
                Assert.That(data!.InteractionRange, Is.EqualTo(range), "the range comes back");
            }
        });
    }

    /// <summary>
    /// A network that outgrows its region gets a bigger one, with a node for every machine, and the runners in
    /// it come along.
    /// </summary>
    [Test]
    public async Task NetworksOutgrowTheirRegions()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();

        const int count = 40;
        EntityUid accessPoint = default, router = default, runner = default, deck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < count + 4; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            accessPoint = Place("AccessPoint", 1, 0);
            router = Place("NetworkRouter", 0, 1);
            power.SetNeedsPower(accessPoint, false);
            power.SetNeedsPower(router, false);
            (runner, deck) = Runner(minds, hands, godmode, 1, 1);
        });

        await Pair.RunTicksSync(30);

        CyberRect small = default;
        var machines = new List<EntityUid>();
        await server.WaitAssertion(() =>
        {
            small = cyberspace.RegionRect(cyberspace.RegionOf(router)!.Value)!.Value;
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            for (var i = 0; i < count; i++)
            {
                var machine = Place("VendingMachineCola", i + 3, 0);
                power.SetNeedsPower(machine, false);
                machines.Add(machine);
            }
        });

        await Pair.RunTicksSync(30);
        await server.WaitAssertion(() =>
        {
            var rect = cyberspace.RegionRect(cyberspace.RegionOf(router)!.Value)!.Value;
            Assert.That(rect.W * rect.H, Is.GreaterThan(small.W * small.H), "the region grew");
            foreach (var machine in machines)
            {
                Assert.That(cyberspace.NodeOf(machine), Is.Not.Null, $"{machine} has a node");
            }

            Assert.That(cyberspace.IsJackedIn(runner, out var avatar), "the runner came along");
            var at = Tile(avatar!.Value);
            Assert.That(rect.Contains(at.X, at.Y));
            Assert.That(CyberLayout.Walkable(cyberspace.FloorAt(at.X, at.Y)));
        });
    }

    /// <summary>
    /// Consoles on data cable get nodes like any machine with a UI, and a runner opens them from there.
    /// </summary>
    [Test]
    [TestCase("ComputerCrewMonitoring")]
    [TestCase("ComputerPowerMonitoring")]
    [TestCase("ComputerStationRecords")]
    public async Task RunnersOpenConsolesFromTheirNodes(string prototype)
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var interaction = _entMan.System<SharedInteractionSystem>();
        var ui = _entMan.System<SharedUserInterfaceSystem>();

        EntityUid console = default, accessPoint = default, runner = default, deck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            console = Place(prototype, 0, 0);
            accessPoint = Place("AccessPoint", 3, 0);
            var router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { console, accessPoint, router })
            {
                power.SetNeedsPower(ent, false);
            }

            (runner, deck) = Runner(minds, hands, godmode, 3, 1);
        });

        await Pair.RunTicksSync(30);

        // One built on cable once the network's up, and one that has cable run to it.
        EntityUid builtOnCable = default, cabledLater = default;
        await server.WaitAssertion(() =>
        {
            builtOnCable = Place(prototype, 5, 0);
            cabledLater = Place(prototype, 4, 2);
            power.SetNeedsPower(builtOnCable, false);
            power.SetNeedsPower(cabledLater, false);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Place("CableData", 4, 1);
            Place("CableData", 4, 2);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(builtOnCable), Is.Not.Null, "a console built on cable has a node");
            Assert.That(cyberspace.NodeOf(cabledLater), Is.Not.Null, "a console cabled later has a node");

            var node = cyberspace.NodeOf(console);
            Assert.That(node, Is.Not.Null, "the console has a node");
            Assert.That(Name(node!.Value), Does.StartWith("device 10."));

            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var avatar));

            var key = _entMan.GetComponent<ActivatableUIComponent>(console).Key;
            Stand(avatar!.Value, node.Value);
            interaction.InteractionActivate(avatar.Value, node.Value);
            Assert.That(ui.IsUiOpen(console, key, avatar.Value));
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.IsJackedIn(runner, out var avatar));
            Assert.That(ui.IsUiOpen(console, _entMan.GetComponent<ActivatableUIComponent>(console).Key, avatar!.Value),
                "it stays open across maps");
        });
    }

    /// <summary>
    /// A deck comes with a programmable computer's examples. Its runner holds programs in their virtual hands and
    /// pushes files onto computers they may use, and a blade used on another runner strikes them until they're cut
    /// out of cyberspace.
    /// </summary>
    [Test]
    public async Task RunnersHoldProgramsPushFilesAndStrike()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var machines = _entMan.System<WasmMachineSystem>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var interaction = _entMan.System<SharedInteractionSystem>();
        var access = _entMan.System<AccessReaderSystem>();

        EntityUid free = default, locked = default, accessPoint = default, runner = default, deck = default;
        EntityUid rival = default, rivalDeck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            free = Place("ComputerProgrammable", 0, 0);
            locked = Place("ComputerProgrammable", 5, 0);
            accessPoint = Place("AccessPoint", 3, 0);
            var router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { free, locked, accessPoint, router })
            {
                power.SetNeedsPower(ent, false);
            }

            var reader = _entMan.EnsureComponent<AccessReaderComponent>(locked);
            access.TryAddAccess((locked, reader), "Captain");

            (runner, deck) = Runner(minds, hands, godmode, 3, 1);
            (rival, rivalDeck) = Runner(minds, hands, godmode, 4, 1);
        });

        await Pair.RunTicksSync(30);

        EntityUid avatar = default, rivalAvatar = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.TryJackIn(rival, rivalDeck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            Assert.That(cyberspace.IsJackedIn(rival, out var r));
            avatar = a!.Value;
            rivalAvatar = r!.Value;

            var disk = _entMan.GetComponent<WasmMachineComponent>(avatar).Vm!.Disk;
            Assert.That(disk.Read("examples/hello.wire"), Is.Not.Null, "the deck has the computer's examples");
            Assert.That(disk.Read("readme.txt"), Is.Not.Null);
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var at = (avatar, _entMan.GetComponent<WasmMachineComponent>(avatar));
            machines.TypeLine(at, "hold blade");
            machines.TypeLine(at, "write note.txt hello");
            machines.TypeLine(at, "push " + MachineIo.FormatAddress(machines.AddressOf(locked)!.Value) + " note.txt");
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var screen = _entMan.GetComponent<WasmMachineComponent>(avatar).Screen;
            Assert.That(screen, Does.Contain("[hold: blade is in your hand]"));
            Assert.That(screen, Does.Contain("[push: refused, its card reader wants its owner's card]"));
            Assert.That(hands.EnumerateHeld(avatar).Any(e =>
                _entMan.TryGetComponent<CyberProgramComponent>(e, out var program) && program.File == "blade"));

            Stand(avatar, cyberspace.NodeOf(free)!.Value);
            machines.TypeLine((avatar, _entMan.GetComponent<WasmMachineComponent>(avatar)), "push note.txt");
        });

        await Pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.GetComponent<WasmMachineComponent>(avatar).Screen, Does.Contain("[push: note.txt copied]"));
            var disk = _entMan.GetComponent<WasmMachineComponent>(free).Vm!.Disk;
            Assert.That(disk.Read("note.txt"), Is.Not.Null);

            // The rival steps up beside them, and they go for the rival with the blade.
            Stand(rivalAvatar, cyberspace.NodeOf(free)!.Value);
            var blade = hands.EnumerateHeld(avatar).First(e => _entMan.HasComponent<CyberProgramComponent>(e));
            interaction.InteractDoAfter(avatar, blade, rivalAvatar,
                _entMan.GetComponent<TransformComponent>(rivalAvatar).Coordinates, true);
        });

        await Pair.RunTicksSync(150);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.IsJackedIn(rival, out _), Is.False, "cut out of cyberspace");
            Assert.That(cyberspace.TryJackIn(rival, rivalDeck, accessPoint, true), Is.False, "dumpshock");
            Assert.That(cyberspace.IsJackedIn(runner, out _), "the striker stays");
            Assert.That(_entMan.GetComponent<WasmMachineComponent>(avatar).Screen, Does.Contain("blade: going for"));

            cyberspace.JackOut(runner, "", false);
        });
    }

    /// <summary>
    /// A computer's program puts ICE on its pad. A runner's blade derezzes it and halts the program; ICE left
    /// to it engages a runner it sees and strikes them out of cyberspace.
    /// </summary>
    [Test]
    public async Task IceGuardsNetworks()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var machines = _entMan.System<WasmMachineSystem>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var interaction = _entMan.System<SharedInteractionSystem>();
        var access = _entMan.System<AccessReaderSystem>();

        EntityUid guard = default, accessPoint = default, runner = default, deck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                Place("CableData", x, 0);
            }

            guard = Place("ComputerProgrammable", 5, 0);
            accessPoint = Place("AccessPoint", 3, 0);
            var router = Place("NetworkRouter", 1, 1);
            foreach (var ent in new[] { guard, accessPoint, router })
            {
                power.SetNeedsPower(ent, false);
            }

            var reader = _entMan.EnsureComponent<AccessReaderComponent>(guard);
            access.TryAddAccess((guard, reader), "Captain");

            (runner, deck) = Runner(minds, hands, godmode, 3, 1);
        });

        await Pair.RunTicksSync(30);

        EntityUid avatar = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            avatar = a!.Value;
            machines.TypeLine((guard, _entMan.GetComponent<WasmMachineComponent>(guard)), "run ice_basic &");
            machines.TypeLine((avatar, _entMan.GetComponent<WasmMachineComponent>(avatar)), "hold blade");
        });

        await Pair.RunTicksSync(30);

        EntityUid ice = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.GetComponent<WasmMachineComponent>(guard).Screen,
                Does.Contain("[ice: in cyberspace, guarding this network]"));
            ice = IceOf(guard);
            Assert.That(ice, Is.Not.EqualTo(EntityUid.Invalid), "ICE stands guard");

            Stand(avatar, ice);
            var blade = hands.EnumerateHeld(avatar).First(e => _entMan.HasComponent<CyberProgramComponent>(e));
            interaction.InteractDoAfter(avatar, blade, ice, _entMan.GetComponent<TransformComponent>(ice).Coordinates, true);
        });

        await Pair.RunTicksSync(90);
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.EntityExists(ice), Is.False, "four strikes derez it");
            Assert.That(_entMan.GetComponent<WasmMachineComponent>(guard).Screen, Does.Contain("its ICE was derezzed by"));
            machines.TypeLine((guard, _entMan.GetComponent<WasmMachineComponent>(guard)), "run ice_basic &");
        });

        await Pair.RunTicksSync(30);
        await server.WaitAssertion(() =>
        {
            ice = IceOf(guard);
            Assert.That(ice, Is.Not.EqualTo(EntityUid.Invalid));
            Stand(avatar, ice);
        });

        await Pair.RunTicksSync(300);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.IsJackedIn(runner, out _), Is.False, "the ICE threw them out");
            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true), Is.False, "dumpshock");
            Assert.That(_entMan.EntityExists(ice), "the ICE stays");
        });
    }

    /// <summary>
    /// A firewall between the router and a computer shuts the only way to the computer's pad, until a runner whose
    /// ID it doesn't pass breaches it.
    /// </summary>
    [Test]
    public async Task FirewallsShutTheWayPast()
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var minds = _entMan.System<SharedMindSystem>();
        var hands = _entMan.System<SharedHandsSystem>();
        var godmode = _entMan.System<SharedGodmodeSystem>();
        var interaction = _entMan.System<SharedInteractionSystem>();
        var access = _entMan.System<AccessReaderSystem>();
        var machines = _entMan.System<WasmMachineSystem>();

        EntityUid router = default, firewall = default, inside = default, outside = default;
        EntityUid accessPoint = default, runner = default, deck = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 7; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));
                }

                // The firewall's tile has no cable: it joins the cable on either side.
                if (x != 3)
                    Place("CableData", x, 0);
            }

            router = Place("NetworkRouter", 0, 1);
            outside = Place("ComputerProgrammable", 1, 0);
            accessPoint = Place("AccessPoint", 2, 0);
            firewall = Place("NetworkFirewall", 3, 0);
            inside = Place("ComputerProgrammable", 5, 0);
            foreach (var ent in new[] { router, outside, accessPoint, firewall, inside })
            {
                power.SetNeedsPower(ent, false);
            }

            access.TryAddAccess((firewall, _entMan.GetComponent<AccessReaderComponent>(firewall)), "Captain");

            // Its ICE passes everyone, so it only moves on the breach.
            _entMan.EnsureComponent<AccessReaderComponent>(inside);
            (runner, deck) = Runner(minds, hands, godmode, 2, 1);
        });

        await Pair.RunTicksSync(30);

        EntityUid gate = default, avatar = default;
        await server.WaitAssertion(() =>
        {
            gate = cyberspace.NodeOf(firewall)!.Value;
            Assert.That(_entMan.GetComponent<CyberNodeComponent>(gate).Kind, Is.EqualTo(CyberNodeKind.Firewall));
            Assert.That(_entMan.HasComponent<CyberFirewallGateComponent>(gate));

            var centre = Tile(gate);
            var pad = new HashSet<Vector2i>();
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    pad.Add(centre + new Vector2i(dx, dy));
                }
            }

            var from = Tile(cyberspace.NodeOf(router)!.Value);
            Assert.That(Reaches(cyberspace, from, Tile(cyberspace.NodeOf(inside)!.Value)), "the way leads across the firewall");
            Assert.That(Reaches(cyberspace, from, Tile(cyberspace.NodeOf(inside)!.Value), pad), Is.False,
                "and nowhere else");
            Assert.That(Reaches(cyberspace, from, Tile(cyberspace.NodeOf(outside)!.Value), pad));

            Assert.That(cyberspace.TryJackIn(runner, deck, accessPoint, true));
            Assert.That(cyberspace.IsJackedIn(runner, out var a));
            avatar = a!.Value;
            machines.TypeLine((inside, _entMan.GetComponent<WasmMachineComponent>(inside)), "run ice_basic &");

            // Up to the firewall from the router's side, at the end of the path into its pad.
            var side = new[] { new Vector2i(0, 2), new Vector2i(2, 0), new Vector2i(0, -2), new Vector2i(-2, 0) }
                .Select(step => centre + step)
                .First(tile => CyberLayout.Walkable(cyberspace.FloorAt(tile.X, tile.Y)) && Reaches(cyberspace, from, tile, pad));
            var xforms = _entMan.System<SharedTransformSystem>();
            xforms.SetCoordinates(avatar, new EntityCoordinates(cyberspace.MapUid!.Value, side.X + 0.5f, side.Y + 0.5f));
        });

        await Pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.GetComponent<CyberFirewallGateComponent>(gate).Passes, Does.Not.Contain(avatar),
                "shut to a runner without the card");
            interaction.InteractionActivate(avatar, gate);
        });

        await Pair.RunTicksSync(30);
        await server.WaitAssertion(() =>
        {
            var ice = IceOf(inside);
            Assert.That(ice, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(_entMan.GetComponent<IceComponent>(ice).Mode, Is.Not.EqualTo(IceMode.Engaging));
        });

        await Pair.RunTicksSync(90);
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.GetComponent<CyberFirewallGateComponent>(gate).Passes, Does.Contain(avatar),
                "breached, it lets them through");
            Assert.That(_entMan.GetComponent<IceComponent>(IceOf(inside)).Mode, Is.EqualTo(IceMode.Engaging),
                "and the network's ICE is on its way");
            cyberspace.JackOut(runner, "", false);
        });

        await Pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(_entMan.GetComponent<CyberFirewallGateComponent>(gate).Passes, Is.Empty, "shut again once they leave");
        });
    }

    /// <summary>
    /// A firewall joins cable segments, not individual hosts. Closing it must leave every cable side
    /// navigable internally, including sides with no ordinary switch and more than four machines.
    /// </summary>
    [TestCase(2, 6)]
    [TestCase(3, 2)]
    [TestCase(4, 2)]
    public async Task FirewallCableSidesStayNavigable(int sideCount, int hostsPerSide)
    {
        var server = Pair.Server;
        _entMan = server.ResolveDependency<IEntityManager>();
        var cyberspace = _entMan.System<CyberspaceSystem>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var power = _entMan.System<SharedPowerReceiverSystem>();
        var sides = new List<List<EntityUid>>();
        EntityUid firewall = default, router = default;
        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            _grid = mapSys.CreateGridEntity(mapId);
            for (var x = -7; x <= 7; x++)
            for (var y = -7; y <= 7; y++)
                mapSys.SetTile(_grid, new Vector2i(x, y), new Tile(1));

            firewall = Place("NetworkFirewall", 0, 0);
            router = Place("NetworkRouter", -6, -1);
            power.SetNeedsPower(firewall, false);
            power.SetNeedsPower(router, false);
            var directions = new[] { new Vector2i(-1, 0), new Vector2i(1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };
            for (var side = 0; side < sideCount; side++)
            {
                var members = new List<EntityUid>();
                sides.Add(members);
                for (var step = 1; step <= 6; step++)
                {
                    var tile = directions[side] * step;
                    Place("CableData", tile.X, tile.Y);
                    if (step <= hostsPerSide)
                    {
                        var host = Place("ComputerProgrammable", tile.X, tile.Y);
                        power.SetNeedsPower(host, false);
                        members.Add(host);
                    }
                }
            }
            sides[0].Add(router);
        });

        await Pair.RunTicksSync(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(cyberspace.NodeOf(firewall), Is.Not.Null);
            var centre = Tile(cyberspace.NodeOf(firewall)!.Value);
            var blocked = new HashSet<Vector2i>();
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                blocked.Add(centre + new Vector2i(dx, dy));

            var from = Tile(cyberspace.NodeOf(router)!.Value);
            for (var side = 0; side < sides.Count; side++)
            {
                var origin = Tile(cyberspace.NodeOf(sides[side][0])!.Value);
                for (var other = 0; other < sides.Count; other++)
                foreach (var host in sides[other])
                {
                    Assert.That(cyberspace.NodeOf(host), Is.Not.Null);
                    var target = Tile(cyberspace.NodeOf(host)!.Value);
                    Assert.That(Reaches(cyberspace, from, target), $"open firewall reaches {host}");
                    Assert.That(Reaches(cyberspace, origin, target, blocked), Is.EqualTo(side == other),
                        $"closed firewall, side {side} to side {other}, host {host}");
                }
            }
        });
    }

    /// <summary>
    /// The ICE a computer's program runs, or none.
    /// </summary>
    private EntityUid IceOf(EntityUid computer)
    {
        var query = _entMan.EntityQueryEnumerator<IceComponent>();
        while (query.MoveNext(out var uid, out var ice))
        {
            if (ice.Computer == computer)
                return uid;
        }

        return EntityUid.Invalid;
    }

    /// <summary>
    /// Puts a runner's virtual body on a node.
    /// </summary>
    private void Stand(EntityUid avatar, EntityUid node)
    {
        var xforms = _entMan.System<SharedTransformSystem>();
        xforms.SetCoordinates(avatar, _entMan.GetComponent<TransformComponent>(node).Coordinates);
    }

    /// <summary>
    /// Someone with a mind, who can't be hurt, holding a cyberdeck.
    /// </summary>
    private (EntityUid, EntityUid) Runner(SharedMindSystem minds, SharedHandsSystem hands, SharedGodmodeSystem godmode,
        int x, int y, string species = "MobHuman")
    {
        var mob = Place(species, x, y);
        godmode.EnableGodmode(mob);
        var mind = minds.CreateMind(null);
        minds.TransferTo(mind, mob, mind: mind);
        var deck = Place("Cyberdeck", x, y);
        Assert.That(hands.TryPickupAnyHand(mob, deck, checkActionBlocker: false));
        return (mob, deck);
    }

    private EntityUid Place(string prototype, int x, int y)
    {
        return _entMan.SpawnEntity(prototype, new EntityCoordinates(_grid, x + 0.5f, y + 0.5f));
    }

    private string Name(EntityUid uid)
    {
        return _entMan.GetComponent<MetaDataComponent>(uid).EntityName;
    }

    private Vector2i Tile(EntityUid uid)
    {
        var pos = _entMan.GetComponent<TransformComponent>(uid).LocalPosition;
        return new Vector2i((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
    }

    /// <summary>
    /// Whether one tile of cyberspace can be walked to from another, without crossing any tile of
    /// <paramref name="blocked"/>.
    /// </summary>
    private static bool Reaches(CyberspaceSystem cyberspace, Vector2i from, Vector2i to, HashSet<Vector2i> blocked = null)
    {
        var seen = new HashSet<Vector2i> { from };
        var queue = new Queue<Vector2i>();
        queue.Enqueue(from);
        while (queue.TryDequeue(out var at))
        {
            if (at == to)
                return true;

            foreach (var step in new[] { new Vector2i(0, 1), new Vector2i(1, 0), new Vector2i(0, -1), new Vector2i(-1, 0) })
            {
                var next = at + step;
                if (CyberLayout.Walkable(cyberspace.FloorAt(next.X, next.Y)) && blocked?.Contains(next) != true && seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return false;
    }
}
