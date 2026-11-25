namespace PennMUSH.Script.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using PennMUSH.Script.Compiler;
using PennMUSH.Script.Runtime;
using PennMUSH.Script.Vm;

public sealed record StoredScriptRecord(
    int ObjectId,
    string Name,
    AccessModifier Access,
    string Source,
    CompiledMethod Bytecode,
    long UpdatedAt);

public sealed class ScriptStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public ScriptStore(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _conn.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS scripts (
                object_id INTEGER NOT NULL,
                name TEXT NOT NULL COLLATE NOCASE,
                access INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL,
                bytecode TEXT NOT NULL,
                compiler_version INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY (object_id, name)
            );

            CREATE TABLE IF NOT EXISTS suspended_tasks (
                id TEXT PRIMARY KEY,
                resume_at_ms INTEGER NOT NULL,
                state_json TEXT NOT NULL,
                compiler_version INTEGER NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveMethod(int objectId, string name, AccessModifier access, string source, CompiledMethod bytecode)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scripts (object_id, name, access, source, bytecode, compiler_version, updated_at)
            VALUES ($id, $name, $access, $src, $bc, $ver, $ts)
            ON CONFLICT(object_id, name) DO UPDATE SET
                access = excluded.access,
                source = excluded.source,
                bytecode = excluded.bytecode,
                compiler_version = excluded.compiler_version,
                updated_at = excluded.updated_at;
        ";
        cmd.Parameters.AddWithValue("$id", objectId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$access", (int)access);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$bc", bytecode.ToJson());
        cmd.Parameters.AddWithValue("$ver", CompiledMethod.CurrentCompilerVersion);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public bool SetAccess(int objectId, string name, AccessModifier access)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE scripts SET access = $access, updated_at = $ts WHERE object_id = $id AND name = $name;";
        cmd.Parameters.AddWithValue("$id", objectId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$access", (int)access);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery() > 0;
    }

    public StoredScriptRecord? GetMethod(int objectId, string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name, access, source, bytecode, compiler_version, updated_at FROM scripts WHERE object_id = $id AND name = $name;";
        cmd.Parameters.AddWithValue("$id", objectId);
        cmd.Parameters.AddWithValue("$name", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        string actualName = r.GetString(0);
        var access = (AccessModifier)r.GetInt32(1);
        string source = r.GetString(2);
        string bcJson = r.GetString(3);
        int ver = r.GetInt32(4);
        long ts = r.GetInt64(5);

        CompiledMethod compiled;
        if (ver != CompiledMethod.CurrentCompilerVersion)
        {
            compiled = BytecodeCompiler.Compile(source, objectId, actualName);
            SaveMethod(objectId, actualName, access, source, compiled);
        }
        else
        {
            compiled = CompiledMethod.FromJson(bcJson);
        }

        return new StoredScriptRecord(objectId, actualName, access, source, compiled, ts);
    }

    public IReadOnlyList<(string Name, AccessModifier Access)> ListMethods(int objectId)
    {
        var list = new List<(string, AccessModifier)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name, access FROM scripts WHERE object_id = $id ORDER BY name;";
        cmd.Parameters.AddWithValue("$id", objectId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), (AccessModifier)r.GetInt32(1)));
        return list;
    }

    public bool DeleteMethod(int objectId, string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scripts WHERE object_id = $id AND name = $name;";
        cmd.Parameters.AddWithValue("$id", objectId);
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SaveSuspendedTask(SuspendedTaskState state)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO suspended_tasks (id, resume_at_ms, state_json, compiler_version)
            VALUES ($id, $resume, $json, $ver)
            ON CONFLICT(id) DO UPDATE SET
                resume_at_ms = excluded.resume_at_ms,
                state_json = excluded.state_json,
                compiler_version = excluded.compiler_version;
        ";
        cmd.Parameters.AddWithValue("$id", state.TaskId);
        cmd.Parameters.AddWithValue("$resume", state.ResumeAtMs);
        cmd.Parameters.AddWithValue("$json", state.ToJson());
        cmd.Parameters.AddWithValue("$ver", state.CompilerVersion);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SuspendedTaskState> LoadSuspendedTasks()
    {
        var list = new List<SuspendedTaskState>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT state_json FROM suspended_tasks ORDER BY resume_at_ms;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(SuspendedTaskState.FromJson(r.GetString(0)));
        return list;
    }

    public void DeleteSuspendedTask(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM suspended_tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteObjectData(int objectId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scripts WHERE object_id = $id;";
        cmd.Parameters.AddWithValue("$id", objectId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
