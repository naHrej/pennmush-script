# PennMUSH.Script Wiki

**PennMUSH.Script** is a sandboxed, C#-idiomatic scripting DSL extension (`IExtension`) for [pennmush-dotnet](https://github.com/naHrej/pennmush-dotnet). It pairs familiar C# syntax (`{ }` blocks, 0-based indexing, lambdas, `$"..."` interpolation, `try`/`catch`/`finally`) with MUSH/MOO runtime safety (tick quotas, `#dbref` objects, copy-on-write collections, and seamless softcode interop).

## Architecture at a Glance
- **No Roslyn dependency**: Hand-written lexer, recursive-descent/Pratt parser, and tick-metered bytecode VM.
- **PennMUSH-native properties**: Typed properties live directly on PennMUSH attributes using custom `AttributeFlags` (`script_int`, `script_float`, `script_bool`, `script_str`, `script_list`, `script_map`, `script_obj`).
- **Dedicated SQLite store**: Script source code, compiled bytecode cache, and serializable `suspended_tasks` (`Sleep()` / `after()`) live in `<DataDirectory>/script.db` so `ExtensionLoadContext` hot-reloads cleanly and `@reboot` preserves scheduled tasks.
- **PennMUSH command integration**: Instead of replacing PennMUSH's command parser, scripts bind cleanly to standard PennMUSH `$command` regex attributes via `script(<obj>/<method>, ...)`.

## Pages
- **[[Writing Code and Commands|Writing-Code-and-Commands]]** — Step-by-step guide to creating typed properties, authoring `@prog` methods, and wiring them into PennMUSH `$commands`.
- **[[Language Reference|Language-Reference]]** — Types, CoW semantics, operators, control flow, error handling, and concurrency.
- **[[API Reference|API-Reference]]** — Complete reference for `@prop`/`@prog`/`@script`, global functions, collection/string methods, `Math`/`Json`/`Regex`/`Crypto`/`Obj`/`Mush` namespaces, and error codes.
