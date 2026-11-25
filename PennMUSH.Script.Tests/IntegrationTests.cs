namespace PennMUSH.Script.Tests;

using System;
using System.IO;
using PennMUSH.Core.Attributes;
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

                // Typed attribute via custom AttributeFlags
                childObj.SetAttribute("HP", "75", god.Id);
                childObj.AddAttributeCustomFlag("HP", AttributeMarshal.FlagInt);

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
}
