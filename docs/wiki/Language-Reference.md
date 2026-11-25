# Language Reference

## 1. Data Types

| Type | Literals / Examples | Notes |
| --- | --- | --- |
| `int` | `42`, `0xFF`, `0b1010` | 64-bit signed integer |
| `float` | `3.14`, `1e-5` | 64-bit IEEE 754 floating-point |
| `bool` | `true`, `false` | First-class boolean |
| `string` | `"hello"`, `$"hi {name}"` | Immutable string; 0-indexed (`s[0]`, `s[1..4]`) |
| `null` | `null` | Null value |
| `obj` | `#0`, `#123`, `#-1` | PennMUSH `dbref` reference |
| `list` | `[1, "a", #2]`, `[a, ...rest]` | 0-indexed heterogeneous list (CoW value type) |
| `map` | `{ "hp": 100, "mp": 50 }`, `{:}` | Ordered key-value map (`{:}` = empty, CoW value type) |
| `error` | `E_PERM`, `E_TYPE`, `E_RANGE` | First-class throwable error value |
| `func` | `x => x * 2`, `(a, b) => a + b` | Serializable closure |

### Copy-on-Write (CoW) Collections
`list` and `map` are **copy-on-write value types**:
- Passing a `list` or `map` to another object's method never allows the callee to mutate the caller's copy in place.
- Mutating lvalue statements (`items[0] = 99;`, `items.Add(4);`, `matrix[0][1] = 5;`, `this.Stats["hp"] = 120;`) automatically rebind the target variable, nested index, or property using CoW.

---

## 2. Variables, Scoping & Destructuring

```csharp
var hp = 100;
const MAX_HP = 100;
int level = 5; // Optional runtime type hint

var [first, second, ...rest] = [10, 20, 30, 40];
var { hp, mp } = { "hp": 100, "mp": 50 };
```

### Special Context Variables (Read-Only)
- `this` — Host object (`obj`) running the method
- `caller` — Invoking object (`obj`)
- `player` — Triggering enactor (`obj`)
- `args` — Argument `list` passed to the method

---

## 3. Operators (Lowest → Highest Precedence)

1. Assignment: `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `??=`
2. Lambda: `=>`
3. Ternary & null-coalescing: `cond ? a : b`, `a ?? b`
4. Logical: `||`, `&&`
5. Bitwise: `|`, `^`, `&`, `<<`, `>>`
6. Equality, relational & type/membership: `==`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `is` (`x is int`)
7. Additive: `+`, `-`
8. Multiplicative: `*`, `/`, `%`, `**` (power)
9. Unary & postfix: `!`, `~`, `-`, `++`, `--`
10. Member / call / index / slice:
    - `obj.Prop`, `obj?.Prop`, `obj.(expr)` (dynamic property read/write)
    - `obj.Method(args...)`, `obj?.Method(args...)`, `base.Method(args...)`
    - `list[0]`, `list[^1]` (from end), `list[1..4]`, `list[..3]`, `list[1..]`

---

## 4. Control Flow & Error Handling

```csharp
if (hp <= 0) {
    Mush.Notify(player, "Defeated!");
} else if (hp < 20) {
    Mush.Notify(player, "Low HP!");
}

foreach (var item in inventory) { ... }
foreach (var (idx, item) in inventory) { ... }
foreach (var (key, val) in stats) { ... }
for (var i = 0; i < 10; i++) { ... }
while (running) { ... }

switch (cmd) {
    case "n":
    case "north":
        Move(#10);
        break;
    default:
        break;
}
```

### `try` / `catch` / `finally` & Inline `try`
```csharp
try {
    throw E_PERM;
} catch (E_PERM e) {
    Log($"Denied: {e}");
} catch (var e) {
    Log($"Error: {e}");
} finally {
    Cleanup();
}

var safe = try (10 / 0) ?? 0;
```

---

## 5. Quotas & Asynchronous Tasks

- **Tick & Depth Limits**: Every VM instruction consumes ticks (`50,000` default) and call depth is capped (`64` default), aborting infinite loops with `E_QUOTA` / `E_MAXREC`.
- **`Sleep(seconds)`**: Suspends the VM task and persists its serializable frame stack to SQLite (`suspended_tasks`), resuming cleanly even across plugin hot-reloads or `@reboot`.
- **`after (seconds) { ... }`**: Schedules a detached background block backed by `ScriptStore`.
