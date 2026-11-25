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
    private readonly object _lock = new();

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

            CREATE TABLE IF NOT EXISTS script_functions (
                func_name TEXT PRIMARY KEY COLLATE NOCASE,
                object_id INTEGER NOT NULL,
                method_name TEXT NOT NULL,
                created_by INTEGER NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveMethod(int objectId, string name, AccessModifier access, string source, CompiledMethod bytecode)
    {
        lock (_lock)
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
    }

    public bool SetAccess(int objectId, string name, AccessModifier access)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE scripts SET access = $access, updated_at = $ts WHERE object_id = $id AND name = $name;";
            cmd.Parameters.AddWithValue("$id", objectId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$access", (int)access);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public StoredScriptRecord? GetMethod(int objectId, string name)
    {
        lock (_lock)
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
    }

    public IReadOnlyList<(string Name, AccessModifier Access)> ListMethods(int objectId)
    {
        lock (_lock)
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
    }

    public bool DeleteMethod(int objectId, string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM scripts WHERE object_id = $id AND name = $name;";
            cmd.Parameters.AddWithValue("$id", objectId);
            cmd.Parameters.AddWithValue("$name", name);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void SaveSuspendedTask(SuspendedTaskState state)
    {
        lock (_lock)
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
    }

    public IReadOnlyList<SuspendedTaskState> LoadSuspendedTasks()
    {
        lock (_lock)
        {
            var list = new List<SuspendedTaskState>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT state_json FROM suspended_tasks ORDER BY resume_at_ms;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(SuspendedTaskState.FromJson(r.GetString(0)));
            return list;
        }
    }

    public void DeleteSuspendedTask(string id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM suspended_tasks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<string> DeleteObjectData(int objectId)
    {
        lock (_lock)
        {
            var removedFuncs = new List<string>();
            using (var sel = _conn.CreateCommand())
            {
                sel.CommandText = "SELECT func_name FROM script_functions WHERE object_id = $id;";
                sel.Parameters.AddWithValue("$id", objectId);
                using var r = sel.ExecuteReader();
                while (r.Read())
                    removedFuncs.Add(r.GetString(0));
            }
            using (var delFn = _conn.CreateCommand())
            {
                delFn.CommandText = "DELETE FROM script_functions WHERE object_id = $id;";
                delFn.Parameters.AddWithValue("$id", objectId);
                delFn.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM scripts WHERE object_id = $id;";
                cmd.Parameters.AddWithValue("$id", objectId);
                cmd.ExecuteNonQuery();
            }
            return removedFuncs;
        }
    }

    public void SaveBoundFunction(string funcName, int objectId, string methodName, int createdBy)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO script_functions (func_name, object_id, method_name, created_by)
                VALUES ($fn, $id, $method, $by)
                ON CONFLICT(func_name) DO UPDATE SET
                    object_id = excluded.object_id,
                    method_name = excluded.method_name,
                    created_by = excluded.created_by;
            ";
            cmd.Parameters.AddWithValue("$fn", funcName.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$id", objectId);
            cmd.Parameters.AddWithValue("$method", methodName);
            cmd.Parameters.AddWithValue("$by", createdBy);
            cmd.ExecuteNonQuery();
        }
    }

    public bool DeleteBoundFunction(string funcName)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM script_functions WHERE func_name = $fn;";
            cmd.Parameters.AddWithValue("$fn", funcName);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<(string FuncName, int ObjectId, string MethodName)> ListBoundFunctions()
    {
        lock (_lock)
        {
            var list = new List<(string, int, string)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT func_name, object_id, method_name FROM script_functions ORDER BY func_name;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt32(1), r.GetString(2)));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _conn.Dispose();
        }
    }
}
