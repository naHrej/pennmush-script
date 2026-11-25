# Writing Code & Integrating with PennMUSH Commands

`PennMUSH.Script` is designed to work **with** PennMUSH's existing `$command` matcher rather than replacing it. The standard workflow is:

1. Declare typed properties on an object with `@prop`.
2. Write script methods on the object with `@prog` (or `@program`).
3. Wire a PennMUSH `$command` attribute to call `script(<obj>/<method>, ...)`.

---

## 1. Declaring Typed Properties (`@prop`)

Script properties are stored directly in PennMUSH attributes, tagged with a `script_*` custom attribute flag:

```text
@prop me/HP=int:100
@prop me/MaxHP=int:100
@prop me/Inventory=list:["potion", "bandage"]
@prop me/Stats=map:{"str": 12, "dex": 14}
```

- Supported types: `int`, `float`, `bool`, `str` (`string`), `list`, `map`, `obj`, or `none` (to clear the type flag).
- If you omit `:<value>` (e.g., `@prop me/HP=int`), an existing attribute keeps its value (with a warning if invalid) and a new attribute gets a default (`0`, `0.0`, `false`, `""`, `[]`, `{}`, or `#-1`).
- Reading a typed attribute whose text doesn't match its `script_*` flag raises `E_TYPE` at runtime.

---

## 2. Writing Methods (`@prog` / `@program`)

Use `@prog <object>/<method>[(param1, param2)][=<access>]` to enter multi-line programming mode. End with `.` on its own line to compile and save, or `@abort` to cancel:

```text
@prog me/Heal(targetRef, amountStr)=public
var target = Obj.Match(targetRef);
if (!target.Valid) {
    Mush.Notify(player, "Heal whom?");
    return false;
}

var amount = Math.Clamp(ToInt(amountStr), 1, 50);
var nextHp = Math.Min(target.MaxHP, target.HP + amount);
target.HP = nextHp;

Mush.Notify(player, $"You heal {target.Name} for {amount} HP (now {target.HP}/{target.MaxHP}).");
return true;
.
```

### Important: Softcode Arguments Are Strings
When `script(me/Heal, %#, %0)` is called from PennMUSH softcode, every argument arrives as a `string` (just like `%0`, `%1` in softcode):
- Use `ToInt(x)` / `ToFloat(x)` for numbers.
- Use `ToObj(x)` for dbrefs (`#1`, `%#`), or `Obj.Match(x)` to resolve `"me"`, `"here"`, `"#1"`, or object/player names.

---

## 3. Integrating into a PennMUSH `$command`

Create a standard PennMUSH `$command` attribute on your object (or parent/zone) that forwards `%#` and captures (`%0`, `%1`, ...) into `script()`:

```text
&CMD_HEAL me=$+heal * *:think [script(me/Heal, %0, %1)]
```

Or if your method returns a message for the enactor:

```text
&CMD_STATUS me=$+status:@pemit %#=[script(me/GetStatus, %#)]
```

Because PennMUSH handles the `$command` matching, `@lock/use`, `NO_COMMAND`, and switch/wildcard parsing work identically to standard MUSHcode.

---

## 4. Access Modifiers & Inheritance

Every method has an access modifier (default `private`):

| Modifier | Who can call it |
| --- | --- |
| `private` *(default)* | Only the host object itself (`this`) |
| `protected` | Callers with the same or greater permissions as the host object |
| `internal` | Callers with the same `Owner` as the host object |
| `public` | Any object or `script()` call |

- Scripts always execute with the **permissions of the host object** (`TaskPerms()`).
- Inheritance walks the PennMUSH `@parent` chain (`base.Method(...)` calls the parent implementation). `@zone` remains PennMUSH-only.

---

## 5. Debugging & Managing Scripts

| Command | Purpose |
| --- | --- |
| `@script/list <obj>` | List all script methods and access modifiers on `<obj>` |
| `@script/view <obj>/<method>` | Print the source code of a method |
| `@script <obj>/<method>=<access>` | Change a method's access modifier |
| `@script/del <obj>/<method>` | Delete a method from `<obj>` |
| `@script/ps` | List pending `after()` / `Sleep()` tasks |
| `@script/kill <taskId>` | Cancel a pending task (Wizard-only) |
| `@script/eval <code>` | Evaluate a one-off expression or block immediately (Wizard-only) |
