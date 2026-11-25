namespace PennMUSH.Script.Compiler;

using System.Collections.Generic;
using PennMUSH.Script.Runtime;

public abstract record Stmt(int Line);

public sealed record BlockStmt(IReadOnlyList<Stmt> Statements, int Line) : Stmt(Line);
public sealed record ExprStmt(Expr Expression, int Line) : Stmt(Line);
public sealed record VarDeclStmt(string Name, string? TypeHint, bool IsConst, Expr? Initializer, int Line) : Stmt(Line);
public sealed record ListDestructureStmt(IReadOnlyList<(string Name, string? RestName)> Bindings, Expr Initializer, int Line) : Stmt(Line);
public sealed record MapDestructureStmt(IReadOnlyList<string> Keys, Expr Initializer, int Line) : Stmt(Line);
public sealed record IfStmt(Expr Condition, Stmt ThenBranch, Stmt? ElseBranch, int Line) : Stmt(Line);
public sealed record WhileStmt(Expr Condition, Stmt Body, int Line) : Stmt(Line);
public sealed record ForStmt(Stmt? Initializer, Expr? Condition, Expr? Increment, Stmt Body, int Line) : Stmt(Line);
public sealed record ForeachStmt(string ItemVar, string? IndexVar, Expr Collection, Stmt Body, int Line) : Stmt(Line);
public sealed record SwitchCase(IReadOnlyList<Expr> Values, IReadOnlyList<Stmt> Body);
public sealed record SwitchStmt(Expr Value, IReadOnlyList<SwitchCase> Cases, IReadOnlyList<Stmt>? DefaultBody, int Line) : Stmt(Line);
public sealed record BreakStmt(int Line) : Stmt(Line);
public sealed record ContinueStmt(int Line) : Stmt(Line);
public sealed record ReturnStmt(Expr? Value, int Line) : Stmt(Line);
public sealed record ThrowStmt(Expr Value, int Line) : Stmt(Line);
public sealed record CatchClause(ScriptErrorCode? ErrorFilter, string? VarName, BlockStmt Body);
public sealed record TryStmt(BlockStmt TryBlock, IReadOnlyList<CatchClause> Catches, BlockStmt? FinallyBlock, int Line) : Stmt(Line);
public sealed record AfterStmt(Expr DelaySeconds, BlockStmt Body, int Line) : Stmt(Line);

public abstract record Expr(int Line);

public sealed record LiteralExpr(ScriptValue Value, int Line) : Expr(Line);
public sealed record GlobalObjRefExpr(string RefName, int Line) : Expr(Line);
public sealed record IdentifierExpr(string Name, int Line) : Expr(Line);
public sealed record ContextExpr(TokenKind Kind, int Line) : Expr(Line); // this, caller, player, args
public sealed record BaseExpr(int Line) : Expr(Line);
public sealed record ListExpr(IReadOnlyList<(Expr Expr, bool IsSpread)> Elements, int Line) : Expr(Line);
public sealed record MapExpr(IReadOnlyList<(Expr Key, Expr Value)> Entries, int Line) : Expr(Line);
public sealed record InterpolatedStringExpr(IReadOnlyList<Expr> Parts, int Line) : Expr(Line);
public sealed record UnaryExpr(TokenKind Op, Expr Operand, bool IsPostfix, int Line) : Expr(Line);
public sealed record BinaryExpr(TokenKind Op, Expr Left, Expr Right, int Line) : Expr(Line);
public sealed record TypeCheckExpr(Expr Value, string TypeName, int Line) : Expr(Line);
public sealed record ConditionalExpr(Expr Condition, Expr ThenExpr, Expr ElseExpr, int Line) : Expr(Line);
public sealed record AssignExpr(Expr Target, TokenKind Op, Expr Value, int Line) : Expr(Line);
public sealed record MemberExpr(Expr Target, string Name, bool NullConditional, int Line) : Expr(Line);
public sealed record DynamicMemberExpr(Expr Target, Expr NameExpr, bool NullConditional, int Line) : Expr(Line);
public sealed record IndexExpr(Expr Target, Expr Index, bool FromEnd, int Line) : Expr(Line);
public sealed record SliceExpr(Expr Target, Expr? Start, Expr? End, bool StartFromEnd, bool EndFromEnd, int Line) : Expr(Line);
public sealed record CallExpr(Expr Callee, IReadOnlyList<(Expr Expr, bool IsSpread)> Arguments, bool NullConditional, int Line) : Expr(Line);
public sealed record LambdaExpr(IReadOnlyList<string> Parameters, BlockStmt Body, int Line) : Expr(Line);
public sealed record TryExpr(Expr Inner, Expr? Fallback, int Line) : Expr(Line);
