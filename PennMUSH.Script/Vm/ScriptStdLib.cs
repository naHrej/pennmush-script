namespace PennMUSH.Script.Vm;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PennMUSH.Script.Runtime;

public static class ScriptStdLib
{
    public static bool TryInvokeGlobal(
        ScriptVm vm,
        IScriptHostBridge bridge,
        VmCallFrame frame,
        string name,
        IReadOnlyList<ScriptValue> args,
        out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (name)
        {
            case "__iter_pairs":
            {
                var col = RequireArg(args, 0);
                var b = ImmutableList.CreateBuilder<ScriptValue>();
                if (col.Type == ScriptValueType.List)
                {
                    var list = col.AsList();
                    for (int i = 0; i < list.Count; i++)
                        b.Add(ScriptValue.FromList(new[] { ScriptValue.FromInt(i), list[i] }));
                }
                else if (col.Type == ScriptValueType.Map)
                {
                    var map = col.AsMap();
                    foreach (var k in map.Keys)
                        b.Add(ScriptValue.FromList(new[] { k, map.Entries[k] }));
                }
                else if (col.Type == ScriptValueType.String)
                {
                    string s = col.AsString();
                    for (int i = 0; i < s.Length; i++)
                        b.Add(ScriptValue.FromList(new[] { ScriptValue.FromInt(i), ScriptValue.FromString(s[i].ToString()) }));
                }
                else
                {
                    throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, $"Cannot iterate over {col.TypeName}.");
                }
                result = ScriptValue.FromList(b.ToImmutable());
                return true;
            }
            case "TypeOf":
                result = ScriptValue.FromString(RequireArg(args, 0).TypeName);
                return true;
            case "ToInt":
                result = ToInt(RequireArg(args, 0));
                return true;
            case "ToFloat":
                result = ToFloat(RequireArg(args, 0));
                return true;
            case "ToStr":
                result = ScriptValue.FromString(RequireArg(args, 0).ToDisplayString());
                return true;
            case "ToObj":
                result = ToObj(RequireArg(args, 0));
                return true;
            case "IsValid":
                result = ScriptValue.FromBool(bridge.IsValidObject(RequireArg(args, 0).AsObj()));
                return true;
            case "TicksLeft":
                result = ScriptValue.FromInt(vm.RemainingTicks);
                return true;
            case "Callers":
                result = vm.GetCallersList();
                return true;
            case "Log":
                bridge.Log(string.Join(" ", args.Select(a => a.ToDisplayString())));
                return true;
            case "Min":
                result = MinMax(args, isMin: true);
                return true;
            case "Max":
                result = MinMax(args, isMin: false);
                return true;
            case "Clamp":
            {
                double v = RequireArg(args, 0).AsFloat();
                double lo = RequireArg(args, 1).AsFloat();
                double hi = RequireArg(args, 2).AsFloat();
                result = args[0].Type == ScriptValueType.Int && args[1].Type == ScriptValueType.Int && args[2].Type == ScriptValueType.Int
                    ? ScriptValue.FromInt(Math.Clamp(args[0].AsInt(), args[1].AsInt(), args[2].AsInt()))
                    : ScriptValue.FromFloat(Math.Clamp(v, lo, hi));
                return true;
            }
            case "Floor":
                result = ScriptValue.FromInt((long)Math.Floor(RequireArg(args, 0).AsFloat()));
                return true;
            case "Ceil":
                result = ScriptValue.FromInt((long)Math.Ceiling(RequireArg(args, 0).AsFloat()));
                return true;
            case "Round":
                result = ScriptValue.FromInt((long)Math.Round(RequireArg(args, 0).AsFloat()));
                return true;
            case "Sqrt":
                result = ScriptValue.FromFloat(Math.Sqrt(RequireArg(args, 0).AsFloat()));
                return true;
            case "Pow":
                result = ScriptValue.FromFloat(Math.Pow(RequireArg(args, 0).AsFloat(), RequireArg(args, 1).AsFloat()));
                return true;
            case "Rand":
            {
                if (args.Count == 1)
                    result = ScriptValue.FromInt(Random.Shared.NextInt64(0, RequireArg(args, 0).AsInt()));
                else
                    result = ScriptValue.FromInt(Random.Shared.NextInt64(RequireArg(args, 0).AsInt(), RequireArg(args, 1).AsInt()));
                return true;
            }
            default:
                return false;
        }
    }

    public static bool TryInvokeNamespace(
        IScriptHostBridge bridge,
        VmCallFrame frame,
        string ns,
        string method,
        IReadOnlyList<ScriptValue> args,
        out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (ns)
        {
            case "Math":
                switch (method)
                {
                    case "Abs":
                        var v = RequireArg(args, 0);
                        result = v.Type == ScriptValueType.Int ? ScriptValue.FromInt(Math.Abs(v.AsInt())) : ScriptValue.FromFloat(Math.Abs(v.AsFloat()));
                        return true;
                    case "Min": result = MinMax(args, true); return true;
                    case "Max": result = MinMax(args, false); return true;
                    case "Floor": result = ScriptValue.FromInt((long)Math.Floor(RequireArg(args, 0).AsFloat())); return true;
                    case "Ceil": result = ScriptValue.FromInt((long)Math.Ceiling(RequireArg(args, 0).AsFloat())); return true;
                    case "Round": result = ScriptValue.FromInt((long)Math.Round(RequireArg(args, 0).AsFloat())); return true;
                    case "Sqrt": result = ScriptValue.FromFloat(Math.Sqrt(RequireArg(args, 0).AsFloat())); return true;
                    case "Pow": result = ScriptValue.FromFloat(Math.Pow(RequireArg(args, 0).AsFloat(), RequireArg(args, 1).AsFloat())); return true;
                }
                break;

            case "Json":
                switch (method)
                {
                    case "Parse":
                    {
                        using var doc = JsonDocument.Parse(RequireArg(args, 0).AsString());
                        result = ScriptValueJson.FromJsonElement(doc.RootElement);
                        return true;
                    }
                    case "Stringify":
                        result = ScriptValue.FromString(ScriptValueJson.ToJson(RequireArg(args, 0)));
                        return true;
                }
                break;

            case "Base64":
                switch (method)
                {
                    case "Encode":
                        result = ScriptValue.FromString(Convert.ToBase64String(Encoding.UTF8.GetBytes(RequireArg(args, 0).AsString())));
                        return true;
                    case "Decode":
                        result = ScriptValue.FromString(Encoding.UTF8.GetString(Convert.FromBase64String(RequireArg(args, 0).AsString())));
                        return true;
                }
                break;

            case "Regex":
                switch (method)
                {
                    case "Match":
                    {
                        var m = Regex.Match(RequireArg(args, 0).AsString(), RequireArg(args, 1).AsString(), RegexOptions.None, TimeSpan.FromMilliseconds(250));
                        if (!m.Success) { result = ScriptValue.Null; return true; }
                        result = ScriptValue.FromList(m.Groups.Cast<Group>().Select(g => ScriptValue.FromString(g.Value)));
                        return true;
                    }
                    case "Replace":
                        result = ScriptValue.FromString(Regex.Replace(
                            RequireArg(args, 0).AsString(),
                            RequireArg(args, 1).AsString(),
                            RequireArg(args, 2).AsString(),
                            RegexOptions.None,
                            TimeSpan.FromMilliseconds(250)));
                        return true;
                }
                break;

            case "Time":
                switch (method)
                {
                    case "Now":
                        result = ScriptValue.FromInt(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        return true;
                    case "Format":
                    {
                        long ts = RequireArg(args, 0).AsInt();
                        string fmt = args.Count > 1 ? args[1].AsString() : "yyyy-MM-dd HH:mm:ss";
                        result = ScriptValue.FromString(DateTimeOffset.FromUnixTimeSeconds(ts).ToString(fmt, CultureInfo.InvariantCulture));
                        return true;
                    }
                }
                break;

            case "Crypto":
                switch (method)
                {
                    case "Sha256":
                    {
                        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(RequireArg(args, 0).AsString()));
                        result = ScriptValue.FromString(Convert.ToHexStringLower(hash));
                        return true;
                    }
                    case "HmacSha256":
                    {
                        byte[] key = Encoding.UTF8.GetBytes(RequireArg(args, 0).AsString());
                        byte[] msg = Encoding.UTF8.GetBytes(RequireArg(args, 1).AsString());
                        using var hmac = new HMACSHA256(key);
                        result = ScriptValue.FromString(Convert.ToHexStringLower(hmac.ComputeHash(msg)));
                        return true;
                    }
                }
                break;

            case "Obj":
                switch (method)
                {
                    case "Valid": result = ScriptValue.FromBool(bridge.IsValidObject(RequireArg(args, 0).AsObj())); return true;
                    case "Parent": result = ScriptValue.FromObj(bridge.GetParent(RequireArg(args, 0).AsObj())); return true;
                    case "Children": result = ScriptValue.FromList(bridge.GetChildren(RequireArg(args, 0).AsObj()).Select(ScriptValue.FromObj)); return true;
                    case "Contents": result = ScriptValue.FromList(bridge.GetContents(RequireArg(args, 0).AsObj()).Select(ScriptValue.FromObj)); return true;
                    case "Location": result = ScriptValue.FromObj(bridge.GetLocation(RequireArg(args, 0).AsObj())); return true;
                    case "Owner": result = ScriptValue.FromObj(bridge.GetOwner(RequireArg(args, 0).AsObj())); return true;
                    case "Name": result = ScriptValue.FromString(bridge.GetObjectName(RequireArg(args, 0).AsObj())); return true;
                    case "Players": result = ScriptValue.FromList(bridge.GetPlayers().Select(ScriptValue.FromObj)); return true;
                    case "Connected": result = ScriptValue.FromList(bridge.GetConnectedPlayers().Select(ScriptValue.FromObj)); return true;
                }
                break;

            case "Mush":
                switch (method)
                {
                    case "Eval":
                        result = ScriptValue.FromString(bridge.EvalSoftcode(frame.ThisDbref, frame.PlayerDbref, RequireArg(args, 0).AsString()));
                        return true;
                    case "Call":
                    {
                        int obj = RequireArg(args, 0).AsObj();
                        string attr = RequireArg(args, 1).AsString();
                        var passArgs = args.Skip(2).ToList();
                        result = ScriptValue.FromString(bridge.CallSoftcodeAttr(frame.ThisDbref, frame.PlayerDbref, obj, attr, passArgs));
                        return true;
                    }
                    case "Get":
                        result = ScriptValue.FromString(bridge.GetSoftcodeAttr(frame.ThisDbref, RequireArg(args, 0).AsObj(), RequireArg(args, 1).AsString()));
                        return true;
                    case "Set":
                        bridge.SetSoftcodeAttr(frame.ThisDbref, RequireArg(args, 0).AsObj(), RequireArg(args, 1).AsString(), RequireArg(args, 2).ToDisplayString());
                        return true;
                    case "Notify":
                        bridge.NotifyPlayer(RequireArg(args, 0).AsObj(), RequireArg(args, 1).ToDisplayString());
                        return true;
                }
                break;
        }
        return false;
    }

    public static bool TryGetBuiltinProperty(IScriptHostBridge bridge, ScriptValue target, string prop, out ScriptValue value)
    {
        value = ScriptValue.Null;
        switch (target.Type)
        {
            case ScriptValueType.List when prop == "Length":
                value = ScriptValue.FromInt(target.AsList().Count);
                return true;
            case ScriptValueType.Map when prop == "Length":
                value = ScriptValue.FromInt(target.AsMap().Count);
                return true;
            case ScriptValueType.String when prop == "Length":
                value = ScriptValue.FromInt(target.AsString().Length);
                return true;
            case ScriptValueType.Obj:
                switch (prop)
                {
                    case "Valid": value = ScriptValue.FromBool(bridge.IsValidObject(target.AsObj())); return true;
                    case "Name": value = ScriptValue.FromString(bridge.GetObjectName(target.AsObj())); return true;
                    case "Owner": value = ScriptValue.FromObj(bridge.GetOwner(target.AsObj())); return true;
                    case "Location": value = ScriptValue.FromObj(bridge.GetLocation(target.AsObj())); return true;
                    case "Parent": value = ScriptValue.FromObj(bridge.GetParent(target.AsObj())); return true;
                    case "Contents": value = ScriptValue.FromList(bridge.GetContents(target.AsObj()).Select(ScriptValue.FromObj)); return true;
                    case "Children": value = ScriptValue.FromList(bridge.GetChildren(target.AsObj()).Select(ScriptValue.FromObj)); return true;
                }
                break;
        }
        return false;
    }

    public static bool TryInvokeBuiltinMethod(
        ScriptVm vm,
        IScriptHostBridge bridge,
        ScriptValue target,
        string method,
        IReadOnlyList<ScriptValue> args,
        out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (target.Type)
        {
            case ScriptValueType.List:
                return TryInvokeListMethod(vm, target.AsList(), method, args, out result);
            case ScriptValueType.Map:
                return TryInvokeMapMethod(target.AsMap(), method, args, out result);
            case ScriptValueType.String:
                return TryInvokeStringMethod(target.AsString(), method, args, out result);
            case ScriptValueType.Obj when method == "Notify":
                bridge.NotifyPlayer(target.AsObj(), RequireArg(args, 0).ToDisplayString());
                return true;
            default:
                return false;
        }
    }

    private static bool TryInvokeListMethod(ScriptVm vm, ImmutableList<ScriptValue> list, string method, IReadOnlyList<ScriptValue> args, out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (method)
        {
            case "Add":
            case "Push":
                result = ScriptValue.FromList(list.Add(RequireArg(args, 0)));
                return true;
            case "Insert":
            {
                int idx = (int)RequireArg(args, 0).AsInt();
                if (idx < 0 || idx > list.Count) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
                result = ScriptValue.FromList(list.Insert(idx, RequireArg(args, 1)));
                return true;
            }
            case "RemoveAt":
            {
                int idx = (int)RequireArg(args, 0).AsInt();
                if (idx < 0 || idx >= list.Count) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
                result = ScriptValue.FromList(list.RemoveAt(idx));
                return true;
            }
            case "Remove":
                result = ScriptValue.FromList(list.Remove(RequireArg(args, 0)));
                return true;
            case "Clear":
                result = ScriptValue.EmptyList;
                return true;
            case "Contains":
                result = ScriptValue.FromBool(list.Contains(RequireArg(args, 0)));
                return true;
            case "IndexOf":
                result = ScriptValue.FromInt(list.IndexOf(RequireArg(args, 0)));
                return true;
            case "Reverse":
                result = ScriptValue.FromList(list.Reverse());
                return true;
            case "Slice":
            {
                int start = (int)RequireArg(args, 0).AsInt();
                int count = args.Count > 1 ? (int)args[1].AsInt() : list.Count - start;
                if (start < 0 || count < 0 || start + count > list.Count) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
                result = ScriptValue.FromList(list.GetRange(start, count));
                return true;
            }
            case "Join":
            {
                string sep = args.Count > 0 ? args[0].AsString() : "";
                result = ScriptValue.FromString(string.Join(sep, list.Select(x => x.ToDisplayString())));
                return true;
            }
            case "Map":
            {
                var fn = RequireArg(args, 0);
                var b = ImmutableList.CreateBuilder<ScriptValue>();
                foreach (var item in list)
                    b.Add(vm.InvokeClosureSync(fn, new[] { item }));
                result = ScriptValue.FromList(b.ToImmutable());
                return true;
            }
            case "Filter":
            {
                var fn = RequireArg(args, 0);
                var b = ImmutableList.CreateBuilder<ScriptValue>();
                foreach (var item in list)
                    if (vm.InvokeClosureSync(fn, new[] { item }).IsTruthy())
                        b.Add(item);
                result = ScriptValue.FromList(b.ToImmutable());
                return true;
            }
            case "Sort":
            {
                var items = list.ToList();
                if (args.Count > 0)
                {
                    var fn = args[0];
                    items.Sort((a, b) => (int)vm.InvokeClosureSync(fn, new[] { a, b }).AsInt());
                }
                else
                {
                    items.Sort((a, b) => string.Compare(a.ToDisplayString(), b.ToDisplayString(), StringComparison.Ordinal));
                }
                result = ScriptValue.FromList(items);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryInvokeMapMethod(ScriptMap map, string method, IReadOnlyList<ScriptValue> args, out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (method)
        {
            case "Keys":
                result = ScriptValue.FromList(map.Keys);
                return true;
            case "Values":
                result = ScriptValue.FromList(map.Keys.Select(k => map.Entries[k]));
                return true;
            case "HasKey":
                result = ScriptValue.FromBool(map.ContainsKey(RequireArg(args, 0)));
                return true;
            case "Remove":
                result = ScriptValue.FromMap(map.Remove(RequireArg(args, 0)));
                return true;
            case "Clear":
                result = ScriptValue.EmptyMap;
                return true;
            case "Set":
                result = ScriptValue.FromMap(map.Set(RequireArg(args, 0), RequireArg(args, 1)));
                return true;
            default:
                return false;
        }
    }

    private static bool TryInvokeStringMethod(string s, string method, IReadOnlyList<ScriptValue> args, out ScriptValue result)
    {
        result = ScriptValue.Null;
        switch (method)
        {
            case "Split":
            {
                string sep = args.Count > 0 ? args[0].AsString() : " ";
                var parts = string.IsNullOrEmpty(sep)
                    ? s.Select(c => ScriptValue.FromString(c.ToString()))
                    : s.Split(sep).Select(ScriptValue.FromString);
                result = ScriptValue.FromList(parts);
                return true;
            }
            case "Trim":
                result = ScriptValue.FromString(s.Trim());
                return true;
            case "Replace":
                result = ScriptValue.FromString(s.Replace(RequireArg(args, 0).AsString(), RequireArg(args, 1).AsString(), StringComparison.Ordinal));
                return true;
            case "StartsWith":
                result = ScriptValue.FromBool(s.StartsWith(RequireArg(args, 0).AsString(), StringComparison.Ordinal));
                return true;
            case "EndsWith":
                result = ScriptValue.FromBool(s.EndsWith(RequireArg(args, 0).AsString(), StringComparison.Ordinal));
                return true;
            case "Contains":
                result = ScriptValue.FromBool(s.Contains(RequireArg(args, 0).AsString(), StringComparison.Ordinal));
                return true;
            case "Substring":
            {
                int start = (int)RequireArg(args, 0).AsInt();
                if (start < 0 || start > s.Length) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
                if (args.Count > 1)
                {
                    int len = (int)args[1].AsInt();
                    if (len < 0 || start + len > s.Length) throw new ScriptRuntimeException(ScriptErrorCode.E_RANGE);
                    result = ScriptValue.FromString(s.Substring(start, len));
                }
                else
                {
                    result = ScriptValue.FromString(s.Substring(start));
                }
                return true;
            }
            case "ToUpper":
                result = ScriptValue.FromString(s.ToUpperInvariant());
                return true;
            case "ToLower":
                result = ScriptValue.FromString(s.ToLowerInvariant());
                return true;
            default:
                return false;
        }
    }

    private static ScriptValue RequireArg(IReadOnlyList<ScriptValue> args, int idx)
        => idx < args.Count ? args[idx] : throw new ScriptRuntimeException(ScriptErrorCode.E_ARGS);

    private static ScriptValue ToInt(ScriptValue v) => v.Type switch
    {
        ScriptValueType.Int => v,
        ScriptValueType.Float => ScriptValue.FromInt((long)v.AsFloat()),
        ScriptValueType.Bool => ScriptValue.FromInt(v.AsBool() ? 1 : 0),
        ScriptValueType.Obj => ScriptValue.FromInt(v.AsObj()),
        ScriptValueType.String => long.TryParse(v.AsString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
            ? ScriptValue.FromInt(l) : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Cannot convert string to int."),
        _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE)
    };

    private static ScriptValue ToFloat(ScriptValue v) => v.Type switch
    {
        ScriptValueType.Float => v,
        ScriptValueType.Int => ScriptValue.FromFloat(v.AsInt()),
        ScriptValueType.String => double.TryParse(v.AsString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? ScriptValue.FromFloat(d) : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Cannot convert string to float."),
        _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE)
    };

    private static ScriptValue ToObj(ScriptValue v) => v.Type switch
    {
        ScriptValueType.Obj => v,
        ScriptValueType.Int => ScriptValue.FromObj((int)v.AsInt()),
        ScriptValueType.String => int.TryParse(v.AsString().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? ScriptValue.FromObj(id) : throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE, "Cannot convert string to obj."),
        _ => throw new ScriptRuntimeException(ScriptErrorCode.E_TYPE)
    };

    private static ScriptValue MinMax(IReadOnlyList<ScriptValue> args, bool isMin)
    {
        if (args.Count < 1) throw new ScriptRuntimeException(ScriptErrorCode.E_ARGS);
        var best = args[0];
        for (int i = 1; i < args.Count; i++)
        {
            int cmp = best.AsFloat().CompareTo(args[i].AsFloat());
            if ((isMin && cmp > 0) || (!isMin && cmp < 0))
                best = args[i];
        }
        return best;
    }
}
