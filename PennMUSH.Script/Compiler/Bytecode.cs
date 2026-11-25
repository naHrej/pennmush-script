namespace PennMUSH.Script.Compiler;

using System;
using System.Collections.Generic;
using System.Text.Json;
using PennMUSH.Script.Runtime;

public enum OpCode : byte
{
    Nop = 0,
    PushConst,
    PushNull,
    PushTrue,
    PushFalse,
    Pop,
    Dup,
    Swap,

    LoadLocal,
    StoreLocal,
    LoadUpvalue,
    StoreUpvalue,
    LoadContext, // 0=this, 1=caller, 2=player, 3=args

    MakeList,
    ListSpread,
    MakeMap,
    InterpJoin,

    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Pow,
    Neg,
    Not,
    BitNot,
    BitOr,
    BitXor,
    BitAnd,
    Shl,
    Shr,
    Eq,
    Neq,
    Lt,
    Lte,
    Gt,
    Gte,
    In,
    IsType,

    GetProp,      // (obj, name) -> val
    SetProp,      // (obj, name, val) -> val
    GetIndex,     // (target, idx, fromEndFlag) -> val
    SetIndex,     // (target, idx, fromEndFlag, val) -> newTarget
    GetSlice,     // (target, startOrNull, endOrNull, flags: bit0=startFromEnd, bit1=endFromEnd) -> val

    CallGlobal,   // operandA=nameIdx, operandB=argc
    CallMethod,   // operandA=nameIdx, operandB=argc (receiver below args; mutates/rebinds if CoW method)
    CallBase,     // operandA=nameIdx, operandB=argc
    CallValue,    // operandA=argc (callee below args)
    CallSpread,   // packs spread marker? (handled via arglist build)
    MakeClosure,  // operandA=funcIndex

    Jump,
    JumpIfFalse,
    JumpIfTrue,
    JumpIfNull,
    JumpIfNotNull,

    EnterTry,     // operandA=catchIp (-1 if none), operandB=finallyIp (-1 if none)
    LeaveTry,
    Throw,
    EndFinally,
    CheckTypeHint, // operandA=typeNameConstIdx (pops/peeks top, validates or throws E_TYPE)

    ScheduleAfter, // operandA=funcIndex (pops delaySeconds)
    Return
}

public readonly record struct Instruction(OpCode Op, int A = 0, int B = 0, int Line = 0);

public readonly record struct UpvalueDesc(bool IsLocal, int Index);

public readonly record struct TryHandlerEntry(int TryStart, int TryEnd, int CatchIp, int FinallyIp, int StackDepth);

public sealed class CompiledFunction
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int ParamCount { get; init; }
    public int LocalCount { get; init; }
    public List<UpvalueDesc> Upvalues { get; init; } = new();
    public List<Instruction> Code { get; init; } = new();
    public List<ScriptValue> Constants { get; init; } = new();
    public List<TryHandlerEntry> Handlers { get; init; } = new();
}

public sealed class CompiledMethod
{
    public const int CurrentCompilerVersion = 1;

    public int CompilerVersion { get; init; } = CurrentCompilerVersion;
    public int TargetDbref { get; init; }
    public string MethodName { get; init; } = "";
    public List<CompiledFunction> Functions { get; init; } = new();

    public string ToJson()
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("v", CompilerVersion);
        w.WriteNumber("d", TargetDbref);
        w.WriteString("m", MethodName);
        w.WriteStartArray("f");
        foreach (var fn in Functions)
        {
            w.WriteStartObject();
            w.WriteNumber("i", fn.Index);
            w.WriteString("n", fn.Name);
            w.WriteNumber("p", fn.ParamCount);
            w.WriteNumber("l", fn.LocalCount);
            w.WriteStartArray("u");
            foreach (var u in fn.Upvalues)
            {
                w.WriteStartArray();
                w.WriteBooleanValue(u.IsLocal);
                w.WriteNumberValue(u.Index);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("c");
            foreach (var c in fn.Constants)
                ScriptValueJson.WriteTagged(w, c);
            w.WriteEndArray();
            w.WriteStartArray("k");
            foreach (var ins in fn.Code)
            {
                w.WriteStartArray();
                w.WriteNumberValue((int)ins.Op);
                w.WriteNumberValue(ins.A);
                w.WriteNumberValue(ins.B);
                w.WriteNumberValue(ins.Line);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("h");
            foreach (var h in fn.Handlers)
            {
                w.WriteStartArray();
                w.WriteNumberValue(h.TryStart);
                w.WriteNumberValue(h.TryEnd);
                w.WriteNumberValue(h.CatchIp);
                w.WriteNumberValue(h.FinallyIp);
                w.WriteNumberValue(h.StackDepth);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static CompiledMethod FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var funcs = new List<CompiledFunction>();
        foreach (var fe in root.GetProperty("f").EnumerateArray())
        {
            var fn = new CompiledFunction
            {
                Index = fe.GetProperty("i").GetInt32(),
                Name = fe.GetProperty("n").GetString() ?? "",
                ParamCount = fe.GetProperty("p").GetInt32(),
                LocalCount = fe.GetProperty("l").GetInt32()
            };
            foreach (var u in fe.GetProperty("u").EnumerateArray())
                fn.Upvalues.Add(new UpvalueDesc(u[0].GetBoolean(), u[1].GetInt32()));
            foreach (var c in fe.GetProperty("c").EnumerateArray())
                fn.Constants.Add(ScriptValueJson.ReadTagged(c));
            foreach (var k in fe.GetProperty("k").EnumerateArray())
                fn.Code.Add(new Instruction((OpCode)k[0].GetInt32(), k[1].GetInt32(), k[2].GetInt32(), k[3].GetInt32()));
            foreach (var h in fe.GetProperty("h").EnumerateArray())
                fn.Handlers.Add(new TryHandlerEntry(h[0].GetInt32(), h[1].GetInt32(), h[2].GetInt32(), h[3].GetInt32(), h[4].GetInt32()));
            funcs.Add(fn);
        }

        return new CompiledMethod
        {
            CompilerVersion = root.GetProperty("v").GetInt32(),
            TargetDbref = root.GetProperty("d").GetInt32(),
            MethodName = root.GetProperty("m").GetString() ?? "",
            Functions = funcs
        };
    }
}
