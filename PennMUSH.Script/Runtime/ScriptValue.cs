namespace PennMUSH.Script.Runtime;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using PennMUSH.Core.Types;

public enum ScriptValueType
{
    Null = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    String = 4,
    Obj = 5,
    List = 6,
    Map = 7,
    Error = 8,
    Func = 9
}

/// <summary>
/// Serializable closure reference (never holds live CLR delegates or AST nodes).
/// </summary>
public sealed record ScriptClosure(
    int TargetDbref,
    string MethodName,
    int FunctionIndex,
    ImmutableArray<ScriptValue> Upvalues);

/// <summary>
/// Immutable, copy-on-write ordered map preserving insertion order.
/// </summary>
public sealed class ScriptMap : IEquatable<ScriptMap>
{
    public static readonly ScriptMap Empty = new(
        ImmutableList<ScriptValue>.Empty,
        ImmutableDictionary<ScriptValue, ScriptValue>.Empty);

    public ImmutableList<ScriptValue> Keys { get; }
    public ImmutableDictionary<ScriptValue, ScriptValue> Entries { get; }

    private ScriptMap(ImmutableList<ScriptValue> keys, ImmutableDictionary<ScriptValue, ScriptValue> entries)
    {
        Keys = keys;
        Entries = entries;
    }

    public int Count => Keys.Count;

    public bool TryGetValue(ScriptValue key, out ScriptValue value)
        => Entries.TryGetValue(key, out value!);

    public bool ContainsKey(ScriptValue key)
        => Entries.ContainsKey(key);

    public ScriptMap Set(ScriptValue key, ScriptValue value)
    {
        if (Entries.ContainsKey(key))
            return new ScriptMap(Keys, Entries.SetItem(key, value));
        return new ScriptMap(Keys.Add(key), Entries.Add(key, value));
    }

    public ScriptMap Remove(ScriptValue key)
    {
        if (!Entries.ContainsKey(key))
            return this;
        return new ScriptMap(Keys.Remove(key), Entries.Remove(key));
    }

    public bool Equals(ScriptMap? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Count != other.Count) return false;
        foreach (var kv in Entries)
        {
            if (!other.Entries.TryGetValue(kv.Key, out var otherVal) || !kv.Value.Equals(otherVal))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ScriptMap other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Count);
        foreach (var k in Keys)
            hc.Add(k);
        return hc.ToHashCode();
    }
}

public sealed class ScriptValue : IEquatable<ScriptValue>
{
    public static readonly ScriptValue Null = new(ScriptValueType.Null, null);
    public static readonly ScriptValue True = new(ScriptValueType.Bool, true);
    public static readonly ScriptValue False = new(ScriptValueType.Bool, false);
    public static readonly ScriptValue EmptyList = new(ScriptValueType.List, ImmutableList<ScriptValue>.Empty);
    public static readonly ScriptValue EmptyMap = new(ScriptValueType.Map, ScriptMap.Empty);

    public ScriptValueType Type { get; }
    private readonly object? _data;

    private ScriptValue(ScriptValueType type, object? data)
    {
        Type = type;
        _data = data;
    }

    public static ScriptValue FromBool(bool v) => v ? True : False;
    public static ScriptValue FromInt(long v) => new(ScriptValueType.Int, v);
    public static ScriptValue FromFloat(double v) => new(ScriptValueType.Float, v);
    public static ScriptValue FromString(string? v) => v is null ? Null : new(ScriptValueType.String, v);
    public static ScriptValue FromObj(Dbref v) => new(ScriptValueType.Obj, v.Value);
    public static ScriptValue FromObj(int dbref) => new(ScriptValueType.Obj, dbref);
    public static ScriptValue FromList(ImmutableList<ScriptValue> items) => items.IsEmpty ? EmptyList : new(ScriptValueType.List, items);
    public static ScriptValue FromList(IEnumerable<ScriptValue> items) => FromList(items.ToImmutableList());
    public static ScriptValue FromMap(ScriptMap map) => map.Count == 0 ? EmptyMap : new(ScriptValueType.Map, map);
    public static ScriptValue FromError(ScriptErrorCode code) => new(ScriptValueType.Error, code);
    public static ScriptValue FromFunc(ScriptClosure closure) => new(ScriptValueType.Func, closure);

    public bool AsBool() => Type == ScriptValueType.Bool ? (bool)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected bool.");
    public long AsInt() => Type == ScriptValueType.Int ? (long)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected int.");
    public double AsFloat() => Type switch
    {
        ScriptValueType.Float => (double)_data!,
        ScriptValueType.Int => (long)_data!,
        _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected number.")
    };
    public string AsString() => Type == ScriptValueType.String ? (string)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected string.");
    public int AsObj() => Type == ScriptValueType.Obj ? (int)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected obj.");
    public Dbref AsDbref() => new(AsObj());
    public ImmutableList<ScriptValue> AsList() => Type == ScriptValueType.List ? (ImmutableList<ScriptValue>)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected list.");
    public ScriptMap AsMap() => Type == ScriptValueType.Map ? (ScriptMap)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected map.");
    public ScriptErrorCode AsError() => Type == ScriptValueType.Error ? (ScriptErrorCode)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected error.");
    public ScriptClosure AsFunc() => Type == ScriptValueType.Func ? (ScriptClosure)_data! : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Expected func.");

    public bool IsTruthy() => Type switch
    {
        ScriptValueType.Null => false,
        ScriptValueType.Bool => (bool)_data!,
        ScriptValueType.Int => (long)_data! != 0,
        ScriptValueType.Float => (double)_data! != 0.0,
        ScriptValueType.String => ((string)_data!).Length > 0,
        ScriptValueType.Obj => (int)_data! >= 0,
        ScriptValueType.List => ((ImmutableList<ScriptValue>)_data!).Count > 0,
        ScriptValueType.Map => ((ScriptMap)_data!).Count > 0,
        ScriptValueType.Error => false,
        ScriptValueType.Func => true,
        _ => false
    };

    public string TypeName => Type switch
    {
        ScriptValueType.Null => "null",
        ScriptValueType.Bool => "bool",
        ScriptValueType.Int => "int",
        ScriptValueType.Float => "float",
        ScriptValueType.String => "string",
        ScriptValueType.Obj => "obj",
        ScriptValueType.List => "list",
        ScriptValueType.Map => "map",
        ScriptValueType.Error => "error",
        ScriptValueType.Func => "func",
        _ => "unknown"
    };

    public string ToDisplayString() => Type switch
    {
        ScriptValueType.Null => "",
        ScriptValueType.Bool => (bool)_data! ? "true" : "false",
        ScriptValueType.Int => ((long)_data!).ToString(CultureInfo.InvariantCulture),
        ScriptValueType.Float => ((double)_data!).ToString("G", CultureInfo.InvariantCulture),
        ScriptValueType.String => (string)_data!,
        ScriptValueType.Obj => $"#{(int)_data!}",
        ScriptValueType.List => "[" + string.Join(", ", ((ImmutableList<ScriptValue>)_data!).Select(v => v.ToLiteralString())) + "]",
        ScriptValueType.Map => FormatMap((ScriptMap)_data!),
        ScriptValueType.Error => ((ScriptErrorCode)_data!).ToString(),
        ScriptValueType.Func => $"<func:{((ScriptClosure)_data!).MethodName}>",
        _ => ""
    };

    public string ToLiteralString() => Type switch
    {
        ScriptValueType.Null => "null",
        ScriptValueType.String => "\"" + EscapeString((string)_data!) + "\"",
        _ => ToDisplayString()
    };

    private static string FormatMap(ScriptMap map)
    {
        if (map.Count == 0) return "{:}";
        var parts = map.Keys.Select(k => $"{k.ToLiteralString()}: {map.Entries[k].ToLiteralString()}");
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string EscapeString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public bool Equals(ScriptValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Type != other.Type) return false;
        return Type switch
        {
            ScriptValueType.Null => true,
            ScriptValueType.Bool => (bool)_data! == (bool)other._data!,
            ScriptValueType.Int => (long)_data! == (long)other._data!,
            ScriptValueType.Float => ((double)_data!).Equals((double)other._data!),
            ScriptValueType.String => string.Equals((string)_data!, (string)other._data!, StringComparison.Ordinal),
            ScriptValueType.Obj => (int)_data! == (int)other._data!,
            ScriptValueType.Error => (ScriptErrorCode)_data! == (ScriptErrorCode)other._data!,
            ScriptValueType.List => ((ImmutableList<ScriptValue>)_data!).SequenceEqual((ImmutableList<ScriptValue>)other._data!),
            ScriptValueType.Map => ((ScriptMap)_data!).Equals((ScriptMap)other._data!),
            ScriptValueType.Func => Equals(_data, other._data),
            _ => false
        };
    }

    public override bool Equals(object? obj) => obj is ScriptValue other && Equals(other);

    public override int GetHashCode() => Type switch
    {
        ScriptValueType.Null => 0,
        ScriptValueType.Bool => HashCode.Combine(Type, (bool)_data!),
        ScriptValueType.Int => HashCode.Combine(Type, (long)_data!),
        ScriptValueType.Float => HashCode.Combine(Type, (double)_data!),
        ScriptValueType.String => HashCode.Combine(Type, (string)_data!),
        ScriptValueType.Obj => HashCode.Combine(Type, (int)_data!),
        ScriptValueType.Error => HashCode.Combine(Type, (ScriptErrorCode)_data!),
        ScriptValueType.Map => HashCode.Combine(Type, (ScriptMap)_data!),
        ScriptValueType.List => HashCodeList((ImmutableList<ScriptValue>)_data!),
        ScriptValueType.Func => HashCode.Combine(Type, _data),
        _ => (int)Type
    };

    private static int HashCodeList(ImmutableList<ScriptValue> list)
    {
        var hc = new HashCode();
        hc.Add(ScriptValueType.List);
        foreach (var item in list)
            hc.Add(item);
        return hc.ToHashCode();
    }

    public override string ToString() => ToDisplayString();
}
