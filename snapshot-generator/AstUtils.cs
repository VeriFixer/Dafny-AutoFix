using System.Diagnostics;
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
    
    /// ----------------------------------------------------
    /// New methods for the creation of resolved expressions
    /// ----------------------------------------------------
    public static void ResolveNameSegment(NameSegment nSegExpr, Type type, IVariable? var, MemberDecl? field, TopLevelDeclWithMembers? enclosingClass = null) {
        nSegExpr.Type = type;
        Expression resolvedExpr;
        if (var != null) {
            resolvedExpr = new IdentifierExpr(nSegExpr.Origin, var);
        } else if (field != null && enclosingClass != null) {
            resolvedExpr = CreateMemberSelectExpr(nSegExpr.Origin, field, enclosingClass);
        } else {
            return;
        }
        nSegExpr.ResolvedExpression = resolvedExpr;
    }

    public static MemberSelectExpr? CreateMemberSelectExpr(IOrigin token, MemberDecl call, TopLevelDeclWithMembers? enclosingClass, Expression? obj = null) {
        Expression? myObj = null;
        if (enclosingClass != null) {
            if (enclosingClass is DefaultClassDecl) {
                myObj = new StaticReceiverExpr(token, enclosingClass, true);
            } else {
                myObj = new ImplicitThisExpr(token);
                var className = new NameSegment(token, enclosingClass.Name, []);
                myObj.Type = new UserDefinedType(token, enclosingClass.Name, enclosingClass, [], className);
            } 
        }

        if (myObj == null && obj == null) return null;
        return new MemberSelectExpr(token, obj ?? myObj, call.NameNode) {
            Member = call,
            TypeApplicationJustMember = []
        };
    }

    public static SpecialField CreateLengthSpecialField(IOrigin token, int? param = null) {
        return new SpecialField(
            token, "Length", SpecialField.ID.ArrayLength, param, 
            false, false, false, Type.Int, null
        );
    }

    /// ---------------------------------------------------
    /// New methods for the creation of resolved statements
    /// ---------------------------------------------------
    public static void ResolveNormalAssignStatement(AssignStatement aStmt) {
        aStmt.ResolvedStatements = [];
        for (var i = 0; i < aStmt.Lhss.Count; i++) {
          var resolvedAssign = new SingleAssignStmt(aStmt.Origin, aStmt.Lhss[i].Resolved, aStmt.Rhss[i]);
          aStmt.ResolvedStatements.Add(resolvedAssign);
        }
    }
    
    public static void ResolveCallAssignStatement(AssignStatement aStmt, Method call, List<Expression> args, TopLevelDeclWithMembers enclosingClass) {
        if (!(aStmt.Rhss.Count == 1 && aStmt.Rhss[0] is ExprRhs exprRhs && 
              exprRhs.Expr is ApplySuffix appSufExpr))
            return;
        
        var memberSelectExpr = CreateMemberSelectExpr(appSufExpr.Origin, call, enclosingClass);
        var resolvedStmt = new CallStmt(aStmt.Origin, [], memberSelectExpr, args);
        aStmt.ResolvedStatements = [resolvedStmt];
    }

    public static void ResolveAssignSuchThatStatement(AssignSuchThatStmt aStStmt) {
        var varLhss = new List<IVariable>();
        foreach (var lhs in aStStmt.Lhss) {
            var ide = (IdentifierExpr)lhs.Resolved;
            varLhss.Add(ide.Var);
        } 
        aStStmt.Bounds = ModuleResolver.DiscoverBestBounds_MultipleVars(varLhss, aStStmt.Expr, true);
    }
}