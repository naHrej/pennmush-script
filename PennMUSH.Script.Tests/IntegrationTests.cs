namespace PennMUSH.Script.Tests;

using System;
using System.IO;
using PennMUSH.Core.Attributes;
using PennMUSH.Core.Commands;
using PennMUSH.Core.MUSHcode;
using PennMUSH.Core.Types;
using PennMUSH.Script.Host;
using PennMUSH.Server.Engine;
using PennMUSH.Server.Extensions;
using Xunit;

public class IntegrationTests
{
    [Fact]
    public void ProgramCommand_ScriptFunction_TypedAttributes_And_Inheritance_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pennmush_script_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "mush.db");

        try
        {
            var server = new MUSHServer(dbPath);
            var hostProxy = new ExtensionHostProxy(server.Extensions, server, "PennMUSH.Script");
            var ext = new ScriptExtension();
            ext.Initialize(hostProxy);

            try
            {
                var db = server.Database;
                var god = db.CreateObject("One", ObjectType.Player, Dbref.Nothing);
                god.Owner = god.Id;
                god.AddFlag("WIZARD");

                var parentObj = db.CreateObject("Parent", ObjectType.Thing, god.Id);
                var childObj = db.CreateObject("Child", ObjectType.Thing, god.Id);
                childObj.AddParent(parentObj.Id);

                // Typed attribute via @prop command (auto-sets script_int flag)
                var propCtx = server.Dispatcher.Dispatch(god.Id, $"@prop {childObj.Id}/HP=int:75", db);
                Assert.Contains(propCtx.Notifications, n => n.Contains(AttributeMarshal.FlagInt, StringComparison.Ordinal));
                Assert.True(childObj.GetAttribute("HP")!.HasCustomFlag(AttributeMarshal.FlagInt));

                // Program Parent/Greet via @prog
                var progParent = server.Dispatcher.Dispatch(god.Id, $"@prog {parentObj.Id}/Greet=public", db);
                Assert.Contains(progParent.Notifications, n => n.Contains("End with '.'", StringComparison.Ordinal));
                Assert.True(server.TryHandleCapturedInput(god.Id, "return $\"Hello {args[0]}\";"));
                Assert.True(server.TryHandleCapturedInput(god.Id, "."));
                Assert.False(server.TryHandleCapturedInput(god.Id, "released"));

                // Program Child/Greet overriding Parent/Greet with base.Greet() + typed property + Mush.Eval
                server.Dispatcher.Dispatch(god.Id, $"@prog {childObj.Id}/Greet=public", db);
                Assert.True(server.TryHandleCapturedInput(god.Id, "this.HP = this.HP + 25;"));
                Assert.True(server.TryHandleCapturedInput(god.Id, "var sum = Mush.Eval(\"add(2, 3)\");"));
                Assert.True(server.TryHandleCapturedInput(god.Id, "return $\"{base.Greet(args[0])}! HP={this.HP} SUM={sum}\";"));
                Assert.True(server.TryHandleCapturedInput(god.Id, "."));
                Assert.False(server.TryHandleCapturedInput(god.Id, "released"));

                // Invoke from PennMUSH softcode
                var evalCtx = new EvaluationContext(god.Id, god.Id);
                string output = server.Evaluator.Evaluate($"[script({childObj.Id}/Greet, Traveler)]", evalCtx);
                Assert.Equal("Hello Traveler! HP=100 SUM=5", output);
                Assert.Equal("100", childObj.GetAttribute("HP")!.Value);
                Assert.True(childObj.GetAttribute("HP")!.HasCustomFlag(AttributeMarshal.FlagInt));

                // Malformed typed attribute raises E_TYPE
                childObj.SetAttribute("HP", "not-an-int", god.Id);
                string badType = server.Evaluator.Evaluate($"[script({childObj.Id}/Greet, Traveler)]", evalCtx);
                Assert.Contains("E_TYPE", badType, StringComparison.Ordinal);

                // Object destruction cleans up ScriptStore
                Assert.True(db.DestroyObject(childObj.Id));
                Assert.Empty(ext.Store.ListMethods(childObj.Id.Value));
            }
            finally
            {
                hostProxy.Cleanup();
                ext.Shutdown();
                server.Storage.Dispose();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DynamicPlugin_LoadAndUnload_WorksCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pennmush_script_alc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "mush.db");
        string dllPath = Path.Combine(AppContext.BaseDirectory, "PennMUSH.Script.dll");
        Assert.True(File.Exists(dllPath), $"Expected {dllPath} to exist.");

        try
        {
            var server = new MUSHServer(dbPath);
            try
            {
                var record = server.Extensions.LoadExtension(dllPath);
                Assert.NotNull(record);
                Assert.True(server.Extensions.IsLoaded("PennMUSH.Script"));

                Assert.Equal(CommandLookupStatus.Found, server.Dispatcher.Table.Lookup("@script").Status);
                Assert.Equal(CommandLookupStatus.Found, server.Dispatcher.Table.Lookup("@program").Status);
                Assert.Equal(CommandLookupStatus.Found, server.Dispatcher.Table.Lookup("@prog").Status);
                Assert.Equal(CommandLookupStatus.Found, server.Dispatcher.Table.Lookup("@prop").Status);
                Assert.True(server.Functions.HasFunction("SCRIPT"));
                Assert.True(StandardAttributes.IsCustomFlag(AttributeMarshal.FlagInt));

                bool unloaded = server.Extensions.UnloadExtension("PennMUSH.Script");
                Assert.True(unloaded);
                Assert.False(server.Extensions.IsLoaded("PennMUSH.Script"));
                Assert.Equal(CommandLookupStatus.NotFound, server.Dispatcher.Table.Lookup("@script").Status);
                Assert.Equal(CommandLookupStatus.NotFound, server.Dispatcher.Table.Lookup("@prog").Status);
                Assert.Equal(CommandLookupStatus.NotFound, server.Dispatcher.Table.Lookup("@prop").Status);
                Assert.False(server.Functions.HasFunction("SCRIPT"));
                Assert.False(StandardAttributes.IsCustomFlag(AttributeMarshal.FlagInt));
            }
            finally
            {
                server.Storage.Dispose();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AccessModifiers_EnforceHostPermissions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pennmush_script_perm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "mush.db");

        try
        {
            var server = new MUSHServer(dbPath);
            var hostProxy = new ExtensionHostProxy(server.Extensions, server, "PennMUSH.Script");
            var ext = new ScriptExtension();
            ext.Initialize(hostProxy);

            try
            {
                var db = server.Database;
                var wiz = db.CreateObject("Wiz", ObjectType.Player, Dbref.Nothing);
                wiz.Owner = wiz.Id;
                wiz.AddFlag("WIZARD");

                var mortal1 = db.CreateObject("Mortal1", ObjectType.Player, wiz.Id);
                mortal1.Owner = mortal1.Id;
                var mortal2 = db.CreateObject("Mortal2", ObjectType.Player, wiz.Id);
                mortal2.Owner = mortal2.Id;

                var hostObj = db.CreateObject("HostObj", ObjectType.Thing, mortal1.Id);
                var sameOwnerObj = db.CreateObject("SameOwner", ObjectType.Thing, mortal1.Id);

                // private (default): only hostObj itself
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/Secret=private", db);
                server.TryHandleCapturedInput(mortal1.Id, "return 1;");
                server.TryHandleCapturedInput(mortal1.Id, ".");

                // internal: same owner
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/Team=internal", db);
                server.TryHandleCapturedInput(mortal1.Id, "return 2;");
                server.TryHandleCapturedInput(mortal1.Id, ".");

                // protected: caller >= host perms (wiz >= mortal1)
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/Prot=protected", db);
                server.TryHandleCapturedInput(mortal1.Id, "return 3;");
                server.TryHandleCapturedInput(mortal1.Id, ".");

                Assert.Equal("1", server.Evaluator.Evaluate($"[script({hostObj.Id}/Secret)]", new EvaluationContext(hostObj.Id, hostObj.Id)));
                Assert.Contains("PERMISSION DENIED", server.Evaluator.Evaluate($"[script({hostObj.Id}/Secret)]", new EvaluationContext(sameOwnerObj.Id, sameOwnerObj.Id)));

                Assert.Equal("2", server.Evaluator.Evaluate($"[script({hostObj.Id}/Team)]", new EvaluationContext(sameOwnerObj.Id, sameOwnerObj.Id)));
                Assert.Contains("PERMISSION DENIED", server.Evaluator.Evaluate($"[script({hostObj.Id}/Team)]", new EvaluationContext(mortal2.Id, mortal2.Id)));

                Assert.Equal("3", server.Evaluator.Evaluate($"[script({hostObj.Id}/Prot)]", new EvaluationContext(wiz.Id, wiz.Id)));

                // Named parameters via @prog obj/Method(a, b)=public + after (0.05) { ... }
                server.Dispatcher.Dispatch(mortal1.Id, $"@prop {hostObj.Id}/HP=int:10", db);
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/AddAndDelay(a, b)=public", db);
                server.TryHandleCapturedInput(mortal1.Id, "after (0.05) { this.HP = 555; }");
                server.TryHandleCapturedInput(mortal1.Id, "return ToInt(a) + ToInt(b);");
                server.TryHandleCapturedInput(mortal1.Id, ".");

                string sumOut = server.Evaluator.Evaluate($"[script({hostObj.Id}/AddAndDelay, 12, 30)]", new EvaluationContext(wiz.Id, wiz.Id));
                Assert.Equal("42", sumOut);

                // Wait briefly for after() timer to fire
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (hostObj.GetAttribute("HP")!.Value != "555" && DateTime.UtcNow < deadline)
                    System.Threading.Thread.Sleep(25);
                Assert.Equal("555", hostObj.GetAttribute("HP")!.Value);

                // @script/ps and @script/kill + Obj.Match + String.PadLeft
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/LongWait=public", db);
                server.TryHandleCapturedInput(mortal1.Id, "after (30) { this.HP = 1; }");
                server.TryHandleCapturedInput(mortal1.Id, "return Obj.Match(\"Wiz\").Name.PadLeft(5, \"_\");");
                server.TryHandleCapturedInput(mortal1.Id, ".");

                string matchOut = server.Evaluator.Evaluate($"[script({hostObj.Id}/LongWait)]", new EvaluationContext(wiz.Id, wiz.Id));
                Assert.Equal("__Wiz", matchOut);

                var psCtx = server.Dispatcher.Dispatch(wiz.Id, "@script/ps", db);
                Assert.Contains(psCtx.Notifications, n => n.Contains("LongWait", StringComparison.Ordinal));
                string taskId = psCtx.Notifications[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

                var killCtx = server.Dispatcher.Dispatch(wiz.Id, $"@script/kill {taskId}", db);
                Assert.Contains(killCtx.Notifications, n => n.Contains("Canceled", StringComparison.Ordinal));
                Assert.Empty(ext.Store.LoadSuspendedTasks());

                // @script/eval (Wizard-only)
                var evalCmd = server.Dispatcher.Dispatch(wiz.Id, "@script/eval [1, 2, 3].Map(x => x * 10)", db);
                Assert.Contains(evalCmd.Notifications, n => n == "=> [10, 20, 30]");

                var deniedEval = server.Dispatcher.Dispatch(mortal1.Id, "@script/eval 1 + 1", db);
                Assert.Contains("Permission denied.", deniedEval.Notifications);

                // Nested CoW mutation directly on typed map/list attributes
                server.Dispatcher.Dispatch(mortal1.Id, $"@prop {hostObj.Id}/Stats=map:{{\"hp\":50}}", db);
                server.Dispatcher.Dispatch(mortal1.Id, $"@prog {hostObj.Id}/Boost=public", db);
                server.TryHandleCapturedInput(mortal1.Id, "this.Stats[\"hp\"] = 120;");
                server.TryHandleCapturedInput(mortal1.Id, "return this.Stats[\"hp\"];");
                server.TryHandleCapturedInput(mortal1.Id, ".");
                Assert.Equal("120", server.Evaluator.Evaluate($"[script({hostObj.Id}/Boost)]", new EvaluationContext(wiz.Id, wiz.Id)));

                // Quickstart $command flow on player
                server.Dispatcher.Dispatch(wiz.Id, "@prop me/HP=int:100", db);
                server.Dispatcher.Dispatch(wiz.Id, "@prog me/Heal(targetName, amountStr)=public", db);
                server.TryHandleCapturedInput(wiz.Id, "var target = Obj.Match(targetName);");
                server.TryHandleCapturedInput(wiz.Id, "var amount = ToInt(amountStr);");
                server.TryHandleCapturedInput(wiz.Id, "target.HP += amount;");
                server.TryHandleCapturedInput(wiz.Id, "return $\"{target.Name} healed +{amount} -> {target.HP} HP\";");
                server.TryHandleCapturedInput(wiz.Id, ".");
                server.Dispatcher.Dispatch(wiz.Id, "&CMD_HEAL me=$+heal * *:@pemit %#=[script(me/Heal, %0, %1)]", db);

                var healCmd = server.Dispatcher.Dispatch(wiz.Id, "+heal me 25", db);
                Assert.Single(healCmd.TargetedNotifications);
                Assert.Equal("Wiz healed +25 -> 125 HP", healCmd.TargetedNotifications[0].Message);

                // @script/func, @script/funcs, @script/unfunc + builtin protection + DestroyObject cleanup
                var builtinDeny = server.Dispatcher.Dispatch(wiz.Id, $"@script/func ADD={hostObj.Id}/Boost", db);
                Assert.Contains(builtinDeny.Notifications, n => n.Contains("Cannot override built-in", StringComparison.Ordinal));

                var funcCtx = server.Dispatcher.Dispatch(wiz.Id, $"@script/func heal_me={wiz.Id}/Heal", db);
                Assert.Contains(funcCtx.Notifications, n => n.Contains("Registered function HEAL_ME()", StringComparison.Ordinal));
                Assert.Equal("Wiz healed +10 -> 135 HP", server.Evaluator.Evaluate("[heal_me(me, 10)]", new EvaluationContext(wiz.Id, wiz.Id)));

                var funcsCtx = server.Dispatcher.Dispatch(wiz.Id, "@script/funcs", db);
                Assert.Contains(funcsCtx.Notifications, n => n.Contains("HEAL_ME()", StringComparison.Ordinal));

                var unfuncCtx = server.Dispatcher.Dispatch(wiz.Id, "@script/unfunc heal_me", db);
                Assert.Contains(unfuncCtx.Notifications, n => n.Contains("Unregistered function HEAL_ME()", StringComparison.Ordinal));
                Assert.False(server.Functions.HasFunction("HEAL_ME"));

                // Binding on destroyed object is automatically unregistered
                server.Dispatcher.Dispatch(wiz.Id, $"@script/func boost_fn={hostObj.Id}/Boost", db);
                Assert.True(server.Functions.HasFunction("BOOST_FN"));
                Assert.True(db.DestroyObject(hostObj.Id));
                Assert.False(server.Functions.HasFunction("BOOST_FN"));
            }
            finally
            {
                hostProxy.Cleanup();
                ext.Shutdown();
                server.Storage.Dispose();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
