namespace PennMUSH.Script.Compiler;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PennMUSH.Script.Runtime;

public sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private readonly List<Token> _tokens = new();

    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["var"] = TokenKind.Var,
        ["const"] = TokenKind.Const,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["null"] = TokenKind.Null,
        ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else,
        ["for"] = TokenKind.For,
        ["foreach"] = TokenKind.Foreach,
        ["in"] = TokenKind.In,
        ["while"] = TokenKind.While,
        ["switch"] = TokenKind.Switch,
        ["case"] = TokenKind.Case,
        ["default"] = TokenKind.Default,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["return"] = TokenKind.Return,
        ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch,
        ["finally"] = TokenKind.Finally,
        ["throw"] = TokenKind.Throw,
        ["after"] = TokenKind.After,
        ["is"] = TokenKind.Is,
        ["this"] = TokenKind.This,
        ["caller"] = TokenKind.Caller,
        ["player"] = TokenKind.Player,
        ["args"] = TokenKind.Args,
        ["base"] = TokenKind.Base,
    };

    public Lexer(string source)
    {
        _src = source ?? string.Empty;
    }

    public List<Token> Tokenize()
    {
        while (!IsAtEnd)
        {
            SkipWhitespaceAndComments();
            if (IsAtEnd) break;

            int line = _line;
            int col = _col;
            char c = Peek();

            if (c == '$' && Peek(1) == '"')
            {
                Advance(); // $
                Advance(); // "
                ReadInterpolatedString(line, col);
                continue;
            }

            if (c == '"')
            {
                Advance();
                string s = ReadStringLiteral(line, col);
                _tokens.Add(new Token(TokenKind.StringLiteral, s, line, col));
                continue;
            }

            if (c == '#')
            {
                ReadObjLiteral(line, col);
                continue;
            }

            if (char.IsDigit(c))
            {
                ReadNumber(line, col);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                ReadIdentifierOrKeyword(line, col);
                continue;
            }

            ReadOperatorOrDelimiter(line, col);
        }

        _tokens.Add(new Token(TokenKind.Eof, "", _line, _col));
        return _tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (!IsAtEnd)
        {
            char c = Peek();
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (!IsAtEnd && Peek() != '\n')
                    Advance();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                int startLine = _line, startCol = _col;
                Advance();
                Advance();
                while (!IsAtEnd && !(Peek() == '*' && Peek(1) == '/'))
                    Advance();
                if (IsAtEnd)
                    throw new ScriptCompileException("Unterminated block comment.", startLine, startCol);
                Advance();
                Advance();
            }
            else
            {
                break;
            }
        }
    }

    private string ReadStringLiteral(int startLine, int startCol)
    {
        var sb = new StringBuilder();
        while (!IsAtEnd && Peek() != '"')
        {
            char c = Advance();
            if (c == '\\')
                sb.Append(ReadEscape(startLine, startCol));
            else
                sb.Append(c);
        }
        if (IsAtEnd)
            throw new ScriptCompileException("Unterminated string literal.", startLine, startCol);
        Advance(); // closing "
        return sb.ToString();
    }

    private void ReadInterpolatedString(int startLine, int startCol)
    {
        _tokens.Add(new Token(TokenKind.InterpStart, "$\"", startLine, startCol));
        var sb = new StringBuilder();

        while (!IsAtEnd)
        {
            char c = Peek();
            if (c == '"')
            {
                if (sb.Length > 0)
                {
                    _tokens.Add(new Token(TokenKind.InterpText, sb.ToString(), _line, _col));
                    sb.Clear();
                }
                Advance();
                _tokens.Add(new Token(TokenKind.InterpEnd, "\"", _line, _col));
                return;
            }
            if (c == '{')
            {
                if (Peek(1) == '{')
                {
                    Advance();
                    Advance();
                    sb.Append('{');
                    continue;
                }
                if (sb.Length > 0)
                {
                    _tokens.Add(new Token(TokenKind.InterpText, sb.ToString(), _line, _col));
                    sb.Clear();
                }
                int exprLine = _line, exprCol = _col;
                Advance(); // {
                _tokens.Add(new Token(TokenKind.InterpExprStart, "{", exprLine, exprCol));
                LexEmbeddedExpression(exprLine, exprCol);
                continue;
            }
            if (c == '}' && Peek(1) == '}')
            {
                Advance();
                Advance();
                sb.Append('}');
                continue;
            }
            if (c == '\\')
            {
                Advance();
                sb.Append(ReadEscape(startLine, startCol));
                continue;
            }
            sb.Append(Advance());
        }

        throw new ScriptCompileException("Unterminated interpolated string.", startLine, startCol);
    }

    private void LexEmbeddedExpression(int startLine, int startCol)
    {
        int depth = 1;
        while (!IsAtEnd)
        {
            SkipWhitespaceAndComments();
            if (IsAtEnd) break;

            int line = _line, col = _col;
            char c = Peek();
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    Advance();
                    _tokens.Add(new Token(TokenKind.InterpExprEnd, "}", line, col));
                    return;
                }
            }
            if (c == '{')
            {
                depth++;
                Advance();
                _tokens.Add(new Token(TokenKind.LBrace, "{", line, col));
                continue;
            }
            if (c == '$' && Peek(1) == '"')
            {
                Advance(); Advance();
                ReadInterpolatedString(line, col);
                continue;
            }
            if (c == '"')
            {
                Advance();
                _tokens.Add(new Token(TokenKind.StringLiteral, ReadStringLiteral(line, col), line, col));
                continue;
            }
            if (c == '#') { ReadObjLiteral(line, col); continue; }
            if (char.IsDigit(c)) { ReadNumber(line, col); continue; }
            if (char.IsLetter(c) || c == '_') { ReadIdentifierOrKeyword(line, col); continue; }
            ReadOperatorOrDelimiter(line, col);
        }
        throw new ScriptCompileException("Unterminated interpolation expression '{'.", startLine, startCol);
    }

    private char ReadEscape(int line, int col)
    {
        if (IsAtEnd)
            throw new ScriptCompileException("Unexpected end of string escape.", line, col);
        char esc = Advance();
        return esc switch
        {
            '\\' => '\\',
            '"' => '"',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '0' => '\0',
            _ => throw new ScriptCompileException($"Unknown escape sequence '\\{esc}'.", line, col)
        };
    }

    private void ReadObjLiteral(int line, int col)
    {
        Advance(); // #
        int start = _pos;
        if (Peek() == '-')
            Advance();
        if (!char.IsDigit(Peek()))
            throw new ScriptCompileException("Expected dbref number after '#'.", line, col);
        while (!IsAtEnd && char.IsDigit(Peek()))
            Advance();
        string numStr = _src[start.._pos];
        if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            throw new ScriptCompileException($"Invalid dbref literal '#{numStr}'.", line, col);
        _tokens.Add(new Token(TokenKind.ObjLiteral, $"#{numStr}", line, col, ObjValue: id));
    }

    private void ReadNumber(int line, int col)
    {
        int start = _pos;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance(); Advance();
            int hexStart = _pos;
            while (!IsAtEnd && IsHexDigit(Peek())) Advance();
            string hex = _src[hexStart.._pos];
            if (hex.Length == 0 || !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                throw new ScriptCompileException($"Invalid hex literal '0x{hex}'.", line, col);
            _tokens.Add(new Token(TokenKind.IntLiteral, _src[start.._pos], line, col, IntValue: hv));
            return;
        }
        if (Peek() == '0' && (Peek(1) == 'b' || Peek(1) == 'B'))
        {
            Advance(); Advance();
            int binStart = _pos;
            while (!IsAtEnd && (Peek() == '0' || Peek() == '1')) Advance();
            string bin = _src[binStart.._pos];
            if (bin.Length == 0)
                throw new ScriptCompileException("Invalid binary literal.", line, col);
            _tokens.Add(new Token(TokenKind.IntLiteral, _src[start.._pos], line, col, IntValue: Convert.ToInt64(bin, 2)));
            return;
        }

        while (!IsAtEnd && char.IsDigit(Peek())) Advance();
        bool isFloat = false;
        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            Advance();
            while (!IsAtEnd && char.IsDigit(Peek())) Advance();
        }
        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance();
            if (Peek() == '+' || Peek() == '-') Advance();
            if (!char.IsDigit(Peek()))
                throw new ScriptCompileException("Invalid exponent in float literal.", line, col);
            while (!IsAtEnd && char.IsDigit(Peek())) Advance();
        }

        string text = _src[start.._pos];
        if (isFloat)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                throw new ScriptCompileException($"Invalid float literal '{text}'.", line, col);
            _tokens.Add(new Token(TokenKind.FloatLiteral, text, line, col, FloatValue: dv));
        }
        else
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                throw new ScriptCompileException($"Invalid integer literal '{text}'.", line, col);
            _tokens.Add(new Token(TokenKind.IntLiteral, text, line, col, IntValue: iv));
        }
    }

    private void ReadIdentifierOrKeyword(int line, int col)
    {
        int start = _pos;
        while (!IsAtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();
        string text = _src[start.._pos];

        if (text.StartsWith("E_", StringComparison.Ordinal) && Enum.TryParse<ScriptErrorCode>(text, out var errCode))
        {
            _tokens.Add(new Token(TokenKind.ErrorLiteral, text, line, col, ErrorValue: errCode));
            return;
        }

        if (Keywords.TryGetValue(text, out var kw))
        {
            _tokens.Add(new Token(kw, text, line, col));
            return;
        }

        _tokens.Add(new Token(TokenKind.Identifier, text, line, col));
    }

    private void ReadOperatorOrDelimiter(int line, int col)
    {
        char c = Advance();
        switch (c)
        {
            case '(': _tokens.Add(new Token(TokenKind.LParen, "(", line, col)); break;
            case ')': _tokens.Add(new Token(TokenKind.RParen, ")", line, col)); break;
            case '{': _tokens.Add(new Token(TokenKind.LBrace, "{", line, col)); break;
            case '}': _tokens.Add(new Token(TokenKind.RBrace, "}", line, col)); break;
            case '[': _tokens.Add(new Token(TokenKind.LBracket, "[", line, col)); break;
            case ']': _tokens.Add(new Token(TokenKind.RBracket, "]", line, col)); break;
            case ',': _tokens.Add(new Token(TokenKind.Comma, ",", line, col)); break;
            case ';': _tokens.Add(new Token(TokenKind.Semicolon, ";", line, col)); break;
            case ':': _tokens.Add(new Token(TokenKind.Colon, ":", line, col)); break;
            case '~': _tokens.Add(new Token(TokenKind.Tilde, "~", line, col)); break;
            case '^': _tokens.Add(new Token(TokenKind.Caret, "^", line, col)); break;
            case '.':
                if (Match('.'))
                    _tokens.Add(Match('.') ? new Token(TokenKind.Spread, "...", line, col) : new Token(TokenKind.DotDot, "..", line, col));
                else
                    _tokens.Add(new Token(TokenKind.Dot, ".", line, col));
                break;
            case '?':
                if (Match('.')) _tokens.Add(new Token(TokenKind.QuestionDot, "?.", line, col));
                else if (Match('?'))
                    _tokens.Add(Match('=') ? new Token(TokenKind.QuestionQuestionAssign, "??=", line, col) : new Token(TokenKind.QuestionQuestion, "??", line, col));
                else _tokens.Add(new Token(TokenKind.Question, "?", line, col));
                break;
            case '=':
                if (Match('=')) _tokens.Add(new Token(TokenKind.EqEq, "==", line, col));
                else if (Match('>')) _tokens.Add(new Token(TokenKind.Arrow, "=>", line, col));
                else _tokens.Add(new Token(TokenKind.Assign, "=", line, col));
                break;
            case '!':
                _tokens.Add(Match('=') ? new Token(TokenKind.BangEq, "!=", line, col) : new Token(TokenKind.Bang, "!", line, col));
                break;
            case '<':
                if (Match('<')) _tokens.Add(new Token(TokenKind.Shl, "<<", line, col));
                else if (Match('=')) _tokens.Add(new Token(TokenKind.LtEq, "<=", line, col));
                else _tokens.Add(new Token(TokenKind.Lt, "<", line, col));
                break;
            case '>':
                if (Match('>')) _tokens.Add(new Token(TokenKind.Shr, ">>", line, col));
                else if (Match('=')) _tokens.Add(new Token(TokenKind.GtEq, ">=", line, col));
                else _tokens.Add(new Token(TokenKind.Gt, ">", line, col));
                break;
            case '|':
                _tokens.Add(Match('|') ? new Token(TokenKind.OrOr, "||", line, col) : new Token(TokenKind.Pipe, "|", line, col));
                break;
            case '&':
                _tokens.Add(Match('&') ? new Token(TokenKind.AndAnd, "&&", line, col) : new Token(TokenKind.Amp, "&", line, col));
                break;
            case '+':
                if (Match('+')) _tokens.Add(new Token(TokenKind.PlusPlus, "++", line, col));
                else if (Match('=')) _tokens.Add(new Token(TokenKind.PlusAssign, "+=", line, col));
                else _tokens.Add(new Token(TokenKind.Plus, "+", line, col));
                break;
            case '-':
                if (Match('-')) _tokens.Add(new Token(TokenKind.MinusMinus, "--", line, col));
                else if (Match('=')) _tokens.Add(new Token(TokenKind.MinusAssign, "-=", line, col));
                else _tokens.Add(new Token(TokenKind.Minus, "-", line, col));
                break;
            case '*':
                if (Match('*')) _tokens.Add(new Token(TokenKind.Power, "**", line, col));
                else if (Match('=')) _tokens.Add(new Token(TokenKind.StarAssign, "*=", line, col));
                else _tokens.Add(new Token(TokenKind.Star, "*", line, col));
                break;
            case '/':
                _tokens.Add(Match('=') ? new Token(TokenKind.SlashAssign, "/=", line, col) : new Token(TokenKind.Slash, "/", line, col));
                break;
            case '%':
                _tokens.Add(Match('=') ? new Token(TokenKind.PercentAssign, "%=", line, col) : new Token(TokenKind.Percent, "%", line, col));
                break;
            default:
                throw new ScriptCompileException($"Unexpected character '{c}'.", line, col);
        }
    }

    private bool IsAtEnd => _pos >= _src.Length;
    private char Peek(int offset = 0) => (_pos + offset) < _src.Length ? _src[_pos + offset] : '\0';
    private bool Match(char expected)
    {
        if (IsAtEnd || _src[_pos] != expected) return false;
        Advance();
        return true;
    }
    private char Advance()
    {
        char c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else { _col++; }
        return c;
    }
    private static bool IsHexDigit(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
