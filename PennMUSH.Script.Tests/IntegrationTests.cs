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
