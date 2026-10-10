using System.Globalization;
using System.Linq;
using System.Text;
using Wasmtime;

namespace Content.Server._CyberPunk.Wire;

/// <summary>
/// Compiles Wire into a WebAssembly program for the machines, checking everything it can first: names are
/// defined, functions get the right number of arguments, <c>return</c>, <c>break</c> and <c>continue</c> are where
/// they belong, and hooks take what the machine gives them. Ported from Switchboard's
/// <c>wasm/wire/src/compiler.rs</c>, which compiled to bytecode for an interpreter; this compiles straight to
/// WebAssembly instead.
/// </summary>
/// <remarks>
/// <para>
/// Names assigned at the top level are global: functions read and assign them directly. Any other name a
/// function assigns is its own.
/// </para>
/// <para>
/// The program is the <see cref="WireRuntime"/> with the program's code after it, in WAT: each Wire function
/// becomes a WebAssembly function taking and returning values, the top-level code runs from <c>start</c>, and
/// <c>tick</c>, <c>on_door_request</c> and <c>on_breach_signal</c> are exported when the program defines them.
/// </para>
/// </remarks>
public static class WireCompiler
{
    /// <summary>
    /// Compiles a program into WebAssembly.
    /// </summary>
    /// <exception cref="WireException">The program has a mistake.</exception>
    public static byte[] Compile(string source)
    {
        return Module.ConvertText(CompileToWat(source));
    }

    /// <summary>
    /// Compiles a program into WAT, WebAssembly's text form.
    /// </summary>
    /// <exception cref="WireException">The program has a mistake.</exception>
    public static string CompileToWat(string source)
    {
        return new Generator(WireParser.Parse(source)).Generate();
    }

    /// <summary>
    /// A function the program defines; <paramref name="Index"/> numbers them from 1 in the order they appear.
    /// </summary>
    private sealed record Function(string Name, int Index, List<string> Params, List<Stmt> Body);

    private sealed class Loop(string breakLabel, string continueLabel)
    {
        public readonly string Break = breakLabel;
        public readonly string Continue = continueLabel;
    }

    /// <summary>
    /// A function being compiled: its locals, and the temporaries and labels it needs.
    /// </summary>
    private sealed class FnCx(string name, int index, List<string> locals, int parameters, bool inDef)
    {
        public readonly string Name = name;
        public readonly int Index = index;
        public readonly List<string> Locals = locals;
        public readonly int Params = parameters;
        public readonly bool InDef = inDef;
        public readonly List<Loop> Loops = new();
        public int Temps;
        public int Iters;
        public int Labels;

        /// <summary>Where the statement being compiled is, as <c>$pos</c> holds it.</summary>
        public int Pos;

        public int? Local(string n)
        {
            var i = Locals.IndexOf(n);
            return i < 0 ? null : i;
        }

        public string Temp() => $"$t.{Temps++}";
    }

    private sealed class Generator(List<Stmt> body)
    {
        private readonly Dictionary<string, Function> _funcs = new();
        private readonly List<Function> _order = new();
        private readonly List<string> _globals = new();
        private readonly List<string> _strings = new();
        private readonly Dictionary<string, int> _stringIndex = new();
        private readonly List<long> _ints = new();
        private readonly Dictionary<long, int> _intIndex = new();

        public string Generate()
        {
            CollectFunctions();
            CollectGlobals();

            var runtime = WireRuntime.Source(s => $"(ref.as_non_null (global.get {Str(s)}))");
            var functions = new StringBuilder();
            foreach (var f in _order)
            {
                functions.Append(CompileFunction(f));
            }

            var main = new FnCx("<top level>", 0, new List<string>(), 0, false);
            var mainBody = new StringBuilder();
            foreach (var stmt in body)
            {
                if (stmt is not DefStmt)
                    CompileStmt(main, stmt, mainBody);
            }

            functions.Append("  (func $main\n").Append(Locals(main)).Append(mainBody).Append("  )\n");

            var wat = new StringBuilder("(module\n");
            wat.Append(runtime);
            wat.Append(functions);

            for (var i = 0; i < _globals.Count; i++)
            {
                wat.Append($"  (global $g.{i} (mut eqref) (ref.null none))\n");
            }

            for (var i = 0; i < _ints.Count; i++)
            {
                wat.Append($"  (global $k.{i} (ref $Int) (struct.new $Int (i64.const {_ints[i].ToString(CultureInfo.InvariantCulture)})))\n");
            }

            // The function names, for runtime errors, as text constants: add them before writing the strings out.
            var names = new StringBuilder($"(array.new_fixed $Arr {_order.Count + 1} (global.get {Str("<top level>")})");
            foreach (var f in _order)
            {
                names.Append($" (global.get {Str(f.Name)})");
            }

            names.Append(')');

            var data = new List<byte>();
            var init = new StringBuilder("  (func $init\n");
            for (var i = 0; i < _strings.Count; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(_strings[i]);
                wat.Append($"  (global $s.{i} (mut (ref null $Str)) (ref.null none))\n");
                init.Append($"    (global.set $s.{i} (struct.new $Str (array.new_data $Bytes $strings (i32.const {data.Count}) (i32.const {bytes.Length}))))\n");
                data.AddRange(bytes);
            }

            init.Append($"    (global.set $fnames {names}))\n");
            wat.Append(init);

            wat.Append("  (func (export \"start\")\n    (call $init)\n    (global.set $depth (i32.const 0))\n    (call $main))\n");
            if (_funcs.TryGetValue("tick", out var tick))
                wat.Append($"  (func (export \"tick\")\n    (global.set $depth (i32.const 0))\n    (drop (call $f.{tick.Index})))\n");

            if (_funcs.TryGetValue("on_door_request", out var door))
                wat.Append($"  (func (export \"on_door_request\") (result i32)\n    (global.set $depth (i32.const 0))\n    (call $truthy (call $f.{door.Index} (call $who))))\n");

            if (_funcs.TryGetValue("on_breach_signal", out var breach))
                wat.Append($"  (func (export \"on_breach_signal\")\n    (global.set $depth (i32.const 0))\n    (drop (call $f.{breach.Index} (call $tile (call $ice_breach (i32.const 16) (i32.const 8))))))\n");

            wat.Append("  (data $strings \"");
            foreach (var b in data)
            {
                if (b is >= 0x20 and < 0x7f and not (byte) '"' and not (byte) '\\')
                    wat.Append((char) b);
                else
                    wat.Append('\\').Append(b.ToString("x2"));
            }

            wat.Append("\")\n)\n");
            return wat.ToString();
        }

        #region Names

        private void CollectFunctions()
        {
            // Functions first, so code can use them before (above) where they are defined.
            foreach (var stmt in body)
            {
                if (stmt is not DefStmt def)
                    continue;

                if (_funcs.ContainsKey(def.Name))
                    throw new WireException(def.Line, def.Column, $"{def.Name} is already defined");

                if (WireLibrary.Builtin(def.Name) != null || WireLibrary.IsModule(def.Name))
                    throw new WireException(def.Line, def.Column, $"{def.Name} is built in; give your function another name");

                (int Params, string How)? wanted = def.Name switch
                {
                    "tick" => (0, "def tick():"),
                    "on_door_request" => (1, "def on_door_request(who):"),
                    "on_breach_signal" => (1, "def on_breach_signal(at):"),
                    _ => null,
                };

                if (wanted is { } w && def.Params.Count != w.Params)
                    throw new WireException(def.Line, def.Column, $"the {def.Name} hook is written {w.How}");

                var f = new Function(def.Name, _order.Count + 1, def.Params, def.Body);
                _funcs[def.Name] = f;
                _order.Add(f);
            }
        }

        private void CollectGlobals()
        {
            Assigned(body, _globals, true);
            foreach (var g in _globals)
            {
                if (_funcs.ContainsKey(g))
                {
                    var (line, col) = FirstAssignment(body, g) ?? (1, 1);
                    throw new WireException(line, col, $"{g} is a function; a variable can't share its name");
                }

                if (WireLibrary.IsModule(g) || WireLibrary.Builtin(g) != null)
                {
                    var (line, col) = FirstAssignment(body, g) ?? (1, 1);
                    throw new WireException(line, col, $"{g} is built in; give your variable another name");
                }
            }
        }

        /// <summary>
        /// Names assigned in statements (not inside defs), in order.
        /// </summary>
        private static void Assigned(List<Stmt> stmts, List<string> output, bool top)
        {
            foreach (var stmt in stmts)
            {
                switch (stmt)
                {
                    case AssignStmt { Target: NameExpr n }:
                        Add(n.Name);
                        break;
                    case AugAssignStmt { Target: NameExpr n }:
                        Add(n.Name);
                        break;
                    case ForStmt f:
                        Add(f.Variable);
                        Assigned(f.Body, output, top);
                        break;
                    case WhileStmt w:
                        Assigned(w.Body, output, top);
                        break;
                    case IfStmt i:
                        foreach (var (_, inner) in i.Arms)
                        {
                            Assigned(inner, output, top);
                        }

                        Assigned(i.Else, output, top);
                        break;
                    case DefStmt when !top:
                        throw new WireException(stmt.Line, stmt.Column,
                            "functions can only be defined at the top level, not inside other blocks");
                }
            }

            void Add(string n)
            {
                if (!output.Contains(n))
                    output.Add(n);
            }
        }

        private static (int, int)? FirstAssignment(List<Stmt> stmts, string name)
        {
            foreach (var stmt in stmts)
            {
                switch (stmt)
                {
                    case AssignStmt { Target: NameExpr n } when n.Name == name:
                    case AugAssignStmt { Target: NameExpr m } when m.Name == name:
                    case ForStmt f when f.Variable == name:
                        return (stmt.Line, stmt.Column);
                    case ForStmt f:
                        if (FirstAssignment(f.Body, name) is { } inFor)
                            return inFor;

                        break;
                    case WhileStmt w:
                        if (FirstAssignment(w.Body, name) is { } inWhile)
                            return inWhile;

                        break;
                    case IfStmt i:
                        foreach (var inner in i.Arms.Select(a => a.Body).Append(i.Else))
                        {
                            if (FirstAssignment(inner, name) is { } inIf)
                                return inIf;
                        }

                        break;
                }
            }

            return null;
        }

        private int? Global(string n)
        {
            var i = _globals.IndexOf(n);
            return i < 0 ? null : i;
        }

        /// <summary>
        /// The global holding a text constant.
        /// </summary>
        private string Str(string s)
        {
            if (!_stringIndex.TryGetValue(s, out var i))
            {
                i = _strings.Count;
                _strings.Add(s);
                _stringIndex[s] = i;
            }

            return $"$s.{i}";
        }

        private string Int(long v)
        {
            if (!_intIndex.TryGetValue(v, out var i))
            {
                i = _ints.Count;
                _ints.Add(v);
                _intIndex[v] = i;
            }

            return $"(global.get $k.{i})";
        }

        /// <summary>
        /// If <paramref name="e"/> names a module (and no variable hides it), which.
        /// </summary>
        private string? ModuleName(FnCx f, Expr e)
        {
            return e is NameExpr n && WireLibrary.IsModule(n.Name) && f.Local(n.Name) == null && Global(n.Name) == null
                ? n.Name
                : null;
        }

        /// <summary>
        /// The manual page for a module.
        /// </summary>
        private static string ModulePage(string module)
        {
            return module switch
            {
                "door" or "camera" or "ice" or "deck" or "ui" => module,
                "body" => "implant",
                _ => "modules",
            };
        }

        /// <summary>
        /// " (did you mean x?)" for a likely misspelling.
        /// </summary>
        private string Suggest(string name)
        {
            var known = new HashSet<string>(_globals);
            known.UnionWith(_funcs.Keys);
            known.UnionWith(WireLibrary.Builtins.Select(b => b.Name));
            known.UnionWith(WireLibrary.Modules);

            var limit = Math.Min(2, name.Length / 2 + 1);
            var close = known
                .Select(k => (Name: k, Distance: Distance(k, name)))
                .Where(k => k.Distance <= limit)
                .OrderBy(k => k.Distance)
                .ThenBy(k => k.Name, StringComparer.Ordinal)
                .Select(k => k.Name)
                .FirstOrDefault();

            if (close != null)
                return $" (did you mean {close}?)";

            return name switch
            {
                "true" => " (did you mean True?)",
                "false" => " (did you mean False?)",
                "null" or "nil" or "none" => " (did you mean None?)",
                _ => "",
            };
        }

        /// <summary>
        /// Edit distance, for suggestions.
        /// </summary>
        private static int Distance(string a, string b)
        {
            var prev = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var row = new int[b.Length + 1];
                row[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    row[j] = Math.Min(Math.Min(prev[j] + 1, row[j - 1] + 1), prev[j - 1] + cost);
                }

                prev = row;
            }

            return prev[b.Length];
        }

        #endregion

        #region Statements

        private string CompileFunction(Function fn)
        {
            var locals = new List<string>(fn.Params);
            var names = new List<string>();
            Assigned(fn.Body, names, false);
            foreach (var n in names)
            {
                if (!_globals.Contains(n) && !locals.Contains(n))
                    locals.Add(n);
            }

            foreach (var p in fn.Params)
            {
                if (_globals.Contains(p))
                {
                    throw new WireException(fn.Body.Count > 0 ? fn.Body[0].Line : 1, 1,
                        $"parameter {p} has the same name as a global variable; rename it");
                }
            }

            var f = new FnCx(fn.Name, fn.Index, locals, fn.Params.Count, true);
            var code = new StringBuilder();
            foreach (var stmt in fn.Body)
            {
                CompileStmt(f, stmt, code);
            }

            var head = new StringBuilder($"  (func $f.{fn.Index}");
            for (var i = 0; i < f.Params; i++)
            {
                head.Append($" (param $v.{i} eqref)");
            }

            head.Append(" (result eqref)\n");
            return $"{head}{Locals(f)}{code}    (ref.null none))\n";
        }

        private static string Locals(FnCx f)
        {
            var s = new StringBuilder();
            for (var i = f.Params; i < f.Locals.Count; i++)
            {
                s.Append($"    (local $v.{i} eqref)\n");
            }

            for (var i = 0; i < f.Temps; i++)
            {
                s.Append($"    (local $t.{i} eqref)\n");
            }

            for (var i = 0; i < f.Iters; i++)
            {
                s.Append($"    (local $it.{i} (ref null $List)) (local $ix.{i} i32)\n");
            }

            return s.ToString();
        }

        private string Store(FnCx f, string name, string value)
        {
            if (f.Local(name) is { } i)
                return $"(local.set $v.{i} {value})";

            var g = Global(name) ?? throw new InvalidOperationException($"{name} is assigned, so it's a local or a global");
            return $"(global.set $g.{g} {value})";
        }

        private void Block(FnCx f, List<Stmt> stmts, StringBuilder code)
        {
            foreach (var stmt in stmts)
            {
                CompileStmt(f, stmt, code);
            }
        }

        private void SetPos(FnCx f, int line, StringBuilder code)
        {
            f.Pos = (f.Index << WireRuntime.FunctionShift) | Math.Min(line, (1 << WireRuntime.FunctionShift) - 1);
            code.Append($"    (global.set $pos (i32.const {f.Pos}))\n");
        }

        private void CompileStmt(FnCx f, Stmt stmt, StringBuilder code)
        {
            switch (stmt)
            {
                case ExprStmt e:
                    SetPos(f, stmt.Line, code);
                    code.Append($"    (drop {Expr(f, e.Value)})\n");
                    break;

                case AssignStmt { Target: NameExpr n } a:
                    SetPos(f, stmt.Line, code);
                    code.Append($"    {Store(f, n.Name, Expr(f, a.Value))}\n");
                    break;

                case AssignStmt { Target: IndexExpr i } a:
                    SetPos(f, stmt.Line, code);
                    code.Append($"    (call $set_index {Expr(f, i.Target)} {Expr(f, i.Index)} {Expr(f, a.Value)})\n");
                    break;

                case AugAssignStmt { Target: NameExpr n } a:
                    SetPos(f, stmt.Line, code);
                    code.Append($"    {Store(f, n.Name, $"(call {OpFunc(a.Op)} {Expr(f, n)} {Expr(f, a.Value)})")}\n");
                    break;

                case AugAssignStmt { Target: IndexExpr i } a:
                {
                    SetPos(f, stmt.Line, code);
                    var c = f.Temp();
                    var k = f.Temp();
                    code.Append($"    (local.set {c} {Expr(f, i.Target)})\n");
                    code.Append($"    (local.set {k} {Expr(f, i.Index)})\n");
                    code.Append($"    (call $set_index (local.get {c}) (local.get {k}) (call {OpFunc(a.Op)} (call $get_index (local.get {c}) (local.get {k})) {Expr(f, a.Value)}))\n");
                    break;
                }

                case AssignStmt or AugAssignStmt:
                    throw new WireException(stmt.Line, stmt.Column, "you can only assign to a name or an item");

                case IfStmt i:
                {
                    var close = 0;
                    foreach (var (condition, arm) in i.Arms)
                    {
                        SetPos(f, condition.Line, code);
                        code.Append($"    (if (call $truthy {Expr(f, condition)})\n    (then\n");
                        Block(f, arm, code);
                        code.Append("    )\n    (else\n");
                        close++;
                    }

                    Block(f, i.Else, code);
                    for (var j = 0; j < close; j++)
                    {
                        code.Append("    ))\n");
                    }

                    break;
                }

                case WhileStmt w:
                {
                    var n = f.Labels++;
                    code.Append($"    (block $b{n} (loop $l{n}\n");
                    SetPos(f, w.Condition.Line, code);
                    code.Append($"    (br_if $b{n} (i32.eqz (call $truthy {Expr(f, w.Condition)})))\n");
                    code.Append($"    (block $c{n}\n");
                    f.Loops.Add(new Loop($"$b{n}", $"$c{n}"));
                    Block(f, w.Body, code);
                    f.Loops.RemoveAt(f.Loops.Count - 1);
                    code.Append($"    )\n    (br $l{n})))\n");
                    break;
                }

                case ForStmt loop:
                {
                    var n = f.Labels++;
                    var it = f.Iters++;
                    SetPos(f, stmt.Line, code);
                    code.Append($"    (local.set $it.{it} (call $iter {Expr(f, loop.Over)}))\n");
                    code.Append($"    (local.set $ix.{it} (i32.const 0))\n");
                    code.Append($"    (block $b{n} (loop $l{n}\n");
                    code.Append($"    (br_if $b{n} (i32.ge_u (local.get $ix.{it}) (struct.get $List $ln (local.get $it.{it}))))\n");
                    code.Append($"    {Store(f, loop.Variable, $"(array.get $Arr (struct.get $List $li (local.get $it.{it})) (local.get $ix.{it}))")}\n");
                    code.Append($"    (local.set $ix.{it} (i32.add (local.get $ix.{it}) (i32.const 1)))\n");
                    code.Append($"    (block $c{n}\n");
                    f.Loops.Add(new Loop($"$b{n}", $"$c{n}"));
                    Block(f, loop.Body, code);
                    f.Loops.RemoveAt(f.Loops.Count - 1);
                    code.Append($"    )\n    (br $l{n})))\n");
                    break;
                }

                case BreakStmt:
                    if (f.Loops.Count == 0)
                        throw new WireException(stmt.Line, stmt.Column, "break is only for inside a loop");

                    code.Append($"    (br {f.Loops[^1].Break})\n");
                    break;

                case ContinueStmt:
                    if (f.Loops.Count == 0)
                        throw new WireException(stmt.Line, stmt.Column, "continue is only for inside a loop");

                    code.Append($"    (br {f.Loops[^1].Continue})\n");
                    break;

                case ReturnStmt r:
                    if (!f.InDef)
                    {
                        throw new WireException(stmt.Line, stmt.Column,
                            "return is only for inside a def (use sys.exit() to end the program)");
                    }

                    SetPos(f, stmt.Line, code);
                    code.Append($"    (return {(r.Value == null ? "(ref.null none)" : Expr(f, r.Value))})\n");
                    break;

                case PassStmt:
                    break;

                case DefStmt:
                    throw new WireException(stmt.Line, stmt.Column,
                        "functions can only be defined at the top level, not inside other blocks");
            }
        }

        private static string OpFunc(BinOp op)
        {
            return op switch
            {
                BinOp.Add => "$add",
                BinOp.Sub => "$sub",
                BinOp.Mul => "$mul",
                BinOp.Div => "$div",
                _ => "$mod",
            };
        }

        #endregion

        #region Expressions

        private string Expr(FnCx f, Expr e)
        {
            switch (e)
            {
                case IntExpr i:
                    return Int(i.Value);
                case StrExpr s:
                    return $"(global.get {Str(s.Value)})";
                case BoolExpr b:
                    return b.Value ? "(global.get $true)" : "(global.get $false)";
                case NoneExpr:
                    return "(ref.null none)";
                case NameExpr n:
                    if (f.Local(n.Name) is { } local)
                        return $"(local.get $v.{local})";

                    if (Global(n.Name) is { } global)
                        return $"(global.get $g.{global})";

                    if (_funcs.ContainsKey(n.Name) || WireLibrary.Builtin(n.Name) != null)
                        throw new WireException(e.Line, e.Column, $"{n.Name} is a function: call it, like {n.Name}()");

                    if (WireLibrary.IsModule(n.Name))
                    {
                        throw new WireException(e.Line, e.Column,
                            $"{n.Name} is a module: use its functions, like {n.Name}.something() (see man {ModulePage(n.Name)})");
                    }

                    throw new WireException(e.Line, e.Column, $"{n.Name} isn't defined{Suggest(n.Name)}");
                case ListExpr l:
                    return $"(call $list_of (array.new_fixed $Arr {l.Items.Count}{Args(f, l.Items)}))";
                case DictExpr d:
                {
                    var t = f.Temp();
                    var s = new StringBuilder($"(block (result eqref) (local.set {t} (call $dict_new))");
                    foreach (var (key, value) in d.Pairs)
                    {
                        s.Append($" (call $dict_set (local.get {t}) {Expr(f, key)} {Expr(f, value)})");
                    }

                    return s.Append($" (local.get {t}))").ToString();
                }
                case NegExpr n:
                    return $"(call $neg {Expr(f, n.Inner)})";
                case NotExpr n:
                    return $"(call $not {Expr(f, n.Inner)})";
                case BinaryExpr b:
                    return $"(call {OpFunc(b.Op)} {Expr(f, b.Left)} {Expr(f, b.Right)})";
                case CompareExpr c:
                {
                    var func = c.Op switch
                    {
                        CmpOp.Eq => "$eq",
                        CmpOp.Ne => "$ne",
                        CmpOp.Lt => "$lt",
                        CmpOp.Le => "$le",
                        CmpOp.Gt => "$gt",
                        CmpOp.Ge => "$ge",
                        CmpOp.In => "$in",
                        _ => "$not_in",
                    };
                    return $"(call {func} {Expr(f, c.Left)} {Expr(f, c.Right)})";
                }
                case AndExpr a:
                {
                    var t = f.Temp();
                    return $"(if (result eqref) (call $truthy (local.tee {t} {Expr(f, a.Left)})) (then {Expr(f, a.Right)}) (else (local.get {t})))";
                }
                case OrExpr o:
                {
                    var t = f.Temp();
                    return $"(if (result eqref) (call $truthy (local.tee {t} {Expr(f, o.Left)})) (then (local.get {t})) (else {Expr(f, o.Right)}))";
                }
                case IndexExpr i:
                    return $"(call $get_index {Expr(f, i.Target)} {Expr(f, i.Index)})";
                case AttrExpr a:
                {
                    if (ModuleName(f, a.Target) is { } module)
                    {
                        var full = $"{module}.{a.Name}";
                        if (WireLibrary.Constant(full) is { } v)
                            return Int(v);

                        if (WireLibrary.ModuleFunction(full) != null)
                            throw new WireException(e.Line, e.Column, $"{full} is a function: call it, like {full}()");

                        throw new WireException(e.Line, e.Column, $"{module} has no {a.Name} (see man {ModulePage(module)})");
                    }

                    return $"(call $attr {Expr(f, a.Target)} (global.get {Str(a.Name)}))";
                }
                case CallExpr c:
                    return Call(f, c);
                default:
                    throw new InvalidOperationException($"Unknown expression {e}");
            }
        }

        private string Args(FnCx f, IEnumerable<Expr> args)
        {
            var s = new StringBuilder();
            foreach (var a in args)
            {
                s.Append(' ').Append(Expr(f, a));
            }

            return s.ToString();
        }

        /// <summary>
        /// The arguments, then None for each of the rest up to <paramref name="count"/>.
        /// </summary>
        private string Padded(FnCx f, List<Expr> args, int count)
        {
            var s = new StringBuilder(Args(f, args));
            for (var i = args.Count; i < count; i++)
            {
                s.Append(" (ref.null none)");
            }

            return s.ToString();
        }

        private static void Arity(CallExpr e, string name, WireFunction fn)
        {
            if (e.Args.Count < fn.MinArgs || e.Args.Count > fn.MaxArgs)
                throw new WireException(e.Line, e.Column, $"{name} is called {fn.Usage}");
        }

        private string Call(FnCx f, CallExpr e)
        {
            var argc = e.Args.Count;
            switch (e.Callee)
            {
                case NameExpr n when f.Local(n.Name) == null && Global(n.Name) == null:
                {
                    if (_funcs.TryGetValue(n.Name, out var fn))
                    {
                        if (argc != fn.Params.Count)
                        {
                            var s = fn.Params.Count == 1 ? "" : "s";
                            throw new WireException(e.Line, e.Column,
                                $"{n.Name} takes {fn.Params.Count} argument{s}, not {argc}");
                        }

                        return $"(block (result eqref) (call $enter) (call $f.{fn.Index}{Args(f, e.Args)}) (call $leave (i32.const {f.Pos})))";
                    }

                    if (WireLibrary.Builtin(n.Name) is { } builtin)
                    {
                        Arity(e, n.Name, builtin);
                        return n.Name switch
                        {
                            "print" or "min" or "max" => $"(call $b.{n.Name} (array.new_fixed $Arr {argc}{Args(f, e.Args)}))",
                            "range" => $"(call $b.range{Padded(f, e.Args, 3)} (i32.const {argc}))",
                            _ => $"(call $b.{n.Name}{Args(f, e.Args)})",
                        };
                    }

                    throw new WireException(e.Line, e.Column, $"{n.Name} isn't a function Wire knows{Suggest(n.Name)}");
                }

                case AttrExpr a:
                {
                    if (ModuleName(f, a.Target) is { } module)
                    {
                        var full = $"{module}.{a.Name}";
                        if (WireLibrary.ModuleFunction(full) is not { } fn)
                        {
                            throw new WireException(e.Line, e.Column,
                                $"{module} has no function {a.Name} (see man {ModulePage(module)})");
                        }

                        Arity(e, full, fn);
                        return $"(call ${full}{Padded(f, e.Args, fn.MaxArgs)})";
                    }

                    var target = Expr(f, a.Target);
                    if (!WireLibrary.MethodNames.Contains(a.Name))
                    {
                        // No value has it: work out what it was called on, and say so.
                        var t = f.Temp();
                        var s = new StringBuilder($"(block (result eqref) (local.set {t} {target})");
                        foreach (var arg in e.Args)
                        {
                            s.Append($" (drop {Expr(f, arg)})");
                        }

                        return s.Append($" (call $no_method (local.get {t}) (global.get {Str(a.Name)})))").ToString();
                    }

                    if (argc > 2)
                        throw new WireException(e.Line, e.Column, $"{a.Name} doesn't take {argc} arguments (see man wire)");

                    return $"(call $m.{a.Name} {target}{Padded(f, e.Args, 2)} (i32.const {argc}))";
                }

                default:
                    throw new WireException(e.Line, e.Column, "only functions can be called");
            }
        }

        #endregion
    }
}
