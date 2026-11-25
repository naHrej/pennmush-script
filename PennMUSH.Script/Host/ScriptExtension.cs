namespace PennMUSH.Script.Host;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PennMUSH.Core.Attributes;
using PennMUSH.Core.Commands;
using PennMUSH.Core.Database;
using PennMUSH.Core.Entities;
using PennMUSH.Core.MUSHcode;
using PennMUSH.Core.Types;
using PennMUSH.Extensions;
using PennMUSH.Script.Compiler;
using PennMUSH.Script.Runtime;
using PennMUSH.Script.Storage;
using PennMUSH.Script.Vm;

public sealed class ScriptExtension : IExtension
{
    private IExtensionHost _host = null!;
    private ScriptStore _store = null!;
    private MushScriptBridge _bridge = null!;
    private readonly ConcurrentDictionary<string, Timer> _pendingTimers = new();

    public string Name => "PennMUSH.Script";
    public string Version => "1.0.0";
    public string Description => "C#-style sandboxed MUSH scripting DSL with SQLite storage and attribute type marshaling.";
    public string Author => "PennMUSH .NET";

    public void Initialize(IExtensionHost host)
    {
        _host = host;
        _store = new ScriptStore(Path.Combine(host.DataDirectory, "script.db"));
        _bridge = new MushScriptBridge(host, _store, ScheduleTask);

        foreach (string flag in AttributeMarshal.AllTypeFlags)
            host.RegisterAttributeFlag(flag);

        host.RegisterCommand(new ScriptCommandHandler(this), "@script");
        host.RegisterCommand(new ProgramCommandHandler(this), "@program", "@prog");
        host.RegisterFunction("SCRIPT", InvokeScriptFunction);
        host.OnObjectDestroyed(dbref => _store.DeleteObjectData(dbref.Value));

        RehydrateSuspendedTasks();
        host.Log("Initialized PennMUSH.Script DSL.");
    }

    public void Shutdown()
    {
        foreach (var kv in _pendingTimers)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _pendingTimers.Clear();
        _store?.Dispose();
    }

    internal ScriptStore Store => _store;
    internal IExtensionHost Host => _host;

    private string InvokeScriptFunction(EvaluationContext ctx, string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            return "#-1 SCRIPT() REQUIRES OBJECT/METHOD";

        if (!TryParseObjMethod(ctx.Executor, args[0], out var targetObj, out var methodName))
            return "#-1 INVALID OBJECT/METHOD";

        var resolved = _bridge.ResolveMethod(targetObj.Id.Value, methodName);
        if (resolved == null)
            return "#-1 METHOD NOT FOUND";

        if (!_bridge.CanCallMethod(resolved.DefiningDbref, ctx.Executor.Value, resolved.Access))
            return "#-1 PERMISSION DENIED";

        var callArgs = args.Skip(1).Select(ScriptValue.FromString).ToList();
        var vm = new ScriptVm(_bridge);
        var result = vm.Execute(resolved.Bytecode, targetObj.Id.Value, ctx.Executor.Value, ctx.Enactor.Value, callArgs);
        return HandleVmResult(result);
    }

    private string HandleVmResult(VmExecutionResult result)
    {
        switch (result.Status)
        {
            case VmExecutionStatus.Completed:
                return result.ReturnValue.ToDisplayString();
            case VmExecutionStatus.Suspended when result.SuspendedState != null:
                ScheduleTask(result.SuspendedState);
                return "";
            case VmExecutionStatus.Faulted:
                return $"#-1 {result.ErrorCode}: {result.ErrorMessage}";
            default:
                return "";
        }
    }

    internal void ScheduleTask(SuspendedTaskState state)
    {
        _store.SaveSuspendedTask(state);
        long delayMs = Math.Max(0, state.ResumeAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            _pendingTimers.TryRemove(state.TaskId, out Timer? _);
            timer?.Dispose();
            _store.DeleteSuspendedTask(state.TaskId);
            var vm = new ScriptVm(_bridge);
            var res = vm.Resume(state);
            if (res.Status == VmExecutionStatus.Suspended && res.SuspendedState != null)
                ScheduleTask(res.SuspendedState);
            else if (res.Status == VmExecutionStatus.Faulted)
                _host.Log($"Suspended task {state.TaskId} faulted: {res.ErrorCode} {res.ErrorMessage}");
        }, null, delayMs, Timeout.Infinite);
        _pendingTimers[state.TaskId] = timer;
    }

    private void RehydrateSuspendedTasks()
    {
        foreach (var task in _store.LoadSuspendedTasks())
        {
            if (task.CompilerVersion != CompiledMethod.CurrentCompilerVersion)
            {
                _store.DeleteSuspendedTask(task.TaskId);
                continue;
            }
            ScheduleTask(task);
        }
    }

    internal bool TryParseObjMethod(Dbref executor, string spec, out MUSHObject targetObj, out string methodName)
    {
        targetObj = null!;
        methodName = string.Empty;
        int slash = spec.IndexOf('/');
        if (slash <= 0 || slash >= spec.Length - 1) return false;
        string objPart = spec[..slash].Trim();
        methodName = spec[(slash + 1)..].Trim();
        var matched = _host.Database.MatchObject(executor, objPart);
        if (matched == null) return false;
        targetObj = matched;
        return !string.IsNullOrWhiteSpace(methodName);
    }

    internal static bool Controls(MUSHObject actor, MUSHObject target)
        => actor.IsWizard || actor.Id == target.Id || target.Owner == actor.Id;

    private sealed class ScriptCommandHandler : ICommandHandler
    {
        private readonly ScriptExtension _ext;
        public ScriptCommandHandler(ScriptExtension ext) => _ext = ext;
        public string Name => "@script";

        public bool Execute(CommandContext context, ObjectDatabase db)
        {
            var actor = db.GetObject(context.Executor);
            if (actor == null) return false;

            if (context.Switches.Contains("LIST", StringComparer.OrdinalIgnoreCase))
            {
                var target = db.MatchObject(context.Executor, context.LeftArg);
                if (target == null) { context.Notify("I can't see that here."); return false; }
                var methods = _ext.Store.ListMethods(target.Id.Value);
                if (methods.Count == 0) { context.Notify($"No scripts on {target.Name}({target.Id})."); return true; }
                foreach (var (mName, access) in methods)
                    context.Notify($"  {access.ToString().ToLowerInvariant()} {mName}");
                return true;
            }

            if (!_ext.TryParseObjMethod(context.Executor, context.LeftArg, out var targetObj, out var methodName))
            {
                context.Notify("Usage: @script <object>/<method>[=<access>] | @script/list <object> | @script/del <object>/<method>");
                return false;
            }

            if (!Controls(actor, targetObj))
            {
                context.Notify("Permission denied.");
                return false;
            }

            if (context.Switches.Contains("DEL", StringComparer.OrdinalIgnoreCase))
            {
                bool deleted = _ext.Store.DeleteMethod(targetObj.Id.Value, methodName);
                context.Notify(deleted ? $"Deleted {targetObj.Name}/{methodName}." : "Method not found.");
                return deleted;
            }

            if (context.Switches.Contains("VIEW", StringComparer.OrdinalIgnoreCase))
            {
                var rec = _ext.Store.GetMethod(targetObj.Id.Value, methodName);
                if (rec == null) { context.Notify("Method not found."); return false; }
                context.Notify($"[{rec.Access.ToString().ToLowerInvariant()}] {targetObj.Name}/{rec.Name}:");
                foreach (string line in rec.Source.Split('\n'))
                    context.Notify(line.TrimEnd('\r'));
                return true;
            }

            if (!Enum.TryParse<AccessModifier>(context.RightArg.Trim(), ignoreCase: true, out var newAccess))
            {
                context.Notify("Access modifier must be private, protected, internal, or public.");
                return false;
            }

            if (!_ext.Store.SetAccess(targetObj.Id.Value, methodName, newAccess))
            {
                context.Notify("Method not found. Program it first with @prog <object>/<method>.");
                return false;
            }

            context.Notify($"Set {targetObj.Name}/{methodName} access to {newAccess.ToString().ToLowerInvariant()}.");
            return true;
        }
    }

    private sealed class ProgramCommandHandler : ICommandHandler
    {
        private readonly ScriptExtension _ext;
        public ProgramCommandHandler(ScriptExtension ext) => _ext = ext;
        public string Name => "@program";

        public bool Execute(CommandContext context, ObjectDatabase db)
        {
            var actor = db.GetObject(context.Executor);
            if (actor == null) return false;

            if (!_ext.TryParseObjMethod(context.Executor, context.LeftArg, out var targetObj, out var methodName))
            {
                context.Notify("Usage: @program <object>/<method>[=<access>]");
                return false;
            }

            if (!Controls(actor, targetObj))
            {
                context.Notify("Permission denied.");
                return false;
            }

            AccessModifier access = AccessModifier.Private;
            if (!string.IsNullOrWhiteSpace(context.RightArg))
            {
                if (!Enum.TryParse(context.RightArg.Trim(), ignoreCase: true, out access))
                {
                    context.Notify("Access modifier must be private, protected, internal, or public.");
                    return false;
                }
            }
            else
            {
                var existing = _ext.Store.GetMethod(targetObj.Id.Value, methodName);
                if (existing != null) access = existing.Access;
            }

            var lines = new List<string>();
            context.Notify($"Enter lines for {targetObj.Name}({targetObj.Id})/{methodName} [{access.ToString().ToLowerInvariant()}]. End with '.' or '@abort' to cancel.");

            _ext.Host.CapturePlayerInput(context.Executor, line =>
            {
                if (line == "@abort")
                {
                    _ext.Host.NotifyPlayer(context.Executor, "Programming aborted.");
                    return false;
                }
                if (line == ".")
                {
                    string source = string.Join("\n", lines);
                    try
                    {
                        var compiled = BytecodeCompiler.Compile(source, targetObj.Id.Value, methodName);
                        _ext.Store.SaveMethod(targetObj.Id.Value, methodName, access, source, compiled);
                        _ext.Host.NotifyPlayer(context.Executor, $"Compiled and saved {targetObj.Name}/{methodName} ({lines.Count} lines).");
                    }
                    catch (ScriptCompileException ex)
                    {
                        _ext.Host.NotifyPlayer(context.Executor, $"Compile error {ex.Message}");
                    }
                    return false;
                }
                lines.Add(line);
                return true;
            });

            return true;
        }
    }
}

internal sealed class MushScriptBridge : IScriptHostBridge
{
    private readonly IExtensionHost _host;
    private readonly ScriptStore _store;
    private readonly Action<SuspendedTaskState> _scheduleAfter;

    public MushScriptBridge(IExtensionHost host, ScriptStore store, Action<SuspendedTaskState> scheduleAfter)
    {
        _host = host;
        _store = store;
        _scheduleAfter = scheduleAfter;
    }

    public ResolvedMethod? ResolveMethod(int targetDbref, string methodName, bool startAtParent = false)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        if (startAtParent)
        {
            var startObj = _host.Database.GetObject(new Dbref(targetDbref));
            if (startObj != null)
                foreach (var p in startObj.Parents) queue.Enqueue(p.Value);
        }
        else
        {
            queue.Enqueue(targetDbref);
        }

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            var rec = _store.GetMethod(id, methodName);
            if (rec != null)
                return new ResolvedMethod(id, rec.Access, rec.Bytecode);

            var obj = _host.Database.GetObject(new Dbref(id));
            if (obj != null)
            {
                foreach (var p in obj.Parents)
                    queue.Enqueue(p.Value);
            }
        }
        return null;
    }

    public CompiledMethod? GetCompiledMethod(int definingDbref, string methodName)
        => _store.GetMethod(definingDbref, methodName)?.Bytecode;

    public bool CanCallMethod(int hostDbref, int callerDbref, AccessModifier access)
    {
        if (access == AccessModifier.Public) return true;
        if (callerDbref == hostDbref) return true;
        if (access == AccessModifier.Private) return false;

        var hostObj = _host.Database.GetObject(new Dbref(hostDbref));
        var callerObj = _host.Database.GetObject(new Dbref(callerDbref));
        if (hostObj == null || callerObj == null) return false;

        if (access == AccessModifier.Internal)
            return callerObj.Owner == hostObj.Owner;

        // Protected: caller has the same or greater permissions as the host object
        return PermissionRank(callerObj) >= PermissionRank(hostObj);
    }

    private static int PermissionRank(MUSHObject obj)
    {
        if (obj.IsWizard) return 3;
        if (obj.HasFlag("ROYALTY")) return 2;
        return 1;
    }

    public ScriptValue GetProperty(int targetDbref, string propName, int callerDbref)
    {
        var obj = _host.Database.GetObject(new Dbref(targetDbref))
            ?? throw new ScriptRuntimeException(ScriptErrorCode.E_INVIND);
        var attr = obj.GetAttribute(propName, _host.Database)
            ?? throw new ScriptRuntimeException(ScriptErrorCode.E_PROPNF, $"Property '{propName}' not found on #{targetDbref}.");
        return AttributeMarshal.FromAttribute(attr);
    }

    public void SetProperty(int targetDbref, string propName, ScriptValue value, int callerDbref)
    {
        var obj = _host.Database.GetObject(new Dbref(targetDbref))
            ?? throw new ScriptRuntimeException(ScriptErrorCode.E_INVIND);
        var caller = _host.Database.GetObject(new Dbref(callerDbref));
        if (caller == null || (!caller.IsWizard && caller.Id != obj.Id && obj.Owner != caller.Owner))
            throw new ScriptRuntimeException(ScriptErrorCode.E_PERM);

        var (text, typeFlag) = AttributeMarshal.ToAttribute(value);
        var err = obj.SetAttribute(propName, text, new Dbref(callerDbref), wizardOverride: caller.IsWizard);
        if (err != AttributeErrorCode.Okay)
            throw new ScriptRuntimeException(ScriptErrorCode.E_PERM, $"Attribute error: {err}");

        var attr = obj.GetAttribute(propName);
        if (attr != null)
        {
            foreach (string f in AttributeMarshal.AllTypeFlags)
                obj.RemoveAttributeCustomFlag(attr.Name, f);
            if (typeFlag != null)
                obj.AddAttributeCustomFlag(attr.Name, typeFlag);
        }
    }

    public bool IsValidObject(int dbref) => _host.Database.GetObject(new Dbref(dbref)) is { Type: not ObjectType.Garbage };
    public int GetParent(int dbref) => _host.Database.GetObject(new Dbref(dbref))?.Parents.FirstOrDefault(Dbref.Nothing).Value ?? -1;
    public IReadOnlyList<int> GetChildren(int dbref) => _host.Database.GetAllObjects().Where(o => o.Parents.Contains(new Dbref(dbref))).Select(o => o.Id.Value).ToList();
    public IReadOnlyList<int> GetContents(int dbref) => _host.Database.GetAllObjects().Where(o => o.Location.Value == dbref).Select(o => o.Id.Value).ToList();
    public int GetLocation(int dbref) => _host.Database.GetObject(new Dbref(dbref))?.Location.Value ?? -1;
    public int GetOwner(int dbref) => _host.Database.GetObject(new Dbref(dbref))?.Owner.Value ?? -1;
    public string GetObjectName(int dbref) => _host.Database.GetObject(new Dbref(dbref))?.Name ?? "";
    public IReadOnlyList<int> GetPlayers() => _host.Database.GetAllObjects().Where(o => o.Type == ObjectType.Player).Select(o => o.Id.Value).ToList();
    public IReadOnlyList<int> GetConnectedPlayers() => _host.Database.GetAllObjects().Where(o => o.Type == ObjectType.Player && _host.Database.IsConnected(o.Id)).Select(o => o.Id.Value).ToList();

    public string EvalSoftcode(int executorDbref, int enactorDbref, string expr)
        => _host.Evaluator.Evaluate(expr, new EvaluationContext(new Dbref(executorDbref), new Dbref(enactorDbref)));

    public string CallSoftcodeAttr(int executorDbref, int enactorDbref, int objDbref, string attrName, IReadOnlyList<ScriptValue> args)
    {
        var obj = _host.Database.GetObject(new Dbref(objDbref));
        var attr = obj?.GetAttribute(attrName, _host.Database);
        if (attr == null) return "";
        var ctx = new EvaluationContext(
            new Dbref(executorDbref),
            new Dbref(enactorDbref),
            new Dbref(executorDbref),
            Dbref.Nothing,
            args.Select(a => a.ToDisplayString()).ToArray());
        return _host.Evaluator.Evaluate(attr.Value, ctx);
    }

    public string GetSoftcodeAttr(int executorDbref, int objDbref, string attrName)
        => _host.Database.GetObject(new Dbref(objDbref))?.GetAttribute(attrName, _host.Database)?.Value ?? "";

    public void SetSoftcodeAttr(int executorDbref, int objDbref, string attrName, string value)
    {
        var obj = _host.Database.GetObject(new Dbref(objDbref))
            ?? throw new ScriptRuntimeException(ScriptErrorCode.E_INVIND);
        obj.SetAttribute(attrName, value, new Dbref(executorDbref));
    }

    public void NotifyPlayer(int playerDbref, string message) => _host.NotifyPlayer(new Dbref(playerDbref), message);
    public void Log(string message) => _host.Log(message);
    public void ScheduleAfterTask(SuspendedTaskState taskState) => _scheduleAfter(taskState);
}
