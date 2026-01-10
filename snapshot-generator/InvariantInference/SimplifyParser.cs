using System.Globalization;
using Microsoft.BaseTypes;
using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator.InvariantInference;

/// This program file was adapted from another project.
/// Original source: github.com/xbreu/ContractChecking
/// Author: Alexandre Abreu

public class SimplifyExpression 
{
    private readonly List<SimplifyExpression> _args;
    private readonly SimplifyToken _t;
    
    private static bool _replaceArraySelectionWithMembership;
    private static Type? _sibblingNodeType;
    private static (Expression?, Type?) _forallBoundVar;

    private SimplifyExpression(SimplifyToken t, List<SimplifyExpression> args) {
        _t = t;
        _args = args;
    }

    private SimplifyExpression(SimplifyToken t) : this(t, []) {}

    public static (SimplifyExpression, List<SimplifyToken>) Parse(List<SimplifyToken> tokens) {
        SimplifyExpression result;
        if (tokens.First().Type == SimplifyToken.SimplifyTokenType.OpenParen) {
            // If our value begins with '(', it must end with a ')'
            tokens.RemoveAt(0);
            var index = FindClosingParen(tokens);

            // every token between the first '(' and last ')'
            // corresponds to a list where the first token is a function and the remaining arguments
            var nonParensTokens = tokens.GetRange(0, index);
            tokens.RemoveRange(0, index + 1);
            var fun = nonParensTokens[0];
            nonParensTokens.RemoveAt(0);
            var args = new List<SimplifyExpression>();
            while (nonParensTokens.Count > 0) {
                (var arg, nonParensTokens) = Parse(nonParensTokens);
                args.Add(arg);
            }
            result = new SimplifyExpression(fun, args);
        } else {
            result = new SimplifyExpression(tokens.First());
            tokens.RemoveAt(0);
        }
        return (result, tokens);
    }

    private static int FindClosingParen(List<SimplifyToken> tokens) {
        var depth = 1;
        var index = -1;
        while (depth != 0) {
            index += 1;
            depth += tokens[index].Type switch {
                SimplifyToken.SimplifyTokenType.OpenParen => 1,
                SimplifyToken.SimplifyTokenType.CloseParen => -1,
                _ => 0
            };
        }
        return index;
    }
    
    public Expression? ToExpression() {
        if (_t.Type == SimplifyToken.SimplifyTokenType.Forall)
            _forallBoundVar = (_args[0].ToExpression(), null);

        var strArgIdx = _args.FindIndex(arg => arg._t.Type == SimplifyToken.SimplifyTokenType.StrConst);
        if (strArgIdx != -1) {
            _sibblingNodeType = _args.Where((_, i) => i != strArgIdx)
                .Select(arg => arg.ToExpression())
                .FirstOrDefault(arg => arg?.Type != null)?.Type;
        }
        var argExprs = _args.Select(arg => arg.ToExpression()).ToList();
        if (argExprs.Contains(null))
            return null;
        
        return _t.Type switch {
            SimplifyToken.SimplifyTokenType.Add => ToBinaryExpression(BinaryExpr.Opcode.Add, argExprs),
            SimplifyToken.SimplifyTokenType.Sub => ToBinaryExpression(BinaryExpr.Opcode.Sub, argExprs),
            SimplifyToken.SimplifyTokenType.Mul => ToBinaryExpression(BinaryExpr.Opcode.Mul, argExprs),
            SimplifyToken.SimplifyTokenType.Div => ToBinaryExpression(BinaryExpr.Opcode.Div, argExprs),
            SimplifyToken.SimplifyTokenType.Mod => ToBinaryExpression(BinaryExpr.Opcode.Mod, argExprs),
            SimplifyToken.SimplifyTokenType.Eq => ToBinaryExpression(BinaryExpr.Opcode.Eq, argExprs),
            SimplifyToken.SimplifyTokenType.Neq => ToBinaryExpression(BinaryExpr.Opcode.Neq, argExprs),
            SimplifyToken.SimplifyTokenType.Le => ToBinaryExpression(BinaryExpr.Opcode.Le, argExprs),
            SimplifyToken.SimplifyTokenType.Lt => ToBinaryExpression(BinaryExpr.Opcode.Lt, argExprs),
            SimplifyToken.SimplifyTokenType.Gt => ToBinaryExpression(BinaryExpr.Opcode.Gt, argExprs),
            SimplifyToken.SimplifyTokenType.Ge => ToBinaryExpression(BinaryExpr.Opcode.Ge, argExprs),
            SimplifyToken.SimplifyTokenType.And => ToBinaryExpression(BinaryExpr.Opcode.And, argExprs),
            SimplifyToken.SimplifyTokenType.Or => ToBinaryExpression(BinaryExpr.Opcode.Or, argExprs),
            SimplifyToken.SimplifyTokenType.Iff => ToBinaryExpression(BinaryExpr.Opcode.Iff, argExprs),
            SimplifyToken.SimplifyTokenType.Implies => ToBinaryExpression(BinaryExpr.Opcode.Imp, argExprs),
            SimplifyToken.SimplifyTokenType.Not => new UnaryOpExpr(null, UnaryOpExpr.Opcode.Not, argExprs[0]) {Type = Type.Bool},
            SimplifyToken.SimplifyTokenType.ArrayLen => ToArrayLengthExpression(argExprs[0]),
            SimplifyToken.SimplifyTokenType.ArraySel => ToArraySelectExpression(argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.ArrayElems => argExprs[0],
            SimplifyToken.SimplifyTokenType.NullCmp => ToNullComparisonSubexpression(argExprs[0]),
            SimplifyToken.SimplifyTokenType.Forall => ToForallExpression(argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.Var => ToIdentifierExpression(),
            SimplifyToken.SimplifyTokenType.IntConst => new LiteralExpr(null, ((IntSimplifyToken)_t).Value) {Type = Type.Int},
            SimplifyToken.SimplifyTokenType.RealConst => new LiteralExpr(null, BigDec.FromString(((DoubleSimplifyToken)_t).Value)) {Type = Type.Real},
            SimplifyToken.SimplifyTokenType.StrConst => ToStringExpression(),
            SimplifyToken.SimplifyTokenType.True => new LiteralExpr(null, true) {Type = Type.Bool},
            SimplifyToken.SimplifyTokenType.False => new LiteralExpr(null, false) {Type = Type.Bool},
            SimplifyToken.SimplifyTokenType.Null => new LiteralExpr(new AutoGeneratedOrigin(null), null),
            _ => null
        };
    }
    
    private Expression ToIdentifierExpression() {
        var idExpr = new IdentifierExpr(null, ((VarSimplifyToken)_t).Name);
        var type = GetTypeFromVarName(((VarSimplifyToken)_t).Name);
        if (type != null)
            idExpr.Type = type;
        return idExpr;
    }

    private Expression ToBinaryExpression(BinaryExpr.Opcode op, List<Expression?> argExprs) {
        Type? type = null;
        if (op == BinaryExpr.Opcode.Add || op == BinaryExpr.Opcode.Sub || 
            op == BinaryExpr.Opcode.Mul || op == BinaryExpr.Opcode.Div || op == BinaryExpr.Opcode.Mod) {
            type = Type.Int;
        } else if (op == BinaryExpr.Opcode.And || op == BinaryExpr.Opcode.Or ||
                   op == BinaryExpr.Opcode.Iff || op == BinaryExpr.Opcode.Imp) {
            type = Type.Bool;
        } else if (op == BinaryExpr.Opcode.Eq || op == BinaryExpr.Opcode.Neq ||
                   op == BinaryExpr.Opcode.Le || op == BinaryExpr.Opcode.Lt ||
                   op == BinaryExpr.Opcode.Ge || op == BinaryExpr.Opcode.Gt) {
            type = argExprs[0]?.Type ?? argExprs[1]?.Type;
        }
        if (_forallBoundVar.Item1 != null && _forallBoundVar.Item2 == null && 
            argExprs.Any(arg => arg?.ToString() == _forallBoundVar.Item1.ToString())) {
            _forallBoundVar.Item2 = type;
        }
        
        if (op == BinaryExpr.Opcode.Eq && _replaceArraySelectionWithMembership)
            return ToArrayMembershipExpression(argExprs[0], argExprs[1]);
        if (op == BinaryExpr.Opcode.Add || op == BinaryExpr.Opcode.Sub ||
            op == BinaryExpr.Opcode.Mul || op == BinaryExpr.Opcode.Div ||
            op == BinaryExpr.Opcode.And || op == BinaryExpr.Opcode.Or)
            return ToRecBinaryExpression(op, argExprs, type);
        return new BinaryExpr(null, op, argExprs[0], argExprs[1]) {Type = type};
    }

    private Expression ToRecBinaryExpression(BinaryExpr.Opcode op, List<Expression?> argExprs, Type? type) {
        if (argExprs.Count > 2)
            return new BinaryExpr(null, op, argExprs[0], ToRecBinaryExpression(op, argExprs[1..], type)) {Type = type};
        return new BinaryExpr(null, op, argExprs[0], argExprs[1]) { Type = type };
    }

    private Expression? ToStringExpression() {
        var strValue = ((StringSimplifyToken)_t).Value;
        LiteralExpr? expr = _sibblingNodeType switch {
            CharType => new CharLiteralExpr(null, strValue) {Type = Type.Char},
            SeqType or SetType or MultiSetType or MapType => null,
            UserDefinedType uType when uType.Name.StartsWith("array") => null,
            _ => new StringLiteralExpr(null, strValue, false) {Type = Type.String()}
        };
        _sibblingNodeType = null;
        return expr;
    }

    private Expression ToArrayMembershipExpression(Expression? array, Expression? elem) {
        _replaceArraySelectionWithMembership = false;
        return new BinaryExpr(null, BinaryExpr.Opcode.In, elem, array) {Type = Type.Bool};
    }

    private Expression? ToArrayLengthExpression(Expression? array) {
        if (array == null) return null;
        var type = "";
        if (array is IdentifierExpr idExpr) {
            type = DaikonInvariantParser.TypeInfo.First(
                var => var.Item1 == idExpr.Name
            ).Item2;
        }

        return type switch {
            _ when type.StartsWith("array<") => new ExprDotName(
                null, array, new Name(null, "Length"), null
            ) {Type = Type.Int},
            // default case: seq, set, multiset and map all use the same cardinality operator
            _ => new UnaryOpExpr(null, UnaryOpExpr.Opcode.Cardinality, array) {Type = Type.Int},
        };
    }

    private Expression? ToArraySelectExpression(Expression? array, Expression? index) {
        if (array == null || index == null) return null;
        var type = "";
        if (array is IdentifierExpr idExpr) {
            type = DaikonInvariantParser.TypeInfo.First(
                var => var.Item1 == idExpr.Name
            ).Item2;
        }
        var argType = type switch {
            _ when type.StartsWith("seq<") => GetType(type[4..^1]),
            _ when type.StartsWith("array<") => GetType(type[6..^1]),
            _ => null
        };
        
        if (!type.StartsWith("array<") && !type.StartsWith("seq<")) {
            _replaceArraySelectionWithMembership = true;
            return array;
        }
        return new SeqSelectExpr(null, true, array, index, null) {Type = argType};
    }
    
    private Expression? ToNullComparisonSubexpression(Expression? obj) {
        if (obj == null || obj is not IdentifierExpr idExpr) return null;
        var type = DaikonInvariantParser.TypeInfo.First(
            var => var.Item1 == idExpr.Name
        ).Item2;
        return type.StartsWith("array") ? obj : null;
    }

    private Expression? ToForallExpression(Expression? boundVar, Expression? expr) {
        if (boundVar == null || expr == null) return null;
        if (boundVar is not IdentifierExpr idExpr) return null;
        if (_forallBoundVar.Item2 == null) return null;

        var cloner = new Cloner();
        var type = cloner.CloneType(_forallBoundVar.Item2);
        return new ForallExpr(null, 
            [new BoundVar(null, new Name(null, idExpr.Name), type)], 
            null, expr
        );
    }

    private Type? GetTypeFromVarName(string varName) {
        string typeStr = DaikonInvariantParser.TypeInfo.FirstOrDefault(
            var => var.Item1 == varName
        ).Item2;
        return GetType(typeStr);
    }
    
    private Type? GetType(string? type) {
        if (type == null) return null;
        return type switch {
            "int" or "nat" => Type.Int,
            "real" => Type.Real,
            "bool" => Type.Bool,
            "char" => Type.Char,
            "string" => Type.String(),
            _ when type.StartsWith("seq<") => new SeqType(GetType(type[4..^1])),
            _ when type.StartsWith("set<") => new SetType(true, GetType(type[4..^1])),
            _ when type.StartsWith("multiset<") => new MultiSetType(GetType(type[9..^1])),
            _ when type.StartsWith("map<") => new MapType(true, 
                GetType(type[4..^1].Split(",")[0]),  
                GetType(type[4..^1].Split(",")[1])),
            _ when type.StartsWith("array") => new UserDefinedType(
                null, type[..type.IndexOf("<")], [GetType(type[(type.IndexOf("<") + 1)..^1])]),
            _ => null
        };
    }
}

public class SimplifyToken(SimplifyToken.SimplifyTokenType type)
{
    public enum SimplifyTokenType
    {
        OpenParen,
        CloseParen,
        Add,
        Sub,
        Mul,
        Div,
        Mod,
        Eq,
        Neq,
        Le,
        Lt,
        Gt,
        Ge,
        And,
        Or,
        Iff,
        Implies,
        Not,
        ArrayLen,
        ArraySel,
        ArrayElems,
        NullCmp,
        Forall,
        Var,
        IntConst,
        RealConst,
        StrConst,
        True,
        False,
        Null
    }

    public SimplifyTokenType Type = type;

    public static SimplifyToken? GetSimplifyToken(String word) {
        return word switch {
            "+" => new SimplifyToken(SimplifyTokenType.Add),
            "-" => new SimplifyToken(SimplifyTokenType.Sub),
            "*" => new SimplifyToken(SimplifyTokenType.Mul),
            "/" => new SimplifyToken(SimplifyTokenType.Div),
            "MOD" => new SimplifyToken(SimplifyTokenType.Mod),
            "EQ" => new SimplifyToken(SimplifyTokenType.Eq),
            "NEQ" => new SimplifyToken(SimplifyTokenType.Neq),
            "<=" => new SimplifyToken(SimplifyTokenType.Le),
            "<" => new SimplifyToken(SimplifyTokenType.Lt),
            ">" => new SimplifyToken(SimplifyTokenType.Gt),
            ">=" => new SimplifyToken(SimplifyTokenType.Ge),
            "AND" => new SimplifyToken(SimplifyTokenType.And),
            "OR" => new SimplifyToken(SimplifyTokenType.Or),
            "IFF" => new SimplifyToken(SimplifyTokenType.Iff),
            "IMPLIES" => new SimplifyToken(SimplifyTokenType.Implies),
            "NOT" => new SimplifyToken(SimplifyTokenType.Not),
            "arrayLength" => new SimplifyToken(SimplifyTokenType.ArrayLen),
            "select" => new SimplifyToken(SimplifyTokenType.ArraySel),
            "selectElems" => new SimplifyToken(SimplifyTokenType.ArrayElems),
            "hash" => new SimplifyToken(SimplifyTokenType.NullCmp),
            "FORALL" => new SimplifyToken(SimplifyTokenType.Forall),
            "true" => new SimplifyToken(SimplifyTokenType.True),
            "false" => new SimplifyToken(SimplifyTokenType.False),
            "null" => new SimplifyToken(SimplifyTokenType.Null),
            _ => int.TryParse(word, out var i) ? new IntSimplifyToken(i) : 
                double.TryParse(word, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? 
                    new DoubleSimplifyToken(word) : null
        };
    }
}

internal class VarSimplifyToken(string name) : SimplifyToken(SimplifyTokenType.Var)
{
    public readonly string Name = name;
}

internal class IntSimplifyToken(int value) : SimplifyToken(SimplifyTokenType.IntConst)
{
    public readonly int Value = value;
}

internal class DoubleSimplifyToken(string value) : SimplifyToken(SimplifyTokenType.RealConst)
{
    public readonly string Value = value;
}

internal class StringSimplifyToken(string value) : SimplifyToken(SimplifyTokenType.StrConst)
{
    public readonly string Value = value.Replace("_string_space_", " ");
}
