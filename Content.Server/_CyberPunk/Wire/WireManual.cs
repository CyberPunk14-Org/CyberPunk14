using System.Linq;
using System.Text;

namespace Content.Server._CyberPunk.Wire;

/// <summary>
/// Wire's manual pages and the programs <c>new</c> starts from, written from the tables in
/// <see cref="WireLibrary"/> so they always match what programs can call. Ported from Switchboard's
/// <c>wasm/wire/src/docs.rs</c>.
/// </summary>
public static class WireManual
{
    private static string Entry(WireFunction f) => $"  {f.Usage}\n      {f.Doc}\n";

    /// <summary>
    /// <c>man wire</c>: the language.
    /// </summary>
    public static string Reference()
    {
        var page = new StringBuilder("""
            WIRE

            Wire is how you program these machines. It looks like Python:

              # a comment
              names = ["Ana", "Bo"]          # a list; {"key": value} is a dict
              count = 0
              def greet(name):               # a function
                  return "hi " + name
              for name in names:             # blocks are indented
                  if name == "Ana" and count < 3:
                      print(greet(name))
                  elif not name:
                      pass
                  else:
                      count += 1
              while count > 0:
                  count -= 1                 # also break and continue

            Values: whole numbers (no fractions: 7 / 2 is 3), text in quotes, True,
            False, None, lists and dicts. Compare with == != < <= > >=, and with `in`
            (is it in a list, a dict's keys, or a text). Combine with and, or, not.

            Names you assign at the top level are global: every function can read
            and change them. Other names a function assigns are its own.

            A program's code at the top level runs once when it starts; then the
            machine calls its hooks (man hooks). Start one with `new NAME`, build it
            with `build NAME.wire` and run it with `run NAME.bin`. Mistakes are
            reported with their line, when you build or as it runs.

            BUILT-IN FUNCTIONS

            """);

        foreach (var f in WireLibrary.Builtins)
        {
            page.Append(Entry(f));
        }

        page.Append("\nMETHODS (called on a value, like names.append(\"Cy\"))\n");
        foreach (var (_, usage, doc) in WireLibrary.Methods)
        {
            page.Append($"  {usage}\n      {doc}\n");
        }

        page.Append("""

            The machine's own functions are in modules: term, fs, net and sys on
            every machine (man modules), door on door controllers (man door),
            camera on cameras (man camera), ice for ICE programs on computers (man
            ice), deck on netrunners' decks (man deck), body in implants (man
            implant), and dev for working other machines from a computer (man
            dev).

            """);
        return page.ToString();
    }

    private static string ModuleEntries(string module)
    {
        return string.Concat(WireLibrary.ModuleFunctions.Where(f => f.Name.StartsWith($"{module}.")).Select(Entry));
    }

    /// <summary>
    /// <c>man modules</c>: what every machine has.
    /// </summary>
    public static string Modules()
    {
        var page = new StringBuilder("MODULES\n\nEvery machine has these. Call them like net.send(\"10.2.1.1\", 7, \"hi\").\n\nsys: the program\n");
        page.Append(ModuleEntries("sys"));
        page.Append("\nfs: files on this machine's disk (1 MiB, 64 files)\n");
        page.Append(ModuleEntries("fs"));
        page.Append("\nnet: the network (addresses look like 10.2.1.1, and machines can go by a hostname too; packets arrive the next tick, and are dropped if 64 are already waiting)\n");
        page.Append(ModuleEntries("net"));
        page.Append("\nterm: the terminal (computers only)\n");
        page.Append(ModuleEntries("term"));
        page.Append("  term.ENTER, term.BACKSPACE, term.TAB, term.DELETE, term.UP, term.DOWN,\n  term.LEFT, term.RIGHT, term.HOME, term.END, term.PAGE_UP, term.PAGE_DOWN\n      The keys term.key() returns that aren't characters.\n");
        page.Append("\nComputers can show a UI of buttons, lists and drawing instead of text: man ui.\n");
        page.Append("\nComputers can also work other machines on the network that have a UI, like vending machines and consoles: man dev.\n");
        page.Append("\nDoors, cameras, ICE, decks and implants have more: man door, man camera, man ice, man deck, man implant.\n");
        return page.ToString();
    }

    /// <summary>
    /// The Wire part of <c>man dev</c>: the dev module.
    /// </summary>
    public static string Dev()
    {
        var page = new StringBuilder("In Wire, the dev module works them:\n\n");
        page.Append("""
              print(dev.info("10.2.1.5"))
              stock = dev.state("10.2.1.5")
              if not dev.call("10.2.1.5", "vending_machine_eject", {"type": "Regular", "id": "DrinkColaCan"}):
                  print(sys.error())

            """);
        page.Append(ModuleEntries("dev"));
        return page.ToString();
    }

    /// <summary>
    /// The Wire part of <c>man ui</c>: the ui module.
    /// </summary>
    public static string Ui()
    {
        var page = new StringBuilder("In Wire, the ui module builds a UI from widgets and shows it:\n\n");
        page.Append("""
              ui.show(ui.column([
                  ui.label("Door control"),
                  ui.row([ui.button("open", "Open"), ui.button("shut", "Shut")]),
              ]))
              def tick():
                  for e in ui.events():
                      if e.id == "open":
                          net.send("door-1", 1701, "open")

            """);
        page.Append(ModuleEntries("ui"));
        return page.ToString();
    }

    /// <summary>
    /// The Wire part of a kind of machine's page (computer, door, camera, ice, deck or implant): its own module
    /// and hooks.
    /// </summary>
    public static string Device(string kind)
    {
        var module = kind switch
        {
            "door" => "door",
            "camera" => "camera",
            "ice" => "ice",
            "deck" => "deck",
            "implant" => "body",
            _ => "term",
        };

        var who = kind switch
        {
            "door" => "door controllers have the door",
            "camera" => "cameras have the camera",
            "ice" => "ICE programs have the ice",
            "deck" => "decks have the deck (and everything computers have)",
            "implant" => "implants have the body (and everything computers have)",
            _ => "computers have the term",
        };

        var page = new StringBuilder($"In Wire, {who} functions:\n");
        page.Append(ModuleEntries(module));
        foreach (var (hook, device, doc) in WireLibrary.Hooks)
        {
            if (device == kind)
                page.Append($"\nand the hook\n  {hook}\n      {doc}\n");
        }

        return page.ToString();
    }

    /// <summary>
    /// <c>man hooks</c>, in Wire.
    /// </summary>
    public static string Hooks()
    {
        var page = new StringBuilder("In Wire, the machine runs a program's code at these points:\n\n");
        foreach (var (hook, device, doc) in WireLibrary.Hooks)
        {
            var only = device switch
            {
                null => "",
                "door" => " (only on door controllers)",
                "ice" => " (only in ICE programs)",
                _ => $" (only on {device}s)",
            };
            page.Append($"  {hook}{only}\n      {doc}\n");
        }

        page.Append("\nEach run of a hook gets a budget of about 2 million steps; a hook that\nruns over (an endless loop) stops the program.\n");
        return page.ToString();
    }

    /// <summary>
    /// The kinds of machine <see cref="Scaffold"/> has a program for.
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "computer", "door", "camera", "ice", "deck", "implant" };

    /// <summary>
    /// A starting program for a kind of machine, explaining each hook; null for a kind there's none for.
    /// </summary>
    public static string? Scaffold(string kind)
    {
        return kind.Trim() switch
        {
            "" or "computer" => Computer,
            "door" => Door,
            "camera" => Camera,
            "ice" => Ice,
            "deck" => Deck,
            "implant" => Implant,
            _ => null,
        } is { } text ? text + "\n" : null;
    }

    private const string Computer = """
        # A Wire program for a computer.
        # Build it with `build FILE.wire`, then `run FILE.bin`.
        # `man wire` explains the language, `man modules` what you can call.

        # Code at the top level runs once, when the program starts.
        print("Hello from a new program!")
        ticks = 0

        # HOOK tick: runs 30 times a second after that, until sys.exit().
        # Without a tick, the program ends once the top-level code has run.
        def tick():
            ticks += 1
            if ticks == 30:
                print("A second has gone by. Bye!")
                sys.exit()
        """;

    private const string Door = """
        # A Wire program for a door controller.
        # Build it with `build FILE.wire`, open the controller's maintenance panel
        # with a screwdriver, then `flash ADDRESS FILE.bin` to put it there.
        # `man door` lists what a door controller can do.

        # Code at the top level runs once, when the controller starts this.
        allowed = []    # names to let in by hand; empty lets everyone in

        # HOOK tick: runs 30 times a second. This one takes commands over the
        # network on port 1701 (open, close, bolt, unbolt, allow NAME), like the
        # controller's own firmware, and answers each.
        def tick():
            for packet in net.receive():
                if packet.port != 1701:
                    continue
                words = packet.text.split()
                if len(words) == 0:
                    continue
                command = words[0]
                if command == "open":
                    door.open()
                elif command == "close":
                    door.close()
                elif command == "bolt":
                    door.bolt()
                elif command == "unbolt":
                    door.unbolt()
                elif command == "allow" and len(words) == 2:
                    allowed.append(words[1])
                net.send(packet.sender, 1701, "done: " + packet.text)

        # HOOK on_door_request: runs when someone tries to open the door by hand,
        # before it opens. Return True to let them in, False to keep it shut.
        # who.name is their name, who.holding what is in their hand ("" if
        # nothing), who.cards the tags of the ID cards they carry. This one
        # keeps out anyone holding a crowbar.
        def on_door_request(who):
            if who.holding == "crowbar":
                return False
            return len(allowed) == 0 or who.name in allowed
        """;

    private const string Camera = """
        # A Wire program for a security camera.
        # Build it with `build FILE.wire`, open the camera's maintenance panel
        # with a screwdriver, then `flash ADDRESS FILE.bin` to put it there.
        # `man camera` lists what a camera can do.

        # Code at the top level runs once, when the camera starts this.
        seen = []

        # HOOK tick: runs 30 times a second. This one keeps a list of everyone
        # who has walked past, and answers "count", "names" or "seen" sent
        # to it on port 1702.
        def tick():
            for name in camera.names():
                if name not in seen:
                    seen.append(name)
            for packet in net.receive():
                if packet.port != 1702:
                    continue
                if packet.text == "count":
                    answer = str(camera.count())
                elif packet.text == "names":
                    answer = ", ".join(camera.names())
                else:
                    answer = ", ".join(seen)
                net.send(packet.sender, 1702, answer)
        """;

    private const string Ice = """
        # A Wire program for ICE, guarding a building's network in cyberspace.
        # Build it with `build FILE.wire`, then `run FILE.bin &` on one of the
        # building's computers (the & runs it in the background, so the terminal
        # stays free; run it several times for several ICE). `man ice` lists what
        # ICE can do.
        #
        # The ICE lasts as long as this program runs. If a runner beats it, the
        # program is halted, and someone has to run it again. A computer's
        # autorun file can run it at boot; `new NAME ice_basic` copies the ICE
        # that comes with every computer.

        # Code at the top level runs once: this puts the ICE in cyberspace, on
        # this computer's pad. It arrives in time for the first tick.
        ice.start()
        state = "patrolling"
        spotted = 0       # when it first saw the runner it is watching
        last_seen = 0
        where = None      # where an intruder was last seen, as [x, y]

        def intruders():
            found = []
            for runner in ice.runners():
                if not runner.authorized:
                    found.append(runner)
            return found

        def become(new):
            state = new
            ice.mode(new)

        # HOOK on_breach_signal: runs when a runner breaches one of the
        # network's firewalls, with its tile as [x, y]. Rush there, engaging.
        def on_breach_signal(at):
            become("engaging")
            where = at
            last_seen = sys.clock()
            ice.go_to(at[0], at[1])

        # HOOK tick: runs 30 times a second. Patrolling, it walks to random nodes.
        # A trace alert, or an intruder in sight, sets it searching where they
        # were; three seconds with them in sight, and it engages: chases and
        # strikes. Long enough with nobody found, and it patrols again.
        def tick():
            now = sys.clock()
            seen = intruders()
            alert = ice.alert()
            if alert != None and state != "engaging":
                become("searching")
                where = alert
                last_seen = now
                ice.go_to(where[0], where[1])
            if len(seen) > 0:
                runner = seen[0]
                where = [runner.x, runner.y]
                last_seen = now
                if state == "patrolling":
                    become("searching")
                    spotted = now
                if state == "searching":
                    ice.go_to(runner.x, runner.y)
                    if now - spotted >= 3000:
                        become("engaging")
                if state == "engaging":
                    ice.chase(runner.id)
                    if runner.in_reach:
                        ice.attack(runner.id)
                return
            spotted = now
            if state != "patrolling" and now - last_seen > 10000:
                become("patrolling")
            nodes = ice.nodes()
            if state == "patrolling" and ice.here() != None and len(nodes) > 0:
                ice.go(nodes[sys.random(len(nodes))])
        """;

    private const string Deck = """
        # A Wire program for your deck, in cyberspace.
        # Build it with `build FILE.wire`, then `hold FILE.bin` to hold it: Z
        # runs it, and clicking ICE (or a runner) with it runs it at them, with
        # their id as its argument (sys.args()). `man deck` lists what a deck can
        # do.

        # Code at the top level runs once: aim at the target given, or the
        # nearest ICE in sight.
        target = int(sys.args())
        if target == None:
            for t in deck.targets():
                if t.ice and target == None:
                    target = t.id
        if target == None:
            print("nothing to strike")
            sys.exit()

        # HOOK tick: runs 30 times a second. This one keeps a ward up while the
        # target is in reach, and strikes it whenever it can, until it is gone.
        def tick():
            for t in deck.targets():
                if t.id == target:
                    if t.in_reach:
                        deck.ward()
                        deck.strike(target)
                    return
            print("target gone")
            sys.exit()
        """;

    private const string Implant = """
        # A Wire program for the computer in a piece of cyberware.
        # Build it with `build FILE.wire`, then `run FILE.bin &` to keep it
        # running in the background. `man implant` lists what the body can do.

        # Checks the owner's vitals once a second, and speaks up when they are
        # badly hurt.
        last = 0

        def tick():
            now = sys.clock()
            if now - last < 1000:
                return
            last = now
            v = body.vitals()
            if v == None:
                return
            if v.damage >= 60:
                body.alert("You're badly hurt: damage " + str(v.damage))
        """;
}
