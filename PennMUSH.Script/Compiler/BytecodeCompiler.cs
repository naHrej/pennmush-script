namespace PennMUSH.Script.Compiler;

using System;
using System.Collections.Generic;
using PennMUSH.Script.Runtime;

public sealed class BytecodeCompiler
{
    private sealed class LocalSlot
    {
        public string Name { get; init; } = "";
        public int Slot { get; init; }
        public int Depth { get; init; }
        public bool IsConst { get; init; }
    }

    private sealed class LoopContext
    {
        public int ContinueTarget { get; set; }
        public List<int> BreakJumps { get; } = new();
        public List<int> ContinueJumps { get; } = new();
    }

    private sealed class FunctionScope
    {
        public FunctionScope? Parent { get; init; }
        public CompiledFunction Fn { get; init; } = new();
        public List<LocalSlot> Locals { get; } = new();
        public int NextLocalSlot { get; set; }
        public int ScopeDepth { get; set; }
        public Stack<LoopContext> Loops { get; } = new();
        public Stack<int> TryDepthStack { get; } = new();
    }

    private static readonly HashSet<string> MutatingCollectionMethods = new(StringComparer.Ordinal)
    {
        "Add", "Push", "Pop", "Insert", "Remove", "RemoveAt", "Clear", "Reverse", "Sort", "Set"
    };

    private static readonly HashSet<string> BuiltInNamespaces = new(StringComparer.Ordinal)
    {
        "Math", "Json", "Base64", "Regex", "Time", "Crypto", "Obj", "Mush"
    };

    private readonly List<CompiledFunction> _functions = new();
    private FunctionScope _cur = null!;

    public static CompiledMethod Compile(string source, int targetDbref = 0, string methodName = "<eval>")
    {
        var tokens = new Lexer(source).Tokenize();
        var stmts = new Parser(tokens).ParseProgram();
        var compiler = new BytecodeCompiler();
        return compiler.CompileInternal(stmts, targetDbref, methodName);
    }

    private CompiledMethod CompileInternal(IReadOnlyList<Stmt> stmts, int targetDbref, string methodName)
    {
        _cur = NewFunctionScope(null, methodName, 0);
        foreach (var stmt in stmts)
            CompileStmt(stmt);
        Emit(OpCode.PushNull, 0, 0, 0);
        Emit(OpCode.Return, 0, 0, 0);
        _cur.Fn.GetType(); // keep
        var mainFn = FinalizeScope(_cur);

        return new CompiledMethod
        {
            TargetDbref = targetDbref,
            MethodName = methodName,
            Functions = _functions
        };
    }

    private FunctionScope NewFunctionScope(FunctionScope? parent, string name, int paramCount)
    {
        int idx = _functions.Count;
        var fn = new CompiledFunction { Index = idx, Name = name, ParamCount = paramCount };
        _functions.Add(fn);
        return new FunctionScope { Parent = parent, Fn = fn };
    }

    private CompiledFunction FinalizeScope(FunctionScope scope)
    {
        // Note: LocalCount is tracked on NextLocalSlot; recreate or mutate? Let's use a mutable wrapper or set via reflection?
        // Actually CompiledFunction.LocalCount is init-only in Bytecode.cs; let's replace entry in _functions!
        var updated = new CompiledFunction
        {
            Index = scope.Fn.Index,
            Name = scope.Fn.Name,
            ParamCount = scope.Fn.ParamCount,
            LocalCount = scope.NextLocalSlot,
            Upvalues = scope.Fn.Upvalues,
            Code = scope.Fn.Code,
            Constants = scope.Fn.Constants,
            Handlers = scope.Fn.Handlers
        };
        _functions[scope.Fn.Index] = updated;
        return updated;
    }

    private void BeginScope() => _cur.ScopeDepth++;

    private void EndScope()
    {
        for (int i = _cur.Locals.Count - 1; i >= 0; i--)
        {
            if (_cur.Locals[i].Depth == _cur.ScopeDepth)
                _cur.Locals.RemoveAt(i);
            else
                break;
        }
        _cur.ScopeDepth--;
    }

    private int DeclareLocal(string name, bool isConst, int line)
    {
        for (int i = _cur.Locals.Count - 1; i >= 0; i--)
        {
            if (_cur.Locals[i].Depth < _cur.ScopeDepth) break;
            if (_cur.Locals[i].Name == name)
                throw new ScriptCompileException($"Variable '{name}' is already declared in this scope.", line);
        }
        int slot = _cur.NextLocalSlot++;
        _cur.Locals.Add(new LocalSlot { Name = name, Slot = slot, Depth = _cur.ScopeDepth, IsConst = isConst });
        return slot;
    }

    private (bool Found, bool IsLocal, int Index, bool IsConst) ResolveVariable(FunctionScope scope, string name)
    {
        for (int i = scope.Locals.Count - 1; i >= 0; i--)
        {
            if (scope.Locals[i].Name == name)
                return (true, true, scope.Locals[i].Slot, scope.Locals[i].IsConst);
        }

        if (scope.Parent != null)
        {
            var parentRes = ResolveVariable(scope.Parent, name);
            if (parentRes.Found)
            {
                int uv = AddUpvalue(scope, parentRes.IsLocal, parentRes.Index);
                return (true, false, uv, parentRes.IsConst);
            }
        }
        return (false, false, -1, false);
    }

    private static int AddUpvalue(FunctionScope scope, bool isLocal, int index)
    {
        for (int i = 0; i < scope.Fn.Upvalues.Count; i++)
        {
            if (scope.Fn.Upvalues[i].IsLocal == isLocal && scope.Fn.Upvalues[i].Index == index)
                return i;
        }
        scope.Fn.Upvalues.Add(new UpvalueDesc(isLocal, index));
        return scope.Fn.Upvalues.Count - 1;
    }

    private void CompileStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt b:
                BeginScope();
                foreach (var s in b.Statements) CompileStmt(s);
                EndScope();
                break;

            case ExprStmt e:
                CompileExpr(e.Expression);
                Emit(OpCode.Pop, 0, 0, e.Line);
                break;

            case VarDeclStmt v:
            {
                int slot = DeclareLocal(v.Name, v.IsConst, v.Line);
                if (v.Initializer != null)
                {
                    CompileExpr(v.Initializer);
                    if (v.TypeHint != null)
                        Emit(OpCode.CheckTypeHint, AddConst(ScriptValue.FromString(v.TypeHint)), 0, v.Line);
                }
                else
                {
                    Emit(OpCode.PushNull, 0, 0, v.Line);
                }
                Emit(OpCode.StoreLocal, slot, 0, v.Line);
                Emit(OpCode.Pop, 0, 0, v.Line);
                break;
            }

            case ListDestructureStmt ld:
            {
                CompileExpr(ld.Initializer);
                int tmpSlot = DeclareLocal($"__destr_{_cur.NextLocalSlot}", false, ld.Line);
                Emit(OpCode.StoreLocal, tmpSlot, 0, ld.Line);
                Emit(OpCode.Pop, 0, 0, ld.Line);

                int idx = 0;
                foreach (var (name, restName) in ld.Bindings)
                {
                    if (restName != null)
                    {
                        int rSlot = DeclareLocal(restName, false, ld.Line);
                        Emit(OpCode.LoadLocal, tmpSlot, 0, ld.Line);
                        EmitConst(ScriptValue.FromInt(idx), ld.Line);
                        Emit(OpCode.PushNull, 0, 0, ld.Line);
                        Emit(OpCode.GetSlice, 0, 0, ld.Line);
                        Emit(OpCode.StoreLocal, rSlot, 0, ld.Line);
                        Emit(OpCode.Pop, 0, 0, ld.Line);
                    }
                    else
                    {
                        int slot = DeclareLocal(name, false, ld.Line);
                        Emit(OpCode.LoadLocal, tmpSlot, 0, ld.Line);
                        EmitConst(ScriptValue.FromInt(idx), ld.Line);
                        Emit(OpCode.GetIndex, 0, 0, ld.Line);
                        Emit(OpCode.StoreLocal, slot, 0, ld.Line);
                        Emit(OpCode.Pop, 0, 0, ld.Line);
                        idx++;
                    }
                }
                break;
            }

            case MapDestructureStmt md:
            {
                CompileExpr(md.Initializer);
                int tmpSlot = DeclareLocal($"__mdestr_{_cur.NextLocalSlot}", false, md.Line);
                Emit(OpCode.StoreLocal, tmpSlot, 0, md.Line);
                Emit(OpCode.Pop, 0, 0, md.Line);

                foreach (var key in md.Keys)
                {
                    int slot = DeclareLocal(key, false, md.Line);
                    Emit(OpCode.LoadLocal, tmpSlot, 0, md.Line);
                    EmitConst(ScriptValue.FromString(key), md.Line);
                    Emit(OpCode.GetIndex, 0, 0, md.Line);
                    Emit(OpCode.StoreLocal, slot, 0, md.Line);
                    Emit(OpCode.Pop, 0, 0, md.Line);
                }
                break;
            }

            case IfStmt ifs:
            {
                CompileExpr(ifs.Condition);
                int elseJump = EmitJump(OpCode.JumpIfFalse, ifs.Line);
                Emit(OpCode.Pop, 0, 0, ifs.Line);
                CompileStmt(ifs.ThenBranch);
                int endJump = EmitJump(OpCode.Jump, ifs.Line);
                PatchJump(elseJump);
                Emit(OpCode.Pop, 0, 0, ifs.Line);
                if (ifs.ElseBranch != null)
                    CompileStmt(ifs.ElseBranch);
                PatchJump(endJump);
                break;
            }

            case WhileStmt ws:
            {
                var loop = new LoopContext { ContinueTarget = CurrentIp };
                _cur.Loops.Push(loop);

                int loopStart = CurrentIp;
                CompileExpr(ws.Condition);
                int exitJump = EmitJump(OpCode.JumpIfFalse, ws.Line);
                Emit(OpCode.Pop, 0, 0, ws.Line);
                CompileStmt(ws.Body);
                EmitJumpTo(OpCode.Jump, loopStart, ws.Line);

                PatchJump(exitJump);
                Emit(OpCode.Pop, 0, 0, ws.Line);
                foreach (int bj in loop.BreakJumps) PatchJump(bj);
                foreach (int cj in loop.ContinueJumps) PatchJumpTo(cj, loopStart);
                _cur.Loops.Pop();
                break;
            }

            case ForStmt fs:
            {
                BeginScope();
                if (fs.Initializer != null) CompileStmt(fs.Initializer);

                var loop = new LoopContext();
                _cur.Loops.Push(loop);

                int condIp = CurrentIp;
                int exitJump = -1;
                if (fs.Condition != null)
                {
                    CompileExpr(fs.Condition);
                    exitJump = EmitJump(OpCode.JumpIfFalse, fs.Line);
                    Emit(OpCode.Pop, 0, 0, fs.Line);
                }

                CompileStmt(fs.Body);

                int incIp = CurrentIp;
                foreach (int cj in loop.ContinueJumps) PatchJumpTo(cj, incIp);
                if (fs.Increment != null)
                {
                    CompileExpr(fs.Increment);
                    Emit(OpCode.Pop, 0, 0, fs.Line);
                }
                EmitJumpTo(OpCode.Jump, condIp, fs.Line);

                if (exitJump >= 0)
                {
                    PatchJump(exitJump);
                    Emit(OpCode.Pop, 0, 0, fs.Line);
                }
                foreach (int bj in loop.BreakJumps) PatchJump(bj);
                _cur.Loops.Pop();
                EndScope();
                break;
            }

            case ForeachStmt fe:
            {
                BeginScope();
                CompileExpr(fe.Collection);
                int colSlot = DeclareLocal($"__col_{_cur.NextLocalSlot}", false, fe.Line);
                Emit(OpCode.StoreLocal, colSlot, 0, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);

                // Call __iter_pairs(col) -> list of [key, val] pairs
                Emit(OpCode.LoadLocal, colSlot, 0, fe.Line);
                Emit(OpCode.CallGlobal, AddConst(ScriptValue.FromString("__iter_pairs")), 1, fe.Line);
                int pairsSlot = DeclareLocal($"__pairs_{_cur.NextLocalSlot}", false, fe.Line);
                Emit(OpCode.StoreLocal, pairsSlot, 0, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);

                int idxSlot = DeclareLocal($"__idx_{_cur.NextLocalSlot}", false, fe.Line);
                EmitConst(ScriptValue.FromInt(0), fe.Line);
                Emit(OpCode.StoreLocal, idxSlot, 0, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);

                int? keyUserSlot = fe.IndexVar != null ? DeclareLocal(fe.IndexVar, false, fe.Line) : null;
                int itemUserSlot = DeclareLocal(fe.ItemVar, false, fe.Line);

                var loop = new LoopContext();
                _cur.Loops.Push(loop);

                int condIp = CurrentIp;
                Emit(OpCode.LoadLocal, idxSlot, 0, fe.Line);
                Emit(OpCode.LoadLocal, pairsSlot, 0, fe.Line);
                Emit(OpCode.GetProp, AddConst(ScriptValue.FromString("Length")), 0, fe.Line);
                // Wait: GetProp expects (obj, name) on stack -> emit name const!
                _cur.Fn.Code.RemoveAt(_cur.Fn.Code.Count - 1);
                EmitConst(ScriptValue.FromString("Length"), fe.Line);
                Emit(OpCode.GetProp, 0, 0, fe.Line);
                Emit(OpCode.Lt, 0, 0, fe.Line);
                int exitJump = EmitJump(OpCode.JumpIfFalse, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);

                // pair = pairs[idx]
                Emit(OpCode.LoadLocal, pairsSlot, 0, fe.Line);
                Emit(OpCode.LoadLocal, idxSlot, 0, fe.Line);
                Emit(OpCode.GetIndex, 0, 0, fe.Line);
                if (keyUserSlot.HasValue)
                {
                    Emit(OpCode.Dup, 0, 0, fe.Line);
                    EmitConst(ScriptValue.FromInt(0), fe.Line);
                    Emit(OpCode.GetIndex, 0, 0, fe.Line);
                    Emit(OpCode.StoreLocal, keyUserSlot.Value, 0, fe.Line);
                    Emit(OpCode.Pop, 0, 0, fe.Line);
                }
                EmitConst(ScriptValue.FromInt(1), fe.Line);
                Emit(OpCode.GetIndex, 0, 0, fe.Line);
                Emit(OpCode.StoreLocal, itemUserSlot, 0, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);

                CompileStmt(fe.Body);

                int incIp = CurrentIp;
                foreach (int cj in loop.ContinueJumps) PatchJumpTo(cj, incIp);
                Emit(OpCode.LoadLocal, idxSlot, 0, fe.Line);
                EmitConst(ScriptValue.FromInt(1), fe.Line);
                Emit(OpCode.Add, 0, 0, fe.Line);
                Emit(OpCode.StoreLocal, idxSlot, 0, fe.Line);
                Emit(OpCode.Pop, 0, 0, fe.Line);
                EmitJumpTo(OpCode.Jump, condIp, fe.Line);

                PatchJump(exitJump);
                Emit(OpCode.Pop, 0, 0, fe.Line);
                foreach (int bj in loop.BreakJumps) PatchJump(bj);
                _cur.Loops.Pop();
                EndScope();
                break;
            }

            case SwitchStmt sw:
            {
                BeginScope();
                CompileExpr(sw.Value);
                int valSlot = DeclareLocal($"__sw_{_cur.NextLocalSlot}", false, sw.Line);
                Emit(OpCode.StoreLocal, valSlot, 0, sw.Line);
                Emit(OpCode.Pop, 0, 0, sw.Line);

                var loop = new LoopContext(); // allows break; inside switch
                _cur.Loops.Push(loop);

                var caseBodyJumps = new List<(int JumpIp, IReadOnlyList<Stmt> Body)>();
                foreach (var c in sw.Cases)
                {
                    var matchJumps = new List<int>();
                    foreach (var v in c.Values)
                    {
                        Emit(OpCode.LoadLocal, valSlot, 0, sw.Line);
                        CompileExpr(v);
                        Emit(OpCode.Eq, 0, 0, sw.Line);
                        matchJumps.Add(EmitJump(OpCode.JumpIfTrue, sw.Line));
                        Emit(OpCode.Pop, 0, 0, sw.Line);
                    }
                    int skipCase = EmitJump(OpCode.Jump, sw.Line);
                    foreach (int mj in matchJumps)
                    {
                        PatchJump(mj);
                        Emit(OpCode.Pop, 0, 0, sw.Line);
                    }
                    foreach (var s in c.Body) CompileStmt(s);
                    caseBodyJumps.Add((EmitJump(OpCode.Jump, sw.Line), c.Body));
                    PatchJump(skipCase);
                }

                if (sw.DefaultBody != null)
                {
                    foreach (var s in sw.DefaultBody) CompileStmt(s);
                }

                foreach (var (j, _) in caseBodyJumps) PatchJump(j);
                foreach (int bj in loop.BreakJumps) PatchJump(bj);
                _cur.Loops.Pop();
                EndScope();
                break;
            }

            case BreakStmt brk:
                if (_cur.Loops.Count == 0)
                    throw new ScriptCompileException("'break' used outside of loop or switch.", brk.Line);
                _cur.Loops.Peek().BreakJumps.Add(EmitJump(OpCode.Jump, brk.Line));
                break;

            case ContinueStmt cont:
                if (_cur.Loops.Count == 0)
                    throw new ScriptCompileException("'continue' used outside of loop.", cont.Line);
                _cur.Loops.Peek().ContinueJumps.Add(EmitJump(OpCode.Jump, cont.Line));
                break;

            case ReturnStmt ret:
                if (ret.Value != null) CompileExpr(ret.Value);
                else Emit(OpCode.PushNull, 0, 0, ret.Line);
                Emit(OpCode.Return, 0, 0, ret.Line);
                break;

            case ThrowStmt thr:
                CompileExpr(thr.Value);
                Emit(OpCode.Throw, 0, 0, thr.Line);
                break;

            case TryStmt ts:
                CompileTryStmt(ts);
                break;

            case AfterStmt aft:
            {
                // Compile body as an inner 0-arg function and schedule it
                var innerScope = NewFunctionScope(_cur, $"<after@{aft.Line}>", 0);
                var prev = _cur;
                _cur = innerScope;
                BeginScope();
                foreach (var s in aft.Body.Statements) CompileStmt(s);
                EndScope();
                Emit(OpCode.PushNull, 0, 0, aft.Line);
                Emit(OpCode.Return, 0, 0, aft.Line);
                var innerFn = FinalizeScope(innerScope);
                _cur = prev;

                CompileExpr(aft.DelaySeconds);
                Emit(OpCode.ScheduleAfter, innerFn.Index, 0, aft.Line);
                break;
            }
        }
    }

    private void CompileTryStmt(TryStmt ts)
    {
        int enterIp = Emit(OpCode.EnterTry, -1, -1, ts.Line);
        int tryStart = CurrentIp;
        CompileStmt(ts.TryBlock);
        Emit(OpCode.LeaveTry, 0, 0, ts.Line);
        int afterTryJump = EmitJump(OpCode.Jump, ts.Line);

        int catchIp = ts.Catches.Count > 0 ? CurrentIp : -1;
        var afterCatchJumps = new List<int>();
        if (ts.Catches.Count > 0)
        {
            // Top of stack on catch entry is thrown ScriptValue
            for (int i = 0; i < ts.Catches.Count; i++)
            {
                var cc = ts.Catches[i];
                int nextCatchJump = -1;
                if (cc.ErrorFilter.HasValue)
                {
                    Emit(OpCode.Dup, 0, 0, ts.Line);
                    EmitConst(ScriptValue.FromError(cc.ErrorFilter.Value), ts.Line);
                    Emit(OpCode.Eq, 0, 0, ts.Line);
                    nextCatchJump = EmitJump(OpCode.JumpIfFalse, ts.Line);
                    Emit(OpCode.Pop, 0, 0, ts.Line);
                }

                BeginScope();
                if (cc.VarName != null)
                {
                    int slot = DeclareLocal(cc.VarName, false, ts.Line);
                    Emit(OpCode.StoreLocal, slot, 0, ts.Line);
                }
                Emit(OpCode.Pop, 0, 0, ts.Line);
                CompileStmt(cc.Body);
                EndScope();
                afterCatchJumps.Add(EmitJump(OpCode.Jump, ts.Line));

                if (nextCatchJump >= 0)
                {
                    PatchJump(nextCatchJump);
                    Emit(OpCode.Pop, 0, 0, ts.Line);
                }
            }
            // No catch matched -> rethrow
            Emit(OpCode.Throw, 0, 0, ts.Line);
        }

        PatchJump(afterTryJump);
        foreach (int j in afterCatchJumps) PatchJump(j);

        int finallyIp = ts.FinallyBlock != null ? CurrentIp : -1;
        if (ts.FinallyBlock != null)
        {
            CompileStmt(ts.FinallyBlock);
            Emit(OpCode.EndFinally, 0, 0, ts.Line);
        }

        _cur.Fn.Code[enterIp] = new Instruction(OpCode.EnterTry, catchIp, finallyIp, ts.Line);
        _cur.Fn.Handlers.Add(new TryHandlerEntry(tryStart, catchIp >= 0 ? catchIp : finallyIp, catchIp, finallyIp, 0));
    }

    private void CompileExpr(Expr expr)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                EmitConst(lit.Value, lit.Line);
                break;

            case ContextExpr ctx:
                int cType = ctx.Kind switch
                {
                    TokenKind.This => 0,
                    TokenKind.Caller => 1,
                    TokenKind.Player => 2,
                    TokenKind.Args => 3,
                    _ => 0
                };
                Emit(OpCode.LoadContext, cType, 0, ctx.Line);
                break;

            case IdentifierExpr id:
            {
                var res = ResolveVariable(_cur, id.Name);
                if (res.Found)
                {
                    Emit(res.IsLocal ? OpCode.LoadLocal : OpCode.LoadUpvalue, res.Index, 0, id.Line);
                    break;
                }
                if (BuiltInNamespaces.Contains(id.Name))
                {
                    EmitConst(ScriptValue.FromString($"__ns:{id.Name}"), id.Line);
                    break;
                }
                throw new ScriptCompileException($"Undefined variable '{id.Name}'.", id.Line);
            }

            case ListExpr list:
            {
                bool hasSpread = false;
                foreach (var el in list.Elements)
                    if (el.IsSpread) { hasSpread = true; break; }

                if (!hasSpread)
                {
                    foreach (var el in list.Elements) CompileExpr(el.Expr);
                    Emit(OpCode.MakeList, list.Elements.Count, 0, list.Line);
                }
                else
                {
                    Emit(OpCode.MakeList, 0, 0, list.Line);
                    foreach (var (el, isSpread) in list.Elements)
                    {
                        CompileExpr(el);
                        if (isSpread)
                            Emit(OpCode.ListSpread, 0, 0, list.Line);
                        else
                        {
                            EmitConst(ScriptValue.FromString("Add"), list.Line);
                            // Actually simpler: ListSpread(isSpread ? 1 : 0) appends 1 item or spreads list!
                            _cur.Fn.Code.RemoveAt(_cur.Fn.Code.Count - 1);
                            Emit(OpCode.ListSpread, 1, 0, list.Line);
                        }
                    }
                }
                break;
            }

            case MapExpr map:
                foreach (var (k, v) in map.Entries)
                {
                    CompileExpr(k);
                    CompileExpr(v);
                }
                Emit(OpCode.MakeMap, map.Entries.Count, 0, map.Line);
                break;

            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts) CompileExpr(p);
                Emit(OpCode.InterpJoin, interp.Parts.Count, 0, interp.Line);
                break;

            case UnaryExpr un:
                CompileUnary(un);
                break;

            case BinaryExpr bin:
                CompileBinary(bin);
                break;

            case TypeCheckExpr tc:
                CompileExpr(tc.Value);
                Emit(OpCode.IsType, AddConst(ScriptValue.FromString(tc.TypeName)), 0, tc.Line);
                break;

            case ConditionalExpr cond:
            {
                CompileExpr(cond.Condition);
                int elseJump = EmitJump(OpCode.JumpIfFalse, cond.Line);
                Emit(OpCode.Pop, 0, 0, cond.Line);
                CompileExpr(cond.ThenExpr);
                int endJump = EmitJump(OpCode.Jump, cond.Line);
                PatchJump(elseJump);
                Emit(OpCode.Pop, 0, 0, cond.Line);
                CompileExpr(cond.ElseExpr);
                PatchJump(endJump);
                break;
            }

            case AssignExpr asgn:
                CompileAssign(asgn);
                break;

            case MemberExpr mem:
                CompileExpr(mem.Target);
                if (mem.NullConditional)
                {
                    int skip = EmitJump(OpCode.JumpIfNull, mem.Line);
                    EmitConst(ScriptValue.FromString(mem.Name), mem.Line);
                    Emit(OpCode.GetProp, 0, 0, mem.Line);
                    PatchJump(skip);
                }
                else
                {
                    EmitConst(ScriptValue.FromString(mem.Name), mem.Line);
                    Emit(OpCode.GetProp, 0, 0, mem.Line);
                }
                break;

            case DynamicMemberExpr dmem:
                CompileExpr(dmem.Target);
                if (dmem.NullConditional)
                {
                    int skip = EmitJump(OpCode.JumpIfNull, dmem.Line);
                    CompileExpr(dmem.NameExpr);
                    Emit(OpCode.GetProp, 0, 0, dmem.Line);
                    PatchJump(skip);
                }
                else
                {
                    CompileExpr(dmem.NameExpr);
                    Emit(OpCode.GetProp, 0, 0, dmem.Line);
                }
                break;

            case IndexExpr idx:
                CompileExpr(idx.Target);
                CompileExpr(idx.Index);
                Emit(OpCode.GetIndex, idx.FromEnd ? 1 : 0, 0, idx.Line);
                break;

            case SliceExpr sl:
            {
                CompileExpr(sl.Target);
                if (sl.Start != null) CompileExpr(sl.Start); else Emit(OpCode.PushNull, 0, 0, sl.Line);
                if (sl.End != null) CompileExpr(sl.End); else Emit(OpCode.PushNull, 0, 0, sl.Line);
                int flags = (sl.StartFromEnd ? 1 : 0) | (sl.EndFromEnd ? 2 : 0);
                Emit(OpCode.GetSlice, flags, 0, sl.Line);
                break;
            }

            case CallExpr call:
                CompileCall(call);
                break;

            case LambdaExpr lam:
            {
                var lamScope = NewFunctionScope(_cur, $"<lambda@{lam.Line}>", lam.Parameters.Count);
                var prev = _cur;
                _cur = lamScope;
                BeginScope();
                foreach (var p in lam.Parameters)
                    DeclareLocal(p, false, lam.Line);
                foreach (var s in lam.Body.Statements)
                    CompileStmt(s);
                EndScope();
                Emit(OpCode.PushNull, 0, 0, lam.Line);
                Emit(OpCode.Return, 0, 0, lam.Line);
                var fn = FinalizeScope(lamScope);
                _cur = prev;
                Emit(OpCode.MakeClosure, fn.Index, 0, lam.Line);
                break;
            }

            case TryExpr te:
            {
                int enterIp = Emit(OpCode.EnterTry, -1, -1, te.Line);
                int tryStart = CurrentIp;
                CompileExpr(te.Inner);
                Emit(OpCode.LeaveTry, 0, 0, te.Line);
                int endJump = EmitJump(OpCode.Jump, te.Line);
                int catchIp = CurrentIp;
                Emit(OpCode.Pop, 0, 0, te.Line); // pop error
                if (te.Fallback != null) CompileExpr(te.Fallback);
                else Emit(OpCode.PushNull, 0, 0, te.Line);
                PatchJump(endJump);
                _cur.Fn.Code[enterIp] = new Instruction(OpCode.EnterTry, catchIp, -1, te.Line);
                _cur.Fn.Handlers.Add(new TryHandlerEntry(tryStart, catchIp, catchIp, -1, 0));
                break;
            }

            default:
                throw new ScriptCompileException($"Unsupported expression {expr.GetType().Name}.", expr.Line);
        }
    }

    private void CompileUnary(UnaryExpr un)
    {
        if (un.Op is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var binOp = un.Op == TokenKind.PlusPlus ? TokenKind.Plus : TokenKind.Minus;
            if (!un.IsPostfix)
            {
                CompileAssign(new AssignExpr(un.Operand, un.Op == TokenKind.PlusPlus ? TokenKind.PlusAssign : TokenKind.MinusAssign, new LiteralExpr(ScriptValue.FromInt(1), un.Line), un.Line));
                return;
            }
            // Postfix: load old value, then update, leave old value on stack
            CompileExpr(un.Operand);
            CompileAssign(new AssignExpr(un.Operand, un.Op == TokenKind.PlusPlus ? TokenKind.PlusAssign : TokenKind.MinusAssign, new LiteralExpr(ScriptValue.FromInt(1), un.Line), un.Line));
            Emit(OpCode.Pop, 0, 0, un.Line);
            return;
        }

        CompileExpr(un.Operand);
        Emit(un.Op switch
        {
            TokenKind.Bang => OpCode.Not,
            TokenKind.Tilde => OpCode.BitNot,
            TokenKind.Minus => OpCode.Neg,
            _ => throw new ScriptCompileException($"Unknown unary operator {un.Op}.", un.Line)
        }, 0, 0, un.Line);
    }

    private void CompileBinary(BinaryExpr bin)
    {
        if (bin.Op == TokenKind.AndAnd)
        {
            CompileExpr(bin.Left);
            int j = EmitJump(OpCode.JumpIfFalse, bin.Line);
            Emit(OpCode.Pop, 0, 0, bin.Line);
            CompileExpr(bin.Right);
            PatchJump(j);
            return;
        }
        if (bin.Op == TokenKind.OrOr)
        {
            CompileExpr(bin.Left);
            int j = EmitJump(OpCode.JumpIfTrue, bin.Line);
            Emit(OpCode.Pop, 0, 0, bin.Line);
            CompileExpr(bin.Right);
            PatchJump(j);
            return;
        }
        if (bin.Op == TokenKind.QuestionQuestion)
        {
            CompileExpr(bin.Left);
            int j = EmitJump(OpCode.JumpIfNotNull, bin.Line);
            Emit(OpCode.Pop, 0, 0, bin.Line);
            CompileExpr(bin.Right);
            PatchJump(j);
            return;
        }

        CompileExpr(bin.Left);
        CompileExpr(bin.Right);
        Emit(bin.Op switch
        {
            TokenKind.Plus => OpCode.Add,
            TokenKind.Minus => OpCode.Sub,
            TokenKind.Star => OpCode.Mul,
            TokenKind.Slash => OpCode.Div,
            TokenKind.Percent => OpCode.Mod,
            TokenKind.Power => OpCode.Pow,
            TokenKind.Pipe => OpCode.BitOr,
            TokenKind.Caret => OpCode.BitXor,
            TokenKind.Amp => OpCode.BitAnd,
            TokenKind.Shl => OpCode.Shl,
            TokenKind.Shr => OpCode.Shr,
            TokenKind.EqEq => OpCode.Eq,
            TokenKind.BangEq => OpCode.Neq,
            TokenKind.Lt => OpCode.Lt,
            TokenKind.LtEq => OpCode.Lte,
            TokenKind.Gt => OpCode.Gt,
            TokenKind.GtEq => OpCode.Gte,
            TokenKind.In => OpCode.In,
            _ => throw new ScriptCompileException($"Unknown binary operator {bin.Op}.", bin.Line)
        }, 0, 0, bin.Line);
    }

    private void CompileAssign(AssignExpr asgn)
    {
        switch (asgn.Target)
        {
            case IdentifierExpr id:
            {
                var res = ResolveVariable(_cur, id.Name);
                if (!res.Found)
                    throw new ScriptCompileException($"Undefined variable '{id.Name}'.", asgn.Line);
                if (res.IsConst)
                    throw new ScriptCompileException($"Cannot assign to const '{id.Name}'.", asgn.Line);

                if (asgn.Op == TokenKind.Assign)
                {
                    CompileExpr(asgn.Value);
                }
                else
                {
                    Emit(res.IsLocal ? OpCode.LoadLocal : OpCode.LoadUpvalue, res.Index, 0, asgn.Line);
                    CompileCompoundValue(asgn.Op, asgn.Value, asgn.Line);
                }
                Emit(res.IsLocal ? OpCode.StoreLocal : OpCode.StoreUpvalue, res.Index, 0, asgn.Line);
                break;
            }

            case MemberExpr mem:
            {
                CompileExpr(mem.Target);
                EmitConst(ScriptValue.FromString(mem.Name), asgn.Line);
                if (asgn.Op == TokenKind.Assign)
                {
                    CompileExpr(asgn.Value);
                }
                else
                {
                    CompileExpr(mem);
                    CompileCompoundValue(asgn.Op, asgn.Value, asgn.Line);
                }
                Emit(OpCode.SetProp, 0, 0, asgn.Line);
                break;
            }

            case IndexExpr idx:
            {
                // CoW lvalue update: compute new collection via SetIndex, then assign back to idx.Target!
                CompileExpr(idx.Target);
                CompileExpr(idx.Index);
                if (asgn.Op == TokenKind.Assign)
                {
                    CompileExpr(asgn.Value);
                }
                else
                {
                    CompileExpr(idx);
                    CompileCompoundValue(asgn.Op, asgn.Value, asgn.Line);
                }
                Emit(OpCode.SetIndex, idx.FromEnd ? 1 : 0, 0, asgn.Line);
                StoreBackLvalue(idx.Target, asgn.Line);
                break;
            }

            default:
                throw new ScriptCompileException("Invalid assignment target.", asgn.Line);
        }
    }

    private void StoreBackLvalue(Expr target, int line)
    {
        switch (target)
        {
            case IdentifierExpr id:
            {
                var res = ResolveVariable(_cur, id.Name);
                if (!res.Found || res.IsConst)
                    throw new ScriptCompileException($"Cannot mutate '{id.Name}'.", line);
                Emit(res.IsLocal ? OpCode.StoreLocal : OpCode.StoreUpvalue, res.Index, 0, line);
                break;
            }
            case MemberExpr mem:
            {
                int tmp = DeclareTempSlot();
                Emit(OpCode.StoreLocal, tmp, 0, line);
                Emit(OpCode.Pop, 0, 0, line);
                CompileExpr(mem.Target);
                EmitConst(ScriptValue.FromString(mem.Name), line);
                Emit(OpCode.LoadLocal, tmp, 0, line);
                Emit(OpCode.SetProp, 0, 0, line);
                break;
            }
            default:
                throw new ScriptCompileException("Unsupported CoW lvalue target.", line);
        }
    }

    private int DeclareTempSlot() => _cur.NextLocalSlot++;

    private void CompileCompoundValue(TokenKind op, Expr rhs, int line)
    {
        if (op == TokenKind.QuestionQuestionAssign)
        {
            int j = EmitJump(OpCode.JumpIfNotNull, line);
            Emit(OpCode.Pop, 0, 0, line);
            CompileExpr(rhs);
            PatchJump(j);
            return;
        }
        CompileExpr(rhs);
        Emit(op switch
        {
            TokenKind.PlusAssign => OpCode.Add,
            TokenKind.MinusAssign => OpCode.Sub,
            TokenKind.StarAssign => OpCode.Mul,
            TokenKind.SlashAssign => OpCode.Div,
            TokenKind.PercentAssign => OpCode.Mod,
            _ => throw new ScriptCompileException($"Unsupported compound assignment {op}.", line)
        }, 0, 0, line);
    }

    private void CompileCall(CallExpr call)
    {
        // Pack arguments into a single list if any spread arg exists, otherwise push normally?
        // Simpler & uniform: always compile args; if any IsSpread, build a list via ListSpread and pass argc = -1!
        bool hasSpread = false;
        foreach (var a in call.Arguments)
            if (a.IsSpread) { hasSpread = true; break; }

        if (call.Callee is IdentifierExpr id)
        {
            // Check if local/upvalue variable holds a closure first at compile time
            var res = ResolveVariable(_cur, id.Name);
            if (res.Found)
            {
                Emit(res.IsLocal ? OpCode.LoadLocal : OpCode.LoadUpvalue, res.Index, 0, call.Line);
                int argc = EmitCallArgs(call.Arguments, hasSpread, call.Line);
                Emit(OpCode.CallValue, argc, 0, call.Line);
            }
            else
            {
                int argc = EmitCallArgs(call.Arguments, hasSpread, call.Line);
                Emit(OpCode.CallGlobal, AddConst(ScriptValue.FromString(id.Name)), argc, call.Line);
            }
            return;
        }

        if (call.Callee is MemberExpr mem)
        {
            if (mem.Target is BaseExpr)
            {
                int argc = EmitCallArgs(call.Arguments, hasSpread, call.Line);
                Emit(OpCode.CallBase, AddConst(ScriptValue.FromString(mem.Name)), argc, call.Line);
                return;
            }

            CompileExpr(mem.Target);
            if (mem.NullConditional)
            {
                int skip = EmitJump(OpCode.JumpIfNull, call.Line);
                int argc = EmitCallArgs(call.Arguments, hasSpread, call.Line);
                Emit(OpCode.CallMethod, AddConst(ScriptValue.FromString(mem.Name)), argc, call.Line);
                PatchJump(skip);
                return;
            }

            int argc2 = EmitCallArgs(call.Arguments, hasSpread, call.Line);
            Emit(OpCode.CallMethod, AddConst(ScriptValue.FromString(mem.Name)), argc2, call.Line);

            // Lvalue CoW ergonomics: if calling a mutating collection method on an identifier/member, rebind it!
            if (MutatingCollectionMethods.Contains(mem.Name) && (mem.Target is IdentifierExpr || mem.Target is MemberExpr))
            {
                // CallMethod leaves the mutated collection (or Pop/Remove return? Wait: for CoW mutators,
                // let's have CallMethod push (newCollection, returnValue) or handle it cleanly!
                // Even cleaner: for Pop(), user expects the popped element, while Add/Remove/Insert/Clear/Sort/Reverse return the new collection!
                if (mem.Name != "Pop")
                    StoreBackLvalue(mem.Target, call.Line);
            }
            return;
        }

        CompileExpr(call.Callee);
        int argcVal = EmitCallArgs(call.Arguments, hasSpread, call.Line);
        Emit(OpCode.CallValue, argcVal, 0, call.Line);
    }

    private int EmitCallArgs(IReadOnlyList<(Expr Expr, bool IsSpread)> args, bool hasSpread, int line)
    {
        if (!hasSpread)
        {
            foreach (var (e, _) in args) CompileExpr(e);
            return args.Count;
        }

        Emit(OpCode.MakeList, 0, 0, line);
        foreach (var (e, isSpread) in args)
        {
            CompileExpr(e);
            Emit(OpCode.ListSpread, isSpread ? 0 : 1, 0, line);
        }
        return -1; // sentinel: top of stack is an arg list
    }

    private int AddConst(ScriptValue val)
    {
        for (int i = 0; i < _cur.Fn.Constants.Count; i++)
            if (_cur.Fn.Constants[i].Equals(val)) return i;
        _cur.Fn.Constants.Add(val);
        return _cur.Fn.Constants.Count - 1;
    }

    private void EmitConst(ScriptValue val, int line) => Emit(OpCode.PushConst, AddConst(val), 0, line);
    private int Emit(OpCode op, int a, int b, int line)
    {
        _cur.Fn.Code.Add(new Instruction(op, a, b, line));
        return _cur.Fn.Code.Count - 1;
    }
    private int CurrentIp => _cur.Fn.Code.Count;
    private int EmitJump(OpCode op, int line) => Emit(op, -1, 0, line);
    private void EmitJumpTo(OpCode op, int target, int line) => Emit(op, target, 0, line);
    private void PatchJump(int jumpIp)
    {
        var ins = _cur.Fn.Code[jumpIp];
        _cur.Fn.Code[jumpIp] = ins with { A = CurrentIp };
    }
    private void PatchJumpTo(int jumpIp, int target)
    {
        var ins = _cur.Fn.Code[jumpIp];
        _cur.Fn.Code[jumpIp] = ins with { A = target };
    }
}
