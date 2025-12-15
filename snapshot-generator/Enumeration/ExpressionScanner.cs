using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator.Enumeration;

public class ExpressionScanner : IdentifierAvailabilityScanner
{
    private readonly List<Expression> _integerExprs = [];
    private readonly List<(string, Type, string)> _allArgumentlessPreds = []; // (scope, return type, predicate name)
    private string _currentTopLevelDecl = "";
    
    /// -------------------------
    /// General AST node visitors
    /// -------------------------
    protected override void HandleSourceDecls(ModuleDefinition module) {
        foreach (var decl in module.SourceDecls) {
            if (decl is not TopLevelDeclWithMembers declWithMembers) // includes class, trait, datatype, etc.
                continue;
            HandleMemberDecls(declWithMembers);
        }
    }
    
    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {  
        _currentTopLevelDecl = decl.Name;
        base.HandleMemberDecls(decl);
    }
    
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (method.StartToken.line <= Enumerator.ViolationLine &&
            method.EndToken.line >= Enumerator.ViolationLine)
            Enumerator.FaultyMethod = method;
    }
    
    protected override void HandleFunction(Function function) {
        // collect argumentless boolean predicates
        if (function.Ins.Count == 0 && function.Body != null) {
            if (function.ResultType is BoolType) {
                if (InsideDefaultClass || InsideFaultyTopLevelDecl)
                    AddExpression(function.Body, Enumerator.ProgramAbstractions);
            }
            
            // argumentless predicates callable by objects of type _currentTopLevelDecl
            if (!function.IsGhost)
                _allArgumentlessPreds.Add((_currentTopLevelDecl, function.ResultType, function.Name));
        }
    }
    
    /// -------------------------
    /// Faulty method visit
    /// -------------------------
    public void VisitFaultyMethod() {
        var faultyMethod = Enumerator.FaultyMethod;
        if (faultyMethod == null)
            return;
        
        base.HandleMethod(faultyMethod);
        CollectBoolExprsFromIntegers();
        CollectExprsComplements();
    }

    protected override void HandleExpression(Expression expr) {
        CollectExpressions(expr);
        CollectArgumentlessCalls(expr);
        base.HandleExpression(expr);
    }
    
    protected override void VisitExpression(BinaryExpr bExpr) {
        if (bExpr.Op == BinaryExpr.Opcode.Imp)
            CollectImpliesMutations(bExpr);
        base.VisitExpression(bExpr);
    }

    /// -------------------------
    /// Expression collection
    /// -------------------------
    private void CollectExpressions(Expression expr) {
        if (expr.Type is BoolType)
            AddExpression(expr, Enumerator.ProgramAbstractions);
        if (expr.Type is IntType || (expr.Type is UserDefinedType intUType && intUType.Name == "nat"))
            AddExpression(expr, _integerExprs);
        if (expr.Type is UserDefinedType uType && uType.Name[^1] == '?')
            CollectBoolExprFromNullableRef(expr);
    }

    private void CollectArgumentlessCalls(Expression expr) {
        if (!(expr.Type is UserDefinedType) || expr is ThisExpr)
            return;
        
        var applicablePreds = _allArgumentlessPreds.Where(
            (pred) => pred.Item1 == expr.Type.ToString()
        );
        foreach (var pred in applicablePreds) {
            var suffixName = new Name(pred.Item3);
            var exprDotName = new ExprDotName(expr.Origin, expr, suffixName, null);
            var callExpr = new ApplySuffix(expr.Origin, null, exprDotName, [], null);
            
            if (pred.Item2 is BoolType) {
                AddExpression(callExpr, Enumerator.ProgramAbstractions);
            } else if (pred.Item2 is IntType || (pred.Item2 is UserDefinedType intUType && intUType.Name == "nat")) {
                AddExpression(callExpr, _integerExprs);
            } else if (pred.Item2 is UserDefinedType uType && uType.Name[^1] == '?') {
                CollectBoolExprFromNullableRef(callExpr);
            }
        }
    }

    private void CollectBoolExprsFromIntegers() {
        var containsZeroLiteral = false;
        foreach (var intExpr in _integerExprs) {
            if (intExpr is LiteralExpr lExpr && lExpr.Value is int i && i == 0)
                containsZeroLiteral = true;
        }
        
        foreach (var intExpr1 in _integerExprs) {
            foreach (var intExpr2 in _integerExprs) {
                if (intExpr1 == intExpr2) continue;
                CollectBoolExprsFromIntegers(intExpr1, intExpr2);
            }
            
            if (containsZeroLiteral) continue;
            var zeroLiteral = Expression.CreateIntLiteral(intExpr1.Origin, 0);
            CollectBoolExprsFromIntegers(intExpr1, zeroLiteral);
        }
    }
    
    private void CollectBoolExprsFromIntegers(Expression intExpr1, Expression intExpr2) {
        var intCompExpr = Expression.CreateEq(intExpr1, intExpr2, Type.Int);
        AddExpression(intCompExpr, Enumerator.ProgramAbstractions);
        intCompExpr = Expression.CreateLess(intExpr1, intExpr2);
        AddExpression(intCompExpr, Enumerator.ProgramAbstractions);
        intCompExpr = Expression.CreateAtMost(intExpr1, intExpr2);
        AddExpression(intCompExpr, Enumerator.ProgramAbstractions);
    }

    private void CollectBoolExprFromNullableRef(Expression expr) {
        if (expr is LiteralExpr lExpr && lExpr.Value == null)
            return;
        var nullLiteral = new LiteralExpr(expr.Origin, null);
        nullLiteral.Type = expr.Type; // expression should be resolved to avoid errors
        var nullCompExpr = Expression.CreateEq(expr, nullLiteral, expr.Type);
        AddExpression(nullCompExpr, Enumerator.ProgramAbstractions);
    }

    private void CollectImpliesMutations(BinaryExpr bExpr) { // bExpr = a ==> b
        Expression mutation = CreateExprComplement(bExpr);
        AddExpression(mutation, Enumerator.ProgramAbstractions); // not a ==> b
        var negateConsequent = CreateExprComplement(bExpr.E1);
        mutation = Expression.CreateImplies(bExpr.E0, negateConsequent, false);
        AddExpression(mutation, Enumerator.ProgramAbstractions); // a ==> not b
        mutation = Expression.CreateImplies(bExpr.E1, bExpr.E0, false);
        AddExpression(mutation, Enumerator.ProgramAbstractions); // b ==> a
    }

    private void CollectExprsComplements() {
        foreach (var expr in Enumerator.ProgramAbstractions.ToList()) {
            Expression? complement = null;
            if (expr is BinaryExpr bExpr)
                complement = CollectBinaryExprComplement(bExpr);
            complement ??= CreateExprComplement(expr);
            AddExpression(complement, Enumerator.ProgramAbstractions);
        }
    }

    private Expression? CollectBinaryExprComplement(BinaryExpr bExpr) {
        return bExpr.Op switch {
            BinaryExpr.Opcode.Eq => CreateNeq(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.Neq => Expression.CreateEq(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.Lt => CreateAtLeast(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Le => CreateGreater(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Gt => Expression.CreateAtMost(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Ge => Expression.CreateLess(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.In => CreateIn(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.NotIn => CreateNotIn(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            _ => null
        };
    }
    
    private Expression CreateExprComplement(Expression expr) {
        if (expr is UnaryOpExpr uOpExpr && uOpExpr.Op == UnaryOpExpr.Opcode.Not)
            return uOpExpr.E;
        var complement = new UnaryOpExpr(expr.Origin, UnaryOpExpr.Opcode.Not, expr) {
            Type = Type.Bool
        };
        return complement;
    }
    
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

    /// -------------------------
    /// Utils
    /// -------------------------
    private void AddExpression(Expression expr, List<Expression> collection) {
        if (expr is ApplySuffix { ResolvedExpression: FunctionCallExpr fCallExpr } &&
            fCallExpr.Function.IsGhost) return;
        if (!ExprAlreadyCollected(expr, collection))
            collection.Add(expr);
    }
    
    private bool ExprAlreadyCollected(Expression expr, List<Expression> collection) {
        return collection.Find((e) => e.ToString() == expr.ToString()) != null;
    }
}