namespace PennMUSH.Script.Vm;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using PennMUSH.Script.Compiler;
using PennMUSH.Script.Runtime;

public sealed class ScriptVm
{
    private readonly IScriptHostBridge _bridge;
    private readonly List<VmCallFrame> _frames = new();
    private readonly List<ScriptValue> _stack = new();
    private readonly Dictionary<(int Dbref, string Method), CompiledMethod> _methodCache = new();

    public int RemainingTicks { get; private set; }
    public int MaxCallDepth { get; }

    public ScriptVm(IScriptHostBridge bridge, int maxTicks = 50_000, int maxCallDepth = 64)
    {
        _bridge = bridge;
        RemainingTicks = maxTicks;
        MaxCallDepth = maxCallDepth;
    }

    public VmExecutionResult Execute(
        CompiledMethod method,
        int thisDbref,
        int callerDbref,
        int playerDbref,
        IReadOnlyList<ScriptValue>? args = null)
    {
        _frames.Clear();
        _stack.Clear();
        _methodCache[(method.TargetDbref, method.MethodName)] = method;

        var argsList = args != null ? ScriptValue.FromList(args) : ScriptValue.EmptyList;
        var fn = method.Functions[0];
        var locals = new ScriptValue[fn.LocalCount];
        Array.Fill(locals, ScriptValue.Null);

        _frames.Add(new VmCallFrame
        {
            TargetDbref = method.TargetDbref,
            MethodName = method.MethodName,
            FunctionIndex = 0,
            Ip = 0,
            Locals = locals,
            ThisDbref = thisDbref,
            CallerDbref = callerDbref,
            PlayerDbref = playerDbref,
            Args = argsList,
            BaseStackDepth = 0
        });

        return RunLoop(0);
    }

    public VmExecutionResult Resume(SuspendedTaskState state)
    {
        if (state.CompilerVersion != CompiledMethod.CurrentCompilerVersion)
            return new VmExecutionResult(VmExecutionStatus.Faulted, ScriptValue.Null, null, ScriptErrorCode.E_INTRPT, "Compiler version mismatch on resume.");

        _frames.Clear();
        _stack.Clear();
        _frames.AddRange(state.Frames);
        _stack.AddRange(state.OperandStack);
        RemainingTicks = state.RemainingTicks > 0 ? state.RemainingTicks : 50_000;
        return RunLoop(0);
    }

    internal ScriptValue InvokeClosureSync(ScriptValue funcVal, IReadOnlyList<ScriptValue> args)
    {
        var closure = funcVal.AsFunc();
        int targetFrameCount = _frames.Count;
        var method = GetMethodOrThrow(closure.TargetDbref, closure.MethodName);
        var fn = method.Functions[closure.FunctionIndex];
        var locals = new ScriptValue[fn.LocalCount];
        Array.Fill(locals, ScriptValue.Null);
        for (int i = 0; i < fn.ParamCount && i < args.Count; i++)
            locals[i] = args[i];

        var parentFrame = _frames.Count > 0 ? _frames[^1] : null;
        _frames.Add(new VmCallFrame
        {
            TargetDbref = closure.TargetDbref,
            MethodName = closure.MethodName,
            FunctionIndex = closure.FunctionIndex,
            Ip = 0,
            Locals = locals,
            Upvalues = closure.Upvalues.ToArray(),
            ThisDbref = parentFrame?.ThisDbref ?? closure.TargetDbref,
            CallerDbref = parentFrame?.CallerDbref ?? closure.TargetDbref,
            PlayerDbref = parentFrame?.PlayerDbref ?? closure.TargetDbref,
            Args = ScriptValue.FromList(args),
            BaseStackDepth = _stack.Count
        });

        var res = RunLoop(targetFrameCount);
        if (res.Status == VmExecutionStatus.Faulted)
            throw new ScriptRuntimeException(res.ErrorCode, res.ErrorMessage);
        if (res.Status == VmExecutionStatus.Suspended)
            throw new ScriptRuntimeException(ScriptErrorCode.E_INTRPT, "Cannot Sleep() inside synchronous collection callback.");
        return res.ReturnValue;
    }

    public ScriptValue GetCallersList()
    {
        var list = _frames.Select(f =>
        {
            var m = ScriptMap.Empty
                .Set(ScriptValue.FromString("obj"), ScriptValue.FromObj(f.ThisDbref))
                .Set(ScriptValue.FromString("method"), ScriptValue.FromString(f.MethodName))
                .Set(ScriptValue.FromString("caller"), ScriptValue.FromObj(f.CallerDbref));
            return ScriptValue.FromMap(m);
        });
        return ScriptValue.FromList(list);
    }

    private VmExecutionResult RunLoop(int stopFrameDepth)
    {
        while (_frames.Count > stopFrameDepth)
        {
            if (--RemainingTicks < 0)
                return HandleException(ScriptValue.FromError(ScriptErrorCode.E_QUOTA), "Tick quota exceeded.", stopFrameDepth, out var res) ? res! : RunLoop(stopFrameDepth);

            var frame = _frames[^1];
            var method = GetMethodOrThrow(frame.TargetDbref, frame.MethodName);
            var fn = method.Functions[frame.FunctionIndex];

            if (frame.Ip >= fn.Code.Count)
            {
                PopFrameAndPush(ScriptValue.Null, stopFrameDepth, out var doneVal);
                if (_frames.Count == stopFrameDepth)
                    return new VmExecutionResult(VmExecutionStatus.Completed, doneVal);
                continue;
            }

            var ins = fn.Code[frame.Ip++];
            try
            {
                switch (ins.Op)
                {
                    case OpCode.Nop: break;
                    case OpCode.PushConst: Push(fn.Constants[ins.A]); break;
                    case OpCode.PushNull: Push(ScriptValue.Null); break;
                    case OpCode.PushTrue: Push(ScriptValue.True); break;
                    case OpCode.PushFalse: Push(ScriptValue.False); break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: Push(Peek()); break;
                    case OpCode.Swap:
                    {
                        var a = Pop(); var b = Pop();
                        Push(a); Push(b);
                        break;
                    }
                    case OpCode.LoadLocal: Push(frame.Locals[ins.A]); break;
                    case OpCode.StoreLocal: frame.Locals[ins.A] = Peek(); break;
                    case OpCode.LoadUpvalue: Push(frame.Upvalues[ins.A]); break;
                    case OpCode.StoreUpvalue: frame.Upvalues[ins.A] = Peek(); break;
                    case OpCode.LoadContext:
                        Push(ins.A switch
                        {
                            0 => ScriptValue.FromObj(frame.ThisDbref),
                            1 => ScriptValue.FromObj(frame.CallerDbref),
                            2 => ScriptValue.FromObj(frame.PlayerDbref),
                            3 => frame.Args,
                            _ => ScriptValue.Null
                        });
                        break;

                    case OpCode.MakeList:
                    {
                        var arr = new ScriptValue[ins.A];
                        for (int i = ins.A - 1; i >= 0; i--) arr[i] = Pop();
                        Push(ScriptValue.FromList(arr));
                        break;
                    }
                    case OpCode.ListSpread:
                    {
                        var item = Pop();
                        var list = Pop().AsList();
                        if (ins.A == 1)
                            Push(ScriptValue.FromList(list.Add(item)));
                        else
                            Push(ScriptValue.FromList(list.AddRange(item.AsList())));
                        break;
                    }
                    case OpCode.MakeMap:
                    {
                        var pairs = new (ScriptValue K, ScriptValue V)[ins.A];
                        for (int i = ins.A - 1; i >= 0; i--)
                        {
                            var v = Pop(); var k = Pop();
                            pairs[i] = (k, v);
                        }
                        var map = ScriptMap.Empty;
                        foreach (var (k, v) in pairs) map = map.Set(k, v);
                        Push(ScriptValue.FromMap(map));
                        break;
                    }
                    case OpCode.InterpJoin:
                    {
                        var parts = new string[ins.A];
                        for (int i = ins.A - 1; i >= 0; i--) parts[i] = Pop().ToDisplayString();
                        Push(ScriptValue.FromString(string.Concat(parts)));
                        break;
                    }

                    case OpCode.Add: ExecAdd(); break;
                    case OpCode.Sub: ExecNumeric((a, b) => a - b, (a, b) => a - b); break;
                    case OpCode.Mul: ExecNumeric((a, b) => a * b, (a, b) => a * b); break;
                    case OpCode.Div: ExecDiv(); break;
                    case OpCode.Mod: ExecMod(); break;
                    case OpCode.Pow:
                    {
                        var b = Pop().AsFloat(); var a = Pop().AsFloat();
                        Push(ScriptValue.FromFloat(Math.Pow(a, b)));
                        break;
                    }
                    case OpCode.Neg:
                    {
                        var v = Pop();
                        Push(v.Type == ScriptValueType.Int ? ScriptValue.FromInt(-v.AsInt()) : ScriptValue.FromFloat(-v.AsFloat()));
                        break;
                    }
                    case OpCode.Not: Push(ScriptValue.FromBool(!Pop().IsTruthy())); break;
                    case OpCode.BitNot: Push(ScriptValue.FromInt(~Pop().AsInt())); break;
                    case OpCode.BitOr: { var b = Pop().AsInt(); var a = Pop().AsInt(); Push(ScriptValue.FromInt(a | b)); break; }
                    case OpCode.BitXor: { var b = Pop().AsInt(); var a = Pop().AsInt(); Push(ScriptValue.FromInt(a ^ b)); break; }
                    case OpCode.BitAnd: { var b = Pop().AsInt(); var a = Pop().AsInt(); Push(ScriptValue.FromInt(a & b)); break; }
                    case OpCode.Shl: { var b = (int)Pop().AsInt(); var a = Pop().AsInt(); Push(ScriptValue.FromInt(a << b)); break; }
                    case OpCode.Shr: { var b = (int)Pop().AsInt(); var a = Pop().AsInt(); Push(ScriptValue.FromInt(a >> b)); break; }
                    case OpCode.Eq: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(a.Equals(b))); break; }
                    case OpCode.Neq: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(!a.Equals(b))); break; }
                    case OpCode.Lt: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(Compare(a, b) < 0)); break; }
                    case OpCode.Lte: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(Compare(a, b) <= 0)); break; }
                    case OpCode.Gt: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(Compare(a, b) > 0)); break; }
                    case OpCode.Gte: { var b = Pop(); var a = Pop(); Push(ScriptValue.FromBool(Compare(a, b) >= 0)); break; }
                    case OpCode.In:
                    {
                        var col = Pop(); var item = Pop();
                        bool found = col.Type switch
                        {
                            ScriptValueType.List => col.AsList().Contains(item),
                            ScriptValueType.Map => col.AsMap().ContainsKey(item),
                            ScriptValueType.String => col.AsString().Contains(item.AsString(), StringComparison.Ordinal),
                            _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE)
                        };
                        Push(ScriptValue.FromBool(found));
                        break;
                    }
                    case OpCode.IsType:
                    {
                        var val = Pop();
                        string typeName = fn.Constants[ins.A].AsString();
                        Push(ScriptValue.FromBool(string.Equals(val.TypeName, typeName, StringComparison.Ordinal)));
                        break;
                    }
                    case OpCode.CheckTypeHint:
                    {
                        var val = Peek();
                        string hint = fn.Constants[ins.A].AsString();
                        if (val.Type != ScriptValueType.Null && !string.Equals(val.TypeName, hint, StringComparison.Ordinal))
                            throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot assign {val.TypeName} to {hint}.");
                        break;
                    }

                    case OpCode.GetProp:
                    {
                        string propName = Pop().AsString();
                        var target = Pop();
                        if (ScriptStdLib.TryGetBuiltinProperty(_bridge, target, propName, out var builtinVal))
                            Push(builtinVal);
                        else if (target.Type == ScriptValueType.Obj)
                            Push(_bridge.GetProperty(target.AsObj(), propName, frame.ThisDbref));
                        else if (target.Type == ScriptValueType.Map)
                            Push(target.AsMap().TryGetValue(ScriptValue.FromString(propName), out var mv) ? mv : ScriptValue.Null);
                        else
                            throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot access property '{propName}' on {target.TypeName}.");
                        break;
                    }
                    case OpCode.SetProp:
                    {
                        var val = Pop();
                        string propName = Pop().AsString();
                        var target = Pop();
                        if (target.Type != ScriptValueType.Obj)
                            throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot set property '{propName}' on {target.TypeName}.");
                        _bridge.SetProperty(target.AsObj(), propName, val, frame.ThisDbref);
                        Push(val);
                        break;
                    }
                    case OpCode.GetIndex:
                    {
                        var idxVal = Pop();
                        var target = Pop();
                        bool fromEnd = ins.A == 1;
                        Push(ExecGetIndex(target, idxVal, fromEnd));
                        break;
                    }
                    case OpCode.SetIndex:
                    {
                        var val = Pop();
                        var idxVal = Pop();
                        var target = Pop();
                        bool fromEnd = ins.A == 1;
                        Push(ExecSetIndex(target, idxVal, fromEnd, val));
                        break;
                    }
                    case OpCode.GetSlice:
                    {
                        var endVal = Pop();
                        var startVal = Pop();
                        var target = Pop();
                        bool startFromEnd = (ins.A & 1) != 0;
                        bool endFromEnd = (ins.A & 2) != 0;
                        Push(ExecGetSlice(target, startVal, endVal, startFromEnd, endFromEnd));
                        break;
                    }

                    case OpCode.Jump: frame.Ip = ins.A; break;
                    case OpCode.JumpIfFalse: if (!Peek().IsTruthy()) frame.Ip = ins.A; break;
                    case OpCode.JumpIfTrue: if (Peek().IsTruthy()) frame.Ip = ins.A; break;
                    case OpCode.JumpIfNull: if (Peek().Type == ScriptValueType.Null) frame.Ip = ins.A; break;
                    case OpCode.JumpIfNotNull: if (Peek().Type != ScriptValueType.Null) frame.Ip = ins.A; break;

                    case OpCode.EnterTry:
                        frame.TryStack.Add(new TryFrameState(ins.A, ins.B, _stack.Count));
                        break;
                    case OpCode.LeaveTry:
                        if (frame.TryStack.Count > 0)
                            frame.TryStack.RemoveAt(frame.TryStack.Count - 1);
                        break;
                    case OpCode.EndFinally:
                        break;
                    case OpCode.Throw:
                    {
                        var errVal = Pop();
                        if (HandleException(errVal, errVal.ToDisplayString(), stopFrameDepth, out var faultRes))
                            return faultRes!;
                        break;
                    }

                    case OpCode.MakeClosure:
                    {
                        var targetFn = method.Functions[ins.A];
                        var uvs = ImmutableArray.CreateBuilder<ScriptValue>(targetFn.Upvalues.Count);
                        foreach (var u in targetFn.Upvalues)
                            uvs.Add(u.IsLocal ? frame.Locals[u.Index] : frame.Upvalues[u.Index]);
                        Push(ScriptValue.FromFunc(new ScriptClosure(frame.TargetDbref, frame.MethodName, ins.A, uvs.ToImmutable())));
                        break;
                    }

                    case OpCode.CallGlobal:
                    {
                        string name = fn.Constants[ins.A].AsString();
                        var args = PopArgs(ins.B);
                        if (name == "Sleep")
                        {
                            double secs = args.Count > 0 ? args[0].AsFloat() : 0;
                            long resumeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(secs * 1000.0);
                            Push(ScriptValue.Null); // return value of Sleep()
                            var suspended = new SuspendedTaskState
                            {
                                ResumeAtMs = resumeAt,
                                RemainingTicks = RemainingTicks,
                                Frames = _frames.ToList(),
                                OperandStack = _stack.ToList()
                            };
                            return new VmExecutionResult(VmExecutionStatus.Suspended, ScriptValue.Null, suspended);
                        }
                        if (!ScriptStdLib.TryInvokeGlobal(this, _bridge, frame, name, args, out var gRes))
                            throw new ScriptRuntimeException(ScriptErrorCode.E_VERBNF, $"Unknown function '{name}'.");
                        Push(gRes);
                        break;
                    }

                    case OpCode.CallMethod:
                    {
                        string mName = fn.Constants[ins.A].AsString();
                        var args = PopArgs(ins.B);
                        var receiver = Pop();
                        if (receiver.Type == ScriptValueType.String && receiver.AsString().StartsWith("__ns:", StringComparison.Ordinal))
                        {
                            string ns = receiver.AsString()[5..];
                            if (!ScriptStdLib.TryInvokeNamespace(_bridge, frame, ns, mName, args, out var nsRes))
                                throw new ScriptRuntimeException(ScriptErrorCode.E_VERBNF, $"Unknown {ns}.{mName}().");
                            Push(nsRes);
                        }
                        else if (ScriptStdLib.TryInvokeBuiltinMethod(this, _bridge, receiver, mName, args, out var bRes))
                        {
                            Push(bRes);
                        }
                        else if (receiver.Type == ScriptValueType.Obj)
                        {
                            PushObjectMethodCall(receiver.AsObj(), mName, args, startAtParent: false, frame);
                        }
                        else
                        {
                            throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot call '{mName}' on {receiver.TypeName}.");
                        }
                        break;
                    }

                    case OpCode.CallBase:
                    {
                        string mName = fn.Constants[ins.A].AsString();
                        var args = PopArgs(ins.B);
                        PushObjectMethodCall(frame.TargetDbref, mName, args, startAtParent: true, frame);
                        break;
                    }

                    case OpCode.CallValue:
                    {
                        var args = PopArgs(ins.A);
                        var closure = Pop().AsFunc();
                        PushClosureCall(closure, args, frame);
                        break;
                    }

                    case OpCode.ScheduleAfter:
                    {
                        double secs = Pop().AsFloat();
                        long resumeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(secs * 1000.0);
                        var afterFn = method.Functions[ins.A];
                        var uvs = new ScriptValue[afterFn.Upvalues.Count];
                        for (int i = 0; i < afterFn.Upvalues.Count; i++)
                        {
                            var u = afterFn.Upvalues[i];
                            uvs[i] = u.IsLocal ? frame.Locals[u.Index] : frame.Upvalues[u.Index];
                        }
                        var locs = new ScriptValue[afterFn.LocalCount];
                        Array.Fill(locs, ScriptValue.Null);
                        var task = new SuspendedTaskState
                        {
                            ResumeAtMs = resumeAt,
                            RemainingTicks = 50_000,
                            Frames = new List<VmCallFrame>
                            {
                                new()
                                {
                                    TargetDbref = frame.TargetDbref,
                                    MethodName = frame.MethodName,
                                    FunctionIndex = ins.A,
                                    Ip = 0,
                                    Locals = locs,
                                    Upvalues = uvs,
                                    ThisDbref = frame.ThisDbref,
                                    CallerDbref = frame.CallerDbref,
                                    PlayerDbref = frame.PlayerDbref,
                                    Args = frame.Args,
                                    BaseStackDepth = 0
                                }
                            }
                        };
                        _bridge.ScheduleAfterTask(task);
                        break;
                    }

                    case OpCode.Return:
                    {
                        var retVal = Pop();
                        PopFrameAndPush(retVal, stopFrameDepth, out var doneVal);
                        if (_frames.Count == stopFrameDepth)
                            return new VmExecutionResult(VmExecutionStatus.Completed, doneVal);
                        break;
                    }
                }
            }
            catch (ScriptRuntimeException ex)
            {
                if (HandleException(ScriptValue.FromError(ex.Code), ex.Message, stopFrameDepth, out var faultRes))
                    return faultRes!;
            }
        }

        return new VmExecutionResult(VmExecutionStatus.Completed, ScriptValue.Null);
    }

    private void PushObjectMethodCall(int targetDbref, string methodName, IReadOnlyList<ScriptValue> args, bool startAtParent, VmCallFrame callerFrame)
    {
        if (_frames.Count >= MaxCallDepth)
            throw new ScriptRuntimeException(ScriptErrorCode.E_MAXREC, "Maximum call depth exceeded.");

        var resolved = _bridge.ResolveMethod(targetDbref, methodName, startAtParent);
        if (resolved == null)
            throw new ScriptRuntimeException(ScriptErrorCode.E_VERBNF, $"Method '{methodName}' not found on #{targetDbref}.");

        if (!_bridge.CanCallMethod(resolved.DefiningDbref, callerFrame.ThisDbref, resolved.Access))
            throw new ScriptRuntimeException(ScriptErrorCode.E_PERM, $"Access denied to '{methodName}' on #{resolved.DefiningDbref}.");

        _methodCache[(resolved.DefiningDbref, methodName)] = resolved.Bytecode;
        var fn = resolved.Bytecode.Functions[0];
        var locals = new ScriptValue[fn.LocalCount];
        Array.Fill(locals, ScriptValue.Null);

        _frames.Add(new VmCallFrame
        {
            TargetDbref = resolved.DefiningDbref,
            MethodName = methodName,
            FunctionIndex = 0,
            Ip = 0,
            Locals = locals,
            ThisDbref = startAtParent ? callerFrame.ThisDbref : targetDbref,
            CallerDbref = callerFrame.ThisDbref,
            PlayerDbref = callerFrame.PlayerDbref,
            Args = ScriptValue.FromList(args),
            BaseStackDepth = _stack.Count
        });
    }

    private void PushClosureCall(ScriptClosure closure, IReadOnlyList<ScriptValue> args, VmCallFrame callerFrame)
    {
        if (_frames.Count >= MaxCallDepth)
            throw new ScriptRuntimeException(ScriptErrorCode.E_MAXREC, "Maximum call depth exceeded.");

        var method = GetMethodOrThrow(closure.TargetDbref, closure.MethodName);
        var fn = method.Functions[closure.FunctionIndex];
        var locals = new ScriptValue[fn.LocalCount];
        Array.Fill(locals, ScriptValue.Null);
        for (int i = 0; i < fn.ParamCount && i < args.Count; i++)
            locals[i] = args[i];

        _frames.Add(new VmCallFrame
        {
            TargetDbref = closure.TargetDbref,
            MethodName = closure.MethodName,
            FunctionIndex = closure.FunctionIndex,
            Ip = 0,
            Locals = locals,
            Upvalues = closure.Upvalues.ToArray(),
            ThisDbref = callerFrame.ThisDbref,
            CallerDbref = callerFrame.ThisDbref,
            PlayerDbref = callerFrame.PlayerDbref,
            Args = ScriptValue.FromList(args),
            BaseStackDepth = _stack.Count
        });
    }

    private CompiledMethod GetMethodOrThrow(int dbref, string name)
    {
        if (_methodCache.TryGetValue((dbref, name), out var m))
            return m;
        m = _bridge.GetCompiledMethod(dbref, name)
            ?? throw new ScriptRuntimeException(ScriptErrorCode.E_VERBNF, $"Method '{name}' not found on #{dbref}.");
        _methodCache[(dbref, name)] = m;
        return m;
    }

    private void PopFrameAndPush(ScriptValue retVal, int stopFrameDepth, out ScriptValue finalReturn)
    {
        var frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        while (_stack.Count > frame.BaseStackDepth)
            _stack.RemoveAt(_stack.Count - 1);
        if (_frames.Count > stopFrameDepth)
        {
            Push(retVal);
            finalReturn = ScriptValue.Null;
        }
        else
        {
            finalReturn = retVal;
        }
    }

    private bool HandleException(ScriptValue errorVal, string message, int stopFrameDepth, out VmExecutionResult? faultResult)
    {
        while (_frames.Count > stopFrameDepth)
        {
            var frame = _frames[^1];
            if (frame.TryStack.Count > 0)
            {
                var handler = frame.TryStack[^1];
                frame.TryStack.RemoveAt(frame.TryStack.Count - 1);
                while (_stack.Count > handler.StackDepth)
                    _stack.RemoveAt(_stack.Count - 1);
                if (handler.CatchIp >= 0)
                {
                    Push(errorVal);
                    frame.Ip = handler.CatchIp;
                    faultResult = null;
                    return false;
                }
                if (handler.FinallyIp >= 0)
                {
                    frame.Ip = handler.FinallyIp;
                    faultResult = null;
                    return false;
                }
            }
            _frames.RemoveAt(_frames.Count - 1);
            while (_stack.Count > frame.BaseStackDepth)
                _stack.RemoveAt(_stack.Count - 1);
        }

        var code = errorVal.Type == ScriptValueType.Error ? errorVal.AsError() : ScriptErrorCode.E_RUNTIME;
        faultResult = new VmExecutionResult(VmExecutionStatus.Faulted, ScriptValue.Null, null, code, message);
        return true;
    }

    private IReadOnlyList<ScriptValue> PopArgs(int argc)
    {
        if (argc == -1)
            return Pop().AsList();
        var arr = new ScriptValue[argc];
        for (int i = argc - 1; i >= 0; i--)
            arr[i] = Pop();
        return arr;
    }

    private void ExecAdd()
    {
        var b = Pop(); var a = Pop();
        if (a.Type == ScriptValueType.String || b.Type == ScriptValueType.String)
            Push(ScriptValue.FromString(a.ToDisplayString() + b.ToDisplayString()));
        else if (a.Type == ScriptValueType.List && b.Type == ScriptValueType.List)
            Push(ScriptValue.FromList(a.AsList().AddRange(b.AsList())));
        else if (a.Type == ScriptValueType.Int && b.Type == ScriptValueType.Int)
            Push(ScriptValue.FromInt(a.AsInt() + b.AsInt()));
        else
            Push(ScriptValue.FromFloat(a.AsFloat() + b.AsFloat()));
    }

    private void ExecNumeric(Func<long, long, long> intOp, Func<double, double, double> floatOp)
    {
        var b = Pop(); var a = Pop();
        if (a.Type == ScriptValueType.Int && b.Type == ScriptValueType.Int)
            Push(ScriptValue.FromInt(intOp(a.AsInt(), b.AsInt())));
        else
            Push(ScriptValue.FromFloat(floatOp(a.AsFloat(), b.AsFloat())));
    }

    private void ExecDiv()
    {
        var b = Pop(); var a = Pop();
        if (a.Type == ScriptValueType.Int && b.Type == ScriptValueType.Int)
        {
            long bv = b.AsInt();
            if (bv == 0) throw new ScriptRuntimeException(ScriptErrorCode.E_DIV);
            Push(ScriptValue.FromInt(a.AsInt() / bv));
        }
        else
        {
            double bv = b.AsFloat();
            if (bv == 0.0) throw new ScriptRuntimeException(ScriptErrorCode.E_DIV);
            Push(ScriptValue.FromFloat(a.AsFloat() / bv));
        }
    }

    private void ExecMod()
    {
        var b = Pop(); var a = Pop();
        long bv = b.AsInt();
        if (bv == 0) throw new ScriptRuntimeException(ScriptErrorCode.E_DIV);
        Push(ScriptValue.FromInt(a.AsInt() % bv));
    }

    private static int Compare(ScriptValue a, ScriptValue b)
    {
        if (a.Type == ScriptValueType.String && b.Type == ScriptValueType.String)
            return string.Compare(a.AsString(), b.AsString(), StringComparison.Ordinal);
        return a.AsFloat().CompareTo(b.AsFloat());
    }

    private static ScriptValue ExecGetIndex(ScriptValue target, ScriptValue idxVal, bool fromEnd)
    {
        if (target.Type == ScriptValueType.Map)
            return target.AsMap().TryGetValue(idxVal, out var mv) ? mv : ScriptValue.Null;

        int raw = (int)idxVal.AsInt();
        if (target.Type == ScriptValueType.List)
        {
            var list = target.AsList();
            int idx = fromEnd ? list.Count - raw : raw;
            if (idx < 0 || idx >= list.Count) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
            return list[idx];
        }
        if (target.Type == ScriptValueType.String)
        {
            string s = target.AsString();
            int idx = fromEnd ? s.Length - raw : raw;
            if (idx < 0 || idx >= s.Length) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
            return ScriptValue.FromString(s[idx].ToString());
        }
        throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE);
    }

    private static ScriptValue ExecSetIndex(ScriptValue target, ScriptValue idxVal, bool fromEnd, ScriptValue val)
    {
        if (target.Type == ScriptValueType.Map)
            return ScriptValue.FromMap(target.AsMap().Set(idxVal, val));

        if (target.Type == ScriptValueType.List)
        {
            var list = target.AsList();
            int raw = (int)idxVal.AsInt();
            int idx = fromEnd ? list.Count - raw : raw;
            if (idx < 0 || idx >= list.Count) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
            return ScriptValue.FromList(list.SetItem(idx, val));
        }
        throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE);
    }

    private static ScriptValue ExecGetSlice(ScriptValue target, ScriptValue startVal, ScriptValue endVal, bool startFromEnd, bool endFromEnd)
    {
        int len = target.Type switch
        {
            ScriptValueType.List => target.AsList().Count,
            ScriptValueType.String => target.AsString().Length,
            _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE)
        };

        int start = startVal.Type == ScriptValueType.Null ? 0 : (int)startVal.AsInt();
        if (startFromEnd) start = len - start;

        int end = endVal.Type == ScriptValueType.Null ? len : (int)endVal.AsInt();
        if (endFromEnd) end = len - end;

        if (start < 0 || end > len || start > end)
            throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);

        return target.Type == ScriptValueType.List
            ? ScriptValue.FromList(target.AsList().GetRange(start, end - start))
            : ScriptValue.FromString(target.AsString()[start..end]);
    }

    private void Push(ScriptValue v) => _stack.Add(v);
    private ScriptValue Peek() => _stack[^1];
    private ScriptValue Pop()
    {
        var v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }
}
