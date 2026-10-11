using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Server._CyberPunk.Wasm;
using Content.Server._CyberPunk.Wire;
using NUnit.Framework;

#nullable enable

namespace Content.Tests.Server._CyberPunk;

/// <summary>
/// Wire, the machines' programming language: its lexer, parser and compiler, and its programs running on a
/// machine. Ported from the tests in Switchboard's <c>wasm/wire/src</c>.
/// </summary>
[TestFixture]
[TestOf(typeof(WireCompiler))]
public sealed class WireTest
{
    private WasmHost _host = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        _host = new WasmHost();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _host.Dispose();
    }

    /// <summary>
    /// Builds a program, runs it as a machine's firmware for a few ticks, and returns what it printed.
    /// </summary>
    private string Run(string source, DeviceKind kind = DeviceKind.Computer, int ticks = 3, Action<Vm>? setup = null)
    {
        using var vm = Vm.WithFirmware("prog", _host.Load(WireCompiler.Compile(source)), kind);
        setup?.Invoke(vm);
        vm.PowerOn(_host);
        var output = new StringBuilder();
        for (var i = 0; i < ticks && vm.State == VmState.Running; i++)
        {
            vm.Tick(_host, 33, WasmHost.FuelPerCall);
            output.Append(vm.TakeOutput());
        }

        return output.ToString();
    }

    private static WireException Mistake(string source)
    {
        return Assert.Throws<WireException>(() => WireCompiler.Compile(source))!;
    }

    #region Lexing and parsing

    private static List<(TokenKind, string)> Tokens(string source)
    {
        return WireLexer.Lex(source).Select(t => (t.Kind, t.Kind == TokenKind.Int ? t.Value.ToString() : t.Text)).ToList();
    }

    [Test]
    public void BlocksComeFromIndentation()
    {
        Assert.That(Tokens("if x:\n    y = 1\n\n    # note\nz\n"), Is.EqualTo(new List<(TokenKind, string)>
        {
            (TokenKind.Name, "if"),
            (TokenKind.Name, "x"),
            (TokenKind.Sym, ":"),
            (TokenKind.Newline, ""),
            (TokenKind.Indent, ""),
            (TokenKind.Name, "y"),
            (TokenKind.Sym, "="),
            (TokenKind.Int, "1"),
            (TokenKind.Newline, ""),
            (TokenKind.Dedent, ""),
            (TokenKind.Name, "z"),
            (TokenKind.Newline, ""),
            (TokenKind.Eof, ""),
        }));
    }

    [Test]
    public void BracketsJoinLinesAndTextHasEscapes()
    {
        var tokens = Tokens("x = [1,\n  2]\ns = 'a\\nb'");
        Assert.That(tokens, Does.Contain((TokenKind.Str, "a\nb")));
        Assert.That(tokens.Count(t => t.Item1 == TokenKind.Newline), Is.EqualTo(2));
        Assert.That(Tokens("a //= 2")[1], Is.EqualTo((TokenKind.Sym, "//=")));
        Assert.That(Tokens("n = 1_000")[2], Is.EqualTo((TokenKind.Int, "1000")));
    }

    [Test]
    public void LexingMistakesSayWhere()
    {
        var e = Assert.Throws<WireException>(() => WireLexer.Lex("x = 'oops"))!;
        Assert.That((e.Line, e.Column), Is.EqualTo((1, 5)));
        Assert.That(Assert.Throws<WireException>(() => WireLexer.Lex("if x:\n    y\n  z\n"))!.Line, Is.EqualTo(3));
        Assert.Throws<WireException>(() => WireLexer.Lex("x = 99999999999999999999"));
        Assert.Throws<WireException>(() => WireLexer.Lex("x = )"));
        Assert.Throws<WireException>(() => WireLexer.Lex("x = (1"));
        Assert.Throws<WireException>(() => WireLexer.Lex("x = $"));
    }

    [Test]
    public void ParsesAProgram()
    {
        const string src = "allowed = ['Ana', 'Bo']\n\ndef on_door_request(who):\n    if who.holding == 'crowbar':\n        return False\n    return who.name in allowed\n\nfor i in range(3): print(i)\n";
        var body = WireParser.Parse(src);
        Assert.That(body, Has.Count.EqualTo(3));
        var def = (DefStmt) body[1];
        Assert.That((def.Name, def.Params.Count, def.Body.Count), Is.EqualTo(("on_door_request", 1, 2)));
    }

    [Test]
    public void OperatorsHavePrecedence()
    {
        var body = WireParser.Parse("x = 1 + 2 * 3 == 7 and not y");
        Assert.That(((AssignStmt) body[0]).Value, Is.TypeOf<AndExpr>());
    }

    [Test]
    public void ParsingMistakesAreFriendly()
    {
        var e = Assert.Throws<WireException>(() => WireParser.Parse("x = 1\nif x\n    y"))!;
        Assert.That(e.Line, Is.EqualTo(2));
        Assert.That(e.Problem, Does.Contain("\":\""));

        string Problem(string src) => Assert.Throws<WireException>(() => WireParser.Parse(src))!.Problem;
        Assert.That(Problem("a < b < c"), Does.Contain("two things"));
        Assert.That(Problem("import os"), Does.Contain("nothing to import"));
        Assert.That(Problem("else:\n  x"), Does.Contain("no `if`"));
        Assert.That(Problem("x[1:2]"), Does.Contain("slices"));
        Assert.That(Problem("x = (1, 2)"), Does.Contain("no tuples"));
        Assert.That(Problem("a, b = 1"), Does.Contain("one thing at a time"));
        Assert.That(Problem("p.name = 1"), Does.Contain("can't set that"));
        Assert.Throws<WireException>(() => WireParser.Parse("f() = 3"));
        Assert.Throws<WireException>(() => WireParser.Parse("  x = 1"));
    }

    #endregion

    #region Checks

    [Test]
    public void MistakesAreCaughtBeforeRunning()
    {
        var cases = new[]
        {
            ("print(nope)", "nope isn't defined"),
            ("pritn('x')", "did you mean print?"),
            ("x = true", "did you mean True?"),
            ("def f(a):\n  return a\nf()", "f takes 1 argument, not 0"),
            ("len(1, 2)", "len is called len(x)"),
            ("net.sned(1)", "net has no function sned"),
            ("door.frobnicate()", "door has no function"),
            ("x = term.UPP", "term has no UPP"),
            ("return 1", "only for inside a def"),
            ("break", "only for inside a loop"),
            ("continue", "only for inside a loop"),
            ("def tick(x):\n  pass", "def tick():"),
            ("def on_door_request():\n  pass", "def on_door_request(who):"),
            ("def len():\n  pass", "built in"),
            ("net = 1", "built in"),
            ("def f():\n  pass\nf = 2", "is a function"),
            ("def f():\n  pass\ndef f():\n  pass", "already defined"),
            ("if True:\n  def g():\n    pass", "top level"),
            ("def f():\n  if True:\n    def g():\n      pass", "top level"),
            ("x = print", "is a function"),
            ("y = net", "is a module"),
            ("x = sys.clock", "is a function: call it"),
            ("x = 1\ndef f(x):\n  return x", "same name as a global"),
            ("x = 3\nx()", "only functions can be called"),
            ("x = [1]\nx.pop(1, 2, 3)", "doesn't take 3 arguments"),
        };

        foreach (var (src, want) in cases)
        {
            Assert.That(Mistake(src).Problem, Does.Contain(want), src);
        }
    }

    [Test]
    public void MistakesSayWhere()
    {
        var e = Mistake("x = 1\n\nprint(y)");
        Assert.That((e.Line, e.Column), Is.EqualTo((3, 7)));
        Assert.That(e.Message, Is.EqualTo("line 3, column 7: y isn't defined (did you mean x?)"));
    }

    [Test]
    public void HooksAreExportedWhenDefined()
    {
        string Exports(string src)
        {
            using var module = Wasmtime.Module.FromBytes(new Wasmtime.Engine(new Wasmtime.Config().WithGc(true).WithFunctionReferences(true).WithReferenceTypes(true)), "p", WireCompiler.Compile(src));
            return string.Join(" ", module.Exports.Select(e => e.Name).Order());
        }

        Assert.That(Exports("x = 1\ndef tick():\n    x += 1\ndef on_door_request(who):\n    return True\n"),
            Is.EqualTo("memory on_door_request start tick"));
        Assert.That(Exports("print('hi')"), Is.EqualTo("memory start"));
    }

    #endregion

    #region Running

    [Test]
    public void ArithmeticTextAndControlFlow()
    {
        Assert.That(Run("print(1 + 2 * 3, 7 // 2, -7 % 3, 2 - 5)"), Does.StartWith("7 3 2 -3\n"));
        Assert.That(Run("print(7 / 2, -7 // 2, 7 // -2, 7 % -3, -7 % -3)"), Does.StartWith("3 -4 -3 -2 -1\n"));
        Assert.That(Run("print('ab' + 'c', 'x' * 3, 2 * 'y', len('héllo'), [1] * 2 + [3])"), Does.StartWith("abc xxx yy 5 [1, 1, 3]\n"));
        Assert.That(Run("total = 0\nfor i in range(10):\n    if i % 2 == 0:\n        continue\n    if i > 7:\n        break\n    total += i\nprint(total)"),
            Does.StartWith("16\n"));
        Assert.That(Run("n = 0\nwhile True:\n    n += 1\n    if n == 5: break\nprint(n)"), Does.StartWith("5\n"));
        Assert.That(Run("print(1 < 2 and 'yes' or 'no', not 0, None == None, [] or 'empty', 0 and 1)"),
            Does.StartWith("yes True True empty 0\n"));
        Assert.That(Run("print('a' < 'b', 'b' <= 'a', 3 >= 3, 2 != 2, 'x' in 'box', 4 not in [1, 2])"),
            Does.StartWith("True False True False True True\n"));
        Assert.That(Run("x = 5\nif x > 9:\n    print('big')\nelif x > 3:\n    print('middle')\nelse:\n    print('small')"),
            Does.StartWith("middle\n"));
        Assert.That(Run("print(-9223372036854775807 - 1, 9223372036854775807)"),
            Does.StartWith("-9223372036854775808 9223372036854775807\n"));
    }

    [Test]
    public void FunctionsAndGlobals()
    {
        const string src = "count = 0\ndef bump(by):\n    count += by\n    return count\ndef fact(n):\n    if n <= 1: return 1\n    return n * fact(n - 1)\nbump(2)\nbump(3)\nprint(count, fact(10))";
        Assert.That(Run(src), Does.StartWith("5 3628800\n"));

        // Locals stay local, and a function with no return gives None.
        Assert.That(Run("x = 1\ndef f():\n    y = 2\n    return y\ndef g():\n    pass\nprint(f(), x, g())"), Does.StartWith("2 1 None\n"));

        // Functions can be used above where they are defined, and call each other.
        Assert.That(Run("print(even(10))\ndef even(n):\n    if n == 0: return True\n    return odd(n - 1)\ndef odd(n):\n    if n == 0: return False\n    return even(n - 1)"),
            Does.StartWith("True\n"));
    }

    [Test]
    public void ListsDictsAndMethods()
    {
        const string src = "xs = [3, 1, 2]\nxs.append(5)\nxs[0] = 9\nprint(xs, sorted(xs), len(xs), 2 in xs, xs.pop(), max(xs), min(4, 8))\nd = {'a': 1}\nd['b'] = 2\nd['a'] += 10\nprint(d, d.get('z', 0), 'b' in d, d.keys())\nfor k in d: print(k, d[k])";
        Assert.That(Run(src), Does.StartWith(
            "[9, 1, 2] [1, 2, 5, 9] 4 True 5 9 4\n{\"a\": 11, \"b\": 2} 0 True [\"a\", \"b\"]\na 11\nb 2\n"));

        Assert.That(Run("s = ' Open the Door '\nprint(s.strip().lower().split(), '-'.join(['a', 'b']), s.find('the'), s.replace('Door', 'gate'))"),
            Does.StartWith("[\"open\", \"the\", \"door\"] a-b 6  Open the gate \n"));
        Assert.That(Run("print(int('42') + 1, int('x'), int(' -7 '), int(True), str(5) + '!', chr(65), ord('a'), chr(233), ord('é'))"),
            Does.StartWith("43 None -7 1 5! A 97 é 233\n"));

        Assert.That(Run("xs = [1, 2, 3, 2]\nxs.insert(0, 0)\nxs.insert(-1, 9)\nxs.remove(2)\nprint(xs, xs.index(2), xs.index(7), xs.count(2), xs.pop(0), xs)"),
            Does.StartWith("[1, 3, 9, 2] 4 None 1 0 [1, 3, 9, 2]\n"));
        Assert.That(Run("d = {1: 'one', 'two': 2, None: True}\nprint(d.pop(1), d.pop(5), d, d.values(), len(d), d.get('two'))"),
            Does.StartWith("one None {\"two\": 2, None: True} [2, True] 2 2\n"));
        Assert.That(Run("t = 'a,b,,c'\nprint(t.split(','), t.count(','), t.upper(), t.startswith('a,'), t.endswith('c'), t.endswith('xa,b,,c'))"),
            Does.StartWith("[\"a\", \"b\", \"\", \"c\"] 3 A,B,,C True True False\n"));
        Assert.That(Run("print(range(3), range(2, 5), range(10, 0, -3), range(5, 1), sorted(['b', 'a', 'c']), max([3, 7, 5]))"),
            Does.StartWith("[0, 1, 2] [2, 3, 4] [10, 7, 4, 1] [] [\"a\", \"b\", \"c\"] 7\n"));
        Assert.That(Run("s = 'héllo'\nprint(s[1], s[-1])"),
            Does.StartWith("é o\n"));
        Assert.That(Run("out = []\nfor c in 'añb': out.append(c)\nprint(out, 'quote\"s', ['quote\"s', 'new\\nline'])"),
            Does.StartWith("[\"a\", \"ñ\", \"b\"] quote\"s [\"quote\\\"s\", \"new\\nline\"]\n"));
        Assert.That(Run("xs = [1, 2]\nfor x in xs:\n    xs.append(x)\nprint(xs, [1, [2, {}]] == [1, [2, {}]], {'a': 1} == {'a': 2})"),
            Does.StartWith("[1, 2, 1, 2] True False\n"));
    }

    [Test]
    public void RuntimeErrorsSayWhere()
    {
        Assert.That(Run("x = 1\n\ny = x / 0"), Does.StartWith("error on line 3: can't divide by zero\n"));
        Assert.That(Run("x = 1\n\ny = x / 0"), Does.Not.Contain("crashed"));

        var cases = new[]
        {
            ("print('a' + 1)", "use str()"),
            ("xs = [1]\nprint(xs[5])", "position 5 is past the end (there are 1)"),
            ("d = {}\nprint(d['k'])", "the dict has no \"k\" (use .get() to allow for that)"),
            ("def f(n):\n    return f(n)\nf(1)", "error on line 2 (in f): too many calls"),
            ("x = 9223372036854775807\nx += 1", "number too big"),
            ("x = -9223372036854775807 - 1\nx = x * -1", "number too big"),
            ("x = range(1000000)", "that range is too big"),
            ("x = 'a' * 100000000", "too long"),
            ("x = 3\nx.append(1)", "a number has no method append"),
            ("x = 'abc'\nx.append(1)", "text has no method append (see man wire)"),
            ("x = [1]\nx.frob()", "lists have no method frob"),
            ("p = [1]\nprint(p.name)", "a list has no name"),
            ("x = 1 < 'a'", "can't compare a number with text"),
            ("x = sorted([1, 'a'])", "can't sort"),
            ("x = {[1]: 2}", "a list can't be a dict key"),
            ("for x in 5: pass", "can't loop over a number"),
            ("x = -'a'", "can't make text negative"),
            ("x = None + 1", "can't add None and a number"),
            ("x = 'abc'\nx[0] = 'z'", "text can't be changed in place"),
            ("x = [].pop()", "pop from an empty list"),
            ("x = min([])", "min of nothing"),
            ("x = chr(-1)", "-1 isn't a character"),
            ("x = len(5)", "a number has no length"),
            ("x = ','.join([1])", "join needs a list of texts, not a number"),
            ("x = net.send('nowhere', 70000, 'hi')", "70000 isn't a port (0 to 65535)"),
            ("x = sys.random(0)", "sys.random needs a number above 0"),
            ("x = door.open()", "door functions only work on door controllers"),
            ("x = camera.count()", "camera.count only works on cameras"),
        };

        foreach (var (src, want) in cases)
        {
            Assert.That(Run(src), Does.Contain(want), src);
        }
    }

    [Test]
    public void AnEndlessLoopRunsOutOfTime()
    {
        Assert.That(Run("print('Spinning forever...')\nn = 0\nwhile True:\n    n += 1"),
            Does.Contain("Spinning forever...\n").And.Contain("[prog killed: it used up its time budget]"));
    }

    [Test]
    public void TickRunsUntilExit()
    {
        const string src = "ticks = 0\ndef tick():\n    ticks += 1\n    print('tick', ticks)\n    if ticks == 3:\n        sys.exit()\n";
        Assert.That(Run(src, ticks: 10), Does.StartWith("tick 1\ntick 2\ntick 3\n").And.Not.Contain("tick 4"));
    }

    [Test]
    public void FilesOnTheDisk()
    {
        const string src = """
            print(fs.write("a.txt", "one"), fs.append("a.txt", " two"), fs.append("b.txt", "new"))
            print(fs.read("a.txt"), fs.read("missing"), fs.files())
            print(fs.delete("b.txt"), fs.delete("b.txt"), fs.write("../bad", "x"), fs.files())
            """;
        Assert.That(Run(src), Does.StartWith(
            "True True True\none two None [\"a.txt\", \"b.txt\"]\nTrue False False [\"a.txt\"]\n"));
    }

    [Test]
    public void TheSystemsTools()
    {
        const string src = """
            fs.write("ok.wire", "print('built')")
            print(sys.version(), fs.size("missing"), fs.write("a.txt", "four"), fs.size("a.txt"))
            print(fs.delete("nope"), sys.error(), fs.delete("a.txt"), sys.error())
            print(sys.run("nope.bin"), sys.error(), sys.start("nope.bin"), sys.error())
            print(sys.man("nonsense"), "man wire" in sys.man(), sys.scaffold("toaster"), "sys.exit" in sys.scaffold("computer"))
            print("NAME" in sys.scaffold("os"))
            term.write("no ")
            term.write("newline\n")
            print("a b  c".split(" ", 1), "a,b,c".split(",", 0), "a,b".split(",", -1))
            print(sys.build("ok.wire", "ok.bin") > 0, sys.build("bad.wire", "x.bin"), sys.error())
            """;
        Assert.That(Run(src), Does.StartWith($$"""
            {{Kernel.ApiVersion}} None True 4
            False no such file True None
            False no such file None no such file
            None True None True
            True
            no newline
            ["a", "b  c"] ["a,b,c"] ["a", "b"]
            True None no such file

            """));
    }

    [Test]
    public void TheNetwork()
    {
        const string src = """
            print(net.address(), net.neighbours())
            def tick():
                for p in net.receive():
                    if p.text == "open":
                        net.send(p.sender, p.port, "ok at " + str(sys.clock()))
                    print(p)
            """;

        using var vm = Vm.WithFirmware("prog", _host.Load(WireCompiler.Compile(src)), DeviceKind.Computer);
        vm.SetNetwork(0x0A020101, new HashSet<uint> { 0x0A020102 }, [0x0A020102], new Dictionary<string, uint>());
        vm.PowerOn(_host);
        vm.Tick(_host, 33, WasmHost.FuelPerCall);
        Assert.That(vm.Deliver(new Packet(0x0A020102, 0x0A020101, 7, "open"u8.ToArray())));
        vm.Tick(_host, 33, WasmHost.FuelPerCall);

        Assert.That(vm.TakeOutput(), Does.StartWith(
            "10.2.1.1 [\"10.2.1.2\"]\npacket(sender=\"10.2.1.2\", port=7, text=\"open\")\n"));
        var sent = vm.TakeOutbox();
        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That((sent[0].To, sent[0].Port), Is.EqualTo((0x0A020102u, (ushort) 7)));
        Assert.That(Encoding.UTF8.GetString(sent[0].Data), Does.StartWith("ok at "));
    }

    private sealed class FakeDevices : IMachineDevices
    {
        public readonly List<(uint, string)> Requests = new();

        public string? Request(uint address, string request)
        {
            Requests.Add((address, request));
            return request switch
            {
                "info" => """{"name": "Vendomat", "calls": {"eject": {"id": "text"}}, "n": -12, "ok": True, "x": None, "s": "a\"b\n"}""",
                "state" => """[1, [ ], {}, "tab\there"]""",
                "call broken {}" => "!broken: no such call",
                _ => "None",
            };
        }
    }

    [Test]
    public void ProgramsWorkMachinesOnTheNetwork()
    {
        const string src = """
            print(dev.info("vend"))
            print(dev.state("10.2.1.3"))
            print(dev.call("vend", "eject", {"id": "Cola"}), dev.call("vend", "eject"))
            print(dev.call("vend", "broken"), sys.error())
            print(dev.info("nobody"), sys.error())
            print(dev.state("10.2.1.9"), sys.error())
            """;

        var devices = new FakeDevices();
        using var vm = Vm.WithFirmware("prog", _host.Load(WireCompiler.Compile(src)), DeviceKind.Computer);
        vm.SetNetwork(0x0A020101,
            new HashSet<uint> { 0x0A020101, 0x0A020103 },
            [0x0A020103],
            new Dictionary<string, uint> { ["vend"] = 0x0A020103 });
        vm.Devices = devices;
        vm.PowerOn(_host);
        vm.Tick(_host, 33, WasmHost.FuelPerCall);

        Assert.That(vm.TakeOutput(), Does.StartWith(
            "{\"name\": \"Vendomat\", \"calls\": {\"eject\": {\"id\": \"text\"}}, \"n\": -12, \"ok\": True, \"x\": None, \"s\": \"a\\\"b\\n\"}\n" +
            "[1, [], {}, \"tab\\there\"]\n" +
            "True True\n" +
            "False broken: no such call\n" +
            "None there's no machine called nobody\n" +
            "None 10.2.1.9 doesn't answer\n"));
        Assert.That(devices.Requests, Is.EqualTo(new List<(uint, string)>
        {
            (0x0A020103, "info"),
            (0x0A020103, "state"),
            (0x0A020103, "call eject {\"id\": \"Cola\"}"),
            (0x0A020103, "call eject {}"),
            (0x0A020103, "call broken {}"),
        }));
    }

    [Test]
    public void DevicesOnlyAnswerComputers()
    {
        Assert.That(Run("dev.info('vend')", DeviceKind.DoorController), Does.Contain("dev.info only works on computers"));
    }

    [Test]
    public void Hostnames()
    {
        const string src = """
            print(net.hostname(), net.set_hostname("Bad Name"), net.set_hostname("-x"), net.set_hostname("lab-1"), net.hostname())
            print(net.resolve("door-3"), net.resolve("nobody"), net.resolve("10.2.1.9"), net.hosts())
            print(net.send("door-3", 1701, "open"), net.send("nobody", 1701, "open"))
            """;

        using var vm = Vm.WithFirmware("prog", _host.Load(WireCompiler.Compile(src)), DeviceKind.Computer);
        vm.SetNetwork(0x0A020101,
            new HashSet<uint> { 0x0A020101, 0x0A020103 },
            [0x0A020103],
            new Dictionary<string, uint> { ["lab-1"] = 0x0A020101, ["door-3"] = 0x0A020103 });
        vm.PowerOn(_host);
        vm.Tick(_host, 33, WasmHost.FuelPerCall);

        Assert.That(vm.TakeOutput(), Does.StartWith(
            "None False False True lab-1\n" +
            "10.2.1.3 None 10.2.1.9 {\"door-3\": \"10.2.1.3\", \"lab-1\": \"10.2.1.1\"}\n" +
            "True False\n"));
        Assert.That(vm.TakeHostnameChanged());
        Assert.That(vm.Hostname, Is.EqualTo("lab-1"));
        var sent = vm.TakeOutbox();
        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That((sent[0].To, sent[0].Port), Is.EqualTo((0x0A020103u, (ushort) 1701)));
    }

    [Test]
    public void TheTerminal()
    {
        const string src = """
            lines = []
            def tick():
                line = term.read_line()
                if line == None:
                    return
                if line == "quit":
                    print("got", lines)
                    sys.exit()
                    return
                lines.append(line)
            """;

        using var vm = Vm.WithFirmware("prog", _host.Load(WireCompiler.Compile(src)), DeviceKind.Computer);
        vm.PowerOn(_host);
        vm.Tick(_host, 33, WasmHost.FuelPerCall);
        vm.TypeLine("one");
        vm.TypeLine("two words");
        vm.TypeLine("quit");
        for (var i = 0; i < 5; i++)
        {
            vm.Tick(_host, 33, WasmHost.FuelPerCall);
        }

        Assert.That(vm.TakeOutput(), Does.Contain("got [\"one\", \"two words\"]\n"));
    }

    [Test]
    public void TheDoorHookDecidesWhoGetsIn()
    {
        const string src = """
            allowed = ["Ana"]
            def on_door_request(who):
                print(who)
                return who.name in allowed and who.holding != "crowbar"
            def tick():
                pass
            """;

        using var vm = Vm.WithFirmware("door", _host.Load(WireCompiler.Compile(src)), DeviceKind.DoorController);
        vm.SetDevice(new DoorDevice(Open: false, Blocked: false, Bolted: false));
        vm.PowerOn(_host);
        vm.Tick(_host, 33, WasmHost.FuelPerCall);

        Assert.That(vm.GuardsDoor);
        Assert.That(vm.DoorRequest(_host, new Requester("Ana", "", ["NT"]), WasmHost.FuelPerCall), Is.True);
        Assert.That(vm.DoorRequest(_host, new Requester("Ana", "crowbar", []), WasmHost.FuelPerCall), Is.False);
        Assert.That(vm.DoorRequest(_host, new Requester("Bo", "", []), WasmHost.FuelPerCall), Is.False);
        Assert.That(vm.TakeOutput(), Does.Contain("person(name=\"Ana\", holding=\"\", cards=[\"NT\"])"));
    }

    [Test]
    public void DoorsWorkFromWire()
    {
        const string src = "print(door.open(), door.status())\ndef tick():\n    pass";
        Assert.That(Run(src, DeviceKind.DoorController, 1, vm => vm.SetDevice(new DoorDevice(false, false, false))),
            Does.StartWith("True door(open=True, bolted=False, blocked=False)\n"));
        Assert.That(Run(src, DeviceKind.DoorController, 1, vm => vm.SetDevice(new DoorDevice(false, false, true))),
            Does.StartWith("False door(open=False, bolted=True, blocked=False)\n"));
    }

    [Test]
    public void ModulesSayWhatTheMachineIs()
    {
        Assert.That(Run("print(sys.device(), sys.args(), sys.jobs())"), Does.StartWith("computer  []\n"));
        Assert.That(Run("print(sys.device(), camera.count(), camera.names())", DeviceKind.Camera, 1,
            vm => vm.SetDevice(new CameraDevice(["Ana", "Bo"]))), Does.StartWith("camera 2 [\"Ana\", \"Bo\"]\n"));
        Assert.That(Run("print(ice.health(), ice.here(), ice.nodes(), ice.position(), ice.alert())"),
            Does.StartWith("None None [] None None\n"));
    }

    #endregion

    #region The shell

    /// <summary>
    /// A machine running the default OS, to type commands at.
    /// </summary>
    private sealed class Shell : IDisposable
    {
        private readonly WasmHost _host;
        public readonly Vm Vm = new();

        public Shell(WasmHost host)
        {
            _host = host;
            Vm.PowerOn(host);
            Command("");
        }

        public string Command(string line, string until = "$ ")
        {
            if (line != "")
                Vm.TypeLine(line);

            var screen = new StringBuilder();
            for (var i = 0; i < 300; i++)
            {
                Vm.Tick(_host, 33, WasmHost.FuelPerCall);
                screen.Append(Vm.TakeOutput());
                if (screen.ToString().EndsWith(until))
                    return screen.ToString();
            }

            Assert.Fail($"Never saw \"{until}\". Screen:\n{screen}");
            return "";
        }

        public void Dispose()
        {
            Vm.Dispose();
        }
    }

    [Test]
    public void NewBuildAndRunAProgram()
    {
        using var shell = new Shell(_host);

        Assert.That(shell.Command("new hello"), Does.Contain("wrote hello.wire, a computer program. Next:\n  cat hello.wire"));
        Assert.That(shell.Command("new hello"), Does.Contain("new: hello.wire already exists"));
        Assert.That(shell.Command("new gadget toaster"), Does.Contain("new: no program for toaster"));
        Assert.That(shell.Command("new gate door"), Does.Contain("flash ADDR gate.bin"));
        Assert.That(shell.Command("build hello.wire"), Does.Match(@"built hello\.bin \(\d+ bytes\)"));

        var run = shell.Command("run hello.bin");
        Assert.That(run, Does.Contain("Hello from a new program!\n"));
        Assert.That(run, Does.Contain("A second has gone by. Bye!\n"));

        Assert.That(shell.Command("write broken.wire print(nope)"), Does.Contain("wrote"));
        Assert.That(shell.Command("build broken.wire"),
            Does.Contain("build: broken.wire: line 1, column 7: nope isn't defined"));
    }

    [Test]
    public void TheOsBuildsFromItsOwnSourceAndBoots()
    {
        Assert.That(DefaultOs.Source, Does.Contain($"NAME = \"{DefaultOs.Name}\""));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(DefaultOs.Source), Is.LessThanOrEqualTo(WasmHost.MaxSource));

        using var shell = new Shell(_host);
        Assert.That(shell.Command("new myos os"), Does.Contain("wrote myos.wire, the operating system's source"));
        Assert.That(shell.Command("build myos.wire boot.bin"), Does.Match(@"built boot\.bin \(\d+ bytes\)"));
        Assert.That(shell.Command("reboot"), Does.Contain("[rebooting]").And.Contain(DefaultOs.Name));
        Assert.That(shell.Vm.Processes, Is.EqualTo(new[] { Vm.BootFile }));
        Assert.That(shell.Command("echo booted from disk"), Does.Contain("booted from disk"));
    }

    [Test]
    public void ProgramsGetTheirArguments()
    {
        using var shell = new Shell(_host);
        Assert.That(shell.Command("write args.wire print('got', sys.args().split())"), Does.Contain("wrote"));
        Assert.That(shell.Command("build args.wire"), Does.Contain("built args.bin"));
        Assert.That(shell.Command("run args.bin one two"), Does.Contain("got [\"one\", \"two\"]\n"));
    }

    #endregion

    #region The manual

    [Test]
    public void EveryScaffoldBuilds()
    {
        foreach (var kind in WireManual.Kinds)
        {
            var source = WireManual.Scaffold(kind)!;
            Assert.DoesNotThrow(() => _host.Load(WireCompiler.Compile(source)), kind);
        }

        Assert.That(WireManual.Scaffold("toaster"), Is.Null);
    }

    [Test]
    public void TheManualCoversEverything()
    {
        var all = string.Concat(new[] { "wire", "modules", "computer", "door", "camera", "ice", "deck", "implant", "hooks", "ui", "dev" }
            .Select(topic => Kernel.Man(topic) ?? throw new AssertionException($"no page on {topic}")));

        // Pages are wrapped to the screen, so compare without line breaks.
        var flat = all.Replace("\n", " ");
        foreach (var f in WireLibrary.Builtins.Concat(WireLibrary.ModuleFunctions))
        {
            Assert.That(flat, Does.Contain(f.Usage), $"{f.Name} isn't documented");
        }

        foreach (var (hook, _, _) in WireLibrary.Hooks)
        {
            Assert.That(flat, Does.Contain(hook));
        }

        Assert.That(Kernel.Man("")!, Does.Contain("man wire"));
    }

    #endregion
}
