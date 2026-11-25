namespace PennMUSH.Script.Tests;

using System;
using System.Collections.Generic;
using PennMUSH.Script.Compiler;
using PennMUSH.Script.Runtime;
using PennMUSH.Script.Vm;
using Xunit;

public class LanguageAndVmTests
{
    private static VmExecutionResult Run(string code, IScriptHostBridge? bridge = null, int maxTicks = 50_000, IReadOnlyList<ScriptValue>? args = null)
    {
        var compiled = BytecodeCompiler.Compile(code, 1, "Main");
        var vm = new ScriptVm(bridge ?? new NullBridge(), maxTicks);
        return vm.Execute(compiled, 1, 1, 1, args);
    }

    [Fact]
    public void Expressions_Interpolation_And_Slicing_Work()
    {
        var res = Run("""
            var name = "Alice";
            var nums = [10, 20, 30, 40, 50];
            var slice = nums[1..4];
            var last = nums[^1];
            return $"{name}:{slice.Length}:{last}:{nums[0]}";
            """);

        Assert.Equal(VmExecutionStatus.Completed, res.Status);
        Assert.Equal("Alice:3:50:10", res.ReturnValue.AsString());
    }

    [Fact]
    public void CopyOnWrite_LvalueMutations_DoNotAliasCaller()
    {
        var bridge = new MethodBridge();
        bridge.Methods[(2, "Mutate")] = (AccessModifier.Public, BytecodeCompiler.Compile("""
            var list = args[0];
            list[0] = 999;
            list.Add(4);
            return list;
            """, 2, "Mutate"));

        var res = Run("""
            var orig = [1, 2, 3];
            var mutated = #2.Mutate(orig);
            return [orig[0], orig.Length, mutated[0], mutated.Length];
            """, bridge);

        Assert.Equal(VmExecutionStatus.Completed, res.Status);
        var vals = res.ReturnValue.AsList();
        Assert.Equal(1, vals[0].AsInt());
        Assert.Equal(3, vals[1].AsInt());
        Assert.Equal(999, vals[2].AsInt());
        Assert.Equal(4, vals[3].AsInt());
    }

    [Fact]
    public void Destructuring_Foreach_Switch_And_Lambdas_Work()
    {
        var res = Run("""
            var [a, b, ...rest] = [1, 2, 3, 4];
            var { hp, mp } = { "hp": 100, "mp": 50 };
            var doubled = rest.Map(x => x * 2).Filter(x => x >= 6);
            var sum = 0;
            foreach (var (i, v) in doubled) {
                sum += i + v;
            }
            switch (a + b) {
                case 3:
                    return hp + mp + sum;
                default:
                    return -1;
            }
            """);

        Assert.Equal(VmExecutionStatus.Completed, res.Status);
        // doubled = [6, 8]; sum = (0+6) + (1+8) = 15; 100 + 50 + 15 = 165
        Assert.Equal(165, res.ReturnValue.AsInt());
    }

    [Fact]
    public void TryCatch_InlineTry_And_TickQuota_Work()
    {
        var res = Run("""
            var safe = try (10 / 0) ?? 42;
            var caught = "";
            try {
                throw E_PERM;
            } catch (E_PERM e) {
                caught = ToStr(e);
            }
            return $"{safe}:{caught}";
            """);

        Assert.Equal(VmExecutionStatus.Completed, res.Status);
        Assert.Equal("42:E_PERM", res.ReturnValue.AsString());

        var quota = Run("while (true) { }", maxTicks: 100);
        Assert.Equal(VmExecutionStatus.Faulted, quota.Status);
        Assert.Equal(ScriptErrorCode.E_QUOTA, quota.ErrorCode);
    }

    [Fact]
    public void Sleep_Produces_SerializableState_And_ResumesCleanly()
    {
        var compiled = BytecodeCompiler.Compile("""
            var x = 21;
            Sleep(0.01);
            return x * 2;
            """, 1, "Sleeper");
        var bridge = new MethodBridge();
        bridge.Methods[(1, "Sleeper")] = (AccessModifier.Public, compiled);

        var vm1 = new ScriptVm(bridge);
        var suspended = vm1.Execute(compiled, 1, 1, 1);
        Assert.Equal(VmExecutionStatus.Suspended, suspended.Status);
        Assert.NotNull(suspended.SuspendedState);

        string json = suspended.SuspendedState!.ToJson();
        var rehydrated = SuspendedTaskState.FromJson(json);

        var vm2 = new ScriptVm(bridge);
        var resumed = vm2.Resume(rehydrated);
        Assert.Equal(VmExecutionStatus.Completed, resumed.Status);
        Assert.Equal(42, resumed.ReturnValue.AsInt());
    }

    [Fact]
    public void ListHelpers_MapMerge_And_StringIndex_Work()
    {
        var res = Run("""
            var nums = [1, 2, 3, 4];
            var total = nums.Reduce(0, (a, b) => a + b);
            var hasEven = nums.Any(x => x % 2 == 0);
            var allPos = nums.All(x => x > 0);
            var firstBig = nums.Find(x => x > 2);
            var merged = { "a": 1 }.Merge({ "b": 2 });
            var idx = "hello world".IndexOf("world");
            return $"{total}:{hasEven}:{allPos}:{firstBig}:{merged.Get("b", 0)}:{idx}";
            """);

        Assert.Equal(VmExecutionStatus.Completed, res.Status);
        Assert.Equal("10:true:true:3:2:6", res.ReturnValue.AsString());
    }

    private class NullBridge : IScriptHostBridge
    {
        public virtual ResolvedMethod? ResolveMethod(int targetDbref, string methodName, bool startAtParent = false) => null;
        public virtual CompiledMethod? GetCompiledMethod(int definingDbref, string methodName) => null;
        public virtual bool CanCallMethod(int hostDbref, int callerDbref, AccessModifier access) => true;
        public ScriptValue GetProperty(int targetDbref, string propName, int callerDbref) => ScriptValue.Null;
        public void SetProperty(int targetDbref, string propName, ScriptValue value, int callerDbref) { }
        public bool IsValidObject(int dbref) => true;
        public int GetParent(int dbref) => -1;
        public IReadOnlyList<int> GetChildren(int dbref) => Array.Empty<int>();
        public IReadOnlyList<int> GetContents(int dbref) => Array.Empty<int>();
        public int GetLocation(int dbref) => 0;
        public int GetOwner(int dbref) => 1;
        public string GetObjectName(int dbref) => $"#{dbref}";
        public IReadOnlyList<int> GetPlayers() => Array.Empty<int>();
        public IReadOnlyList<int> GetConnectedPlayers() => Array.Empty<int>();
        public string EvalSoftcode(int executorDbref, int enactorDbref, string expr) => "";
        public string CallSoftcodeAttr(int executorDbref, int enactorDbref, int objDbref, string attrName, IReadOnlyList<ScriptValue> args) => "";
        public string GetSoftcodeAttr(int executorDbref, int objDbref, string attrName) => "";
        public void SetSoftcodeAttr(int executorDbref, int objDbref, string attrName, string value) { }
        public void DispatchCommand(int executorDbref, string command) { }
        public void NotifyPlayer(int playerDbref, string message) { }
        public void Log(string message) { }
        public void ScheduleAfterTask(SuspendedTaskState taskState) { }
    }

    private sealed class MethodBridge : NullBridge
    {
        public Dictionary<(int, string), (AccessModifier, CompiledMethod)> Methods { get; } = new();
        public override ResolvedMethod? ResolveMethod(int targetDbref, string methodName, bool startAtParent = false)
            => Methods.TryGetValue((targetDbref, methodName), out var m) ? new ResolvedMethod(targetDbref, m.Item1, m.Item2) : null;
        public override CompiledMethod? GetCompiledMethod(int definingDbref, string methodName)
            => Methods.TryGetValue((definingDbref, methodName), out var m) ? m.Item2 : null;
    }
}
