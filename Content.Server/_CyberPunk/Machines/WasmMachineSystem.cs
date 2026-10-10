using System.Diagnostics;
using System.Linq;
using System.Text;
using Content.Server._CyberPunk.Wasm;
using Content.Server.Power.EntitySystems;
using Content.Shared._CyberPunk.Machines;
using Content.Shared.Power;

namespace Content.Server._CyberPunk.Machines;

/// <summary>
/// Runs a <see cref="Vm"/> for every <see cref="WasmMachineComponent"/>: boots it when it gets power, stops it
/// when it loses power, and runs it 30 times a second. Ported from the scheduling in Switchboard's
/// <c>sb_wasm/src/lib.rs</c>.
/// </summary>
/// <remarks>
/// Each machine gets one call within <see cref="WasmHost.FuelPerCall"/> each time it runs, and all machines
/// together share <see cref="FuelPerTick"/> and <see cref="TimePerTick"/> of real time. Machines over the
/// budget wait for the next tick, first in line, so no number of busy computers can slow the server down. A
/// machine that waited is told how much time passed, so its clock is always right.
/// Packets go over the network (WasmMachineSystem.Network.cs), and computers' programs reach machines with a
/// UI of their own over it (WasmMachineSystem.Devices.cs). Door and camera controllers have no devices yet, so
/// what their programs tell devices to do is dropped.
/// </remarks>
public sealed partial class WasmMachineSystem : EntitySystem
{
    [Dependency] private WasmHostSystem _wasm = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    /// <summary>Machine ticks a second, as in Switchboard: a program's <c>tick</c> hook runs this often.</summary>
    public const int TickRate = 30;

    /// <summary>How far a machine's clock moves in one tick.</summary>
    public const ulong TickMs = 1000 / TickRate;

    /// <summary>Fuel all machines together may burn in one tick.</summary>
    public const ulong FuelPerTick = 12 * WasmHost.FuelPerCall;

    /// <summary>Real time all machines together may take in one tick: a fifth of a tick.</summary>
    public static readonly TimeSpan TimePerTick = TimeSpan.FromMilliseconds(6);

    /// <summary>Machine ticks run in one server update at most, so a slow update doesn't snowball.</summary>
    private const int MaxTicksPerUpdate = 2;

    private const float TickSeconds = 1f / TickRate;

    private float _accumulator;
    private ulong _tick;
    private readonly List<Entity<WasmMachineComponent>> _due = new();
    private readonly Stopwatch _watch = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WasmMachineComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<WasmMachineComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<WasmMachineComponent, PowerChangedEvent>(OnPowerChanged);
        InitializeNetwork();
        InitializeDevices();

        Subs.BuiEvents<WasmMachineComponent>(MachineTerminalUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnTerminalOpened);
            subs.Event<MachineTerminalRefreshMessage>(OnTerminalRefresh);
            subs.Event<MachineTerminalKeysMessage>(OnTerminalKeys);
            subs.Event<MachineTerminalUiEventMessage>(OnTerminalUiEvent);
        });
    }

    public override void Shutdown()
    {
        base.Shutdown();

        var query = EntityQueryEnumerator<WasmMachineComponent>();
        while (query.MoveNext(out var machine))
        {
            machine.Vm?.Dispose();
            machine.Vm = null;
        }
    }

    private void OnMapInit(Entity<WasmMachineComponent> ent, ref MapInitEvent args)
    {
        var vm = new Vm(ent.Comp.Kind);
        var files = new Dictionary<string, string>();
        if (ent.Comp.FilesFrom is { } from && ProtoMan.Index(from).TryComp<WasmMachineComponent>(out var other, Factory))
            files = new Dictionary<string, string>(other.Files);

        foreach (var (name, text) in ent.Comp.Files)
        {
            files[name] = text;
        }

        foreach (var (name, text) in files)
        {
            var error = vm.SeedFile(name, Encoding.UTF8.GetBytes(text));
            if (error != DiskError.None)
                Log.Error($"Couldn't put {name} on the disk of {ToPrettyString(ent)}: {error}");
        }

        vm.Hostname = ent.Comp.Hostname;
        vm.Devices = new DeviceLink(this, ent);
        ent.Comp.Hostname = vm.Hostname;
        ent.Comp.Vm = vm;
        _networkDirty = true;
        if (_power.IsPowered(ent))
            PowerOn(ent);
    }

    private void OnShutdown(Entity<WasmMachineComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Vm?.Dispose();
        ent.Comp.Vm = null;
        _networkDirty = true;
    }

    private void OnPowerChanged(Entity<WasmMachineComponent> ent, ref PowerChangedEvent args)
    {
        if (ent.Comp.Vm is not { } vm)
            return;

        if (args.Powered)
        {
            PowerOn(ent);
        }
        else
        {
            vm.PowerOff();
            CollectOutput(ent);
        }
    }

    private void PowerOn(Entity<WasmMachineComponent> ent)
    {
        ent.Comp.Vm!.PowerOn(_wasm.Host);
        ent.Comp.LastRun = _tick;
        CollectOutput(ent);
    }

    /// <summary>
    /// Switches on or off a machine that has no power supply, like a deck when its runner jacks in or out. What's
    /// on its disk stays.
    /// </summary>
    public void SetRunning(Entity<WasmMachineComponent> ent, bool on)
    {
        if (ent.Comp.Vm is not { } vm)
            return;

        if (on && vm.State == VmState.Off)
        {
            PowerOn(ent);
        }
        else if (!on && vm.State != VmState.Off)
        {
            vm.PowerOff();
            CollectOutput(ent);
        }
    }

    /// <summary>
    /// Types a line at a machine's terminal, for the program in front to read.
    /// </summary>
    public void TypeLine(Entity<WasmMachineComponent> ent, string line)
    {
        if (line.Length > TerminalText.MaxLine)
            line = line[..TerminalText.MaxLine];

        ent.Comp.Vm?.TypeLine(line);
    }

    /// <summary>
    /// Presses a key at a machine's terminal.
    /// </summary>
    public void TypeKey(Entity<WasmMachineComponent> ent, int key)
    {
        ent.Comp.Vm?.TypeKey(key);
    }

    /// <summary>
    /// Puts a file on a machine's disk.
    /// </summary>
    public DiskError WriteFile(Entity<WasmMachineComponent> ent, string name, byte[] data)
    {
        return ent.Comp.Vm?.SeedFile(name, data) ?? DiskError.Full;
    }

    /// <summary>
    /// Sets what a deck's programs see of cyberspace; null while it isn't there.
    /// </summary>
    public void SetDeckView(Entity<WasmMachineComponent?> ent, DeckView? view)
    {
        if (Resolve(ent, ref ent.Comp, false) && ent.Comp.Vm is { } vm)
            vm.DeckView = view;
    }

    /// <summary>
    /// Sets what a computer's programs see of the ICE they run, by pid.
    /// </summary>
    public void SetIceViews(Entity<WasmMachineComponent?> ent, IReadOnlyDictionary<uint, IceView> views)
    {
        if (Resolve(ent, ref ent.Comp, false) && ent.Comp.Vm is { } vm)
            vm.IceViews = views;
    }

    /// <summary>
    /// Whether a program is running on a machine, by its pid.
    /// </summary>
    public bool IsRunning(Entity<WasmMachineComponent?> ent, uint pid)
    {
        return Resolve(ent, ref ent.Comp, false) && ent.Comp.Vm is { } vm && vm.IsRunning(pid);
    }

    /// <summary>
    /// Tells a program on a machine, by its pid, that a firewall was breached at a tile.
    /// </summary>
    public void SignalBreach(Entity<WasmMachineComponent?> ent, uint pid, (int X, int Y) at)
    {
        if (Resolve(ent, ref ent.Comp, false))
            ent.Comp.Vm?.SignalBreach(pid, at);
    }

    /// <summary>
    /// Ends a program on a machine, by its pid, saying why on its terminal.
    /// </summary>
    public void Halt(Entity<WasmMachineComponent?> ent, uint pid, string why)
    {
        if (!Resolve(ent, ref ent.Comp, false) || ent.Comp.Vm is not { } vm)
            return;

        vm.Halt(pid, why);
        CollectOutput((ent.Owner, ent.Comp));
    }

    /// <summary>
    /// Starts a program at a machine's shell, as <c>run</c> would.
    /// </summary>
    /// <returns>Why it couldn't, or null.</returns>
    public string? Launch(Entity<WasmMachineComponent?> ent, string file, string args)
    {
        if (!Resolve(ent, ref ent.Comp, false) || ent.Comp.Vm is not { } vm)
            return "the machine isn't running";

        return vm.Launch(_wasm.Host, file, args);
    }

    /// <summary>
    /// Shows a line on a machine's terminal, as if its programs had printed it.
    /// </summary>
    public void Announce(Entity<WasmMachineComponent?> ent, string text)
    {
        if (!Resolve(ent, ref ent.Comp, false) || ent.Comp.Vm is not { } vm)
            return;

        vm.Announce(text);
        CollectOutput((ent.Owner, ent.Comp));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        for (var i = 0; i < MaxTicksPerUpdate && _accumulator >= TickSeconds; i++)
        {
            _accumulator -= TickSeconds;
            RunMachines();
        }

        _accumulator = Math.Min(_accumulator, TickSeconds);
    }

    /// <summary>
    /// One machine tick: one call for each running machine, those that have waited longest first, within the
    /// fuel and time budgets.
    /// </summary>
    private void RunMachines()
    {
        _tick++;
        _due.Clear();

        if (_networkDirty)
            RebuildNetwork();

        var query = EntityQueryEnumerator<WasmMachineComponent>();
        while (query.MoveNext(out var uid, out var machine))
        {
            if (machine.Vm == null)
                continue;

            if (machine.Vm.State == VmState.Off)
                CollectOutput((uid, machine));
            else
                _due.Add((uid, machine));
        }

        if (_due.Count == 0)
            return;

        _due.Sort((a, b) => a.Comp.LastRun != b.Comp.LastRun
            ? a.Comp.LastRun.CompareTo(b.Comp.LastRun)
            : a.Owner.CompareTo(b.Owner));

        var host = _wasm.Host;
        var budget = FuelPerTick;
        _watch.Restart();
        foreach (var ent in _due)
        {
            if (budget < WasmHost.FuelPerCall || _watch.Elapsed >= TimePerTick)
                break;

            var machine = ent.Comp;
            var vm = machine.Vm!;

            // However long it waited, its clock moves on by that much.
            var waited = Math.Max(1, _tick - machine.LastRun);
            machine.LastRun = _tick;
            budget -= Math.Min(budget, vm.Tick(host, waited * TickMs, WasmHost.FuelPerCall));

            _sent.AddRange(vm.TakeOutbox());
            if (vm.TakeHostnameChanged())
            {
                machine.Hostname = vm.Hostname;
                _networkDirty = true;
            }

            if (vm.TakeDeckColour() is { } colour)
            {
                var ev = new DeckColourChangedEvent(Color.FromHex(colour));
                RaiseLocalEvent(ent, ref ev);
            }

            if (vm.TakeDeckOrders() is { } orders)
            {
                var ev = new DeckOrdersEvent(orders);
                RaiseLocalEvent(ent, ref ev);
            }

            if (vm.TakeIceStarts() is { Count: > 0 } starts)
            {
                var ev = new IceStartedEvent(starts);
                RaiseLocalEvent(ent, ref ev);
            }

            if (vm.TakeIceOrders() is { Count: > 0 } iceOrders)
            {
                var ev = new IceOrdersEvent(iceOrders);
                RaiseLocalEvent(ent, ref ev);
            }

            // Nothing is wired to a device yet.
            vm.TakeDeviceCommands();
            vm.TakeFlash();

            CollectOutput(ent);
        }

        DeliverPackets();
    }

    /// <summary>
    /// Adds what a machine's programs printed to its screen, and sends it to everyone with its terminal open,
    /// with the front program's UI when it has changed.
    /// </summary>
    private void CollectOutput(Entity<WasmMachineComponent> ent)
    {
        var open = _ui.IsUiOpen(ent.Owner, MachineTerminalUiKey.Key);
        var text = ent.Comp.Vm?.TakeOutput();
        if (!string.IsNullOrEmpty(text))
        {
            ent.Comp.Screen = TerminalText.Apply(ent.Comp.Screen, text);
            if (open)
                _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalOutputMessage(text));
        }

        var title = ent.Comp.Vm?.Title;
        if (title != ent.Comp.ShownTitle)
        {
            ent.Comp.ShownTitle = title;
            if (open)
                _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalTitleMessage(title));
        }

        var echo = ent.Comp.Vm?.Echoes ?? false;
        if (echo != ent.Comp.ShownEcho)
        {
            ent.Comp.ShownEcho = echo;
            if (open)
                _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalEchoMessage(echo));
        }

        // Each UI a program shows is a new tree, and showing the same text again keeps the old one.
        var ui = ent.Comp.Vm?.Ui;
        if (ReferenceEquals(ui, ent.Comp.ShownUi))
            return;

        ent.Comp.ShownUi = ui;
        if (open)
            _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalUiMessage(ui));
    }

    // A window the client opened itself asks for the screen before the server knows it's open, so that request
    // is dropped; the server sends the screen when it opens the terminal instead. A window the server opened
    // only exists once the client hears it's open, so it asks for the screen itself.
    private void OnTerminalOpened(Entity<WasmMachineComponent> ent, ref BoundUIOpenedEvent args)
    {
        SendScreen(ent, args.Actor);
    }

    private void OnTerminalRefresh(Entity<WasmMachineComponent> ent, ref MachineTerminalRefreshMessage args)
    {
        SendScreen(ent, args.Actor);
    }

    private void SendScreen(Entity<WasmMachineComponent> ent, EntityUid actor)
    {
        CollectOutput(ent);
        _ui.ServerSendUiMessage(ent.Owner,
            MachineTerminalUiKey.Key,
            new MachineTerminalScreenMessage(ent.Comp.Screen),
            actor);
        _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalUiMessage(ent.Comp.ShownUi), actor);
        _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalTitleMessage(ent.Comp.ShownTitle), actor);
        _ui.ServerSendUiMessage(ent.Owner, MachineTerminalUiKey.Key, new MachineTerminalEchoMessage(ent.Comp.ShownEcho), actor);
    }

    private void OnTerminalUiEvent(Entity<WasmMachineComponent> ent, ref MachineTerminalUiEventMessage args)
    {
        // Messages come from clients, so anything may be missing; the machine checks the rest.
        if (ent.Comp.Vm is { } vm && args.Id is { } id && args.Value is { } value)
            vm.UiEvent(id, args.Kind, value);
    }

    private void OnTerminalKeys(Entity<WasmMachineComponent> ent, ref MachineTerminalKeysMessage args)
    {
        // Messages come from clients, so the keys may be missing.
        if (args.Keys is not { } keys)
            return;

        foreach (var key in keys.Take(WasmHost.KeyLimit))
        {
            TypeKey(ent, key);
        }

        // The echo goes out now rather than on the next machine tick, ahead of the answer that tells the
        // sender to stop showing its own.
        CollectOutput(ent);
        _ui.ServerSendUiMessage(ent.Owner,
            MachineTerminalUiKey.Key,
            new MachineTerminalKeysHandledMessage(args.Sequence, ent.Comp.Vm?.Line ?? ""),
            args.Actor);
    }
}

/// <summary>
/// Raised on a deck when its programs ask something of cyberspace: to strike, to ward, to hold a program or to
/// push a file.
/// </summary>
[ByRefEvent]
public readonly record struct DeckOrdersEvent(DeckOrders Orders);

/// <summary>
/// Raised on a computer when programs on it ask for ICE, with their pids.
/// </summary>
[ByRefEvent]
public readonly record struct IceStartedEvent(List<uint> Pids);

/// <summary>
/// Raised on a computer when its programs give the ICE they run orders.
/// </summary>
[ByRefEvent]
public readonly record struct IceOrdersEvent(List<IceOrder> Orders);

/// <summary>
/// Raised on a deck when a program on it sets the colour its runner's virtual body shows in.
/// </summary>
[ByRefEvent]
public readonly record struct DeckColourChangedEvent(Color Colour);
