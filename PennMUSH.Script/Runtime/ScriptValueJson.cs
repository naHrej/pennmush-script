namespace PennMUSH.Script.Runtime;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;

/// <summary>
/// Lossless JSON encoding/decoding for <see cref="ScriptValue"/> (used by both VM task serialization and Json.Parse/Stringify).
/// </summary>
public static class ScriptValueJson
{
    public static ScriptValue FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => ScriptValue.Null,
        JsonValueKind.True => ScriptValue.True,
        JsonValueKind.False => ScriptValue.False,
        JsonValueKind.Number => el.TryGetInt64(out long l) ? ScriptValue.FromInt(l) : ScriptValue.FromFloat(el.GetDouble()),
        JsonValueKind.String => ScriptValue.FromString(el.GetString()!),
        JsonValueKind.Array => ParseArray(el),
        JsonValueKind.Object => ParseObject(el),
        _ => ScriptValue.Null
    };

    private static ScriptValue ParseArray(JsonElement el)
    {
        var builder = ImmutableList.CreateBuilder<ScriptValue>();
        foreach (var item in el.EnumerateArray())
            builder.Add(FromJsonElement(item));
        return ScriptValue.FromList(builder.ToImmutable());
    }

    private static ScriptValue ParseObject(JsonElement el)
    {
        var map = ScriptMap.Empty;
        foreach (var prop in el.EnumerateObject())
            map = map.Set(ScriptValue.FromString(prop.Name), FromJsonElement(prop.Value));
        return ScriptValue.FromMap(map);
    }

    public static string ToJson(ScriptValue value)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteJson(writer, value);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteJson(Utf8JsonWriter writer, ScriptValue value)
    {
        switch (value.Type)
        {
            case ScriptValueType.Null:
                writer.WriteNullValue();
                break;
            case ScriptValueType.Bool:
                writer.WriteBooleanValue(value.AsBool());
                break;
            case ScriptValueType.Int:
                writer.WriteNumberValue(value.AsInt());
                break;
            case ScriptValueType.Float:
                writer.WriteNumberValue(value.AsFloat());
                break;
            case ScriptValueType.String:
                writer.WriteStringValue(value.AsString());
                break;
            case ScriptValueType.Obj:
                writer.WriteStringValue($"#{value.AsObj()}");
                break;
            case ScriptValueType.Error:
                writer.WriteStringValue(value.AsError().ToString());
                break;
            case ScriptValueType.List:
                writer.WriteStartArray();
                foreach (var item in value.AsList())
                    WriteJson(writer, item);
                writer.WriteEndArray();
                break;
            case ScriptValueType.Map:
                writer.WriteStartObject();
                var map = value.AsMap();
                foreach (var key in map.Keys)
                {
                    writer.WritePropertyName(key.ToDisplayString());
                    WriteJson(writer, map.Entries[key]);
                }
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.ToDisplayString());
                break;
        }
    }

    /// <summary>
    /// Tagged lossless serialization for VM persistence (preserves Obj, Error, Func, non-string Map keys).
    /// </summary>
    public static void WriteTagged(Utf8JsonWriter writer, ScriptValue value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("t", (int)value.Type);
        switch (value.Type)
        {
            case ScriptValueType.Bool:
                writer.WriteBoolean("v", value.AsBool());
                break;
            case ScriptValueType.Int:
                writer.WriteNumber("v", value.AsInt());
                break;
            case ScriptValueType.Float:
                writer.WriteNumber("v", value.AsFloat());
                break;
            case ScriptValueType.String:
                writer.WriteString("v", value.AsString());
                break;
            case ScriptValueType.Obj:
                writer.WriteNumber("v", value.AsObj());
                break;
            case ScriptValueType.Error:
                writer.WriteNumber("v", (int)value.AsError());
                break;
            case ScriptValueType.List:
                writer.WriteStartArray("v");
                foreach (var item in value.AsList())
                    WriteTagged(writer, item);
                writer.WriteEndArray();
                break;
            case ScriptValueType.Map:
                writer.WriteStartArray("v");
                var map = value.AsMap();
                foreach (var k in map.Keys)
                {
                    writer.WriteStartArray();
                    WriteTagged(writer, k);
                    WriteTagged(writer, map.Entries[k]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case ScriptValueType.Func:
                var fn = value.AsFunc();
                writer.WriteStartObject("v");
                writer.WriteNumber("d", fn.TargetDbref);
                writer.WriteString("m", fn.MethodName);
                writer.WriteNumber("f", fn.FunctionIndex);
                writer.WriteStartArray("u");
                foreach (var uv in fn.Upvalues)
                    WriteTagged(writer, uv);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
        }
        writer.WriteEndObject();
    }

    public static ScriptValue ReadTagged(JsonElement el)
    {
        var type = (ScriptValueType)el.GetProperty("t").GetInt32();
        switch (type)
        {
            case ScriptValueType.Null: return ScriptValue.Null;
            case ScriptValueType.Bool: return ScriptValue.FromBool(el.GetProperty("v").GetBoolean());
            case ScriptValueType.Int: return ScriptValue.FromInt(el.GetProperty("v").GetInt64());
            case ScriptValueType.Float: return ScriptValue.FromFloat(el.GetProperty("v").GetDouble());
            case ScriptValueType.String: return ScriptValue.FromString(el.GetProperty("v").GetString()!);
            case ScriptValueType.Obj: return ScriptValue.FromObj(el.GetProperty("v").GetInt32());
            case ScriptValueType.Error: return ScriptValue.FromError((ScriptErrorCode)el.GetProperty("v").GetInt32());
            case ScriptValueType.List:
            {
                var b = ImmutableList.CreateBuilder<ScriptValue>();
                foreach (var item in el.GetProperty("v").EnumerateArray())
                    b.Add(ReadTagged(item));
                return ScriptValue.FromList(b.ToImmutable());
            }
            case ScriptValueType.Map:
            {
                var m = ScriptMap.Empty;
                foreach (var pair in el.GetProperty("v").EnumerateArray())
                {
                    var k = ReadTagged(pair[0]);
                    var v = ReadTagged(pair[1]);
                    m = m.Set(k, v);
                }
                return ScriptValue.FromMap(m);
            }
            case ScriptValueType.Func:
            {
                var v = el.GetProperty("v");
                var uvs = ImmutableArray.CreateBuilder<ScriptValue>();
                foreach (var u in v.GetProperty("u").EnumerateArray())
                    uvs.Add(ReadTagged(u));
                return ScriptValue.FromFunc(new ScriptClosure(
                    v.GetProperty("d").GetInt32(),
                    v.GetProperty("m").GetString()!,
                    v.GetProperty("f").GetInt32(),
                    uvs.ToImmutable()));
            }
            default:
                return ScriptValue.Null;
        }
    }
}
