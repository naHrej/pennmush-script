namespace PennMUSH.Script.Vm;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using PennMUSH.Core.Types;
using PennMUSH.Script.Compiler;
using PennMUSH.Script.Runtime;

public sealed record TryFrameState(int CatchIp, int FinallyIp, int StackDepth);

public sealed class VmCallFrame
{
    public int TargetDbref { get; init; }
    public string MethodName { get; init; } = "";
    public int FunctionIndex { get; init; }
    public int Ip { get; set; }
    public ScriptValue[] Locals { get; init; } = Array.Empty<ScriptValue>();
    public ScriptValue[] Upvalues { get; init; } = Array.Empty<ScriptValue>();
    public List<TryFrameState> TryStack { get; init; } = new();
    public int ThisDbref { get; init; }
    public int CallerDbref { get; init; }
    public int PlayerDbref { get; init; }
    public ScriptValue Args { get; init; } = ScriptValue.EmptyList;
    public int BaseStackDepth { get; init; }
}

public sealed class SuspendedTaskState
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public long ResumeAtMs { get; init; }
    public int RemainingTicks { get; init; }
    public int CompilerVersion { get; init; } = CompiledMethod.CurrentCompilerVersion;
    public List<VmCallFrame> Frames { get; init; } = new();
    public List<ScriptValue> OperandStack { get; init; } = new();

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", TaskId);
        w.WriteNumber("resume", ResumeAtMs);
        w.WriteNumber("ticks", RemainingTicks);
        w.WriteNumber("ver", CompilerVersion);

        w.WriteStartArray("stack");
        foreach (var v in OperandStack)
            ScriptValueJson.WriteTagged(w, v);
        w.WriteEndArray();

        w.WriteStartArray("frames");
        foreach (var f in Frames)
        {
            w.WriteStartObject();
            w.WriteNumber("d", f.TargetDbref);
            w.WriteString("m", f.MethodName);
            w.WriteNumber("fn", f.FunctionIndex);
            w.WriteNumber("ip", f.Ip);
            w.WriteNumber("this", f.ThisDbref);
            w.WriteNumber("caller", f.CallerDbref);
            w.WriteNumber("player", f.PlayerDbref);
            w.WriteNumber("base", f.BaseStackDepth);
            w.WritePropertyName("args");
            ScriptValueJson.WriteTagged(w, f.Args);

            w.WriteStartArray("loc");
            foreach (var l in f.Locals)
                ScriptValueJson.WriteTagged(w, l);
            w.WriteEndArray();

            w.WriteStartArray("uv");
            foreach (var u in f.Upvalues)
                ScriptValueJson.WriteTagged(w, u);
            w.WriteEndArray();

            w.WriteStartArray("try");
            foreach (var t in f.TryStack)
            {
                w.WriteStartArray();
                w.WriteNumberValue(t.CatchIp);
                w.WriteNumberValue(t.FinallyIp);
                w.WriteNumberValue(t.StackDepth);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static SuspendedTaskState FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var stack = new List<ScriptValue>();
        foreach (var v in r.GetProperty("stack").EnumerateArray())
            stack.Add(ScriptValueJson.ReadTagged(v));

        var frames = new List<VmCallFrame>();
        foreach (var fe in r.GetProperty("frames").EnumerateArray())
        {
            var locs = new List<ScriptValue>();
            foreach (var l in fe.GetProperty("loc").EnumerateArray())
                locs.Add(ScriptValueJson.ReadTagged(l));

            var uvs = new List<ScriptValue>();
            foreach (var u in fe.GetProperty("uv").EnumerateArray())
                uvs.Add(ScriptValueJson.ReadTagged(u));

            var tries = new List<TryFrameState>();
            foreach (var t in fe.GetProperty("try").EnumerateArray())
                tries.Add(new TryFrameState(t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt32()));

            frames.Add(new VmCallFrame
            {
                TargetDbref = fe.GetProperty("d").GetInt32(),
                MethodName = fe.GetProperty("m").GetString() ?? "",
                FunctionIndex = fe.GetProperty("fn").GetInt32(),
                Ip = fe.GetProperty("ip").GetInt32(),
                ThisDbref = fe.GetProperty("this").GetInt32(),
                CallerDbref = fe.GetProperty("caller").GetInt32(),
                PlayerDbref = fe.GetProperty("player").GetInt32(),
                BaseStackDepth = fe.GetProperty("base").GetInt32(),
                Args = ScriptValueJson.ReadTagged(fe.GetProperty("args")),
                Locals = locs.ToArray(),
                Upvalues = uvs.ToArray(),
                TryStack = tries
            });
        }

        return new SuspendedTaskState
        {
            TaskId = r.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
            ResumeAtMs = r.GetProperty("resume").GetInt64(),
            RemainingTicks = r.GetProperty("ticks").GetInt32(),
            CompilerVersion = r.GetProperty("ver").GetInt32(),
            OperandStack = stack,
            Frames = frames
        };
    }
}

public enum VmExecutionStatus
{
    Completed,
    Suspended,
    Faulted
}

public sealed record VmExecutionResult(
    VmExecutionStatus Status,
    ScriptValue ReturnValue,
    SuspendedTaskState? SuspendedState = null,
    ScriptErrorCode ErrorCode = ScriptErrorCode.E_NONE,
    string? ErrorMessage = null);

public sealed record ResolvedMethod(
    int DefiningDbref,
    AccessModifier Access,
    CompiledMethod Bytecode);

public interface IScriptHostBridge
{
    ResolvedMethod? ResolveMethod(int targetDbref, string methodName, bool startAtParent = false);
    CompiledMethod? GetCompiledMethod(int definingDbref, string methodName);
    bool CanCallMethod(int hostDbref, int callerDbref, AccessModifier access);

    ScriptValue GetProperty(int targetDbref, string propName, int callerDbref);
    void SetProperty(int targetDbref, string propName, ScriptValue value, int callerDbref);

    // MUSH world & softcode bridge
    bool IsValidObject(int dbref);
    int GetParent(int dbref);
    IReadOnlyList<int> GetChildren(int dbref);
    IReadOnlyList<int> GetContents(int dbref);
    int GetLocation(int dbref);
    int GetOwner(int dbref);
    string GetObjectName(int dbref);
    IReadOnlyList<int> GetPlayers();
    IReadOnlyList<int> GetConnectedPlayers();

    string EvalSoftcode(int executorDbref, int enactorDbref, string expr);
    string CallSoftcodeAttr(int executorDbref, int enactorDbref, int objDbref, string attrName, IReadOnlyList<ScriptValue> args);
    string GetSoftcodeAttr(int executorDbref, int objDbref, string attrName);
    void SetSoftcodeAttr(int executorDbref, int objDbref, string attrName, string value);
    void NotifyPlayer(int playerDbref, string message);
    void Log(string message);
    void ScheduleAfterTask(SuspendedTaskState taskState);
}
