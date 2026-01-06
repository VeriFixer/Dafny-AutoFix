using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

/// This program file was adapted from another project.
/// Original source: github.com/xbreu/ContractChecking
/// Author: Alexandre Abreu

public class SimplifyExpression 
{
    private readonly List<SimplifyExpression> _args;
    private readonly SimplifyToken _t;

    private SimplifyExpression(SimplifyToken t, List<SimplifyExpression> args) {
        _t = t;
        _args = args;
    }

    private SimplifyExpression(SimplifyToken t) : this(t, []) {}

    public static (SimplifyExpression, List<SimplifyToken>) Parse(List<SimplifyToken> tokens) {
        SimplifyExpression result;
        if (tokens.First().Type == SimplifyToken.SimplifyTokenType.OPEN_PAREN) {
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
                SimplifyToken.SimplifyTokenType.OPEN_PAREN => 1,
                SimplifyToken.SimplifyTokenType.CLOSE_PAREN => -1,
                _ => 0
            };
        }
        return index;
    }
    
    public Expression? ToExpression() {
        var argExprs = _args.Select(arg => arg.ToExpression()).ToList();
        return _t.Type switch {
            SimplifyToken.SimplifyTokenType.NOT => new UnaryOpExpr(null, UnaryOpExpr.Opcode.Not, argExprs[0]),
            SimplifyToken.SimplifyTokenType.AND => new BinaryExpr(null, BinaryExpr.Opcode.And, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.OR => new BinaryExpr(null, BinaryExpr.Opcode.Or, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.IFF => new BinaryExpr(null, BinaryExpr.Opcode.Iff, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.EQ => new BinaryExpr(null, BinaryExpr.Opcode.Eq, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.NEQ => new BinaryExpr(null, BinaryExpr.Opcode.Neq, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.LE => new BinaryExpr(null, BinaryExpr.Opcode.Le, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.LT => new BinaryExpr(null, BinaryExpr.Opcode.Lt, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.GT => new BinaryExpr(null, BinaryExpr.Opcode.Gt, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.GE => new BinaryExpr(null, BinaryExpr.Opcode.Ge, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.IMPLIES => new BinaryExpr(null, BinaryExpr.Opcode.Imp, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.ADD => new BinaryExpr(null, BinaryExpr.Opcode.Add, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.MUL => new BinaryExpr(null, BinaryExpr.Opcode.Mul, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.MOD => new BinaryExpr(null, BinaryExpr.Opcode.Mod, argExprs[0], argExprs[1]),
            SimplifyToken.SimplifyTokenType.VAR => new IdentifierExpr(null, ((VarSimplifyToken)_t).Name),
            SimplifyToken.SimplifyTokenType.CONST => new LiteralExpr(null, ((IntSimplifyToken)_t).Value),
            SimplifyToken.SimplifyTokenType.TRUE => new LiteralExpr(null, true),
            SimplifyToken.SimplifyTokenType.FALSE => new LiteralExpr(null, false),
            _ => null
        };
    }
}

public class SimplifyToken(SimplifyToken.SimplifyTokenType type)
{
    public enum SimplifyTokenType
    {
        OPEN_PAREN,
        CLOSE_PAREN,
        AND,
        OR,
        IFF,
        EQ,
        NEQ,
        LE,
        LT,
        GT,
        GE,
        IMPLIES,
        VAR,
        CONST,
        TRUE,
        FALSE,
        ADD,
        MUL,
        MOD,
        NOT
    }

    public SimplifyTokenType Type = type;

    public static SimplifyToken? GetSimplifyToken(String word) {
        return word switch {
            "true" => new SimplifyToken(SimplifyTokenType.TRUE),
            "false" => new SimplifyToken(SimplifyTokenType.FALSE),
            "NOT" => new SimplifyToken(SimplifyTokenType.NOT),
            "AND" => new SimplifyToken(SimplifyTokenType.AND),
            "OR" => new SimplifyToken(SimplifyTokenType.OR),
            "IFF" => new SimplifyToken(SimplifyTokenType.IFF),
            "EQ" => new SimplifyToken(SimplifyTokenType.EQ),
            "NEQ" => new SimplifyToken(SimplifyTokenType.NEQ),
            "IMPLIES" => new SimplifyToken(SimplifyTokenType.IMPLIES),
            "<=" => new SimplifyToken(SimplifyTokenType.LE),
            "<" => new SimplifyToken(SimplifyTokenType.LT),
            ">" => new SimplifyToken(SimplifyTokenType.GT),
            ">=" => new SimplifyToken(SimplifyTokenType.GE),
            "+" => new SimplifyToken(SimplifyTokenType.ADD),
            "*" => new SimplifyToken(SimplifyTokenType.MUL),
            "MOD" => new SimplifyToken(SimplifyTokenType.MOD),
            _ => int.TryParse(word, out _) ? new IntSimplifyToken(int.Parse(word)) : null
        };
    }
}

internal class VarSimplifyToken(string name) : SimplifyToken(SimplifyTokenType.VAR)
{
    public readonly string Name = name;
}

internal class IntSimplifyToken(int value) : SimplifyToken(SimplifyTokenType.CONST)
{
    public readonly int Value = value;
}
