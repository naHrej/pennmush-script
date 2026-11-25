namespace PennMUSH.Script.Host;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using PennMUSH.Core.Attributes;
using PennMUSH.Script.Runtime;

public static class AttributeMarshal
{
    public const string FlagInt = "script_int";
    public const string FlagFloat = "script_float";
    public const string FlagBool = "script_bool";
    public const string FlagStr = "script_str";
    public const string FlagList = "script_list";
    public const string FlagMap = "script_map";
    public const string FlagObj = "script_obj";

    public static readonly string[] AllTypeFlags =
    {
        FlagInt, FlagFloat, FlagBool, FlagStr, FlagList, FlagMap, FlagObj
    };

    public static ScriptValue FromAttribute(AttributeEntry attr)
    {
        string? typeFlag = GetTypeFlag(attr);
        return typeFlag switch
        {
            FlagInt => long.TryParse(attr.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv)
                ? ScriptValue.FromInt(iv)
                : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attr.Name} is flagged {FlagInt} but value '{attr.Value}' is not an integer."),

            FlagFloat => double.TryParse(attr.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)
                ? ScriptValue.FromFloat(dv)
                : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attr.Name} is flagged {FlagFloat} but value '{attr.Value}' is not a float."),

            FlagBool => ParseBool(attr.Name, attr.Value),

            FlagList => ParseJsonType(attr.Name, attr.Value, ScriptValueType.List, FlagList),

            FlagMap => ParseJsonType(attr.Name, attr.Value, ScriptValueType.Map, FlagMap),

            FlagObj => int.TryParse(attr.Value.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int oid)
                ? ScriptValue.FromObj(oid)
                : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attr.Name} is flagged {FlagObj} but value '{attr.Value}' is not a dbref."),

            _ => ScriptValue.FromString(attr.Value)
        };
    }

    public static (string Text, string? TypeFlag) ToAttribute(ScriptValue value) => value.Type switch
    {
        ScriptValueType.Null => ("", null),
        ScriptValueType.Bool => (value.AsBool() ? "true" : "false", FlagBool),
        ScriptValueType.Int => (value.AsInt().ToString(CultureInfo.InvariantCulture), FlagInt),
        ScriptValueType.Float => (value.AsFloat().ToString("G", CultureInfo.InvariantCulture), FlagFloat),
        ScriptValueType.String => (value.AsString(), FlagStr),
        ScriptValueType.Obj => ($"#{value.AsObj()}", FlagObj),
        ScriptValueType.List => (ScriptValueJson.ToJson(value), FlagList),
        ScriptValueType.Map => (ScriptValueJson.ToJson(value), FlagMap),
        _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot store {value.TypeName} in an attribute.")
    };

    public static string? GetTypeFlag(AttributeEntry attr)
    {
        foreach (string flag in AllTypeFlags)
        {
            if (attr.HasCustomFlag(flag))
                return flag;
        }
        return null;
    }

    private static ScriptValue ParseBool(string attrName, string raw)
    {
        string s = raw.Trim();
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") return ScriptValue.True;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") return ScriptValue.False;
        throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attrName} is flagged {FlagBool} but value '{raw}' is not a bool.");
    }

    private static ScriptValue ParseJsonType(string attrName, string raw, ScriptValueType expectedType, string flagName)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var val = ScriptValueJson.FromJsonElement(doc.RootElement);
            if (val.Type != expectedType)
                throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attrName} is flagged {flagName} but JSON root is {val.TypeName}.");
            return val;
        }
        catch (JsonException)
        {
            throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Attribute {attrName} is flagged {flagName} but contains invalid JSON.");
        }
    }
}
