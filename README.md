# PennMUSH.Script

A sandboxed, C#-idiomatic MUSH scripting DSL extension (`IExtension`) for [pennmush-dotnet](https://github.com/naHrej/pennmush-dotnet).

## Quickstart

1. **Build & Deploy**:
   ```powershell
   dotnet test PennMUSH.Script.slnx
   ```
   Copy `PennMUSH.Script\bin\Debug\net10.0\PennMUSH.Script.dll` into your `PennMUSH.Server` `plugins/` directory (or load it live with `@plugin/load`).
2. **Declare a typed property (`@prop`)**:
   ```text
   @prop me/HP=int:100
   ```
3. **Write a method (`@prog`)**:
   ```text
   @prog me/Heal(targetName, amountStr)=public
   var target = Obj.Match(targetName);
   var amount = ToInt(amountStr);
   target.HP += amount;
   return $"{target.Name} healed +{amount} -> {target.HP} HP";
   .
   ```
4. **Wire it to a PennMUSH `$command`**:
   ```text
   &CMD_HEAL me=$+heal * *:@pemit %#=[script(me/Heal, %0, %1)]
   ```
   Then run `+heal me 25` in-game!

---

## Features
- **C#-style syntax**: `{ }` blocks, 0-based indexing, `true`/`false`/`null`, `$"..."` string interpolation, `^1` / `1..4` range slicing, destructuring (`var [a, ...rest] = ...`, `var { hp } = ...`), `foreach`, `for`, `while`, `switch`, `try`/`catch`/`finally`, inline `try (expr) ?? fallback`, lambdas (`x => x * 2`), and `after (sec) { ... }` / `Sleep(sec)`.
- **Copy-on-Write (CoW) collections**: `list` (`[]`) and `map` (`{:}` / `{ "k": v }`) enforce CoW value semantics with recursive lvalue rebinding (`items[0] = v`, `items.Add(x)`, `this.Stats["hp"] = 120`).
- **Dedicated SQLite `ScriptStore`**: Script source, bytecode cache, and serializable `suspended_tasks` live in `<DataDirectory>/script.db` (hot-reload and `@reboot` safe).
- **Typed PennMUSH Attributes**: `@prop <obj>/<attr>=<type>[:<value>]` tags attributes with `script_int`, `script_float`, `script_bool`, `script_str`, `script_list`, `script_map`, or `script_obj`.
- **PennMUSH Interop**:
  - Softcode → Script: `script(<obj>/<method>[, <arg0>, ...])`
  - Script → Softcode: `Mush.Eval(expr)`, `Mush.Call(obj, attr, ...)`, `Mush.Get(obj, attr)`, `Mush.Set(obj, attr, val)`, `Mush.Command(cmd)`, `Mush.Notify(player, msg)`

## Documentation
- **[Wiki Home](https://github.com/naHrej/pennmush-script/wiki)** (`docs/wiki/Home.md`)
- **[Writing Code and Commands](https://github.com/naHrej/pennmush-script/wiki/Writing-Code-and-Commands)** (`docs/wiki/Writing-Code-and-Commands.md`)
- **[Language Reference](https://github.com/naHrej/pennmush-script/wiki/Language-Reference)** (`docs/wiki/Language-Reference.md`)
- **[API Reference](https://github.com/naHrej/pennmush-script/wiki/API-Reference)** (`docs/wiki/API-Reference.md`)
