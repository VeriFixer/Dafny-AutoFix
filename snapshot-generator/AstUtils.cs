using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public static class AstUtils
{
    /// -----------------------------------------------------
    /// My version of the (missing) static methods of Dafny's
    /// Expression class for creating resolved expressions
    /// -----------------------------------------------------
    public static Expression CreateNeq(Expression e0, Expression e1, Type ty) {
        var neq = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Neq, e0, e1);
        if (ty is SetType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.SetNeq;
        } else if (ty is SeqType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.SeqNeq;
        } else if (ty is MultiSetType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.MultiSetNeq;
        } else if (ty is MapType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.MapNeq;
        } else {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.NeqCommon;
        }
        neq.Type = Type.Bool;
        return neq;
    }
    
    public static Expression CreateGreater(Expression e0, Expression e1) {
        return new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Gt, e0, e1) {
            ResolvedOp = e0.Type.IsCharType ? BinaryExpr.ResolvedOpcode.GtChar : BinaryExpr.ResolvedOpcode.Gt,
            Type = Type.Bool
        };
    }
    
    public static Expression CreateAtLeast(Expression e0, Expression e1) {
        return new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Ge, e0, e1) {
            ResolvedOp = e0.Type.IsCharType ? BinaryExpr.ResolvedOpcode.GeChar : BinaryExpr.ResolvedOpcode.Ge,
            Type = Type.Bool
        };
    }
    
    public static Expression CreateIn(Expression e0, Expression e1, Type ty) {
        var inExpr = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.In, e0, e1);
        if (ty is SetType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InSet;
        } else if (ty is SeqType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InSeq;
        } else if (ty is MultiSetType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InMultiSet;
        } else if (ty is MapType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InMap;
        }
        inExpr.Type = Type.Bool;
        return inExpr;
    }
    
    public static Expression CreateNotIn(Expression e0, Expression e1, Type ty) {
        var notInExpr = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.NotIn, e0, e1);
        if (ty is SetType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInSet;
        } else if (ty is SeqType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInSeq;
        } else if (ty is MultiSetType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInMultiSet;
        } else if (ty is MapType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInMap;
        }
        notInExpr.Type = Type.Bool;
        return notInExpr;
    }

    public static Expression CreateStringLiteral(IOrigin token, string s) {
        var stringLit = (StringLiteralExpr)Expression.CreateStringLiteral(token, s);
        stringLit.IsVerbatim = false;
        return stringLit;
    } 
}