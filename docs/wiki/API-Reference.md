# API Reference

## 1. In-Game Commands

| Command | Description |
| --- | --- |
| `@prop <obj>/<attr>=<type>[:<val>]` | Set a `script_*` type flag (`int`, `float`, `bool`, `str`, `list`, `map`, `obj`, `none`) and optional value |
| `@prog <obj>/<method>[(p1, p2)][=<access>]` | Alias `@program`; enter multi-line mode (`.` to compile & save, `@abort` to cancel) |
| `@script <obj>/<method>=<access>` | Set access modifier (`private`, `protected`, `internal`, `public`) |
| `@script/list <obj>` | List script methods and access modifiers on `<obj>` |
| `@script/view <obj>/<method>` | Display stored source code for a method |
| `@script/del <obj>/<method>` | Delete a method |
| `@script/ps` (or `@script/tasks`) | List pending `after()` / `Sleep()` tasks |
| `@script/kill <taskId>` | Cancel a pending task (Wizard-only) |
| `@script/func <fn>=<obj>/<method>` | Bind a custom PennMUSH softcode function `<fn>()` to a script method (Wizard-only, persisted) |
| `@script/unfunc <fn>` | Unregister a script-backed softcode function (Wizard-only) |
| `@script/funcs` | List all script-backed softcode functions |
| `@script/eval <code>` | Evaluate an expression or statement block immediately (Wizard-only) |

## 2. Softcode Function

- `script(<obj>/<method>[, <arg0>, <arg1>, ...])` — Invokes a script method on `<obj>` as the calling executor and returns its display string (or `#-1 <ERROR>` on failure).

---

## 3. Global Built-in Functions

| Function | Returns | Description |
| --- | --- | --- |
| `TypeOf(val)` | `string` | Type name (`"int"`, `"float"`, `"bool"`, `"string"`, `"null"`, `"obj"`, `"list"`, `"map"`, `"error"`, `"func"`) |
| `ToInt(val)` | `int` | Converts `int`, `float`, `bool`, `obj`, or numeric `string` (raises `E_TYPE` if invalid) |
| `ToFloat(val)` | `float` | Converts `float`, `int`, or numeric `string` |
| `ToStr(val)` | `string` | Display string representation |
| `ToObj(val)` | `obj` | Converts `obj`, `int`, or `"#123"`/`"123"` string |
| `IsValid(obj)` | `bool` | `true` if `obj` exists and is not `Garbage` |
| `TicksLeft()` | `int` | Remaining VM ticks for the current execution |
| `Callers()` | `list` | Call stack as list of `{ "obj": ..., "method": ..., "caller": ... }` maps |
| `CallerPerms()` | `obj` | Invoking `caller` object |
| `TaskPerms()` | `obj` | Host object (`this`) whose permissions the task runs with |
| `Sleep(seconds)` | `null` | Suspends execution for `seconds` (persisted in SQLite) |
| `Log(...args)` | `null` | Writes to the server log via `IExtensionHost.Log` |
| `Min(a, b, ...)` / `Max(a, b, ...)` | number | Minimum / maximum of arguments |
| `Clamp(v, lo, hi)` | number | Clamps `v` between `lo` and `hi` |
| `Floor(x)` / `Ceil(x)` / `Round(x)` | `int` | Rounding helpers |
| `Sqrt(x)` / `Pow(x, y)` | `float` | Square root / exponentiation |
| `Rand(max)` / `Rand(min, max)` | `int` | Random integer in `[0, max)` or `[min, max)` |

---

## 4. Instance Properties & Methods

### `obj`
- **Properties**: `.Valid`, `.Name`, `.Owner`, `.Location`, `.Parent`, `.Contents`, `.Children` (plus any PennMUSH attribute on the object)
- **Methods**: `.Notify(msg)`

### `list` (Copy-on-Write)
- **Property**: `.Length`
- **Mutators** *(auto-rebind lvalues)*: `.Add(v)`, `.Push(v)`, `.Insert(idx, v)`, `.Remove(v)`, `.RemoveAt(idx)`, `.Clear()`, `.Reverse()`, `.Sort([cmpFn])`
- **Queries / Transforms**: `.Contains(v)`, `.IndexOf(v)`, `.Slice(start, [count])`, `.Join([sep])`, `.First()`, `.Last()`, `.Map(fn)`, `.Filter(fn)`, `.Any([fn])`, `.All(fn)`, `.Find(fn)`, `.Reduce(init, fn)`

### `map` (Copy-on-Write)
- **Property**: `.Length`
- **Mutators** *(auto-rebind lvalues)*: `.Set(k, v)`, `.Remove(k)`, `.Clear()`
- **Queries / Transforms**: `.Keys()`, `.Values()`, `.HasKey(k)`, `.Get(k, [default])`, `.Merge(otherMap)`

### `string`
- **Property**: `.Length`
- **Methods**: `.Split([sep])`, `.Trim()`, `.Replace(old, new)`, `.StartsWith(s)`, `.EndsWith(s)`, `.Contains(s)`, `.IndexOf(s)`, `.LastIndexOf(s)`, `.Substring(start, [len])`, `.ToUpper()`, `.ToLower()`, `.PadLeft(width, [ch])`, `.PadRight(width, [ch])`

---

## 5. Static Namespaces

| Namespace | Members |
| --- | --- |
| `Math` | `Abs`, `Min`, `Max`, `Clamp`, `Floor`, `Ceil`, `Round`, `Sqrt`, `Pow`, `Sin`, `Cos`, `Tan`, `Log`, `Exp` |
| `Json` | `Parse(jsonStr)`, `Stringify(val)` |
| `Base64` | `Encode(str)`, `Decode(b64Str)` |
| `Regex` | `Match(input, pattern)` *(returns match groups `list` or `null`)*, `Replace(input, pattern, replacement)` |
| `Time` | `Now()` *(Unix seconds)*, `Format(unixSec, [fmt])` |
| `Crypto` | `Sha256(str)`, `HmacSha256(key, msg)` |
| `Obj` | `Valid(o)`, `Parent(o)`, `Children(o)`, `Contents(o)`, `Location(o)`, `Owner(o)`, `Name(o)`, `Players()`, `Connected()`, `Match(name)` |
| `Mush` | `Eval(expr)`, `Call(obj, attr, ...args)`, `Get(obj, attr)`, `Set(obj, attr, val)`, `Command(cmd)`, `Notify(player, msg)` |

---

## 6. Error Codes

`E_NONE`, `E_TYPE`, `E_DIV`, `E_PERM`, `E_PROPNF`, `E_VERBNF`, `E_VARNF`, `E_INVIND`, `E_MAXREC`, `E_RANGE`, `E_ARGS`, `E_INVARG`, `E_QUOTA`, `E_FLOAT`, `E_INTRPT`, `E_RUNTIME`.
