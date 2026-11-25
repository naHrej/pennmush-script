namespace PennMUSH.Script.Compiler;

using System;
using System.Collections.Generic;
using PennMUSH.Script.Runtime;

public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    private static readonly HashSet<string> KnownTypeHints = new(StringComparer.Ordinal)
    {
        "int", "float", "bool", "string", "list", "map", "obj", "error", "func"
    };

    public Parser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
    }

    public List<Stmt> ParseProgram()
    {
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.Eof))
            stmts.Add(ParseStatement());
        return stmts;
    }

    private Stmt ParseStatement()
    {
        var t = Current;
        return t.Kind switch
        {
            TokenKind.LBrace => ParseBlock(),
            TokenKind.Var => ParseVarDeclaration(isConst: false, typeHint: null),
            TokenKind.Const => ParseVarDeclaration(isConst: true, typeHint: null),
            TokenKind.Identifier when IsTypedVarDeclaration() => ParseTypedVarDeclaration(),
            TokenKind.If => ParseIf(),
            TokenKind.While => ParseWhile(),
            TokenKind.For => ParseFor(),
            TokenKind.Foreach => ParseForeach(),
            TokenKind.Switch => ParseSwitch(),
            TokenKind.Break => ParseBreak(),
            TokenKind.Continue => ParseContinue(),
            TokenKind.Return => ParseReturn(),
            TokenKind.Throw => ParseThrow(),
            TokenKind.Try when Peek(1).Kind == TokenKind.LBrace => ParseTryStmt(),
            TokenKind.After => ParseAfter(),
            TokenKind.Semicolon => ConsumeSemicolonAsEmptyBlock(),
            _ => ParseExprStmt()
        };
    }

    private BlockStmt ConsumeSemicolonAsEmptyBlock()
    {
        var tok = Advance();
        return new BlockStmt(Array.Empty<Stmt>(), tok.Line);
    }

    private bool IsTypedVarDeclaration()
    {
        return Current.Kind == TokenKind.Identifier
            && KnownTypeHints.Contains(Current.Lexeme)
            && Peek(1).Kind == TokenKind.Identifier;
    }

    private Stmt ParseTypedVarDeclaration()
    {
        string typeHint = Advance().Lexeme;
        return ParseVarBinding(isConst: false, typeHint: typeHint, line: Current.Line);
    }

    private BlockStmt ParseBlock()
    {
        var open = Expect(TokenKind.LBrace, "Expected '{'.");
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            stmts.Add(ParseStatement());
        Expect(TokenKind.RBrace, "Expected '}'.");
        return new BlockStmt(stmts, open.Line);
    }

    private Stmt ParseVarDeclaration(bool isConst, string? typeHint)
    {
        var kw = Advance(); // var or const
        if (!isConst && Check(TokenKind.LBracket))
            return ParseListDestructure(kw.Line);
        if (!isConst && Check(TokenKind.LBrace))
            return ParseMapDestructure(kw.Line);
        return ParseVarBinding(isConst, typeHint, kw.Line);
    }

    private VarDeclStmt ParseVarBinding(bool isConst, string? typeHint, int line, bool expectSemicolon = true)
    {
        var name = Expect(TokenKind.Identifier, "Expected variable name.");
        Expr? init = null;
        if (Match(TokenKind.Assign))
            init = ParseExpression();
        else if (isConst)
            throw Error("const declaration must have an initializer.");
        if (expectSemicolon)
            Expect(TokenKind.Semicolon, "Expected ';' after variable declaration.");
        return new VarDeclStmt(name.Lexeme, typeHint, isConst, init, line);
    }

    private ListDestructureStmt ParseListDestructure(int line)
    {
        Expect(TokenKind.LBracket, "Expected '['.");
        var bindings = new List<(string Name, string? RestName)>();
        while (!Check(TokenKind.RBracket))
        {
            if (Match(TokenKind.Spread))
            {
                var rest = Expect(TokenKind.Identifier, "Expected rest variable name.");
                bindings.Add(("", rest.Lexeme));
                break;
            }
            var item = Expect(TokenKind.Identifier, "Expected variable name.");
            bindings.Add((item.Lexeme, null));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBracket, "Expected ']'.");
        Expect(TokenKind.Assign, "Expected '=' in destructuring.");
        var init = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'.");
        return new ListDestructureStmt(bindings, init, line);
    }

    private MapDestructureStmt ParseMapDestructure(int line)
    {
        Expect(TokenKind.LBrace, "Expected '{'.");
        var keys = new List<string>();
        while (!Check(TokenKind.RBrace))
        {
            var key = Expect(TokenKind.Identifier, "Expected key identifier.");
            keys.Add(key.Lexeme);
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBrace, "Expected '}'.");
        Expect(TokenKind.Assign, "Expected '=' in destructuring.");
        var init = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'.");
        return new MapDestructureStmt(keys, init, line);
    }

    private IfStmt ParseIf()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'if'.");
        var cond = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        var thenBranch = ParseStatement();
        Stmt? elseBranch = Match(TokenKind.Else) ? ParseStatement() : null;
        return new IfStmt(cond, thenBranch, elseBranch, kw.Line);
    }

    private WhileStmt ParseWhile()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'while'.");
        var cond = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        var body = ParseStatement();
        return new WhileStmt(cond, body, kw.Line);
    }

    private ForStmt ParseFor()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'for'.");
        Stmt? init = null;
        if (!Check(TokenKind.Semicolon))
        {
            if (Match(TokenKind.Var))
                init = ParseVarBinding(false, null, Current.Line, expectSemicolon: false);
            else if (IsTypedVarDeclaration())
                init = ParseVarBinding(false, Advance().Lexeme, Current.Line, expectSemicolon: false);
            else
                init = new ExprStmt(ParseExpression(), Current.Line);
        }
        Expect(TokenKind.Semicolon, "Expected ';' after for initializer.");

        Expr? cond = !Check(TokenKind.Semicolon) ? ParseExpression() : null;
        Expect(TokenKind.Semicolon, "Expected ';' after for condition.");

        Expr? inc = !Check(TokenKind.RParen) ? ParseExpression() : null;
        Expect(TokenKind.RParen, "Expected ')'.");

        var body = ParseStatement();
        return new ForStmt(init, cond, inc, body, kw.Line);
    }

    private ForeachStmt ParseForeach()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'foreach'.");
        if (Check(TokenKind.Var)) Advance();
        else if (Check(TokenKind.Identifier) && KnownTypeHints.Contains(Current.Lexeme)) Advance();

        string itemVar;
        string? indexVar = null;
        if (Match(TokenKind.LParen))
        {
            var first = Expect(TokenKind.Identifier, "Expected index/key variable name.");
            Expect(TokenKind.Comma, "Expected ',' in foreach tuple.");
            var second = Expect(TokenKind.Identifier, "Expected item variable name.");
            Expect(TokenKind.RParen, "Expected ')'.");
            indexVar = first.Lexeme;
            itemVar = second.Lexeme;
        }
        else
        {
            itemVar = Expect(TokenKind.Identifier, "Expected loop variable name.").Lexeme;
        }

        Expect(TokenKind.In, "Expected 'in'.");
        var collection = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        var body = ParseStatement();
        return new ForeachStmt(itemVar, indexVar, collection, body, kw.Line);
    }

    private SwitchStmt ParseSwitch()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'switch'.");
        var value = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        Expect(TokenKind.LBrace, "Expected '{'.");

        var cases = new List<SwitchCase>();
        List<Stmt>? defaultBody = null;

        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Match(TokenKind.Case))
            {
                var vals = new List<Expr> { ParseExpression() };
                Expect(TokenKind.Colon, "Expected ':' after case value.");
                while (Match(TokenKind.Case))
                {
                    vals.Add(ParseExpression());
                    Expect(TokenKind.Colon, "Expected ':' after case value.");
                }
                var body = ParseSwitchBody();
                cases.Add(new SwitchCase(vals, body));
            }
            else if (Match(TokenKind.Default))
            {
                Expect(TokenKind.Colon, "Expected ':' after default.");
                defaultBody = ParseSwitchBody();
            }
            else
            {
                throw Error("Expected 'case' or 'default' inside switch.");
            }
        }

        Expect(TokenKind.RBrace, "Expected '}'.");
        return new SwitchStmt(value, cases, defaultBody, kw.Line);
    }

    private List<Stmt> ParseSwitchBody()
    {
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.Case) && !Check(TokenKind.Default) && !Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            stmts.Add(ParseStatement());
        return stmts;
    }

    private BreakStmt ParseBreak()
    {
        var kw = Advance();
        Expect(TokenKind.Semicolon, "Expected ';' after 'break'.");
        return new BreakStmt(kw.Line);
    }

    private ContinueStmt ParseContinue()
    {
        var kw = Advance();
        Expect(TokenKind.Semicolon, "Expected ';' after 'continue'.");
        return new ContinueStmt(kw.Line);
    }

    private ReturnStmt ParseReturn()
    {
        var kw = Advance();
        Expr? val = Check(TokenKind.Semicolon) ? null : ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';' after 'return'.");
        return new ReturnStmt(val, kw.Line);
    }

    private ThrowStmt ParseThrow()
    {
        var kw = Advance();
        var val = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';' after 'throw'.");
        return new ThrowStmt(val, kw.Line);
    }

    private TryStmt ParseTryStmt()
    {
        var kw = Advance();
        var tryBlock = ParseBlock();
        var catches = new List<CatchClause>();
        while (Match(TokenKind.Catch))
        {
            ScriptErrorCode? errFilter = null;
            string? varName = null;
            if (Match(TokenKind.LParen))
            {
                if (Check(TokenKind.ErrorLiteral))
                {
                    errFilter = Advance().ErrorValue;
                    if (Check(TokenKind.Identifier))
                        varName = Advance().Lexeme;
                }
                else
                {
                    if (Check(TokenKind.Var)) Advance();
                    varName = Expect(TokenKind.Identifier, "Expected catch variable name.").Lexeme;
                }
                Expect(TokenKind.RParen, "Expected ')'.");
            }
            var catchBlock = ParseBlock();
            catches.Add(new CatchClause(errFilter, varName, catchBlock));
        }

        BlockStmt? finallyBlock = Match(TokenKind.Finally) ? ParseBlock() : null;
        if (catches.Count == 0 && finallyBlock == null)
            throw Error("Expected 'catch' or 'finally' after 'try'.");

        return new TryStmt(tryBlock, catches, finallyBlock, kw.Line);
    }

    private AfterStmt ParseAfter()
    {
        var kw = Advance();
        Expect(TokenKind.LParen, "Expected '(' after 'after'.");
        var delay = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        var body = ParseBlock();
        return new AfterStmt(delay, body, kw.Line);
    }

    private ExprStmt ParseExprStmt()
    {
        int line = Current.Line;
        var expr = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';' after expression.");
        return new ExprStmt(expr, line);
    }

    // Expressions
    public Expr ParseExpression() => ParseAssignment();

    private Expr ParseAssignment()
    {
        if (IsLambdaStart())
            return ParseLambda();

        var expr = ParseTernary();
        if (Check(TokenKind.Assign) || Check(TokenKind.PlusAssign) || Check(TokenKind.MinusAssign)
            || Check(TokenKind.StarAssign) || Check(TokenKind.SlashAssign) || Check(TokenKind.PercentAssign)
            || Check(TokenKind.QuestionQuestionAssign))
        {
            var op = Advance();
            var rhs = ParseAssignment();
            return new AssignExpr(expr, op.Kind, rhs, op.Line);
        }
        return expr;
    }

    private bool IsLambdaStart()
    {
        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Arrow)
            return true;
        if (!Check(TokenKind.LParen))
            return false;

        int i = _pos + 1;
        if (i < _tokens.Count && _tokens[i].Kind == TokenKind.RParen)
            return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.Arrow;

        while (i < _tokens.Count && _tokens[i].Kind == TokenKind.Identifier)
        {
            i++;
            if (i < _tokens.Count && _tokens[i].Kind == TokenKind.Comma) { i++; continue; }
            if (i < _tokens.Count && _tokens[i].Kind == TokenKind.RParen)
                return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.Arrow;
            return false;
        }
        return false;
    }

    private LambdaExpr ParseLambda()
    {
        int line = Current.Line;
        var parms = new List<string>();
        if (Check(TokenKind.Identifier))
        {
            parms.Add(Advance().Lexeme);
        }
        else
        {
            Expect(TokenKind.LParen, "Expected '('.");
            if (!Check(TokenKind.RParen))
            {
                do
                {
                    parms.Add(Expect(TokenKind.Identifier, "Expected parameter name.").Lexeme);
                } while (Match(TokenKind.Comma));
            }
            Expect(TokenKind.RParen, "Expected ')'.");
        }
        Expect(TokenKind.Arrow, "Expected '=>'.");
        BlockStmt body = Check(TokenKind.LBrace)
            ? ParseBlock()
            : new BlockStmt(new Stmt[] { new ReturnStmt(ParseAssignment(), line) }, line);
        return new LambdaExpr(parms, body, line);
    }

    private Expr ParseTernary()
    {
        var expr = ParseNullCoalesce();
        if (Match(TokenKind.Question))
        {
            int line = Previous.Line;
            var thenExpr = ParseAssignment();
            Expect(TokenKind.Colon, "Expected ':' in conditional expression.");
            var elseExpr = ParseAssignment();
            return new ConditionalExpr(expr, thenExpr, elseExpr, line);
        }
        return expr;
    }

    private Expr ParseNullCoalesce()
    {
        var expr = ParseOr();
        while (Match(TokenKind.QuestionQuestion))
        {
            var op = Previous;
            var right = ParseOr();
            expr = new BinaryExpr(op.Kind, expr, right, op.Line);
        }
        return expr;
    }

    private Expr ParseOr()
    {
        var expr = ParseAnd();
        while (Match(TokenKind.OrOr))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseAnd(), op.Line);
        }
        return expr;
    }

    private Expr ParseAnd()
    {
        var expr = ParseBitOr();
        while (Match(TokenKind.AndAnd))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseBitOr(), op.Line);
        }
        return expr;
    }

    private Expr ParseBitOr()
    {
        var expr = ParseBitXor();
        while (Match(TokenKind.Pipe))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseBitXor(), op.Line);
        }
        return expr;
    }

    private Expr ParseBitXor()
    {
        var expr = ParseBitAnd();
        while (Match(TokenKind.Caret))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseBitAnd(), op.Line);
        }
        return expr;
    }

    private Expr ParseBitAnd()
    {
        var expr = ParseEquality();
        while (Match(TokenKind.Amp))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseEquality(), op.Line);
        }
        return expr;
    }

    private Expr ParseEquality()
    {
        var expr = ParseRelational();
        while (Match(TokenKind.EqEq) || Match(TokenKind.BangEq))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseRelational(), op.Line);
        }
        return expr;
    }

    private Expr ParseRelational()
    {
        var expr = ParseShift();
        while (true)
        {
            if (Match(TokenKind.Lt) || Match(TokenKind.LtEq) || Match(TokenKind.Gt) || Match(TokenKind.GtEq) || Match(TokenKind.In))
            {
                var op = Previous;
                expr = new BinaryExpr(op.Kind, expr, ParseShift(), op.Line);
            }
            else if (Match(TokenKind.Is))
            {
                var op = Previous;
                var typeTok = ExpectTypeName();
                expr = new TypeCheckExpr(expr, typeTok, op.Line);
            }
            else break;
        }
        return expr;
    }

    private string ExpectTypeName()
    {
        if (Check(TokenKind.Identifier) || Check(TokenKind.Null))
            return Advance().Lexeme;
        throw Error("Expected type name after 'is'.");
    }

    private Expr ParseShift()
    {
        var expr = ParseAdditive();
        while (Match(TokenKind.Shl) || Match(TokenKind.Shr))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseAdditive(), op.Line);
        }
        return expr;
    }

    private Expr ParseAdditive()
    {
        var expr = ParseMultiplicative();
        while (Match(TokenKind.Plus) || Match(TokenKind.Minus))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParseMultiplicative(), op.Line);
        }
        return expr;
    }

    private Expr ParseMultiplicative()
    {
        var expr = ParsePower();
        while (Match(TokenKind.Star) || Match(TokenKind.Slash) || Match(TokenKind.Percent))
        {
            var op = Previous;
            expr = new BinaryExpr(op.Kind, expr, ParsePower(), op.Line);
        }
        return expr;
    }

    private Expr ParsePower()
    {
        var expr = ParseUnary();
        if (Match(TokenKind.Power))
        {
            var op = Previous;
            return new BinaryExpr(op.Kind, expr, ParsePower(), op.Line); // right-associative
        }
        return expr;
    }

    private Expr ParseUnary()
    {
        if (Match(TokenKind.Bang) || Match(TokenKind.Tilde) || Match(TokenKind.Minus) || Match(TokenKind.PlusPlus) || Match(TokenKind.MinusMinus))
        {
            var op = Previous;
            return new UnaryExpr(op.Kind, ParseUnary(), IsPostfix: false, op.Line);
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Match(TokenKind.Dot) || Match(TokenKind.QuestionDot))
            {
                bool nullCond = Previous.Kind == TokenKind.QuestionDot;
                int line = Previous.Line;
                if (!nullCond && Match(TokenKind.LParen))
                {
                    var nameExpr = ParseExpression();
                    Expect(TokenKind.RParen, "Expected ')'.");
                    expr = new DynamicMemberExpr(expr, nameExpr, nullCond, line);
                }
                else
                {
                    var member = Expect(TokenKind.Identifier, "Expected member name.");
                    expr = new MemberExpr(expr, member.Lexeme, nullCond, line);
                }
            }
            else if (Match(TokenKind.LParen))
            {
                int line = Previous.Line;
                var args = ParseArgumentList();
                Expect(TokenKind.RParen, "Expected ')'.");
                bool nullCond = expr is MemberExpr { NullConditional: true };
                expr = new CallExpr(expr, args, nullCond, line);
            }
            else if (Match(TokenKind.LBracket))
            {
                int line = Previous.Line;
                expr = ParseIndexOrSlice(expr, line);
            }
            else if (Match(TokenKind.PlusPlus) || Match(TokenKind.MinusMinus))
            {
                var op = Previous;
                expr = new UnaryExpr(op.Kind, expr, IsPostfix: true, op.Line);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private Expr ParseIndexOrSlice(Expr target, int line)
    {
        if (Match(TokenKind.DotDot))
        {
            Expr? end = null;
            bool endFromEnd = false;
            if (!Check(TokenKind.RBracket))
            {
                endFromEnd = Match(TokenKind.Caret);
                end = ParseExpression();
            }
            Expect(TokenKind.RBracket, "Expected ']'.");
            return new SliceExpr(target, null, end, false, endFromEnd, line);
        }

        bool startFromEnd = Match(TokenKind.Caret);
        var first = ParseExpression();
        if (Match(TokenKind.DotDot))
        {
            Expr? end = null;
            bool endFromEnd = false;
            if (!Check(TokenKind.RBracket))
            {
                endFromEnd = Match(TokenKind.Caret);
                end = ParseExpression();
            }
            Expect(TokenKind.RBracket, "Expected ']'.");
            return new SliceExpr(target, first, end, startFromEnd, endFromEnd, line);
        }

        Expect(TokenKind.RBracket, "Expected ']'.");
        return new IndexExpr(target, first, startFromEnd, line);
    }

    private List<(Expr Expr, bool IsSpread)> ParseArgumentList()
    {
        var args = new List<(Expr, bool)>();
        if (Check(TokenKind.RParen)) return args;
        do
        {
            bool spread = Match(TokenKind.Spread);
            args.Add((ParseExpression(), spread));
        } while (Match(TokenKind.Comma));
        return args;
    }

    private Expr ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.IntLiteral: Advance(); return new LiteralExpr(ScriptValue.FromInt(t.IntValue), t.Line);
            case TokenKind.FloatLiteral: Advance(); return new LiteralExpr(ScriptValue.FromFloat(t.FloatValue), t.Line);
            case TokenKind.StringLiteral: Advance(); return new LiteralExpr(ScriptValue.FromString(t.Lexeme), t.Line);
            case TokenKind.ObjLiteral: Advance(); return new LiteralExpr(ScriptValue.FromObj(t.ObjValue), t.Line);
            case TokenKind.GlobalObjRef: Advance(); return new GlobalObjRefExpr(t.Lexeme, t.Line);
            case TokenKind.ErrorLiteral: Advance(); return new LiteralExpr(ScriptValue.FromError(t.ErrorValue), t.Line);
            case TokenKind.True: Advance(); return new LiteralExpr(ScriptValue.True, t.Line);
            case TokenKind.False: Advance(); return new LiteralExpr(ScriptValue.False, t.Line);
            case TokenKind.Null: Advance(); return new LiteralExpr(ScriptValue.Null, t.Line);
            case TokenKind.This:
            case TokenKind.Caller:
            case TokenKind.Player:
            case TokenKind.Args:
                Advance();
                return new ContextExpr(t.Kind, t.Line);
            case TokenKind.Base:
                Advance();
                return new BaseExpr(t.Line);
            case TokenKind.Identifier:
                Advance();
                return new IdentifierExpr(t.Lexeme, t.Line);
            case TokenKind.LParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenKind.RParen, "Expected ')'.");
                return inner;
            case TokenKind.LBracket:
                return ParseListLiteral();
            case TokenKind.LBrace:
                return ParseMapLiteral();
            case TokenKind.InterpStart:
                return ParseInterpolatedString();
            case TokenKind.Try:
                return ParseTryExpr();
            default:
                throw Error($"Unexpected token '{t.Lexeme}'.");
        }
    }

    private ListExpr ParseListLiteral()
    {
        var open = Expect(TokenKind.LBracket, "Expected '['.");
        var items = new List<(Expr, bool)>();
        while (!Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
        {
            bool spread = Match(TokenKind.Spread);
            items.Add((ParseExpression(), spread));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBracket, "Expected ']'.");
        return new ListExpr(items, open.Line);
    }

    private MapExpr ParseMapLiteral()
    {
        var open = Expect(TokenKind.LBrace, "Expected '{'.");
        if (Match(TokenKind.Colon))
        {
            Expect(TokenKind.RBrace, "Expected '}' after '{:'.");
            return new MapExpr(Array.Empty<(Expr, Expr)>(), open.Line);
        }

        var entries = new List<(Expr, Expr)>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var key = ParseExpression();
            Expect(TokenKind.Colon, "Expected ':' in map literal.");
            var val = ParseExpression();
            entries.Add((key, val));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBrace, "Expected '}'.");
        return new MapExpr(entries, open.Line);
    }

    private InterpolatedStringExpr ParseInterpolatedString()
    {
        var start = Expect(TokenKind.InterpStart, "Expected '$\"'.");
        var parts = new List<Expr>();
        while (!Check(TokenKind.InterpEnd) && !Check(TokenKind.Eof))
        {
            if (Match(TokenKind.InterpText))
            {
                parts.Add(new LiteralExpr(ScriptValue.FromString(Previous.Lexeme), Previous.Line));
            }
            else if (Match(TokenKind.InterpExprStart))
            {
                parts.Add(ParseExpression());
                Expect(TokenKind.InterpExprEnd, "Expected '}' in interpolated string.");
            }
            else
            {
                throw Error($"Unexpected token '{Current.Lexeme}' in interpolated string.");
            }
        }
        Expect(TokenKind.InterpEnd, "Expected closing '\"' for interpolated string.");
        return new InterpolatedStringExpr(parts, start.Line);
    }

    private TryExpr ParseTryExpr()
    {
        var kw = Advance(); // try
        Expect(TokenKind.LParen, "Expected '(' after inline 'try'.");
        var inner = ParseExpression();
        Expect(TokenKind.RParen, "Expected ')'.");
        Expr? fallback = Match(TokenKind.QuestionQuestion) ? ParseNullCoalesce() : null;
        return new TryExpr(inner, fallback, kw.Line);
    }

    private Token Current => _tokens[_pos];
    private Token Previous => _tokens[_pos - 1];
    private Token Peek(int offset) => (_pos + offset) < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];
    private bool Check(TokenKind kind) => Current.Kind == kind;
    private bool Match(TokenKind kind)
    {
        if (!Check(kind)) return false;
        _pos++;
        return true;
    }
    private Token Advance() => _tokens[_pos++];
    private Token Expect(TokenKind kind, string message)
    {
        if (Check(kind)) return Advance();
        throw Error(message);
    }
    private ScriptCompileException Error(string message) => new(message, Current.Line, Current.Column);
}
