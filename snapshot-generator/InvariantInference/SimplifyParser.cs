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

    private static readonly List<(Expression, Type?)> AllGeneratedExprsTypes = [];
    private static List<Expression> _exprsNeedingNonZeroCheck = [];
    private static List<Expression> _exprsNeedingIndexInBoundsCheck = [];
    private static bool _isTopLevelExpr = true;
    private static bool _isTopLevelForallExpr;
    private static bool _replaceArraySelectionWithMembership;
    private static Type? _sibblingNodeType;
    private static List<(Expression?, Type?)> _forallBoundVars = [];

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
    
    public List<Expression?> ToExpression() {
        var prevIsTopLevelExpr = _isTopLevelExpr;
        var prevIsTopLevelForallExpr = _isTopLevelForallExpr;
        _isTopLevelExpr = false;
        if (_t.Type == SimplifyToken.SimplifyTokenType.Forall) {
            _isTopLevelForallExpr = true;
            _args.Select(arg => arg.ToExpression())
                .SelectMany(exprList => exprList).ToList()
                .ForEach(arg => _forallBoundVars.Add((arg, null)));
        }

        var strArgIdx = _args.FindIndex(arg => arg._t.Type == SimplifyToken.SimplifyTokenType.StrConst);
        if (strArgIdx != -1) {
            _sibblingNodeType = _args.Where((_, i) => i != strArgIdx)
                .Select(arg => arg.ToExpression())
                .SelectMany(exprList => exprList)
                .Select(GetExpressionType)
                .FirstOrDefault(type => type != null);
        }
        
        var nullArgIdx = _args.FindIndex(arg => arg._t.Type == SimplifyToken.SimplifyTokenType.Null);
        if (nullArgIdx != -1) {
            _sibblingNodeType = _args.Where((_, i) => i != strArgIdx)
                .Select(arg => arg.ToExpression())
                .SelectMany(exprList => exprList)
                .Select(GetExpressionType)
                .FirstOrDefault(type => type != null);
            if (_sibblingNodeType == null || !_sibblingNodeType.ToString().Contains("?"))
                return [null];
        }
        
        var argExprs = _args.Select(arg => arg.ToExpression()).SelectMany(exprList => exprList).ToList();
        _isTopLevelExpr = prevIsTopLevelExpr;
        _isTopLevelForallExpr = prevIsTopLevelForallExpr;
        if (argExprs.Contains(null))
            return [null];
        var expr = _t.Type switch {
            SimplifyToken.SimplifyTokenType.Add => [ToBinaryExpression(BinaryExpr.Opcode.Add, argExprs)],
            SimplifyToken.SimplifyTokenType.Sub => [ToBinaryExpression(BinaryExpr.Opcode.Sub, argExprs)],
            SimplifyToken.SimplifyTokenType.Mul => [ToBinaryExpression(BinaryExpr.Opcode.Mul, argExprs)],
            SimplifyToken.SimplifyTokenType.Div => [ToBinaryExpression(BinaryExpr.Opcode.Div, argExprs)],
            SimplifyToken.SimplifyTokenType.Mod => [ToBinaryExpression(BinaryExpr.Opcode.Mod, argExprs)],
            SimplifyToken.SimplifyTokenType.Eq => [ToBinaryExpression(BinaryExpr.Opcode.Eq, argExprs)],
            SimplifyToken.SimplifyTokenType.Neq => [ToBinaryExpression(BinaryExpr.Opcode.Neq, argExprs)],
            SimplifyToken.SimplifyTokenType.Le => [ToBinaryExpression(BinaryExpr.Opcode.Le, argExprs)],
            SimplifyToken.SimplifyTokenType.Lt => [ToBinaryExpression(BinaryExpr.Opcode.Lt, argExprs)],
            SimplifyToken.SimplifyTokenType.Gt => [ToBinaryExpression(BinaryExpr.Opcode.Gt, argExprs)],
            SimplifyToken.SimplifyTokenType.Ge => [ToBinaryExpression(BinaryExpr.Opcode.Ge, argExprs)],
            SimplifyToken.SimplifyTokenType.And => [ToBinaryExpression(BinaryExpr.Opcode.And, argExprs)],
            SimplifyToken.SimplifyTokenType.Or => [ToBinaryExpression(BinaryExpr.Opcode.Or, argExprs)],
            SimplifyToken.SimplifyTokenType.Iff => [ToBinaryExpression(BinaryExpr.Opcode.Iff, argExprs)],
            SimplifyToken.SimplifyTokenType.Implies => [ToBinaryExpression(BinaryExpr.Opcode.Imp, argExprs)],
            SimplifyToken.SimplifyTokenType.Not => [ToNotUnaryExpression(argExprs[0])],
            SimplifyToken.SimplifyTokenType.ArrayLen => [ToArrayLengthExpression(argExprs[0])],
            SimplifyToken.SimplifyTokenType.ArraySel => [ToArraySelectExpression(argExprs[0], argExprs[1])],
            SimplifyToken.SimplifyTokenType.ArrayElems => [argExprs[0]],
            SimplifyToken.SimplifyTokenType.NullCmp => [ToNullComparisonSubexpression(argExprs[0])],
            SimplifyToken.SimplifyTokenType.Forall => [ToForallExpression(argExprs)],
            SimplifyToken.SimplifyTokenType.Var => ToIdentifierExpression(argExprs),
            SimplifyToken.SimplifyTokenType.IntConst => [ToIntLiteralExpression()],
            SimplifyToken.SimplifyTokenType.RealConst => [ToRealLiteralExpression()],
            SimplifyToken.SimplifyTokenType.StrConst => [ToStringExpression()],
            SimplifyToken.SimplifyTokenType.True => [ToBoolLiteralExpression(true)],
            SimplifyToken.SimplifyTokenType.False => [ToBoolLiteralExpression(false)],
            SimplifyToken.SimplifyTokenType.Null => [ToNullLiteralExpression()],
            _ => [null]
        };
        
        if (_isTopLevelExpr && _exprsNeedingNonZeroCheck.Count > 0 && expr[0] != null) {
            expr = [AddNonZeroCheck(expr[0], _exprsNeedingNonZeroCheck)];
            _exprsNeedingNonZeroCheck = [];
        }
        if (_isTopLevelExpr && _exprsNeedingIndexInBoundsCheck.Count > 0 && expr[0] != null) {
            expr = [AddIndexInBoundsCheck(expr[0], _exprsNeedingIndexInBoundsCheck)];
            _exprsNeedingIndexInBoundsCheck = [];
        }
        return expr;
    }
    
    private List<Expression?> ToIdentifierExpression(List<Expression?> argExprs) {
        var idExpr = new NameSegment(null, ((VarSimplifyToken)_t).Name, null);
        var type = GetTypeFromVarName(((VarSimplifyToken)_t).Name);
        AllGeneratedExprsTypes.Add((idExpr, type));
        
        List<Expression?> idExprs = [idExpr];
        idExprs.AddRange(argExprs);
        return idExprs;
    }

    private Expression ToBinaryExpression(BinaryExpr.Opcode op, List<Expression?> argExprs) {
        if (argExprs.Count == 1 && argExprs[0] != null)
            return argExprs[0];
        
        Type? type = null;
        Type? argType = null;
        if (op == BinaryExpr.Opcode.Add || op == BinaryExpr.Opcode.Sub || 
            op == BinaryExpr.Opcode.Mul || op == BinaryExpr.Opcode.Div || op == BinaryExpr.Opcode.Mod) {
            type = GetExpressionType(argExprs[0]) ?? GetExpressionType(argExprs[1]) ?? Type.Int;
            argType = type;
        } else if (op == BinaryExpr.Opcode.And || op == BinaryExpr.Opcode.Or ||
                   op == BinaryExpr.Opcode.Iff || op == BinaryExpr.Opcode.Imp) {
            type = Type.Bool;
            argType = Type.Bool;
        } else if (op == BinaryExpr.Opcode.Eq || op == BinaryExpr.Opcode.Neq ||
                   op == BinaryExpr.Opcode.Le || op == BinaryExpr.Opcode.Lt ||
                   op == BinaryExpr.Opcode.Ge || op == BinaryExpr.Opcode.Gt) {
            type = Type.Bool;
            argType = GetExpressionType(argExprs[0]) ?? GetExpressionType(argExprs[1]);
        }
        argExprs.Where(arg => arg != null && 
                !AllGeneratedExprsTypes.Select(exprType => exprType.Item1).Contains(arg))
            .ToList().ForEach(arg => AllGeneratedExprsTypes.Add((arg, argType)));
        _forallBoundVars = _forallBoundVars.Select(var =>
                var.Item1 != null && var.Item2 == null && 
                argExprs.Any(arg => arg?.ToString() == var.Item1.ToString()) ?
                    (var.Item1, argType) : var).ToList();
        if (op == BinaryExpr.Opcode.Mod && argExprs[1] != null)
            _exprsNeedingNonZeroCheck.Add(argExprs[1]);
        
        if (op == BinaryExpr.Opcode.Eq && _replaceArraySelectionWithMembership)
            return ToArrayMembershipExpression(argExprs[0], argExprs[1]);
        if (op == BinaryExpr.Opcode.Add || op == BinaryExpr.Opcode.Sub ||
            op == BinaryExpr.Opcode.Mul || op == BinaryExpr.Opcode.Div ||
            op == BinaryExpr.Opcode.And || op == BinaryExpr.Opcode.Or)
            return ToRecBinaryExpression(op, argExprs, type);
        var binExpr = new BinaryExpr(null, op, argExprs[0], argExprs[1]);
        AllGeneratedExprsTypes.Add((binExpr, type));
        return binExpr;
    }

    private Expression ToRecBinaryExpression(BinaryExpr.Opcode op, List<Expression?> argExprs, Type? type) {
        if (op == BinaryExpr.Opcode.Div && argExprs[1] != null)
            _exprsNeedingNonZeroCheck.Add(argExprs[1]);
        var binExpr = argExprs.Count > 2 ? 
            new BinaryExpr(null, op, argExprs[0], ToRecBinaryExpression(op, argExprs[1..], type)) : 
            new BinaryExpr(null, op, argExprs[0], argExprs[1]);
        AllGeneratedExprsTypes.Add((binExpr, type));
        return binExpr;
    }

    private Expression AddNonZeroCheck(Expression expr, List<Expression> divExprs) {
        if (divExprs.Count == 0) return expr;
        var zeroExpr = new LiteralExpr(null, 0);
        var nonZeroExpr = new BinaryExpr(null, BinaryExpr.Opcode.Neq, divExprs[0], zeroExpr);
        return divExprs.Count > 1 ? 
            new BinaryExpr(null, BinaryExpr.Opcode.And, nonZeroExpr, AddNonZeroCheck(expr, divExprs[1..])) :
            new BinaryExpr(null, BinaryExpr.Opcode.And, nonZeroExpr, expr);
    }
    
    private Expression ToNotUnaryExpression(Expression? arg) {
        var unaryExpr = new UnaryOpExpr(null, UnaryOpExpr.Opcode.Not, arg);
        AllGeneratedExprsTypes.Add((unaryExpr, Type.Bool));
        return unaryExpr;
    }

    private Expression? ToStringExpression() {
        var strValue = ((StringSimplifyToken)_t).Value;
        LiteralExpr? expr = _sibblingNodeType switch {
            CharType => new CharLiteralExpr(null, strValue) {Type = Type.Char},
            SeqType or SetType or MultiSetType or MapType => null, // TODO
            UserDefinedType uType when uType.Name.StartsWith("array") => null, // TODO
            _ => new StringLiteralExpr(null, strValue, false) {Type = Type.String()}
        };
        var type = _sibblingNodeType switch {
            CharType => Type.Char,
            SeqType or SetType or MultiSetType or MapType => null, // TODO
            UserDefinedType uType when uType.Name.StartsWith("array") => null, // TODO
            _ => Type.String()
        };
        if (expr != null)
            AllGeneratedExprsTypes.Add((expr, type));
        _sibblingNodeType = null;
        return expr;
    }

    private Expression ToIntLiteralExpression() {
        var intExpr = new LiteralExpr(null, ((IntSimplifyToken)_t).Value);
        AllGeneratedExprsTypes.Add((intExpr, Type.Int));
        return intExpr;
    }
    
    private Expression ToRealLiteralExpression() {
        var decValue = BigDec.FromString(((DoubleSimplifyToken)_t).Value);
        var realExpr = new LiteralExpr(null, decValue);
        AllGeneratedExprsTypes.Add((realExpr, Type.Real));
        return realExpr;
    }
    
    private Expression ToBoolLiteralExpression(bool value) {
        var boolExpr = new LiteralExpr(null, value);
        AllGeneratedExprsTypes.Add((boolExpr, Type.Bool));
        return boolExpr;
    }

    private Expression ToNullLiteralExpression() {
        var nullExpr = new LiteralExpr(new AutoGeneratedOrigin(null), null);
        return nullExpr;
    }

    private Expression ToArrayMembershipExpression(Expression? array, Expression? elem) {
        _replaceArraySelectionWithMembership = false;
        var inExpr = new BinaryExpr(null, BinaryExpr.Opcode.In, elem, array);
        AllGeneratedExprsTypes.Add((inExpr, Type.Bool));
        return inExpr;
    }

    private Expression? ToArrayLengthExpression(Expression? array) {
        if (array == null) return null;
        var type = "";
        if (array is NameSegment idExpr) {
            type = DaikonInvariantParser.TypeInfo.First(
                var => var.Item1 == idExpr.Name
            ).Item2;
        }
        else return null;

        Expression lengthExpr = type switch {
            _ when type.StartsWith("array<") => new ExprDotName(
                null, array, new Name(null, "Length"), null),
            // default case: seq, set, multiset and map all use the same cardinality operator
            _ => new UnaryOpExpr(null, UnaryOpExpr.Opcode.Cardinality, array)
        };
        AllGeneratedExprsTypes.Add((lengthExpr, Type.Int));
        return lengthExpr;
    }

    private Expression? ToArraySelectExpression(Expression? array, Expression? index) {
        if (array == null || index == null) return null;
        if (!AllGeneratedExprsTypes.Select(e => e.Item1).Contains(index))
            AllGeneratedExprsTypes.Add((index, Type.Int));
        var type = "";
        if (array is NameSegment idExpr) {
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
        var selectExpr = new SeqSelectExpr(null, true, array, index, null);
        if (!_isTopLevelForallExpr)
            _exprsNeedingIndexInBoundsCheck.Add(selectExpr);
        AllGeneratedExprsTypes.Add((selectExpr, argType));
        return selectExpr;
    }

    private Expression AddIndexInBoundsCheck(Expression expr, List<Expression> selectExprs) {
        if (selectExprs.Count == 0 || selectExprs[0] is not SeqSelectExpr seqSelExpr) 
            return expr;
        
        var type = "";
        if (seqSelExpr.Seq is NameSegment idExpr) {
            type = DaikonInvariantParser.TypeInfo.First(
                var => var.Item1 == idExpr.Name
            ).Item2;
        }
        
        Expression lengthExpr = type.StartsWith("array<")
            ? new ExprDotName(null, seqSelExpr.Seq, new Name(null, "Length"), null)
            : new UnaryOpExpr(null, UnaryOpExpr.Opcode.Cardinality, seqSelExpr.Seq);
        var inBoundsExpr = new BinaryExpr(null, BinaryExpr.Opcode.Lt, seqSelExpr.E0, lengthExpr);
        return selectExprs.Count > 1 ? 
            new BinaryExpr(null, BinaryExpr.Opcode.And, inBoundsExpr, AddIndexInBoundsCheck(expr, selectExprs[1..])) :
            new BinaryExpr(null, BinaryExpr.Opcode.And, inBoundsExpr, expr);
    }
    
    private Expression? ToNullComparisonSubexpression(Expression? obj) {
        if (obj == null || obj is not NameSegment idExpr) return null;
        var type = DaikonInvariantParser.TypeInfo.First(
            var => var.Item1 == idExpr.Name
        ).Item2;
        return type.StartsWith("array") ? obj : null;
    }

    private Expression? ToForallExpression(List<Expression?> args) {
        var expr = args[^1];
        
        List<BoundVar> boundVars = [];
        foreach (var boundVar in args[..^1]) {
            if (boundVar == null || boundVar is not NameSegment idExpr) continue;
            var boundVarInfo = _forallBoundVars.FirstOrDefault(
                var => var.Item1?.ToString() == idExpr.Name && var.Item2 != null);
            if (boundVarInfo.Item1 == null) return null;
            var cloner = new Cloner();
            var type = cloner.CloneType(boundVarInfo.Item2);
            boundVars.Add(new BoundVar(null, new Name(null, idExpr.Name), type));
            DaikonInvariantParser.TypeInfo.Add((idExpr.Name, type.ToString().Replace(" ", "")));
        }
        if (boundVars.Count == 0 || expr == null) return null;
        
        var forallExpr = new ForallExpr(null, boundVars, null, expr);
        AllGeneratedExprsTypes.Add((forallExpr, Type.Bool));
        return forallExpr;
    }

    private Type? GetExpressionType(Expression? expr) {
        if (expr == null) return null;
        return AllGeneratedExprsTypes.FirstOrDefault(
            exprType => 
                exprType.Item1 == expr &&
                exprType.Item2 != null
        ).Item2;
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
