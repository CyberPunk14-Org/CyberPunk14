using System.Linq;
using System.Text;
using Content.Server._CyberPunk.Wire;

namespace Content.Server._CyberPunk.Wasm;

/// <summary>
/// The kinds of machine a program can run on.
/// </summary>
public enum DeviceKind : byte
{
    Computer,
    DoorController,
    Camera,

    /// <summary>A netrunner's deck: their virtual body in cyberspace, as a computer.</summary>
    Deck,

    /// <summary>The computer in a piece of cyberware, inside someone.</summary>
    Implant,
}

/// <summary>
/// Where a kernel function works.
/// </summary>
public enum KernelScope : byte
{
    /// <summary>On every kind of machine.</summary>
    Any,

    /// <summary>Only on computers (decks and implants are computers too); elsewhere it returns -1.</summary>
    Computer,

    /// <summary>Only on door controllers.</summary>
    DoorController,

    /// <summary>Only on cameras.</summary>
    Camera,

    /// <summary>Only on decks.</summary>
    Deck,

    /// <summary>Only on implants.</summary>
    Implant,

    /// <summary>On a computer, for a program running ICE (after <c>ice_start</c>).</summary>
    IceProgram,
}

/// <summary>
/// A function programs import from the kernel.
/// </summary>
/// <param name="Name">Its import name.</param>
/// <param name="Signature">Its WAT import signature.</param>
/// <param name="Scope">Where it works.</param>
/// <param name="Since">The kernel version it arrived in: it's in <c>sb_vN</c> for that version and every later one.</param>
/// <param name="Doc">What it does, for the manual.</param>
public sealed record KernelFunction(string Name, string Signature, KernelScope Scope, int Since, string Doc);

/// <summary>
/// A function a program exports for the kernel to call.
/// </summary>
public sealed record KernelHook(string Name, string Signature, KernelScope Scope, string Doc);

/// <summary>
/// What programs can call ("kernel functions", in game) and what the kernel calls in them ("hooks"), and the
/// <c>man</c> pages that document them for players. Ported from Switchboard's <c>sb_wasm/src/kernel.rs</c>.
/// </summary>
/// <remarks>
/// This is the one table of kernel functions: <see cref="KernelApi"/> refuses to link a function that isn't
/// listed here, and takes the import modules each one goes in from <see cref="KernelFunction.Since"/>, so the
/// manual can't drift from what the host provides.
/// </remarks>
public static class Kernel
{
    /// <summary>
    /// The newest kernel version. Programs import from <c>sb_v0</c> up to <c>sb_v10</c>, and every one is
    /// provided, so programs built against an older kernel keep working.
    /// </summary>
    public const int ApiVersion = 10;

    /// <summary>
    /// The screen is 80 columns; manual pages are wrapped to fit.
    /// </summary>
    public const int PageWidth = 79;

    /// <summary>
    /// The import module for a kernel version.
    /// </summary>
    public static string Module(int version) => $"sb_v{version}";

    private static readonly DeviceKind[] AllKinds = Enum.GetValues<DeviceKind>();

    /// <summary>
    /// What <c>device_type</c> returns for a kind of machine.
    /// </summary>
    public static int Code(DeviceKind kind) => (int) kind;

    /// <summary>
    /// A kind of machine's <c>man</c> topic.
    /// </summary>
    public static string Topic(DeviceKind kind)
    {
        return kind switch
        {
            DeviceKind.Computer => "computer",
            DeviceKind.DoorController => "door",
            DeviceKind.Camera => "camera",
            DeviceKind.Deck => "deck",
            DeviceKind.Implant => "implant",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string Title(DeviceKind kind)
    {
        return kind switch
        {
            DeviceKind.Computer => "computers",
            DeviceKind.DoorController => "door controllers",
            DeviceKind.Camera => "security cameras",
            DeviceKind.Deck => "decks",
            DeviceKind.Implant => "implants",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static KernelScope ScopeOf(DeviceKind kind)
    {
        return kind switch
        {
            DeviceKind.Computer => KernelScope.Computer,
            DeviceKind.DoorController => KernelScope.DoorController,
            DeviceKind.Camera => KernelScope.Camera,
            DeviceKind.Deck => KernelScope.Deck,
            DeviceKind.Implant => KernelScope.Implant,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>
    /// Every kernel function, in the order <c>man</c> lists them.
    /// </summary>
    public static readonly IReadOnlyList<KernelFunction> Functions = new KernelFunction[]
    {
        new("api_version", "(result i32)", KernelScope.Any, 0,
            $"The kernel's version ({ApiVersion} on this machine)."),
        new("device_type", "(result i32)", KernelScope.Any, 2,
            "What this machine is: 0 a computer, 1 a door controller, 2 a camera, 3 a deck, 4 an implant."),
        new("clock_ms", "(result i64)", KernelScope.Any, 0,
            "Milliseconds since the machine booted."),
        new("power", "(result i32)", KernelScope.Any, 0,
            "1 while the machine has power (always, while anything runs)."),
        new("exit", "(param $code i32)", KernelScope.Any, 0,
            "Ends the program once the current call returns."),
        new("reboot", "", KernelScope.Any, 7,
            "Restarts the machine once the current call returns: everything running stops, and it boots afresh (from boot.bin on its disk, if there is one: man boot)."),
        new("exec", "(param $name i32 $len i32) (result i32)", KernelScope.Any, 0,
            "Runs the program in a file once the current call returns; it takes over until it exits. 0 ok, -1 no such file, -2 not a program, -3 too many running."),
        new("exec_args", "(param $name i32 $len i32 $args i32 $args_len i32) (result i32)", KernelScope.Any, 1,
            "As exec, passing an argument string (up to 256 bytes); -4 if too long."),
        new("random", "(result i32)", KernelScope.Any, 4,
            "A random whole number from 0 to 2147483647. Each program has its own sequence."),
        new("job_start", "(param $name i32 $len i32 $args i32 $args_len i32) (result i32)", KernelScope.Any, 4,
            "Starts a program as a background job, from the next tick, with an argument string: it runs alongside the terminal's programs, with no terminal input of its own (its output still shows). Returns its job id, or -1 no such file, -2 not a program, -3 too many jobs (8), -4 arguments too long."),
        new("job_list", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 4,
            "Copies the background jobs into buf, one per line: its id and the program in front, separated by a space. Returns the full length."),
        new("job_kill", "(param $id i32) (result i32)", KernelScope.Any, 4,
            "Stops a background job and everything in it, at the end of this tick. 0 ok, -1 no such job."),
        new("args", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 1,
            "Copies the program's argument string into buf; returns its length."),
        new("fs_list", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 0,
            "Copies the disk's file names, one per line, into buf; returns the full length."),
        new("fs_read", "(param $name i32 $len i32 $buf i32 $cap i32) (result i32)", KernelScope.Any, 0,
            "Copies a file into buf; returns its full length, or -1 if there's no such file."),
        new("fs_write", "(param $name i32 $len i32 $data i32 $data_len i32) (result i32)", KernelScope.Any, 0,
            "Creates or replaces a file. 0 ok, -1 bad name, -2 disk full (1 MiB, 64 files)."),
        new("fs_delete", "(param $name i32 $len i32) (result i32)", KernelScope.Any, 0,
            "Deletes a file. 0 ok, -1 no such file."),
        new("net_addr", "(result i64)", KernelScope.Any, 1,
            "This machine's network address (10.B.S.H packed big-endian into a u32), or -1."),
        new("net_send", "(param $addr i32 $port i32 $data i32 $len i32) (result i32)", KernelScope.Any, 1,
            "Sends up to 1024 bytes to addr on port; it arrives next tick. 0 ok, -1 unreachable, -2 too big, -3 too many this tick (16), -4 bad port."),
        new("net_recv", "(param $meta i32 $buf i32 $cap i32) (result i32)", KernelScope.Any, 1,
            "Takes the next packet: its sender address and port go to meta (two little-endian u32s), its data to buf. Returns its length, or -1 if none is waiting."),
        new("net_neighbours", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 1,
            "Copies the other addresses on this building's network into buf as little-endian u32s; returns how many there are."),
        new("net_hostname", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 6,
            "Copies this machine's hostname into buf; returns its full length, 0 if it has none."),
        new("net_set_hostname", "(param $name i32 $len i32) (result i32)", KernelScope.Any, 6,
            "Sets this machine's hostname, which its router tells the network from the next tick: 1 to 32 of a-z, 0-9 and -, not starting with -. Empty clears it. Kept through reboots. 0 ok, -1 bad name."),
        new("net_resolve", "(param $name i32 $len i32) (result i64)", KernelScope.Any, 6,
            "The address of the machine with this hostname, as the routers know it, or -1."),
        new("net_hosts", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 6,
            "Copies every hostname the routers know, one \"name address\" a line, sorted by name, into buf; returns the full length."),
        new("man", "(param $topic i32 $len i32 $buf i32 $cap i32) (result i32)", KernelScope.Any, 2,
            "Copies a manual page into buf (an empty topic lists them); returns its full length, or -1 for no such page."),
        new("scaffold", "(param $kind i32 $len i32 $buf i32 $cap i32) (result i32)", KernelScope.Any, 2,
            "Copies a starting Wire program for a kind of machine (computer, door, camera, ice, deck or implant), or the source of the operating system (os) or a program that comes with it (nano, blade, ward, ice_basic), into buf; returns its full length, or -1."),
        new("wire_program", "(param $buf i32 $cap i32) (result i32)", KernelScope.Any, 2,
            "Kept for programs built for the old Wire runtime; always -1, since Wire now builds straight to programs."),
        new("device_io", "(param $port i32 $buf i32 $len i32) (result i32)", KernelScope.Any, 0,
            "The old way to work a door or camera (port 0); use the door_ and camera_ functions instead."),
        new("term_write", "(param $text i32 $len i32)", KernelScope.Computer, 0,
            "Writes text to the terminal (4 KiB a tick at most). A backspace (8) rubs out the character before it."),
        new("term_read", "(param $buf i32 $cap i32) (result i32)", KernelScope.Computer, 0,
            "Copies typed input (whole lines, each ending in a newline) into buf; returns how many bytes."),
        new("term_size", "(result i32)", KernelScope.Computer, 2,
            "The screen's size: rows * 65536 + columns (24 rows of 80)."),
        new("term_raw", "(param $on i32)", KernelScope.Computer, 2,
            "Raw mode on (1) or off (0): in raw mode every key comes to term_key as it is pressed, and nothing typed is shown. It ends with the program."),
        new("term_key", "(result i32)", KernelScope.Computer, 2,
            "The next key pressed in raw mode, or -1: a character's code, 8 Backspace, 9 Tab, 10 Enter, 127 Delete, 1-26 Ctrl+A to Ctrl+Z, and 0x110001-0x110008 for Up, Down, Left, Right, Home, End, PageUp, PageDown."),
        new("term_clear", "", KernelScope.Computer, 2,
            "Clears the screen, for drawing it afresh."),
        new("term_title", "(param $text i32 $len i32) (result i32)", KernelScope.Computer, 9,
            $"Names the terminal window while this program is in front ({KernelApi.MaxTitle} characters at most); an empty title gives the window back to the program under it. 0 set, -1 not at the terminal (a background job)."),
        new("ui_set", "(param $text i32 $len i32 $err i32 $err_cap i32) (result i32)", KernelScope.Computer, 8,
            "Shows a UI in the terminal window, in place of the text, while this program is in front (man ui): the text describes its widgets. 0 shown, -1 not at the terminal (a background job), -2 not a UI (why goes to err)."),
        new("ui_event", "(param $buf i32 $cap i32) (result i32)", KernelScope.Computer, 8,
            "Takes the next thing someone did to this program's UI: copies \"KIND ID VALUE\" into buf (KIND is click, submit or select). Returns its full length (if that's more than cap, it stays, to ask again with room), or -1 if nothing is waiting."),
        new("ui_clear", "", KernelScope.Computer, 8,
            "Takes this program's UI down, so the terminal shows text again."),
        new("build", "(param $src i32 $src_len i32 $out i32 $out_len i32 $err i32 $err_cap i32) (result i32)", KernelScope.Computer, 2,
            "Builds the source file src (WAT if it ends in .wat, Wire otherwise; 64 KiB at most) into the program file out. Returns its size; or -1 no such file, -2 source too big, -3 doesn't build (why, with the line, goes to err), -4 can't write out."),
        new("dev_request", "(param $addr i32 $req i32 $len i32 $buf i32 $cap i32) (result i32)", KernelScope.Computer, 10,
            $"Asks the machine with a UI at addr (a vending machine, an air alarm...) something, and copies its answer into buf (man dev). The request is info (what it is and the calls it takes), state (what its UI shows) or call NAME ARGS (does what one of its UI's buttons does). The answer is a value written as repr() writes it, or ! and why it wasn't done. Returns the answer's full length (ask again with room if that's more than cap: info and state are safe to repeat), or -1 if no such machine answers. {MachineIo.DeviceRequestsPerTick} requests a tick."),
        new("flash", "(param $addr i32 $name i32 $len i32) (result i32)", KernelScope.Computer, 2,
            "Sends the program in a file over the network to replace the firmware of the device at addr, which only takes it with its maintenance panel open. 0 sent (the result shows on the terminal), -1 unreachable, -2 not a program."),
        new("door_status", "(result i32)", KernelScope.DoorController, 2,
            "The door's state: 1 if open, + 2 if bolted, + 4 if someone is in the doorway."),
        new("door_open", "(result i32)", KernelScope.DoorController, 2,
            "Opens the door. 0 ok, -3 it is bolted."),
        new("door_close", "(result i32)", KernelScope.DoorController, 2,
            "Closes the door. 0 ok, -3 it is bolted, -4 someone is in the doorway."),
        new("door_bolt", "(result i32)", KernelScope.DoorController, 2,
            "Bolts the door where it is: it won't move, by hand or by the controller, until unbolted."),
        new("door_unbolt", "(result i32)", KernelScope.DoorController, 2,
            "Unbolts the door."),
        new("request_name", "(param $buf i32 $cap i32) (result i32)", KernelScope.DoorController, 2,
            "During on_door_request: copies the name of the person trying the door into buf; returns its length (-1 outside the hook)."),
        new("request_holding", "(param $buf i32 $cap i32) (result i32)", KernelScope.DoorController, 2,
            "During on_door_request: copies what they hold in their active hand (such as wrench or crowbar; empty if nothing) into buf; returns its length (-1 outside the hook)."),
        new("request_cards", "(param $buf i32 $cap i32) (result i32)", KernelScope.DoorController, 3,
            "During on_door_request: copies the organization tags of the ID cards they carry, one per line (empty if none), into buf; returns its length (-1 outside the hook)."),
        new("camera_count", "(result i32)", KernelScope.Camera, 2,
            "How many people the camera sees (within 6 tiles)."),
        new("camera_names", "(param $buf i32 $cap i32) (result i32)", KernelScope.Camera, 2,
            "Copies the names of the people it sees, one per line, into buf; returns the full length."),
        new("ice_here", "(result i64)", KernelScope.IceProgram, 4,
            "The node the ICE stands at (a machine's id), or -1 if it isn't at one."),
        new("ice_start", "(result i32)", KernelScope.Computer, 4,
            "Puts ICE in cyberspace on this computer's pad, guarding its network, run by this program from the next tick: the ice_ functions direct it. It goes when the program ends; if it is beaten, the program is halted. 0 ok, -1 not a computer (or a deck), -2 this program already runs ICE."),
        new("ice_integrity", "(result i32)", KernelScope.IceProgram, 4,
            "The ICE's integrity, 100 when it starts: runners' strikes wear it down, and at none it derezzes and this program is halted."),
        new("ice_nodes", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "Copies the nodes of the network it guards into buf as little-endian u32s (patrolling, only those on its side of the firewalls); returns how many there are."),
        new("ice_neighbours", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "Copies the nodes linked to the one it stands at into buf as little-endian u32s; returns how many there are."),
        new("ice_go", "(param $node i32) (result i32)", KernelScope.IceProgram, 4,
            "Walks to a node of its network along the paths, the shortest way. Patrolling, it keeps off firewalls unless they're its only way back to its side. 0 ok, -1 not one of its nodes (or past a firewall, patrolling)."),
        new("ice_chase", "(param $runner i32) (result i32)", KernelScope.IceProgram, 4,
            "Chases a runner it can see, along the paths, for as long as they stay in its network; engaging, it follows them out of it too, and comes back once it stops engaging. 0 ok, -1 it can't see them."),
        new("ice_runners", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "Copies the netrunners in its network (anywhere, engaging) that it can see (along a clear path, within 9 tiles) into buf, one per line: id, the node they are nearest, authorized (1 if they carry the organization's ID card), in reach (1 if close enough to strike), the x and y of the tile they stand on, and name, separated by spaces. Returns the full length."),
        new("ice_attack", "(param $runner i32) (result i32)", KernelScope.IceProgram, 4,
            "Strikes a runner in reach, taking a quarter of their integrity (an eighth through a ward; once a second at most); at none left they are thrown out. 0 struck, -1 not in reach."),
        new("ice_position", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "Copies the tile the ICE stands on into buf: x then y, little-endian i32s. Returns 8."),
        new("ice_alert", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "Where a completed trace says an intruder is: copies that tile into buf (x then y, little-endian i32s) and returns 8, or returns 0 if no alert came this tick. A trace runs on any runner who steps up to one of the network's computers without the organization's ID card; when it completes, every ICE on the network is alerted, once."),
        new("ice_breach", "(param $buf i32 $cap i32) (result i32)", KernelScope.IceProgram, 4,
            "During on_breach_signal: copies the tile of the firewall that was breached into buf (x then y, little-endian i32s) and returns 8; -1 outside the hook."),
        new("ice_go_to", "(param $x i32 $y i32) (result i32)", KernelScope.IceProgram, 4,
            "Walks to a tile of its network along the paths, the shortest way; it stops if there is no way there. 0 ok."),
        new("ice_mode", "(param $mode i32) (result i32)", KernelScope.IceProgram, 4,
            "Sets how alert the ICE is, shown in its colour: 0 patrolling, 1 searching, 2 engaging. Patrolling it stays on its side of the firewalls; searching or engaging it crosses them, and engaging it chases runners out of its network. 0 ok, -1 no such mode."),
        new("deck_integrity", "(result i32)", KernelScope.Deck, 4,
            "The runner's integrity, 100 when they jack in: strikes wear it down, and at none they are thrown out."),
        new("deck_status", "(result i32)", KernelScope.Deck, 4,
            "1 if a ward is up, + 2 if the deck can strike now, + 4 if it can raise a ward now."),
        new("deck_targets", "(param $buf i32 $cap i32) (result i32)", KernelScope.Deck, 4,
            "Copies what the deck can see (along a clear path, within 9 tiles) into buf, nearest first, one per line: id, kind (ice or runner), integrity, in reach (1 if close enough to strike), distance in tiles and name, separated by spaces. Returns the full length."),
        new("deck_strike", "(param $target i32) (result i32)", KernelScope.Deck, 4,
            "Strikes ICE or a runner in reach, by id, for a quarter of its integrity (an eighth through a ward); at none left ICE derezzes (its program halts) and a runner is thrown out. Once every two-thirds of a second. 0 struck, -1 not in reach, -2 not ready yet."),
        new("deck_ward", "(result i32)", KernelScope.Deck, 4,
            "Raises a ward: strikes on the runner are halved for 2 seconds. Every 6 seconds at most. 0 raised, -2 not ready yet."),
        new("deck_hold", "(param $name i32 $len i32) (result i32)", KernelScope.Deck, 4,
            "Puts the program in a file (or a program that comes with the deck) in a free hand of the runner's virtual body, to use. 0 ok (the result shows on the terminal), -2 not a program."),
        new("deck_push", "(param $addr i32 $name i32 $len i32) (result i32)", KernelScope.Deck, 4,
            "Copies a file to the computer at addr (one the deck reaches over the network), or with addr 0 onto the computer whose pad the runner stands at, if the runner may use it (it is nobody's, they carry its owner's card, or they stand at it with its lock breached). It lands under its own name, out of any folder. 0 sent (the result shows on the terminal), -2 no such file."),
        new("deck_colour", "(param $hex i32 $len i32) (result i32)", KernelScope.Deck, 10,
            "Sets the colour the runner's virtual body shows in, as hex: RRGGBB, with or without a #. It's kept between runs. 0 ok, -1 not a colour."),
        new("body_vitals", "(param $buf i32 $cap i32) (result i32)", KernelScope.Implant, 5,
            "Copies the vitals of the body the implant is in into buf, separated by spaces: state (ok, critical or dead), total damage (critical at 100, dead at 200), brute, burn, oxygen loss, blood in percent, how fast it bleeds, doses left (a trauma pump's) and 1 if it can boost now (a reflex booster's). Returns the length, -1 if it isn't fitted."),
        new("body_alert", "(param $ptr i32 $len i32) (result i32)", KernelScope.Implant, 5,
            "Shows a line of text to the implant's owner, wherever they are. Once a second at most. 0 sent, -2 too soon."),
        new("body_inject", "(result i32)", KernelScope.Implant, 5,
            "A trauma pump's dose: stops the bleeding, puts some blood back and holds off dying for a minute. Returns the doses left, -1 if this isn't a trauma pump, -2 if it is empty."),
        new("body_boost", "(result i32)", KernelScope.Implant, 5,
            "A reflex booster's boost: five seconds of speed, then thirty to recover. 0 boosted, -1 not a reflex booster, -2 not ready."),
    };

    /// <summary>
    /// Every hook, in the order <c>man hooks</c> lists them.
    /// </summary>
    public static readonly IReadOnlyList<KernelHook> Hooks = new KernelHook[]
    {
        new("start", "(func (export \"start\"))", KernelScope.Any,
            "Runs once when the program is launched (or the device boots it). Every program needs one."),
        new("tick", "(func (export \"tick\"))", KernelScope.Any,
            "Runs 30 times a second after start, until the program calls exit. Without it the program ends when start returns."),
        new("on_door_request", "(func (export \"on_door_request\") (result i32))", KernelScope.DoorController,
            "Runs when someone tries to open the door by hand, before it opens: return 1 to let them in, 0 to keep it shut. request_name, request_holding and request_cards say who they are. While a program has this hook, every hand on the door asks it first."),
        new("on_breach_signal", "(func (export \"on_breach_signal\"))", KernelScope.IceProgram,
            "Runs when a runner breaches a firewall on the network the program's ICE guards, just before its next tick; ice_breach says where."),
    };

    private static readonly Dictionary<string, KernelFunction> ByName = Functions.ToDictionary(f => f.Name);

    /// <summary>
    /// A kernel function by name, if there is one.
    /// </summary>
    public static KernelFunction? Find(string name)
    {
        return ByName.GetValueOrDefault(name);
    }

    private const string Limits =
        "Every call into a program (start, one tick, one hook) gets a budget of about 2 million instructions and is stopped if it runs over. Memory is capped at 8 MiB.";

    private static string Describe(KernelFunction f) => $"  {f.Name} {f.Signature}\n      {f.Doc}\n";

    private static string KernelPage()
    {
        var page = new StringBuilder(
            $"LOW-LEVEL KERNEL FUNCTIONS (every machine)\n\nThese are for programs written in WAT (man wat); Wire programs use modules (man modules).\n\nImport them from \"{Module(ApiVersion)}\", as in\n  (import \"{Module(ApiVersion)}\" \"clock_ms\" (func $clock (result i64)))\nPointers and lengths are into the program's own memory, and every one is checked.\n\n");

        foreach (var f in Functions.Where(f => f.Scope == KernelScope.Any))
        {
            page.Append(Describe(f));
        }

        page.Append("\nSee also: man computer, man door, man camera, man deck, man implant, man ice, man hooks.\n");
        return page.ToString();
    }

    private static string DevicePage(DeviceKind kind)
    {
        var page = new StringBuilder($"{Title(kind).ToUpperInvariant()}\n");
        page.Append(kind switch
        {
            DeviceKind.Computer => "\nPrograms on computers are started with `run`. To put one on a door controller or camera, `flash` it (man flash).\n",
            DeviceKind.DoorController => "\nA door controller has no screen; talk to it over the network. Its own firmware listens on port 1701 (man firmware).\n",
            DeviceKind.Camera => "\nA camera has no screen; talk to it over the network. Its own firmware listens on port 1702 (man firmware).\n",
            DeviceKind.Implant => "\nAn implant's computer is a computer inside its owner: everything a computer has, and the body's functions. Its owner opens its terminal from anywhere. From boot it runs its program in the background (its `autorun` file).\n",
            DeviceKind.Deck => "\nA deck is a netrunner's virtual body in cyberspace, and a computer: everything a computer has, a disk that keeps its files between runs, and a host on whatever network it jacked into. Its terminal opens anywhere in cyberspace.\n",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

        page.Append("\nLOW-LEVEL KERNEL FUNCTIONS (elsewhere they return -1)\n\n");
        var scope = ScopeOf(kind);
        foreach (var f in Functions.Where(f => f.Scope == scope))
        {
            page.Append(Describe(f));
        }

        foreach (var h in Hooks.Where(h => h.Scope == scope))
        {
            page.Append($"  {h.Signature}\n      {h.Doc}\n");
        }

        page.Append('\n').Append(WireManual.Device(Topic(kind)));
        return page.ToString();
    }

    private static string IcePage()
    {
        var page = new StringBuilder("ICE\n\n");
        page.Append("ICE guards a building's network in cyberspace: an entity walking the paths of its region, as netrunners do, that sees them along a clear path and knows which carry the organization's ID card. A program on one of the network's computers starts it (ice_start) and drives it; when that program ends, the ICE goes. A runner who beats it down derezzes it, and its program halts on its computer.\n\nTRACES: a runner who steps up to one of the network's computers without the organization's ID card is traced. When the trace completes, every ICE on the network is alerted, once, to where the runner is (ice_alert).\n");
        page.Append("\nLOW-LEVEL KERNEL FUNCTIONS (elsewhere they return -1)\n\n");

        foreach (var f in Functions.Where(f => f.Scope == KernelScope.IceProgram || f.Name == "ice_start"))
        {
            page.Append(Describe(f));
        }

        page.Append('\n').Append(WireManual.Device("ice"));
        return page.ToString();
    }

    private static string HooksPage()
    {
        var page = new StringBuilder("HOOKS\n\nHooks are functions a program exports for the kernel to call:\n\n");
        foreach (var h in Hooks)
        {
            var only = h.Scope switch
            {
                KernelScope.Any => "",
                KernelScope.IceProgram => " (only in ICE programs)",
                _ => $" (only on {Title(AllKinds.First(k => ScopeOf(k) == h.Scope))})",
            };
            page.Append($"  {h.Signature}{only}\n      {h.Doc}\n");
        }

        page.Append($"\nA program must also export its memory: (memory (export \"memory\") 1).\n{Limits}\n");
        page.Append('\n').Append(WireManual.Hooks());
        return page.ToString();
    }

    private const string ShellPage = """
        THE SHELL

        The operating system every computer boots is a Wire program: `new myos os`
        copies its source, to read or to make your own (man boot).

          help                       the commands
          ls [FOLDER]                list the disk's files (and folders)
          cat FILE                   show a file
          cp FROM TO                 copy a file (TO . copies it out of its folder)
          write FILE TEXT...         write text to a file (replacing it)
          append FILE TEXT...        add a line of text to a file
          rm FILE                    delete a file
          nano FILE                  edit a text file (Ctrl+G in it for help)
          new NAME [KIND]            start a Wire program, NAME.wire, for a computer
                                     (or a door, camera, ice, deck or implant; os,
                                     nano, blade, ward or ice_basic for their own
                                     source)
          build FILE.wire [OUT.bin]  build a program (man wire)
          run FILE [ARGS...]         run a program
          run FILE [ARGS...] &       run it as a background job: it runs alongside
                                     the shell, without the terminal's input
          jobs, kill N               list the background jobs, stop one
          build FILE.wat [OUT.bin]   build a program written in WAT (man wat)
          flash ADDR FILE.bin        put a program on a door or camera (man flash)
          hold FILE, push [ADDR] FILE  on a deck (man deck)
          hostname [NAME], hosts, ip this computer's name, the network's, its address
          man [TOPIC]                these pages
          echo, uptime, ver, reboot

        A file named autorun holds commands the shell runs when it boots, one a
        line; one that runs a program at the terminal is the last it runs.

        """;

    private static string UiPage()
    {
        var page = new StringBuilder("""
            PROGRAM UIS

            A program at a computer's terminal can show a UI instead of text: buttons,
            lines to type on, lists, progress bars and a canvas to draw on. Everyone
            at the terminal sees it, and what they do comes back to the program as
            events. It shows while the program is in front (not from a background
            job), and goes when the program ends. A button on the window switches
            between the UI and the text.

            A UI is text, one (kind ...) per widget:
              (column WIDGET...)        widgets top to bottom
              (row WIDGET...)           widgets left to right
              (label "text")
              (button ID "text")        sends click
              (input ID "text")         sends submit, with the text, on Enter
              (list ID "item"...)       sends select, with the item's number
              (progress VALUE MOST)
              (canvas ID W H DRAW...)   sends click, with "X Y"; draws
                (rect X Y W H "color") (line X1 Y1 X2 Y2 "color") (text X Y "text" "color")
            IDs are 1 to 32 of letters, digits, _ and -. Colors are names (red) or
            #rrggbb. At most 256 widgets, 16 deep, 512 drawing operations, 32 KiB.

            LOW-LEVEL KERNEL FUNCTIONS (computers only)

            """);
        foreach (var f in Functions.Where(f => f.Name.StartsWith("ui_")))
        {
            page.Append(Describe(f));
        }

        page.Append('\n').Append(WireManual.Ui());
        return page.ToString();
    }

    private static string DevPage()
    {
        var page = new StringBuilder($"""
            MACHINES ON THE NETWORK

            Any machine people work through a window, like a vending machine, a
            console or a fax, joins the network when it stands on data cable, and
            gets an address of its own (net.neighbours() lists it).
            A program on a computer can ask it what it is, read what its window
            shows, and press what its window has, as if someone stood at it.

            It does what it's asked as the computer, which has no ID: a machine
            that needs access turns it away. A machine without power doesn't
            answer. A computer can make {MachineIo.DeviceRequestsPerTick} requests a tick.

            Answers are Wire values: info is a dict of the machine's name, its kind
            and its calls (each with the arguments it takes and what they are); a
            call takes a dict of those arguments. Numbers are whole numbers, and
            other machines and things are their ids.

            LOW-LEVEL KERNEL FUNCTIONS (computers only)

            """);
        foreach (var f in Functions.Where(f => f.Name.StartsWith("dev_")))
        {
            page.Append(Describe(f));
        }

        page.Append('\n').Append(WireManual.Dev());
        return page.ToString();
    }

    private const string BootPage = """
        BOOT

        A computer boots boot.bin from its own disk if it has one, and the
        operating system it came with if not. So you can swap the OS:

          new myos os                 copy the OS's Wire source to myos.wire
          build myos.wire boot.bin    build your version to boot
          reboot                      restart and boot it

        If boot.bin won't load, or ends or crashes, the computer starts the OS
        it came with instead, until it is rebooted or loses power: so a broken
        OS can't lock you out. `rm boot.bin` then `reboot` puts things back.

        An OS is a program like any other (man hooks): its tick runs while it is
        in front, and it starts other programs with sys.run.

        """;

    private const string WatPage = """
        WAT (LOW LEVEL)

        Programs are machine code. You can write them by hand in WAT, the
        machine's assembly language:

          write prog.wat (module ...)    write it
          build prog.wat                 builds prog.bin (or says what's wrong)
          run prog.bin

        A program is (module ...) with: imports of the kernel functions it uses
        (man kernel), its memory exported as "memory", and the hooks it exports
        (man hooks). Strings are bytes in memory, passed as pointer and length;
        (data (i32.const 0) "text") puts text at address 0.

        """;

    private const string FlashPage = """
        FLASH

        flash ADDR FILE.bin sends a program over the network to the door
        controller or camera at ADDR, replacing its firmware. It boots the new
        program at once, and keeps it through power cuts.

        A device only takes new firmware with its maintenance panel open: someone
        has to stand at it with a screwdriver first.

        """;

    private const string FirmwarePage = """
        STOCK FIRMWARE

        Door controllers listen on port 1701. Send one a command:
          open, close, status       work the door
          bolt, unbolt              bolt it shut (or open) where it is
          allow NAME, deny NAME     who may open it by hand: once anyone is
                                    allowed, only they may; denied people never
          clear                     forget both lists
          list                      say who is allowed and denied
        It answers with the door's state, or what changed.

        Cameras listen on port 1702: `count` answers how many people it sees,
        `names` who they are.

        """;

    private const string IndexPage = """
        MANUAL

          man wire       the Wire programming language
          man modules    what Wire programs can call on every machine
          man computer   what only computers have
          man door       what door controllers have, and their hook
          man camera     what cameras have
          man deck       what a netrunner's deck has, in cyberspace
          man implant    what the computer in cyberware has
          man ice        ICE: guarding a network in cyberspace
          man hooks      when the machine runs a program's code
          man shell      the shell's commands
          man boot       swapping the operating system
          man ui         programs with buttons, lists and drawing
          man dev        working vending machines, consoles and the like
          man flash      putting programs on devices
          man firmware   what doors and cameras do out of the box
          man kernel     low-level kernel functions
          man wat        writing programs in assembly

        """;

    /// <summary>
    /// The manual page on a topic, or the list of topics for an empty one, wrapped to the screen. Null if
    /// there's no such page.
    /// </summary>
    public static string? Man(string topic)
    {
        var page = Page(topic.Trim());
        return page == null ? null : Wrap(page);
    }

    private static string? Page(string topic)
    {
        switch (topic)
        {
            case "":
            case "index":
            case "man":
                return IndexPage;
            case "kernel":
                return KernelPage();
            case "hooks":
                return HooksPage();
            case "shell":
            case "help":
                return ShellPage;
            case "wat":
                return WatPage;
            case "boot":
            case "os":
                return BootPage;
            case "ui":
                return UiPage();
            case "dev":
                return DevPage();
            case "wire":
                return WireManual.Reference();
            case "modules":
                return WireManual.Modules();
            case "flash":
                return FlashPage;
            case "firmware":
                return FirmwarePage;
            case "ice":
                return IcePage();
        }

        foreach (var kind in AllKinds)
        {
            if (Topic(kind) == topic)
                return DevicePage(kind);
        }

        return null;
    }

    /// <summary>
    /// A starting Wire program for a kind of machine, the default OS's own source for <c>os</c>, or a system
    /// program's (<see cref="SystemPrograms"/>) for its name; null if there's none for it.
    /// </summary>
    public static string? Scaffold(string kind)
    {
        kind = kind.Trim();
        return kind == "os" ? DefaultOs.Source : SystemPrograms.Source(kind) ?? WireManual.Scaffold(kind);
    }

    /// <summary>
    /// Wraps text to <see cref="PageWidth"/> columns, continuing a long line under its own indent.
    /// </summary>
    public static string Wrap(string text)
    {
        var output = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            // A line that fits keeps its spacing, so the tables in the pages stay lined up.
            if (line.Length <= PageWidth)
            {
                output.Append(line.TrimEnd()).Append('\n');
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var current = new StringBuilder(line[..indent]);
            var first = true;

            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var room = current.ToString().TrimEnd().Length > indent || !first;
                if (room && current.Length + 1 + word.Length > PageWidth)
                {
                    output.Append(current.ToString().TrimEnd()).Append('\n');
                    current.Clear().Append(' ', indent);
                }
                else if (current.Length > indent)
                {
                    current.Append(' ');
                }

                current.Append(word);
                first = false;
            }

            output.Append(current.ToString().TrimEnd()).Append('\n');
        }

        // Split leaves one empty piece after the last newline; don't double it.
        var wrapped = output.ToString();
        return text.EndsWith('\n') ? wrapped[..^1] : wrapped;
    }
}
