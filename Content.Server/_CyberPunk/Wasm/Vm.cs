using System.Linq;
using Content.Shared._CyberPunk.Machines;
using Wasmtime;
using WasmStore = Wasmtime.Store;

namespace Content.Server._CyberPunk.Wasm;

/// <summary>
/// Whether a machine is running.
/// </summary>
public enum VmState : byte
{
    Off,
    Running,

    /// <summary>The OS stopped; it restarts at <see cref="Vm.RebootAtMs"/>.</summary>
    Halted,
}

/// <summary>
/// One machine's virtual machine: its disk, terminal and the stack of WASM programs running on it, the OS at
/// the bottom and the program it started on top, plus any background jobs. Ported from <c>Vm</c> in
/// Switchboard's <c>sb_wasm/src/vm.rs</c>.
/// </summary>
/// <remarks>
/// Only the program in front of each stack runs: one call each tick, its <c>start</c> the first time and its
/// <c>tick</c> after that. A program that starts another (<c>exec</c>) waits under it until it ends. Every
/// program on a machine shares its one <see cref="MachineIo"/>.
/// </remarks>
public sealed class Vm : IDisposable
{
    /// <summary>
    /// The file on a machine's disk it boots instead of the default OS, if it has one.
    /// </summary>
    public const string BootFile = "boot.bin";

    private readonly MachineIo _io = new();

    /// <summary>The terminal's programs, the OS at the bottom.</summary>
    private readonly List<Process> _procs = new();

    /// <summary>Programs running in the background, each with its own stack.</summary>
    private readonly List<Job> _jobs = new();

    private uint _nextPid;
    private bool _bootedBefore;

    /// <summary>What it boots instead of the OS: a device's firmware.</summary>
    private (string Name, Module Module)? _firmware;

    /// <summary>Whether the OS running came from <see cref="BootFile"/>.</summary>
    private bool _bootedFromDisk;

    /// <summary>
    /// Whether <see cref="BootFile"/> stopped, so the machine boots the default OS until it's rebooted or
    /// loses power.
    /// </summary>
    private bool _safeBoot;

    public VmState State { get; private set; } = VmState.Off;

    /// <summary>When a halted machine reboots, by its clock.</summary>
    public ulong RebootAtMs { get; private set; }

    public Vm(DeviceKind kind = DeviceKind.Computer)
    {
        _io.Kind = kind;
    }

    /// <summary>
    /// A device of <paramref name="kind"/> that boots <paramref name="module"/> (firmware) instead of the OS.
    /// </summary>
    public static Vm WithFirmware(string name, Module module, DeviceKind kind)
    {
        return new Vm(kind) { _firmware = (name, module) };
    }

    public DeviceKind Kind
    {
        get => _io.Kind;
        set => _io.Kind = value;
    }

    public MachineDisk Disk => _io.Disk;

    /// <summary>Milliseconds since the machine booted.</summary>
    public ulong ClockMs => _io.ClockMs;

    /// <summary>Whether the terminal is in raw mode (keys go to the program one by one).</summary>
    public bool IsRaw => _io.Raw;

    /// <summary>Whether keys typed at the terminal are echoed onto the line.</summary>
    public bool Echoes => State == VmState.Running && !_io.Raw;

    /// <summary>The line being typed, when the terminal isn't in raw mode.</summary>
    public string Line => _io.Editor.Line;

    /// <summary>The name of the firmware a device boots, if it has some.</summary>
    public string? FirmwareName => _firmware?.Name;

    /// <summary>Names of the programs running at the terminal, the OS first.</summary>
    public IReadOnlyList<string> Processes => _procs.Select(p => p.Name).ToList();

    /// <summary>The background jobs: each one's id and the programs in it.</summary>
    public IReadOnlyList<(uint Id, IReadOnlyList<string> Programs)> Jobs =>
        _jobs.Select(j => (j.Id, (IReadOnlyList<string>) j.Procs.Select(p => p.Name).ToList())).ToList();

    /// <summary>
    /// Whether something is waiting for the machine: typed input, keys or packets.
    /// </summary>
    public bool WantsAttention => _io.Input.Count > 0 || _io.Keys.Count > 0 || _io.Inbox.Count > 0;

    /// <summary>
    /// Whether it has work of its own on: a program in front of the shell, or a background job.
    /// </summary>
    public bool Busy => State == VmState.Running && (_procs.Count > 1 || _jobs.Count > 0);

    /// <summary>
    /// Whether the process with this pid is running, at the terminal or in the background.
    /// </summary>
    public bool IsRunning(uint pid)
    {
        return State == VmState.Running && _procs.Concat(_jobs.SelectMany(j => j.Procs)).Any(p => p.Pid == pid);
    }

    #region Power

    /// <summary>
    /// Powers on and boots the OS (or firmware). Files on the disk are kept.
    /// </summary>
    public void PowerOn(WasmHost host)
    {
        if (State != VmState.Off)
            return;

        _io.ClockMs = 0;
        _io.Input.Clear();
        if (_bootedBefore)
            _io.Output.Append("\n[power restored: rebooting]\n");

        _bootedBefore = true;
        _safeBoot = false;
        Boot(host);
    }

    /// <summary>
    /// Cuts power: everything running stops at once. The disk is kept.
    /// </summary>
    public void PowerOff()
    {
        if (State == VmState.Off)
            return;

        EndAll();
        _io.Input.Clear();
        _io.Exec = null;
        _io.Spawns.Clear();
        _io.Kills.Clear();
        _io.Exit = null;
        _io.Reboot = false;
        _io.UiEvents.Clear();
        _io.Inbox.Clear();
        _io.Outbox.Clear();
        _io.DeviceCommands.Clear();
        _io.Flash = null;
        _io.IceStarts.Clear();
        _io.IceOrders.Clear();
        _io.Raw = false;
        _io.Keys.Clear();
        _io.Editor.Clear();
        State = VmState.Off;
        _io.Output.Append("\n[power lost]\n");
    }

    /// <summary>
    /// Starts the OS: a device's firmware, else <see cref="BootFile"/> from the disk, else the default OS. A
    /// boot file that won't start, or stopped since the last reboot, is passed over for the default OS.
    /// </summary>
    private void Boot(WasmHost host)
    {
        EndAll();
        _io.SetRaw(false);
        _io.Reboot = false;
        _io.UiEvents.Clear();
        _bootedFromDisk = false;

        if (_firmware == null && _io.Disk.Read(BootFile) is { } bytes)
        {
            if (_safeBoot)
            {
                _io.Output.Append($"[{BootFile} stopped: starting the default OS]\n");
            }
            else
            {
                try
                {
                    _procs.Add(Spawn(host, BootFile, host.Load(bytes), ""));
                    _bootedFromDisk = true;
                    State = VmState.Running;
                    return;
                }
                catch (WasmLoadException e)
                {
                    _io.Output.Append($"[{BootFile} won't boot: {e.Message}; starting the default OS]\n");
                }
            }
        }

        var (name, os) = _firmware ?? ("os", host.Os);
        try
        {
            _procs.Add(Spawn(host, name, os, ""));
            State = VmState.Running;
        }
        catch (WasmLoadException e)
        {
            _io.Output.Append($"[boot failed: {e.Message}]\n");
            HaltMachine();
        }
    }

    /// <summary>
    /// Replaces a device's firmware with <paramref name="module"/> and boots it, keeping it through power
    /// cuts.
    /// </summary>
    public void Flash(WasmHost host, string name, Module module)
    {
        _firmware = (name, module);
        if (State == VmState.Off)
            return;

        _io.DeviceCommands.Clear();
        Boot(host);
    }

    /// <summary>
    /// Restarts the machine at once, as a program asked: it tries <see cref="BootFile"/> again.
    /// </summary>
    private void Reboot(WasmHost host)
    {
        _io.Exec = null;
        _io.Exit = null;
        _io.Spawns.Clear();
        _io.Kills.Clear();
        _io.Keys.Clear();
        _io.Editor.Clear();
        _io.Output.Append("\n[rebooting]\n");
        _safeBoot = false;
        Boot(host);
    }

    private void HaltMachine()
    {
        // An OS from the disk that stops isn't booted again until the machine is rebooted.
        if (_bootedFromDisk)
            _safeBoot = true;

        _bootedFromDisk = false;
        State = VmState.Halted;
        RebootAtMs = _io.ClockMs + WasmHost.RebootDelayMs;
    }

    #endregion

    #region Terminal

    /// <summary>
    /// Types a whole line and Enter, for the program in front. Ignored in raw mode.
    /// </summary>
    public void TypeLine(string line)
    {
        if (State != VmState.Running || _io.Raw)
            return;

        _io.Editor.Type(line);
        QueueLine(_io.Editor.Submit());
    }

    /// <summary>
    /// A key pressed at the terminal. In raw mode it goes to the program as it is, dropped when too many are
    /// waiting; otherwise it edits the line, which goes to the program on Enter.
    /// </summary>
    public void TypeKey(int key)
    {
        if (State != VmState.Running || !TerminalKeys.Valid(key))
            return;

        if (!_io.Raw)
        {
            if (_io.Editor.Key(key) is { } line)
                QueueLine(line);
        }
        else if (_io.Keys.Count < WasmHost.KeyLimit)
        {
            _io.Keys.Enqueue(key);
        }
    }

    private void QueueLine(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        if (_io.Input.Count + bytes.Length >= WasmHost.InputLimit)
            return;

        _io.Input.AddRange(bytes);
        _io.Input.Add((byte) '\n');
    }

    /// <summary>
    /// Shows a message from the machine itself on its terminal.
    /// </summary>
    public void Announce(string text)
    {
        _io.Output.Append(text);
    }

    /// <summary>
    /// The terminal output since the last call.
    /// </summary>
    public string TakeOutput()
    {
        var text = _io.Output.ToString();
        _io.Output.Clear();
        return text;
    }

    #endregion

    #region Program UIs

    /// <summary>
    /// The UI of the program in front of the terminal, or null if it shows text.
    /// </summary>
    public ProgramUiNode? Ui => State == VmState.Running && _procs.Count > 0 ? _procs[^1].Ui : null;

    /// <summary>
    /// The terminal window's title: the one the front program gave it, or else the one the program under it
    /// gave, and so on down to the OS. Null if none of them has given one.
    /// </summary>
    public string? Title
    {
        get
        {
            if (State != VmState.Running)
                return null;

            for (var i = _procs.Count - 1; i >= 0; i--)
            {
                if (_procs[i].Title is { } title)
                    return title;
            }

            return null;
        }
    }

    /// <summary>
    /// Someone used a widget of the front program's UI. It's checked against the UI (an id it doesn't have, or
    /// a value the widget can't send, is dropped) and queued for the program's <c>ui_event</c>.
    /// </summary>
    /// <returns>Whether it was queued.</returns>
    public bool UiEvent(string id, ProgramUiEventKind kind, string value)
    {
        if (Ui is not { } root || Find(root, id) is not { } widget || !Valid(widget, kind, value))
            return false;

        var front = _procs[^1].Pid;
        if (_io.UiEventsFor != front)
        {
            _io.UiEvents.Clear();
            _io.UiEventsFor = front;
        }

        if (_io.UiEvents.Count >= WasmHost.UiEventLimit)
            return false;

        _io.UiEvents.Enqueue((id, kind, value));
        return true;
    }

    private static ProgramUiNode? Find(ProgramUiNode node, string id)
    {
        if (node.Id == id)
            return node;

        foreach (var child in node.Children)
        {
            if (Find(child, id) is { } found)
                return found;
        }

        return null;
    }

    private static bool Valid(ProgramUiNode widget, ProgramUiEventKind kind, string value)
    {
        switch (widget.Kind, kind)
        {
            case (ProgramUiKind.Button, ProgramUiEventKind.Click):
                return value == "";
            case (ProgramUiKind.Input, ProgramUiEventKind.Submit):
                return value.Length <= ProgramUiParser.MaxText && !value.Contains('\n');
            case (ProgramUiKind.List, ProgramUiEventKind.Select):
                return int.TryParse(value, out var index) && index >= 0 && index < widget.Items.Length
                       && value == index.ToString();
            case (ProgramUiKind.Canvas, ProgramUiEventKind.Click):
            {
                var parts = value.Split(' ');
                return parts.Length == 2
                       && int.TryParse(parts[0], out var x) && x >= 0 && x < widget.Width && parts[0] == x.ToString()
                       && int.TryParse(parts[1], out var y) && y >= 0 && y < widget.Height && parts[1] == y.ToString();
            }
            default:
                return false;
        }
    }

    #endregion

    #region Disk

    /// <summary>
    /// Stores an uploaded program on the disk, after checking it's one this machine can run.
    /// </summary>
    /// <returns>Why it wasn't stored, or null.</returns>
    public string? Upload(WasmHost host, string name, byte[] wasm)
    {
        if (!MachineDisk.ValidFileName(name))
            return $"\"{name}\" isn't a valid file name";

        try
        {
            host.Load(wasm);
        }
        catch (WasmLoadException e)
        {
            return e.Message;
        }

        return _io.Disk.Write(name, wasm) switch
        {
            DiskError.None => null,
            DiskError.Full => "the disk is full",
            _ => $"\"{name}\" isn't a valid file name",
        };
    }

    /// <summary>
    /// Puts a file on the disk directly, as a map or the city generator does before the machine first runs.
    /// </summary>
    public DiskError SeedFile(string name, byte[] data)
    {
        return _io.Disk.Write(name, data);
    }

    #endregion

    #region Network and devices

    /// <summary>
    /// Sets what the machine sees of the network: its address, who it can reach (null when it isn't
    /// connected), its neighbours, and the hostnames the routers know.
    /// </summary>
    public void SetNetwork(
        uint? address,
        IReadOnlySet<uint>? reachable,
        IReadOnlyList<uint> neighbours,
        IReadOnlyDictionary<string, uint> hosts)
    {
        _io.Address = address;
        _io.Reachable = reachable;
        _io.Neighbours = neighbours;
        _io.Hosts = hosts;
    }

    /// <summary>
    /// The machines with a UI that this machine's programs can make requests of (<c>dev_request</c>).
    /// </summary>
    public IMachineDevices? Devices
    {
        get => _io.Devices;
        set => _io.Devices = value;
    }

    /// <summary>
    /// The name the machine goes by on the network, or empty. Programs set it with <c>net_set_hostname</c>; it
    /// is kept through reboots.
    /// </summary>
    public string Hostname
    {
        get => _io.Hostname;
        set => _io.Hostname = MachineIo.ValidHostname(value) ? value : "";
    }

    /// <summary>
    /// Whether a program changed the hostname since the last call.
    /// </summary>
    public bool TakeHostnameChanged()
    {
        var changed = _io.HostnameChanged;
        _io.HostnameChanged = false;
        return changed;
    }

    /// <summary>
    /// The colour, as #RRGGBB, a program asked the deck to show in since the last call, if any.
    /// </summary>
    public string? TakeDeckColour()
    {
        var colour = _io.DeckColour;
        _io.DeckColour = null;
        return colour;
    }

    /// <summary>
    /// The deck as it stands in cyberspace, for its programs to see; null while it isn't in cyberspace.
    /// </summary>
    public DeckView? DeckView
    {
        set => _io.DeckView = value;
    }

    /// <summary>
    /// What the deck's programs asked of cyberspace since the last call, if anything.
    /// </summary>
    public DeckOrders? TakeDeckOrders()
    {
        if (_io.DeckStrike == null && !_io.DeckWard && _io.DeckHold == null && _io.DeckPush == null)
            return null;

        var orders = new DeckOrders(_io.DeckStrike, _io.DeckWard, _io.DeckHold, _io.DeckPush);
        _io.DeckStrike = null;
        _io.DeckWard = false;
        _io.DeckHold = null;
        _io.DeckPush = null;
        return orders;
    }

    /// <summary>
    /// The ICE its programs run, by pid, for them to see.
    /// </summary>
    public IReadOnlyDictionary<uint, IceView> IceViews
    {
        set => _io.IceViews = value;
    }

    /// <summary>
    /// The pids of the programs that asked for ICE since the last call.
    /// </summary>
    public List<uint> TakeIceStarts()
    {
        var starts = _io.IceStarts.Order().ToList();
        _io.IceStarts.Clear();
        return starts;
    }

    /// <summary>
    /// What its programs asked of their ICE since the last call, in order.
    /// </summary>
    public List<IceOrder> TakeIceOrders()
    {
        var orders = new List<IceOrder>(_io.IceOrders);
        _io.IceOrders.Clear();
        return orders;
    }

    /// <summary>
    /// Hands the machine a packet. False (and dropped) if it isn't running or its inbox is full.
    /// </summary>
    public bool Deliver(Packet packet)
    {
        if (State != VmState.Running || _io.Inbox.Count >= WasmHost.InboxLimit)
            return false;

        _io.Inbox.Enqueue(packet);
        return true;
    }

    /// <summary>
    /// Packets sent since the last call.
    /// </summary>
    public List<Packet> TakeOutbox()
    {
        var packets = new List<Packet>(_io.Outbox);
        _io.Outbox.Clear();
        return packets;
    }

    public int InboxCount => _io.Inbox.Count;

    /// <summary>
    /// Sets the device the machine is wired to, as it stands this tick.
    /// </summary>
    public void SetDevice(MachineDevice? device)
    {
        _io.Device = device;
    }

    /// <summary>
    /// What the programs asked the device to do since the last call, in order.
    /// </summary>
    public List<DeviceCommand> TakeDeviceCommands()
    {
        var commands = new List<DeviceCommand>(_io.DeviceCommands);
        _io.DeviceCommands.Clear();
        return commands;
    }

    /// <summary>
    /// A program asked to flash a device since the last call.
    /// </summary>
    public FirmwareFlash? TakeFlash()
    {
        var flash = _io.Flash;
        _io.Flash = null;
        return flash;
    }

    /// <summary>
    /// Tells a program that a firewall was breached at <paramref name="at"/>: its <c>on_breach_signal</c> runs
    /// just before its next tick. Nothing if it has no such hook.
    /// </summary>
    public void SignalBreach(uint pid, (int X, int Y) at)
    {
        if (State != VmState.Running)
            return;

        foreach (var process in _procs.Concat(_jobs.SelectMany(j => j.Procs)))
        {
            if (process.Pid == pid && process.BreachHook != null)
                process.Breaches.Add(at);
        }
    }

    /// <summary>
    /// Whether the program in front vets people opening the door by hand.
    /// </summary>
    public bool GuardsDoor => State == VmState.Running && _procs.Count > 0 && _procs[^1] is { Started: true, DoorHook: not null };

    /// <summary>
    /// Asks the program in front whether <paramref name="who"/> may open the door, with at most
    /// <paramref name="fuel"/>. Null if it has no hook; a hook that fails keeps the door shut, and the program
    /// is ended as if it had crashed.
    /// </summary>
    public bool? DoorRequest(WasmHost host, Requester who, ulong fuel)
    {
        if (!GuardsDoor)
            return null;

        var process = _procs[^1];
        process.Store.Fuel = fuel;
        _io.Host = host;
        _io.Request = who;
        try
        {
            return process.DoorHook!() != 0;
        }
        catch (WasmtimeException e)
        {
            // A program that exited on the way down (a Wire runtime error) has said why itself.
            if (_io.Exit == null)
            {
                var why = e is TrapException { Type: TrapCode.OutOfFuel }
                    ? "it used up its time budget"
                    : WasmHost.FirstLine(e.Message);
                _io.Output.Append($"\n[{process.Name} crashed in on_door_request: {why}]\n");
            }

            _io.Exit = null;
            EndFront(clean: false);
            return false;
        }
        finally
        {
            _io.Request = null;
        }
    }

    #endregion

    #region Programs

    /// <summary>
    /// Starts a program over whatever is running, as <c>run</c> would, if the machine is at its shell. It runs
    /// from the next tick.
    /// </summary>
    /// <returns>Why it couldn't, or null.</returns>
    public string? Launch(WasmHost host, string file, string args)
    {
        if (State != VmState.Running)
            return "the machine isn't running";

        if (_procs.Count != 1)
            return "the machine is busy running something else";

        if (args.Length > WasmHost.MaxArgs)
            return "its arguments are too long";

        try
        {
            _procs.Add(StartProgram(host, file, args));
            return null;
        }
        catch (WasmLoadException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Ends the process with this pid, and any it started, saying why. The ones below it carry on; a
    /// background job left with nothing ends.
    /// </summary>
    public void Halt(uint pid, string why)
    {
        if (State != VmState.Running)
            return;

        var at = _procs.FindIndex(p => p.Pid == pid);
        if (at >= 0)
        {
            var name = _procs[at].Name;
            Truncate(_procs, at);
            _io.SetRaw(false);
            _io.Output.Append($"\n[{name} halted: {why}]\n");
            if (_procs.Count == 0)
                HaltMachine();

            return;
        }

        foreach (var job in _jobs)
        {
            at = job.Procs.FindIndex(p => p.Pid == pid);
            if (at < 0)
                continue;

            var name = job.Procs[at].Name;
            Truncate(job.Procs, at);
            _io.Output.Append($"\n[job {job.Id}: {name} halted: {why}]\n");
        }

        _jobs.RemoveAll(j => j.Procs.Count == 0);
    }

    /// <summary>
    /// Runs one tick: the program in front of the terminal gets one call (its <c>start</c> if it hasn't
    /// started yet, else its <c>tick</c>) with at most <paramref name="fuel"/>, then each background job's
    /// with what's left.
    /// </summary>
    /// <param name="host">The host to run on.</param>
    /// <param name="dtMs">How far the machine's clock moves on: one tick, or more if it slept.</param>
    /// <param name="fuel">The most fuel this tick may burn.</param>
    /// <returns>The fuel used.</returns>
    public ulong Tick(WasmHost host, ulong dtMs, ulong fuel)
    {
        _io.Host = host;
        _io.ClockMs += dtMs;
        _io.OutputThisTick = 0;
        _io.Truncated = false;
        _io.DeviceRequests = 0;

        switch (State)
        {
            case VmState.Off:
                return 0;
            case VmState.Halted:
                if (_io.ClockMs >= RebootAtMs)
                {
                    _io.Output.Append("[rebooting]\n");
                    Boot(host);
                }

                return 0;
        }

        // The terminal's programs, then each background job's, sharing the fuel.
        _io.Jobs.Clear();
        foreach (var job in _jobs)
        {
            if (job.Procs.Count > 0)
                _io.Jobs.Add((job.Id, job.Procs[^1].Name));
        }

        var (used, ended, clean) = Step(host, _procs, 0, fuel);
        if (ended)
        {
            _io.SetRaw(false);
            if (_procs.Count == 0)
            {
                if (clean)
                    _io.Output.Append("[system halted]\n");

                HaltMachine();
                return used;
            }
        }

        foreach (var job in _jobs)
        {
            if (used >= fuel)
                break;

            used += Step(host, job.Procs, job.Id, fuel - used).Used;
        }

        _jobs.RemoveAll(j => j.Procs.Count == 0);

        if (_io.Reboot)
        {
            Reboot(host);
            return used;
        }

        StartAndKillJobs(host);
        return used;
    }

    /// <summary>
    /// One call into the front program of a stack (its <c>start</c> if it hasn't started yet, else its
    /// <c>tick</c>), with at most <paramref name="fuel"/>. A program it asked to run goes on top.
    /// </summary>
    /// <returns>The fuel used, whether the program ended (it's popped), and whether cleanly.</returns>
    private (ulong Used, bool Ended, bool Clean) Step(WasmHost host, List<Process> stack, uint job, ulong fuel)
    {
        if (stack.Count == 0)
            return (0, false, false);

        var process = stack[^1];
        Action call;
        if (process.Started)
        {
            if (process.TickHook == null)
                return (0, false, false);

            call = process.TickHook;
            if (process.BreachHook is { } breach && process.Breaches.Count > 0)
            {
                var breaches = process.Breaches.ToList();
                process.Breaches.Clear();
                var tick = call;
                call = () =>
                {
                    try
                    {
                        foreach (var at in breaches)
                        {
                            _io.Breach = at;
                            breach();
                        }
                    }
                    finally
                    {
                        _io.Breach = null;
                    }

                    tick();
                };
            }
        }
        else
        {
            process.Started = true;
            call = process.Start;
        }

        process.Store.Fuel = fuel;
        _io.Depth = stack.Count;
        _io.Pid = process.Pid;
        _io.Job = job;
        _io.Rng = process.Rng;
        _io.Args = process.Args;
        _io.UiText = process.UiText;
        _io.UiChange = null;
        _io.TitleChange = null;

        string? crash = null;
        var outOfFuel = false;
        try
        {
            call();
        }
        catch (TrapException e) when (e.Type == TrapCode.OutOfFuel)
        {
            outOfFuel = true;
        }
        catch (WasmtimeException e)
        {
            crash = WasmHost.FirstLine(e.Message);
        }

        process.Rng = _io.Rng;
        if (_io.UiChange is { } ui)
        {
            process.Ui = ui.Root;
            process.UiText = ui.Text;
            _io.UiChange = null;
        }

        if (_io.TitleChange is { } title)
        {
            process.Title = title == "" ? null : title;
            _io.TitleChange = null;
        }

        var used = fuel - process.Store.Fuel;
        var ok = !outOfFuel && crash == null;
        var name = job == 0 ? process.Name : $"job {job}: {process.Name}";

        bool finished;
        if (outOfFuel)
        {
            _io.Output.Append($"\n[{name} killed: it used up its time budget]\n");
            finished = true;
        }
        else if (crash != null && _io.Exit == null)
        {
            _io.Output.Append($"\n[{name} crashed: {crash}]\n");
            finished = true;
        }
        else
        {
            finished = _io.Exit != null || process.TickHook == null;
        }

        var exit = _io.Exit;
        var exec = _io.Exec;
        _io.Exit = null;
        _io.Exec = null;

        if (finished)
        {
            stack.RemoveAt(stack.Count - 1);
            process.Dispose();
            return (used, true, exit != null || ok);
        }

        // The program asked to run another.
        if (exec is { } asked)
        {
            var (file, args) = asked;
            try
            {
                stack.Add(StartProgram(host, file, args));
            }
            catch (WasmLoadException e)
            {
                _io.Output.Append($"[could not run {file}: {e.Message}]\n");
            }
        }

        return (used, false, false);
    }

    /// <summary>
    /// Starts the background jobs programs asked for this tick, and stops the ones they asked to kill.
    /// </summary>
    private void StartAndKillJobs(WasmHost host)
    {
        foreach (var id in _io.Kills)
        {
            var at = _jobs.FindIndex(j => j.Id == id);
            if (at < 0)
                continue;

            var job = _jobs[at];
            _jobs.RemoveAt(at);
            var name = job.Procs.Count > 0 ? job.Procs[^1].Name : "";
            Truncate(job.Procs, 0);
            _io.Output.Append($"[job {id}: {name} killed]\n");
        }

        _io.Kills.Clear();

        foreach (var (id, file, args) in _io.Spawns)
        {
            if (_jobs.Count >= WasmHost.MaxJobs)
            {
                _io.Output.Append($"[could not run {file}: too many jobs]\n");
                continue;
            }

            try
            {
                _jobs.Add(new Job(id, [StartProgram(host, file, args)]));
            }
            catch (WasmLoadException e)
            {
                _io.Output.Append($"[could not run {file}: {e.Message}]\n");
            }
        }

        _io.Spawns.Clear();
    }

    /// <summary>
    /// Ends the program in front, and its raw mode with it. If it was the OS, the machine halts and reboots
    /// soon.
    /// </summary>
    private void EndFront(bool clean)
    {
        Truncate(_procs, _procs.Count - 1);
        _io.SetRaw(false);
        if (_procs.Count > 0)
            return;

        if (clean)
            _io.Output.Append("[system halted]\n");

        HaltMachine();
    }

    private Process StartProgram(WasmHost host, string file, string args)
    {
        Module module;
        if (_io.Disk.Read(file) is { } bytes)
            module = host.Load(bytes);
        else
            module = host.SystemProgram(file) ?? throw new WasmLoadException("no such file");

        return Spawn(host, file, module, args);
    }

    /// <summary>
    /// Instantiates a program in a store of its own. Its start-up code (a WASM start section) runs here, with
    /// a call's worth of fuel.
    /// </summary>
    private Process Spawn(WasmHost host, string name, Module module, string args)
    {
        var store = host.NewStore(_io);
        try
        {
            store.Fuel = WasmHost.FuelPerCall;
            _io.Host = host;
            _io.Args = args;

            Instance instance;
            try
            {
                instance = host.Instantiate(store, module);
            }
            catch (WasmtimeException e)
            {
                throw new WasmLoadException(WasmHost.FirstLine(e.Message));
            }

            if (instance.GetMemory("memory") == null)
                throw new WasmLoadException("it exports no memory");

            var start = instance.GetAction("start")
                        ?? throw new WasmLoadException("its `start` entry point has the wrong signature");

            _nextPid++;
            return new Process
            {
                Pid = _nextPid,
                Rng = unchecked(_nextPid * 0x9E37_79B9_7F4A_7C15UL) | 1,
                Name = name,
                Args = args,
                Store = store,
                Start = start,
                TickHook = instance.GetAction("tick"),
                DoorHook = instance.GetFunction<int>("on_door_request"),
                BreachHook = instance.GetAction("on_breach_signal"),
            };
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Ends every process in a stack from <paramref name="from"/> up.
    /// </summary>
    private static void Truncate(List<Process> stack, int from)
    {
        for (var i = stack.Count - 1; i >= from; i--)
        {
            stack[i].Dispose();
            stack.RemoveAt(i);
        }
    }

    private void EndAll()
    {
        Truncate(_procs, 0);
        foreach (var job in _jobs)
        {
            Truncate(job.Procs, 0);
        }

        _jobs.Clear();
    }

    #endregion

    public void Dispose()
    {
        EndAll();
    }

    /// <summary>
    /// A running program.
    /// </summary>
    private sealed class Process : IDisposable
    {
        public required uint Pid;

        /// <summary>Its random number state, seeded from its pid.</summary>
        public required ulong Rng;

        public required string Name;
        public required string Args;
        public required WasmStore Store;
        public required Action Start;
        public required Action? TickHook;

        /// <summary>Its <c>on_door_request</c> hook, if it has one.</summary>
        public required Func<int>? DoorHook;

        /// <summary>Its <c>on_breach_signal</c> hook, if it has one.</summary>
        public required Action? BreachHook;

        /// <summary>Breached firewalls its <c>on_breach_signal</c> hasn't heard of yet.</summary>
        public readonly List<(int X, int Y)> Breaches = new();

        public bool Started;

        /// <summary>The UI it shows while it's in front, and the text it came from.</summary>
        public ProgramUiNode? Ui;

        public string UiText = "";

        /// <summary>The title it gives the terminal window while it's in front, if any.</summary>
        public string? Title;

        public void Dispose()
        {
            Store.Dispose();
        }
    }

    /// <summary>
    /// A background job: programs started with <c>run FILE &amp;</c>, with no terminal of their own.
    /// </summary>
    private sealed record Job(uint Id, List<Process> Procs);
}
