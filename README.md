# PennMUSH.Script

A sandboxed, C#-idiomatic MUSH scripting DSL extension (`IExtension`) for `pennmush-dotnet`.

## Features
- **C#-style syntax**: `{ }` blocks, 0-based indexing, `true`/`false`/`null`, `$"..."` string interpolation, `^1` / `1..4` range slicing, destructuring (`var [a, ...rest] = ...`, `var { hp } = ...`), `foreach`, `for`, `while`, `switch`, `try`/`catch`/`finally`, inline `try (expr) ?? fallback`, lambdas (`x => x * 2`), and `after (sec) { ... }` / `Sleep(sec)`.
- **Copy-on-Write (CoW) collections**: `list` (`[]`) and `map` (`{:}` / `{ "k": v }`) enforce CoW value semantics with lvalue rebinding (`items[0] = v`, `items.Add(x)`, `map.Remove(k)`).
- **Dedicated SQLite `ScriptStore`**: Script source, bytecode cache, and serializable `suspended_tasks` live in `<DataDirectory>/script.db` (hot-reload and `@reboot` safe).
- **Typed PennMUSH Attributes**: `@prop <obj>/<attr>=<type>[:<value>]` tags attributes with `script_int`, `script_float`, `script_bool`, `script_str`, `script_list`, `script_map`, or `script_obj`.
- **PennMUSH Interop**:
  - Softcode → Script: `script(<obj>/<method>[, <arg0>, ...])`
  - Script → Softcode: `Mush.Eval(expr)`, `Mush.Call(obj, attr, ...)`, `Mush.Get(obj, attr)`, `Mush.Set(obj, attr, val)`, `Mush.Command(cmd)`, `Mush.Notify(player, msg)`

## In-Game Commands
- `@prop <object>/<attr>=<type>[:<value>]` (`int`, `float`, `bool`, `str`, `list`, `map`, `obj`, `none`)
- `@program <object>/<method>[(p1, p2)][=<access>]` (alias `@prog`) — enters multi-line mode; end with `.` to compile & save or `@abort` to cancel.
- `@script <object>/<method>=<access>` (`private`, `protected`, `internal`, `public`)
- `@script/list <object>`, `@script/view <object>/<method>`, `@script/del <object>/<method>`
- `@script/ps` (or `@script/tasks`), `@script/kill <taskId>` — inspect or cancel pending `after()` / `Sleep()` tasks

## Build & Test
```powershell
dotnet test PennMUSH.Script.slnx
```
Copy `PennMUSH.Script\bin\Debug\net10.0\PennMUSH.Script.dll` into your `PennMUSH.Server` `plugins/` directory (or load live with `@plugin/load`).
