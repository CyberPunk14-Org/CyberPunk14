using System.Linq;
using System.Text;
using Content.Server._CyberPunk.Wire;
using Content.Shared._CyberPunk.Machines;
using Wasmtime;

namespace Content.Server._CyberPunk.Wasm;

/// <summary>
/// The kernel functions, linked for programs to import. Ported from the host API in Switchboard's
/// <c>sb_wasm/src/vm.rs</c>.
/// </summary>
/// <remarks>
/// Every function goes in the import module of the kernel version it arrived in (from
/// <see cref="Kernel.Functions"/>) and in every later one, so programs built against an older kernel keep
/// working. A function that isn't in that table can't be linked.
///
/// Programs are untrusted. Every pointer and length a program passes is checked, and a bad one traps the
/// program, never the host. Functions take effect on the machine's <see cref="MachineIo"/>; anything that
/// touches the world (sending packets, moving doors, flashing a device) is queued there, for the world to
/// carry out after the machine's tick.
///
/// The body (the <c>body_</c> functions) arrives with cyberware. Until then they're linked, so programs that use
/// them still load, and return -1.
/// </remarks>
internal sealed class KernelApi
{
    /// <summary>
    /// The longest title a program can give the terminal window, in characters.
    /// </summary>
    public const int MaxTitle = 64;

    /// <summary>
    /// The longest request a program can make of a machine with a UI, its arguments included.
    /// </summary>
    public const int MaxDeviceRequest = 16 * 1024;

    private readonly Linker _linker;
    private readonly HashSet<string> _linked = new();

    private KernelApi(Linker linker)
    {
        _linker = linker;
    }

    /// <summary>
    /// Links every kernel function into <paramref name="linker"/>, and returns their names.
    /// </summary>
    public static IReadOnlySet<string> Define(Linker linker)
    {
        var api = new KernelApi(linker);
        api.DefineSystem();
        api.DefineDisk();
        api.DefineNetwork();
        api.DefineTerminal();
        api.DefineTools();
        api.DefineDevices();
        api.DefineJobs();
        api.DefineDeck();
        api.DefineIce();
        api.DefineLater();
        return api._linked;
    }

    #region Linking

    /// <summary>
    /// The import modules a function goes in: its own version's and every later one's.
    /// </summary>
    private IEnumerable<string> ModulesFor(string name)
    {
        var function = Kernel.Find(name) ?? throw new InvalidOperationException(
            $"Kernel function {name} isn't in Kernel.Functions; document it there before linking it.");

        if (!_linked.Add(name))
            throw new InvalidOperationException($"Kernel function {name} is linked twice.");

        for (var version = function.Since; version <= Kernel.ApiVersion; version++)
        {
            yield return Kernel.Module(version);
        }
    }

    private void Def(string name, CallerAction fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerAction<int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerAction<int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<long> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, long> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, int, int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, int, int, int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    private void Def(string name, CallerFunc<int, int, int, int, int, int, int> fn)
    {
        foreach (var m in ModulesFor(name)) _linker.DefineFunction(m, name, fn);
    }

    #endregion

    #region Guest memory

    private static MachineIo Io(Caller caller)
    {
        return (MachineIo) caller.GetData()!;
    }

    private static Memory MemoryOf(Caller caller)
    {
        return caller.GetMemory("memory") ?? throw new TrapException("the program exports no memory");
    }

    /// <summary>
    /// Copies bytes out of the program's memory, trapping it on a pointer or length outside its memory.
    /// </summary>
    private static byte[] ReadBytes(Caller caller, int ptr, int len)
    {
        var memory = MemoryOf(caller);
        var (start, length) = ((uint) ptr, (uint) len);
        if ((ulong) start + length > (ulong) memory.GetLength())
            throw new TrapException("pointer out of bounds");

        return memory.GetSpan(start, (int) length).ToArray();
    }

    /// <summary>
    /// Copies up to <paramref name="cap"/> bytes of <paramref name="bytes"/> into the program's memory,
    /// trapping it on a pointer outside its memory.
    /// </summary>
    private static void WriteBytes(Caller caller, int ptr, int cap, ReadOnlySpan<byte> bytes)
    {
        var memory = MemoryOf(caller);
        var start = (uint) ptr;
        var count = (int) Math.Min((uint) bytes.Length, (uint) cap);
        if ((ulong) start + (ulong) count > (ulong) memory.GetLength())
            throw new TrapException("pointer out of bounds");

        bytes[..count].CopyTo(memory.GetSpan(start, count));
    }

    /// <summary>
    /// Writes text into the program's memory and returns its full length in bytes, which may be more than
    /// fit.
    /// </summary>
    private static int WriteText(Caller caller, int buf, int cap, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteBytes(caller, buf, cap, bytes);
        return bytes.Length;
    }

    private static string ReadText(Caller caller, int ptr, int len)
    {
        return Encoding.UTF8.GetString(ReadBytes(caller, ptr, len));
    }

    /// <summary>
    /// Reads a file or topic name: empty if it's longer than any name can be.
    /// </summary>
    private static string ReadName(Caller caller, int ptr, int len)
    {
        return (uint) len > 64 ? "" : ReadText(caller, ptr, len);
    }

    private static bool IsProgram(byte[]? data)
    {
        return data != null && data.AsSpan().StartsWith("\0asm"u8);
    }

    #endregion

    private void DefineSystem()
    {
        Def("api_version", _ => Kernel.ApiVersion);
        Def("device_type", c => Kernel.Code(Io(c).Kind));
        Def("clock_ms", c => (long) Io(c).ClockMs);

        // A program only runs while its machine has power.
        Def("power", _ => 1);

        Def("exit", (c, code) => Io(c).Exit = code);
        Def("reboot", c => { Io(c).Reboot = true; });
        Def("exec", (c, name, len) => RequestExec(Io(c), ReadName(c, name, len), ""));
        Def("exec_args", (c, name, nameLen, args, argsLen) =>
        {
            var file = ReadName(c, name, nameLen);
            if ((uint) argsLen > WasmHost.MaxArgs)
                return -4;

            return RequestExec(Io(c), file, ReadText(c, args, argsLen));
        });

        Def("args", (c, buf, cap) => WriteText(c, buf, cap, Io(c).Args));

        // Random numbers, a different sequence for every process.
        Def("random", c =>
        {
            var io = Io(c);
            var x = io.Rng;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            io.Rng = x;
            return (int) (x >> 33);
        });
    }

    /// <summary>
    /// Starts the program in a file once the current call returns.
    /// </summary>
    private static int RequestExec(MachineIo io, string name, string args)
    {
        if (io.Depth >= WasmHost.MaxDepth)
            return -3;

        var data = io.Disk.Read(name);
        if (data == null && !io.Host.IsSystemProgram(name))
            return -1;

        if (data != null && !IsProgram(data))
            return -2;

        io.Exec = (name, args);
        return 0;
    }

    private void DefineDisk()
    {
        Def("fs_list", (c, buf, cap) => WriteText(c, buf, cap, string.Join('\n', Io(c).Disk.Files.Select(f => f.Key))));

        Def("fs_read", (c, name, nameLen, buf, cap) =>
        {
            var data = Io(c).Disk.Read(ReadName(c, name, nameLen));
            if (data == null)
                return -1;

            WriteBytes(c, buf, cap, data);
            return data.Length;
        });

        Def("fs_write", (c, name, nameLen, data, len) =>
        {
            var file = ReadName(c, name, nameLen);
            if ((uint) len > MachineDisk.MaxBytes)
                return -2;

            return Io(c).Disk.Write(file, ReadBytes(c, data, len)) switch
            {
                DiskError.None => 0,
                DiskError.BadName => -1,
                _ => -2,
            };
        });

        Def("fs_delete", (c, name, len) => Io(c).Disk.Remove(ReadName(c, name, len)) ? 0 : -1);
    }

    private void DefineNetwork()
    {
        Def("net_addr", c => Io(c).Address is { } addr ? addr : -1L);

        // Sends len bytes to addr on port: 0 sent, -1 unreachable (or not connected), -2 too big, -3 too many
        // this tick, -4 bad port.
        Def("net_send", (c, addr, port, ptr, len) =>
        {
            if ((uint) len > WasmHost.MaxPacket)
                return -2;

            if (port is < 0 or > ushort.MaxValue)
                return -4;

            var io = Io(c);
            var to = (uint) addr;
            if (io.Address is not { } from || io.Reachable is not { } reachable || !reachable.Contains(to))
                return -1;

            if (io.Outbox.Count >= WasmHost.OutboxPerTick)
                return -3;

            io.Outbox.Add(new Packet(from, to, (ushort) port, ReadBytes(c, ptr, len)));
            return 0;
        });

        // Takes the next packet: its sender and port go to meta (two little-endian u32s), up to cap bytes of
        // it to buf. Returns its full length, or -1 if none is waiting.
        Def("net_recv", (c, meta, buf, cap) =>
        {
            if (!Io(c).Inbox.TryDequeue(out var packet))
                return -1;

            Span<byte> header = stackalloc byte[8];
            BitConverter.TryWriteBytes(header, packet.From);
            BitConverter.TryWriteBytes(header[4..], (uint) packet.Port);
            WriteBytes(c, meta, 8, header);
            WriteBytes(c, buf, Math.Max(cap, 0), packet.Data);
            return packet.Data.Length;
        });

        Def("net_neighbours", (c, buf, cap) => WriteIds(c, buf, cap, Io(c).Neighbours));

        Def("net_hostname", (c, buf, cap) => WriteText(c, buf, cap, Io(c).Hostname));

        Def("net_set_hostname", (c, name, len) =>
        {
            var text = (uint) len > MachineIo.MaxHostname ? null : ReadText(c, name, len);
            if (text == null || text.Length > 0 && !MachineIo.ValidHostname(text))
                return -1;

            var io = Io(c);
            if (io.Hostname != text)
            {
                io.Hostname = text;
                io.HostnameChanged = true;
            }

            return 0;
        });

        Def("net_resolve", (c, name, len) =>
            (uint) len <= MachineIo.MaxHostname && Io(c).Hosts.TryGetValue(ReadText(c, name, len), out var addr)
                ? addr
                : -1L);

        Def("net_hosts", (c, buf, cap) =>
        {
            var text = new StringBuilder();
            foreach (var (name, addr) in Io(c).Hosts.OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                text.Append(name).Append(' ').Append(MachineIo.FormatAddress(addr)).Append('\n');
            }

            return WriteText(c, buf, cap, text.ToString());
        });
    }

    /// <summary>
    /// Writes as many ids as fit into the program's memory, as little-endian u32s, and returns how many there
    /// are in all.
    /// </summary>
    private static int WriteIds(Caller caller, int buf, int cap, IReadOnlyList<uint> ids)
    {
        var fit = Math.Min(Math.Max(cap, 0) / 4, ids.Count);
        var bytes = new byte[fit * 4];
        for (var i = 0; i < fit; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), ids[i]);
        }

        WriteBytes(caller, buf, bytes.Length, bytes);
        return ids.Count;
    }

    private void DefineTerminal()
    {
        Def("term_write", (c, ptr, len) =>
        {
            var bytes = ReadBytes(c, ptr, Math.Min(len, WasmHost.OutputPerTick + 1));
            Io(c).Write(Encoding.UTF8.GetString(bytes));
        });

        Def("term_read", (c, buf, cap) =>
        {
            var io = Io(c);

            // Background jobs have no terminal to read.
            if (io.Job != 0)
                return 0;

            var count = Math.Min(Math.Max(cap, 0), io.Input.Count);
            var bytes = io.Input.GetRange(0, count).ToArray();
            io.Input.RemoveRange(0, count);
            WriteBytes(c, buf, cap, bytes);
            return count;
        });

        Def("term_size", c => Io(c).Is(DeviceKind.Computer) ? WasmHost.TermRows * 65536 + WasmHost.TermCols : -1);

        Def("term_raw", (c, on) =>
        {
            var io = Io(c);
            if (io.Is(DeviceKind.Computer) && io.Job == 0)
                io.SetRaw(on != 0);
        });

        Def("term_key", c =>
        {
            var io = Io(c);
            if (io.Job != 0)
                return -1;

            return io.Keys.TryDequeue(out var key) ? key : -1;
        });

        Def("term_clear", c =>
        {
            var io = Io(c);
            if (io.Is(DeviceKind.Computer))
                io.Output.Append(TerminalText.Clear);
        });

        DefineUi();
    }

    /// <summary>
    /// Programs at the terminal showing a UI of widgets instead of text. The Vm keeps each program's UI and shows
    /// the front one's.
    /// </summary>
    private void DefineUi()
    {
        Def("ui_set", (c, ptr, len, err, errCap) =>
        {
            var io = Io(c);
            if (!io.Is(DeviceKind.Computer) || io.Job != 0)
                return -1;

            if ((uint) len > ProgramUiParser.MaxBytes)
            {
                WriteText(c, err, errCap, $"too long ({ProgramUiParser.MaxBytes / 1024} KiB at most)");
                return -2;
            }

            var text = ReadText(c, ptr, len);
            if (text == io.UiText)
                return 0;

            try
            {
                io.UiChange = (ProgramUiParser.Parse(text), text);
                return 0;
            }
            catch (ProgramUiException e)
            {
                WriteText(c, err, errCap, e.Message);
                return -2;
            }
        });

        Def("ui_event", (c, buf, cap) =>
        {
            var io = Io(c);
            if (io.Job != 0 || io.Pid != io.UiEventsFor || !io.UiEvents.TryPeek(out var e))
                return -1;

            var kind = e.Kind switch
            {
                ProgramUiEventKind.Click => "click",
                ProgramUiEventKind.Submit => "submit",
                _ => "select",
            };
            // An event that doesn't fit stays, for the program to ask again with room for it.
            var length = WriteText(c, buf, cap, $"{kind} {e.Id} {e.Value}");
            if (length <= cap)
                io.UiEvents.Dequeue();

            return length;
        });

        Def("ui_clear", c =>
        {
            var io = Io(c);
            if (io.Is(DeviceKind.Computer) && io.Job == 0)
                io.UiChange = (null, "");
        });

        Def("term_title", (c, ptr, len) =>
        {
            var io = Io(c);
            if (!io.Is(DeviceKind.Computer) || io.Job != 0)
                return -1;

            // Only what fits on a window's title bar, with no control characters.
            var title = new StringBuilder();
            foreach (var ch in ReadText(c, ptr, Math.Min(len, MaxTitle * 4)))
            {
                if (title.Length == MaxTitle)
                    break;

                if (!char.IsControl(ch))
                    title.Append(ch);
            }

            io.TitleChange = title.ToString().Trim();
            return 0;
        });
    }

    private void DefineTools()
    {
        Def("man", (c, topic, len, buf, cap) =>
        {
            var page = Kernel.Man(ReadName(c, topic, len));
            return page == null ? -1 : WriteText(c, buf, cap, page);
        });

        Def("scaffold", (c, kind, len, buf, cap) =>
        {
            var text = Kernel.Scaffold(ReadName(c, kind, len));
            return text == null ? -1 : WriteText(c, buf, cap, text);
        });

        Def("wire_program", (_, _, _) => -1);

        Def("build", (c, src, srcLen, outName, outLen, err, errCap) =>
        {
            var io = Io(c);
            if (!io.Is(DeviceKind.Computer))
                return -1;

            var source = ReadName(c, src, srcLen);
            var output = ReadName(c, outName, outLen);
            var code = io.Disk.Read(source);
            if (code == null)
                return -1;

            if (code.Length > WasmHost.MaxSource)
                return -2;

            var bytes = Build(io.Host, source, code, out var why);
            if (bytes == null)
            {
                WriteText(c, err, errCap, why!);
                return -3;
            }

            return io.Disk.Write(output, bytes) == DiskError.None ? bytes.Length : -4;
        });

        // A machine with a UI on the network answers a request (WasmMachineSystem.Devices.cs).
        Def("dev_request", (c, addr, req, len, buf, cap) =>
        {
            var io = Io(c);
            var to = (uint) addr;
            if (!io.Is(DeviceKind.Computer)
                || io.Devices is not { } devices
                || io.Address == null
                || io.Reachable is not { } reachable
                || !reachable.Contains(to))
            {
                return -1;
            }

            if ((uint) len > MaxDeviceRequest)
                return WriteText(c, buf, cap, $"!the request is too long ({MaxDeviceRequest / 1024} KiB at most)");

            if (io.DeviceRequests >= MachineIo.DeviceRequestsPerTick)
                return WriteText(c, buf, cap, $"!too many requests this tick ({MachineIo.DeviceRequestsPerTick} at most)");

            io.DeviceRequests++;
            var answer = devices.Request(to, ReadText(c, req, len));
            return answer == null ? -1 : WriteText(c, buf, cap, answer);
        });

        Def("flash", (c, addr, name, len) =>
        {
            var io = Io(c);
            if (!io.Is(DeviceKind.Computer))
                return -1;

            // The world decides whether it can get there.
            var file = ReadName(c, name, len);
            var bytes = io.Disk.Read(file);
            if (!IsProgram(bytes))
                return -2;

            io.Flash = new FirmwareFlash((uint) addr, file, bytes!);
            return 0;
        });
    }

    /// <summary>
    /// Builds a source file into a program, or says why it doesn't build: WAT if its name ends in
    /// <c>.wat</c>, Wire otherwise.
    /// </summary>
    private static byte[]? Build(WasmHost host, string name, byte[] source, out string? why)
    {
        why = null;
        var text = Encoding.UTF8.GetString(source);
        try
        {
            var bytes = name.EndsWith(".wat") ? Module.ConvertText(text) : WireCompiler.Compile(text);
            host.Load(bytes);
            return bytes;
        }
        catch (WireException e)
        {
            why = e.Message;
        }
        catch (WasmtimeException e)
        {
            why = e.Message.Trim();
        }
        catch (WasmLoadException e)
        {
            why = e.Message;
        }

        return null;
    }

    private void DefineDevices()
    {
        Def("device_io", DeviceIo);

        Def("door_status", c =>
        {
            var io = Io(c);
            if (io.Device is not DoorDevice door || !io.Is(DeviceKind.DoorController))
                return -1;

            return (door.Open ? 1 : 0) + (door.Bolted ? 2 : 0) + (door.Blocked ? 4 : 0);
        });

        // Each door function acts on the door as the program sees it this tick, and the world catches up
        // after.
        Def("door_open", c => DoorFunction(c, door => door.Bolted ? -3 : (door with { Open = true }, DeviceCommand.OpenDoor)));
        Def("door_close", c => DoorFunction(c, door =>
        {
            if (door.Bolted)
                return -3;

            if (door.Open && door.Blocked)
                return -4;

            return (door with { Open = false }, DeviceCommand.CloseDoor);
        }));
        Def("door_bolt", c => DoorFunction(c, door => (door with { Bolted = true }, DeviceCommand.BoltDoor)));
        Def("door_unbolt", c => DoorFunction(c, door => (door with { Bolted = false }, DeviceCommand.UnboltDoor)));

        Def("request_name", (c, buf, cap) => Io(c).Request is { } who ? WriteText(c, buf, cap, who.Name) : -1);
        Def("request_holding", (c, buf, cap) => Io(c).Request is { } who ? WriteText(c, buf, cap, who.Holding) : -1);
        Def("request_cards",
            (c, buf, cap) => Io(c).Request is { } who ? WriteText(c, buf, cap, string.Join('\n', who.Cards)) : -1);

        Def("camera_count", c =>
        {
            var io = Io(c);
            return io.Device is CameraDevice camera && io.Is(DeviceKind.Camera) ? camera.People.Count : -1;
        });

        Def("camera_names", (c, buf, cap) =>
        {
            var io = Io(c);
            if (io.Device is not CameraDevice camera || !io.Is(DeviceKind.Camera))
                return -1;

            return WriteText(c, buf, cap, string.Join('\n', camera.People));
        });
    }

    /// <summary>
    /// What a door function does to the door: an error code, or the door as it now stands and the command
    /// for the world.
    /// </summary>
    private readonly record struct DoorResult(DoorDevice? Door, DeviceCommand Command, int Error)
    {
        public static implicit operator DoorResult(int error) => new(null, default, error);

        public static implicit operator DoorResult((DoorDevice Door, DeviceCommand Command) ok) =>
            new(ok.Door, ok.Command, 0);
    }

    private static int DoorFunction(Caller caller, Func<DoorDevice, DoorResult> act)
    {
        var io = Io(caller);
        if (!io.Is(DeviceKind.DoorController) || io.Device is not DoorDevice door)
            return -1;

        var result = act(door);
        if (result.Door == null)
            return result.Error;

        io.Device = result.Door;
        io.Command(result.Command);
        return 0;
    }

    /// <summary>
    /// Device I/O on port 0, the device the machine is wired to, from kernel v0.
    /// </summary>
    /// <remarks>
    /// A door: <c>buf[0]</c> is the command (0 status, 1 open, 2 close); the door's state (1 open, 0 closed)
    /// is written back to <c>buf[0]</c> and 1 returned. A bolted door refuses with -3, and so does closing one
    /// with someone in it. A camera: how many people it sees is written to <c>buf</c> as a little-endian u32,
    /// and 4 returned. -1 for no device (or another port), -2 for a bad request.
    /// </remarks>
    private static int DeviceIo(Caller caller, int port, int buf, int len)
    {
        var io = Io(caller);
        if (port != 0)
            return -1;

        switch (io.Device)
        {
            case DoorDevice door:
            {
                if (len < 1)
                    return -2;

                var command = ReadBytes(caller, buf, 1)[0];
                bool open;
                switch (command)
                {
                    case 0:
                        open = door.Open;
                        break;
                    case 1 or 2 when door.Bolted:
                        return -3;
                    case 1:
                        io.Command(DeviceCommand.OpenDoor);
                        open = true;
                        break;
                    case 2 when door.Open && door.Blocked:
                        return -3;
                    case 2:
                        io.Command(DeviceCommand.CloseDoor);
                        open = false;
                        break;
                    default:
                        return door.Bolted ? -3 : -2;
                }

                // What the program sees next this tick, before the world catches up after it.
                io.Device = door with { Open = open };
                WriteBytes(caller, buf, 1, [(byte) (open ? 1 : 0)]);
                return 1;
            }
            case CameraDevice camera:
            {
                if (len < 4)
                    return -2;

                Span<byte> count = stackalloc byte[4];
                BitConverter.TryWriteBytes(count, (uint) camera.People.Count);
                WriteBytes(caller, buf, 4, count);
                return 4;
            }
            default:
                return -1;
        }
    }

    private void DefineJobs()
    {
        Def("job_start", (c, name, nameLen, args, argsLen) =>
        {
            var file = ReadName(c, name, nameLen);
            if ((uint) argsLen > WasmHost.MaxArgs)
                return -4;

            var arguments = ReadText(c, args, argsLen);
            var io = Io(c);
            if (io.Jobs.Count + io.Spawns.Count >= WasmHost.MaxJobs)
                return -3;

            var data = io.Disk.Read(file);
            if (data == null && !io.Host.IsSystemProgram(file))
                return -1;

            if (data != null && !IsProgram(data))
                return -2;

            io.NextJob++;
            io.Spawns.Add((io.NextJob, file, arguments));
            return (int) io.NextJob;
        });

        Def("job_list", (c, buf, cap) =>
            WriteText(c, buf, cap, string.Concat(Io(c).Jobs.Select(j => $"{j.Id} {j.Name}\n"))));

        Def("job_kill", (c, id) =>
        {
            var io = Io(c);
            if (!io.Jobs.Any(j => (int) j.Id == id))
                return -1;

            io.Kills.Add((uint) id);
            return 0;
        });
    }

    /// <summary>
    /// A deck's view of cyberspace, and what it asks of it. Every function returns -1 on anything but a deck in
    /// cyberspace.
    /// </summary>
    private void DefineDeck()
    {
        Def("deck_integrity", c => Io(c).DeckView?.Integrity ?? -1);

        Def("deck_status", c => Io(c).DeckView is { } view
            ? (view.Warded ? 1 : 0) + (view.StrikeReady ? 2 : 0) + (view.WardReady ? 4 : 0)
            : -1);

        Def("deck_targets", (c, buf, cap) =>
        {
            if (Io(c).DeckView is not { } view)
                return -1;

            var text = new StringBuilder();
            foreach (var t in view.Targets)
            {
                text.Append($"{t.Id} {(t.Ice ? "ice" : "runner")} {t.Integrity} {(t.InReach ? 1 : 0)} {t.Distance} {t.Name}\n");
            }

            return WriteText(c, buf, cap, text.ToString());
        });

        Def("deck_strike", (c, target) =>
        {
            var io = Io(c);
            if (io.DeckView is not { } view)
                return -1;

            if (!view.StrikeReady)
                return -2;

            if (!view.Targets.Any(t => t.Id == target && t.InReach))
                return -1;

            io.DeckStrike = target;
            return 0;
        });

        Def("deck_ward", c =>
        {
            var io = Io(c);
            if (io.DeckView is not { } view)
                return -1;

            if (!view.WardReady)
                return -2;

            io.DeckWard = true;
            return 0;
        });

        Def("deck_hold", (c, name, len) =>
        {
            var io = Io(c);
            if (io.DeckView == null)
                return -1;

            var file = ReadName(c, name, len);
            var data = io.Disk.Read(file);
            if (data == null ? !io.Host.IsSystemProgram(file) : !IsProgram(data))
                return -2;

            io.DeckHold = file;
            return 0;
        });

        Def("deck_push", (c, addr, name, len) =>
        {
            var io = Io(c);
            if (io.DeckView == null)
                return -1;

            var file = ReadName(c, name, len);
            if (io.Disk.Read(file) is not { } data)
                return -2;

            var to = (uint) addr;
            io.DeckPush = new DeckPush(to, io.Reachable?.Contains(to) == true, file, data);
            return 0;
        });

        Def("deck_colour", (c, hex, len) =>
        {
            var io = Io(c);
            if (!io.Is(DeviceKind.Deck) || (uint) len > 7)
                return -1;

            var text = ReadText(c, hex, len).TrimStart('#');
            if (text.Length != 6 || !Color.TryFromHex("#" + text, out _))
                return -1;

            io.DeckColour = "#" + text;
            return 0;
        });
    }

    /// <summary>
    /// ICE, run by a program on a computer: the world shows each program the ICE it runs before the tick, and
    /// carries out its orders after. Every function but <c>ice_start</c> returns -1 for a program running none.
    /// </summary>
    private void DefineIce()
    {
        Def("ice_start", c =>
        {
            var io = Io(c);
            if (io.Kind != DeviceKind.Computer)
                return -1;

            return io.IceViews.ContainsKey(io.Pid) || !io.IceStarts.Add(io.Pid) ? -2 : 0;
        });

        Def("ice_integrity", c => IceOf(c)?.Integrity ?? -1);
        Def("ice_here", c => IceOf(c)?.Here ?? -1L);
        Def("ice_nodes", (c, buf, cap) => IceOf(c) is { } view ? WriteIds(c, buf, cap, view.Nodes) : -1);
        Def("ice_neighbours", (c, buf, cap) => IceOf(c) is { } view ? WriteIds(c, buf, cap, view.Neighbours) : -1);

        Def("ice_go", (c, node) =>
            IceOf(c) is { } view && view.Nodes.Contains((uint) node) ? IceOrder(c, IceOrderKind.Go, node) : -1);

        Def("ice_chase", (c, runner) =>
            IceOf(c) is { } view && view.Runners.Any(r => r.Id == runner) ? IceOrder(c, IceOrderKind.Chase, runner) : -1);

        Def("ice_attack", (c, runner) =>
            IceOf(c) is { } view && view.Runners.Any(r => r.Id == runner && r.InReach)
                ? IceOrder(c, IceOrderKind.Attack, runner)
                : -1);

        Def("ice_runners", (c, buf, cap) =>
        {
            if (IceOf(c) is not { } view)
                return -1;

            var text = new StringBuilder();
            foreach (var r in view.Runners)
            {
                text.Append($"{r.Id} {r.Node} {(r.Authorized ? 1 : 0)} {(r.InReach ? 1 : 0)} {r.X} {r.Y} {r.Name}\n");
            }

            return WriteText(c, buf, cap, text.ToString());
        });

        Def("ice_position", (c, buf, cap) => IceOf(c) is { } view ? WriteTile(c, buf, cap, (view.X, view.Y)) : -1);

        Def("ice_alert", (c, buf, cap) => IceOf(c) is { } view
            ? view.Alert is { } alert ? WriteTile(c, buf, cap, alert) : 0
            : -1);

        Def("ice_breach", (c, buf, cap) => Io(c).Breach is { } at ? WriteTile(c, buf, cap, at) : -1);

        Def("ice_go_to", (c, x, y) => IceOf(c) != null ? IceOrder(c, IceOrderKind.GoTo, x, y) : -1);

        Def("ice_mode", (c, mode) =>
            IceOf(c) != null && mode is >= 0 and <= 2 ? IceOrder(c, IceOrderKind.Mode, mode) : -1);
    }

    private static IceView? IceOf(Caller caller)
    {
        var io = Io(caller);
        return io.IceViews.GetValueOrDefault(io.Pid);
    }

    private static int IceOrder(Caller caller, IceOrderKind kind, int a, int b = 0)
    {
        var io = Io(caller);
        io.IceOrders.Add(new IceOrder(io.Pid, kind, a, b));
        return 0;
    }

    private static int WriteTile(Caller caller, int buf, int cap, (int X, int Y) tile)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes, tile.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), tile.Y);
        WriteBytes(caller, buf, cap, bytes);
        return 8;
    }

    /// <summary>
    /// The body, which arrives with cyberware. Linked now so programs that use it load; each returns -1 (or does
    /// nothing) until then, as on a machine it doesn't work on.
    /// </summary>
    private void DefineLater()
    {
        Def("body_vitals", (_, _, _) => -1);
        Def("body_alert", (_, _, _) => -1);
        Def("body_inject", _ => -1);
        Def("body_boost", _ => -1);
    }
}
