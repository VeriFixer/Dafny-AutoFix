using Microsoft.Dafny;
using Microsoft.Z3;
using Type = Microsoft.Dafny.Type;

namespace AutoFix.Enumeration;

public class Z3Parser
{
    private static bool _isArithCtx;
        
    public static Expr? Parse(Context ctx, Expression expr) {
        return expr switch {
            NameSegment nSegExpr => CreateVariable(ctx, nSegExpr.Name, nSegExpr.Type),
            IdentifierExpr idExpr => CreateVariable(ctx, idExpr.Name, idExpr.Type),
            LiteralExpr litExpr => CreateLiteral(ctx, litExpr),
            BinaryExpr bExpr => ParseBinaryExpr(ctx, bExpr),
            UnaryExpr uExpr => ParseUnaryExpr(ctx, uExpr),
            NegationExpression nExpr => ParseNegationExpr(ctx, nExpr),
            ParensExpression pExpr => Parse(ctx, pExpr.E),
            ChainingExpression cExpr => Parse(ctx, cExpr.E),
            ITEExpr iteExpr => ParseITEExpr(ctx, iteExpr),
            _ => _isArithCtx ? CreateVariable(ctx, expr.ToString(), Type.Int) : null
        };
    }

    private static Expr? CreateVariable(Context ctx, string name, Type type) {
        if (new List<string> {"int", "nat", "real"}.Contains(type.ToString()))
            _isArithCtx = true;
        
        return type.ToString() switch {
            "int" or "nat" => ctx.MkIntConst(name),
            "real" => ctx.MkRealConst(name),
            "bool" => ctx.MkBoolConst(name),
            _ => null
        };
    }

    private static Expr? CreateLiteral(Context ctx, LiteralExpr litExpr) {
        if (new List<string> {"int", "nat", "real"}.Contains(litExpr.Type.ToString()))
            _isArithCtx = true;
        
        return litExpr.Type.ToString() switch {
            "int" or "nat" => int.TryParse(litExpr.Value.ToString(), out var i) ? ctx.MkInt(i) : null,
            "real" => long.TryParse(litExpr.Value.ToString(), out var r) ? ctx.MkReal(r) : null,
            "bool" => bool.TryParse(litExpr.Value.ToString(), out var b) ? ctx.MkBool(b) : null,
            _ => null
        };
    }

    private static Expr? ParseBinaryExpr(Context ctx, BinaryExpr bExpr) {
        var prevIsArithCtx = _isArithCtx;
        if (new List<BinaryExpr.Opcode> {
                BinaryExpr.Opcode.Add, BinaryExpr.Opcode.Sub, BinaryExpr.Opcode.Mul, 
                BinaryExpr.Opcode.Div, BinaryExpr.Opcode.Mod, BinaryExpr.Opcode.Le, 
                BinaryExpr.Opcode.Lt, BinaryExpr.Opcode.Gt, BinaryExpr.Opcode.Ge
            }.Contains(bExpr.Op))
            _isArithCtx = true;
        
        var e0 = Parse(ctx, bExpr.E0);
        var e1 = Parse(ctx, bExpr.E1);
        if (e0 == null && e1 != null && _isArithCtx)
            e0 = Parse(ctx, bExpr.E0);
        _isArithCtx = prevIsArithCtx;
        if (e0 == null || e1 == null) return null;
        
        return bExpr.Op switch {
            BinaryExpr.Opcode.Add => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkAdd(a0, a1) : null,
            BinaryExpr.Opcode.Sub => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkSub(a0, a1) : null,
            BinaryExpr.Opcode.Mul => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkMul(a0, a1) : null,
            BinaryExpr.Opcode.Div => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkDiv(a0, a1) : null,
            BinaryExpr.Opcode.Mod => e0 is IntExpr i0 && e1 is IntExpr i1 ? ctx.MkMod(i0, i1) : null,
            BinaryExpr.Opcode.Eq => ctx.MkEq(e0, e1),
            BinaryExpr.Opcode.Neq => ctx.MkNot(ctx.MkEq(e0, e1)),
            BinaryExpr.Opcode.Le => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkLe(a0, a1) : null,
            BinaryExpr.Opcode.Lt => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkLt(a0, a1) : null,
            BinaryExpr.Opcode.Gt => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkGt(a0, a1) : null,
            BinaryExpr.Opcode.Ge => e0 is ArithExpr a0 && e1 is ArithExpr a1 ? ctx.MkGe(a0, a1) : null,
            BinaryExpr.Opcode.And => e0 is BoolExpr b0 && e1 is BoolExpr b1 ? ctx.MkAnd(b0, b1) : null,
            BinaryExpr.Opcode.Or => e0 is BoolExpr b0 && e1 is BoolExpr b1 ? ctx.MkOr(b0, b1) : null,
            BinaryExpr.Opcode.Iff => e0 is BoolExpr b0 && e1 is BoolExpr b1 ? ctx.MkIff(b0, b1) : null,
            BinaryExpr.Opcode.Imp => e0 is BoolExpr b0 && e1 is BoolExpr b1 ? ctx.MkImplies(b0, b1) : null,
            _ => null
        };
    }

    private static BoolExpr? ParseUnaryExpr(Context ctx, UnaryExpr uExpr) {
        if (uExpr is not UnaryOpExpr { Op: UnaryOpExpr.Opcode.Not })
            return null;
        var e = Parse(ctx, uExpr.E);
        if (e == null || e is not BoolExpr b) return  null;

        return ctx.MkNot(b);
    }

    private static ArithExpr? ParseNegationExpr(Context ctx, NegationExpression nExpr) {
        var e = Parse(ctx, nExpr.E);
        if (e == null || e is not ArithExpr a) 
            return null;

        return ctx.MkUnaryMinus(a);
    }

    private static Expr? ParseITEExpr(Context ctx, ITEExpr iteExpr) {
        var test = Parse(ctx, iteExpr.Test);
        var thn = Parse(ctx, iteExpr.Thn);
        var els = Parse(ctx, iteExpr.Els);
        if (test == null || thn == null || els == null || test is not BoolExpr testBool)
            return null;
        
        return ctx.MkITE(testBool, thn, els);
    }
}