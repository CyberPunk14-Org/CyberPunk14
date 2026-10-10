using System.Linq;
using System.Text;
using Content.Shared._CyberPunk.Machines;

namespace Content.Server._CyberPunk.Wasm;

/// <summary>
/// A packet between machines.
/// </summary>
public sealed record Packet(uint From, uint To, ushort Port, byte[] Data);

/// <summary>
/// The device a machine is wired to, as it stands this tick.
/// </summary>
public abstract record MachineDevice;

/// <summary>
/// A door: whether it's open, whether something stands in it (so it won't close), and whether it's bolted.
/// </summary>
public sealed record DoorDevice(bool Open, bool Blocked, bool Bolted) : MachineDevice;

/// <summary>
/// A camera, and the names of the people it sees.
/// </summary>
public sealed record CameraDevice(IReadOnlyList<string> People) : MachineDevice;

/// <summary>
/// What a program asked its device to do this tick. The world carries it out after the tick.
/// </summary>
public enum DeviceCommand : byte
{
    OpenDoor,
    CloseDoor,
    BoltDoor,
    UnboltDoor,
}

/// <summary>
/// Someone trying a guarded door, as its controller's <c>on_door_request</c> hook sees them.
/// </summary>
/// <param name="Name">Their name.</param>
/// <param name="Holding">What's in their active hand, or empty.</param>
/// <param name="Cards">The organization tags of the ID cards they carry.</param>
public sealed record Requester(string Name, string Holding, IReadOnlyList<string> Cards);

/// <summary>
/// A program a computer is sending to a device's firmware.
/// </summary>
public sealed record FirmwareFlash(uint To, string Name, byte[] Bytes);

/// <summary>
/// Something a deck sees in cyberspace: its id, whether it's ICE (else a runner), its name and integrity,
/// whether it's close enough to strike, and how far it is in tiles.
/// </summary>
public sealed record DeckTarget(int Id, bool Ice, string Name, int Integrity, bool InReach, int Distance);

/// <summary>
/// A deck as it stands in cyberspace this tick, and what it sees, nearest first.
/// </summary>
public sealed record DeckView(int Integrity, bool Warded, bool StrikeReady, bool WardReady, IReadOnlyList<DeckTarget> Targets);

/// <summary>
/// A file a deck is copying to a computer: the one at <paramref name="Address"/>, or with 0 the one whose pad its
/// runner stands at. <paramref name="Reachable"/> is whether the deck reaches that address over the network.
/// </summary>
public sealed record DeckPush(uint Address, bool Reachable, string File, byte[] Data);

/// <summary>
/// What a deck's programs asked of cyberspace in one tick. The world carries it out after the tick.
/// </summary>
public sealed record DeckOrders(int? Strike, bool Ward, string? Hold, DeckPush? Push);

/// <summary>
/// A runner an ICE sees: their id, the node they're nearest (0 if none), whether their ID passes its computer's
/// reader, whether they're close enough to strike, the tile they stand on and their name.
/// </summary>
public sealed record IceRunner(int Id, int Node, bool Authorized, bool InReach, int X, int Y, string Name);

/// <summary>
/// An ICE as it stands in cyberspace this tick, for the program running it: the tile it's on, the node it's at
/// (-1 if none), the nodes of the network it guards, the nodes linked to the one it's at, the runners it sees,
/// and where a completed trace says an intruder is.
/// </summary>
public sealed record IceView(
    int Integrity,
    int X,
    int Y,
    long Here,
    IReadOnlyList<uint> Nodes,
    IReadOnlyList<uint> Neighbours,
    IReadOnlyList<IceRunner> Runners,
    (int X, int Y)? Alert);

public enum IceOrderKind : byte
{
    Go,
    Chase,
    Attack,
    GoTo,
    Mode,
}

/// <summary>
/// Something a program asked of the ICE it runs, by its pid. The world carries it out after the tick.
/// </summary>
public readonly record struct IceOrder(uint Pid, IceOrderKind Kind, int A, int B = 0);

/// <summary>
/// The machines with a UI of their own that a computer's programs reach over the network.
/// </summary>
public interface IMachineDevices
{
    /// <summary>
    /// The answer of the machine at <paramref name="address"/> to a request (<c>info</c>, <c>state</c> or
    /// <c>call NAME ARGS</c>): a value as Wire's <c>repr</c> writes it, or <c>!</c> and why it wasn't done.
    /// Null when no such machine is there.
    /// </summary>
    string? Request(uint address, string request);
}

/// <summary>
/// Everything about a machine that a program can touch through the kernel. Every store a machine's programs
/// run in carries the machine's one <see cref="MachineIo"/>, and the VM sets which process is being called
/// before each call.
/// </summary>
public sealed class MachineIo
{
    /// <summary>Device commands a machine can give in one tick, every program on it together.</summary>
    public const int CommandsPerTick = 32;

    /// <summary>Requests a machine's programs can make of other machines in one tick, every program together.</summary>
    public const int DeviceRequestsPerTick = 8;

    /// <summary>The longest a hostname can be.</summary>
    public const int MaxHostname = 32;

    public readonly MachineDisk Disk = new();

    /// <summary>The host running this machine's current call.</summary>
    public WasmHost Host = default!;

    public DeviceKind Kind;

    public readonly StringBuilder Output = new();
    public int OutputThisTick;
    public bool Truncated;

    /// <summary>The line being typed, when the terminal isn't in raw mode.</summary>
    public readonly LineEditor Editor;

    /// <summary>Lines typed and waiting to be read.</summary>
    public readonly List<byte> Input = new();

    public ulong ClockMs;

    /// <summary>A program the process being called asked to run when the call returns: its file and arguments.</summary>
    public (string File, string Args)? Exec;

    public int? Exit;

    /// <summary>A program asked to restart the machine once the call returns.</summary>
    public bool Reboot;

    /// <summary>The UI text of the program being called, so showing the same UI again costs nothing.</summary>
    public string UiText = "";

    /// <summary>The UI the program being called set (a null root clears it), and its text.</summary>
    public (ProgramUiNode? Root, string Text)? UiChange;

    /// <summary>The title the program being called gave the terminal window ("" gives it back).</summary>
    public string? TitleChange;

    /// <summary>What people did to the front program's UI, for <c>ui_event</c>.</summary>
    public readonly Queue<(string Id, ProgramUiEventKind Kind, string Value)> UiEvents = new();

    /// <summary>The process <see cref="UiEvents"/> are for: only it can take them.</summary>
    public uint UiEventsFor;

    /// <summary>How many programs are stacked where the process being called runs, it included.</summary>
    public int Depth;

    /// <summary>The process being called.</summary>
    public uint Pid;

    /// <summary>The background job the process being called is in, or 0 for the terminal's programs.</summary>
    public uint Job;

    /// <summary>Background jobs asked for this tick: their id, file and arguments.</summary>
    public readonly List<(uint Id, string File, string Args)> Spawns = new();

    /// <summary>Jobs asked to stop this tick.</summary>
    public readonly List<uint> Kills = new();

    /// <summary>The jobs running, as the process being called sees them: their id and the program in front.</summary>
    public readonly List<(uint Id, string Name)> Jobs = new();

    public uint NextJob;

    /// <summary>The random number state of the process being called.</summary>
    public ulong Rng;

    /// <summary>The arguments of the process being called.</summary>
    public string Args = "";

    public uint? Address;

    /// <summary>Who the machine can reach, or null when it isn't connected.</summary>
    public IReadOnlySet<uint>? Reachable;

    public IReadOnlyList<uint> Neighbours = Array.Empty<uint>();

    /// <summary>The name the machine goes by on the network, or empty. Kept through reboots.</summary>
    public string Hostname = "";

    /// <summary>Whether a program changed <see cref="Hostname"/> since the world last looked.</summary>
    public bool HostnameChanged;

    /// <summary>A colour, as #RRGGBB, a program asked the deck to show in since the world last looked.</summary>
    public string? DeckColour;

    /// <summary>The deck as it stands in cyberspace, set by the world before each tick; null on anything else.</summary>
    public DeckView? DeckView;

    /// <summary>The target a program asked the deck to strike since the world last looked.</summary>
    public int? DeckStrike;

    /// <summary>Whether a program asked the deck to raise a ward since the world last looked.</summary>
    public bool DeckWard;

    /// <summary>The program a program asked the runner to hold since the world last looked.</summary>
    public string? DeckHold;

    public DeckPush? DeckPush;

    /// <summary>The ICE each program runs, by pid, set by the world before each tick.</summary>
    public IReadOnlyDictionary<uint, IceView> IceViews = new Dictionary<uint, IceView>();

    /// <summary>The programs that asked for ICE since the world last looked, by pid.</summary>
    public readonly HashSet<uint> IceStarts = new();

    public readonly List<IceOrder> IceOrders = new();

    /// <summary>The tile of the breached firewall, while <c>on_breach_signal</c> runs.</summary>
    public (int X, int Y)? Breach;

    /// <summary>Every hostname the routers know on the machine's network, and its address.</summary>
    public IReadOnlyDictionary<string, uint> Hosts = new Dictionary<string, uint>();

    public readonly Queue<Packet> Inbox = new();
    public readonly List<Packet> Outbox = new();

    public MachineDevice? Device;
    public readonly List<DeviceCommand> DeviceCommands = new();

    /// <summary>Whether the terminal is in raw mode.</summary>
    public bool Raw;

    /// <summary>Keys waiting to be read in raw mode.</summary>
    public readonly Queue<int> Keys = new();

    /// <summary>Who is trying the door, while <c>on_door_request</c> runs.</summary>
    public Requester? Request;

    public FirmwareFlash? Flash;

    /// <summary>The machines with a UI on the network, for <c>dev_request</c>.</summary>
    public IMachineDevices? Devices;

    /// <summary>Requests made of other machines this tick, every program on this one together.</summary>
    public int DeviceRequests;

    public MachineIo()
    {
        Editor = new LineEditor(Output);
    }

    /// <summary>
    /// Whether this machine counts as <paramref name="kind"/>, for kernel functions only some machines have.
    /// Decks and implants are computers, with more.
    /// </summary>
    public bool Is(DeviceKind kind)
    {
        return Kind == kind || kind == DeviceKind.Computer && Kind is DeviceKind.Deck or DeviceKind.Implant;
    }

    /// <summary>
    /// Whether a hostname can be used: 1 to <see cref="MaxHostname"/> of a-z, 0-9 and '-', not starting with
    /// '-'.
    /// </summary>
    public static bool ValidHostname(string name)
    {
        return name.Length is > 0 and <= MaxHostname
               && name[0] != '-'
               && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    /// <summary>
    /// An address as it's written, like 10.2.1.1.
    /// </summary>
    public static string FormatAddress(uint address)
    {
        return $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";
    }

    public void Command(DeviceCommand command)
    {
        if (DeviceCommands.Count < CommandsPerTick)
            DeviceCommands.Add(command);
    }

    public void SetRaw(bool on)
    {
        if (Raw == on)
            return;

        Raw = on;
        Keys.Clear();
        Input.Clear();
        Editor.Clear();
    }

    /// <summary>
    /// Writes a program's output, up to <see cref="WasmHost.OutputPerTick"/> bytes a tick; past that it's
    /// dropped, with a note.
    /// </summary>
    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (OutputThisTick + bytes > WasmHost.OutputPerTick)
        {
            if (!Truncated)
            {
                Truncated = true;
                Output.Append("\n[output truncated]\n");
            }

            return;
        }

        OutputThisTick += bytes;
        Output.Append(text);
    }
}
