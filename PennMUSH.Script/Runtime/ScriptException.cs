namespace PennMUSH.Script.Runtime;

using System;

public sealed class ScriptRuntimeException : Exception
{
    public ScriptErrorCode Code { get; }

    public ScriptRuntimeException(ScriptErrorCode code, string? message = null)
        : base(message ?? code.ToString())
    {
        Code = code;
    }
}

public sealed class ScriptCompileException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public ScriptCompileException(string message, int line = 1, int column = 1)
        : base($"({line},{column}): {message}")
    {
        Line = line;
        Column = column;
    }
}
