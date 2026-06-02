using Microsoft.Dafny;
using Microsoft.Z3;
using Function = Microsoft.Dafny.Function;
using LiteralExpr = Microsoft.Dafny.LiteralExpr;
using MapType = Microsoft.Dafny.MapType;
using OldExpr = Microsoft.Dafny.OldExpr;
using Type = Microsoft.Dafny.Type;

namespace AutoFix.Enumeration;

public class Enumerator() : IdentifierAvailabilityScanner(true)
{
    public static readonly List<Expression> SeqSelectExprs = [];
    public static readonly List<Expression> MapSelectExprs = [];
    private readonly List<Expression> _integerExprs = [];
    private readonly List<(string, Type, string)> _allArgumentlessPreds = []; // (scope, return type, predicate name)
    private readonly List<string> _varsToAvoid = [];
    private ModuleDefinition? _currentModule;
    private BlockStmt? _currentBlock;
    private string _currentTopLevelDecl = "";
    private bool _avoidCurrentlyVisitingExpr;
    private string? _parentExpr;
    private bool _isSecondVisit;
    private bool _consultingChildrenExprs;
    private bool _hasOldExprChild;
    private int _loopCount;
    
    /// -------------------------
    /// General AST node visitors
    /// -------------------------
    protected override void HandleDefaultClassDecl(ModuleDefinition module) {
        _currentModule = module;
        base.HandleDefaultClassDecl(module);
    }
    
    protected override void HandleSourceDecls(ModuleDefinition module) {
        _currentModule = module;
        foreach (var decl in module.SourceDecls) {
            if (decl is LiteralModuleDecl moduleDecl)
                Visit(moduleDecl.ModuleDef);
            if (decl is not TopLevelDeclWithMembers declWithMembers) // includes class, trait, datatype, etc.
                continue;
            HandleMemberDecls(declWithMembers);
        }
    }
    
    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {  
        _currentTopLevelDecl = decl.Name;
        base.HandleMemberDecls(decl);
    }
    
    protected override void HandleMethod(MethodOrConstructor methodOrConstructor) {
        if (methodOrConstructor is not Method method) return;
        
        // find the faulty method, i.e., where the violation occurs  
        if (method.StartToken.line <= AutoFix.ViolationLine &&
            method.EndToken.line >= AutoFix.ViolationLine) {
            AutoFix.FaultyMethod = method;
            AutoFix.FaultyModule = _currentModule;
        }
        base.HandleMethod(method);
    }
    
    protected override void HandleFunction(Function function) {
        // collect argumentless boolean predicates
        if (function.Ins.Count == 0 && function.Body != null) {
            if (function.ResultType is BoolType) {
                if (InsideDefaultClass || InsideFaultyTopLevelDecl)
                    AddExpression(function.Body, SnapshotGenerator.ProgramAbstractions);
            }
            
            // argumentless predicates callable by objects of type _currentTopLevelDecl
            if (!function.IsGhost)
                _allArgumentlessPreds.Add((_currentTopLevelDecl, function.ResultType, function.Name));
        }
    }

    protected override void HandleBlock(BlockStmt blockStmt) {
        var previousCurrentBlock = _currentBlock;
        _currentBlock = blockStmt;
        base.HandleBlock(blockStmt);
        _currentBlock = previousCurrentBlock;
    }

    /// -------------------------
    /// Faulty method visit
    /// -------------------------
    public void VisitFaultyMethod() {
        _isSecondVisit = true;
        var faultyMethod = AutoFix.FaultyMethod;
        if (faultyMethod == null)
            return;
        
        base.HandleMethod(faultyMethod);
        CollectBoolExprsFromIntegers();
        PruneExpressions();
    }

    protected override void HandleExpression(Expression expr) {
        var prevParentExpr = _parentExpr;
        var prevAvoidCurrentlyVisitingExpr = _avoidCurrentlyVisitingExpr;
        _parentExpr = expr.ToString();
        if (_avoidCurrentlyVisitingExpr || 
            _varsToAvoid.Contains(expr.ToString()) ||
            expr.SubExpressions
                .Select(e => e.ToString())
                .Any(e => _varsToAvoid.Contains(e)))
        {
            _avoidCurrentlyVisitingExpr = true;
            return; 
        }
        base.HandleExpression(expr);
        if (!_avoidCurrentlyVisitingExpr) {
            CollectExpressions(expr);
            CollectArgumentlessCalls(expr);     
        }
        _parentExpr = prevParentExpr;
        if (_parentExpr == null)
            _avoidCurrentlyVisitingExpr = prevAvoidCurrentlyVisitingExpr;
    }
    
    protected override void VisitExpression(BinaryExpr bExpr) {
        base.VisitExpression(bExpr);
        if (bExpr.Op == BinaryExpr.Opcode.Imp && !_avoidCurrentlyVisitingExpr)
            CollectImpliesMutations(bExpr);
    }

    protected override void VisitStatement(LoopStmt loopStmt) {
        if (loopStmt is OneBodyLoopStmt oneBodyLoopStmt && !_isSecondVisit && _currentBlock != null)
            AstUtils.LimitLoopIterations(oneBodyLoopStmt, _currentBlock, _loopCount);
        _loopCount++;
        base.VisitStatement(loopStmt);
    }

    protected override void VisitExpression(SeqSelectExpr seqSExpr) {
        if ((seqSExpr.Seq.Type is SeqType || seqSExpr.Seq.Type.ToString().StartsWith("array") || 
             seqSExpr.Seq.Type.ToString() == "string") && 
            SeqSelectExprs.All(e => e.ToString() != seqSExpr.ToString()))
            SeqSelectExprs.Add(seqSExpr);
        if (seqSExpr.Seq.Type is MapType && MapSelectExprs.All(e => e.ToString() != seqSExpr.ToString()))
            MapSelectExprs.Add(seqSExpr);
        base.VisitExpression(seqSExpr);
    }
    
    protected override void VisitStatement(ForallStmt forStmt) {
        foreach (var boundVar in forStmt.BoundVars) {
            _varsToAvoid.Add(boundVar.Name);
        }
        base.VisitStatement(forStmt);
    }
    
    protected override void VisitExpression(ComprehensionExpr compExpr) {
        foreach (var boundVar in compExpr.BoundVars) {
            _varsToAvoid.Add(boundVar.Name);
        }
        base.VisitExpression(compExpr);
    }
    
    protected override void VisitExpression(OldExpr oldExpr) {
        _hasOldExprChild = true;
        base.VisitExpression(oldExpr);
    }

    /// -------------------------
    /// Expression collection
    /// -------------------------
    private void CollectExpressions(Expression expr) {
        if (expr is ParensExpression) return;
        if (expr.Type is BoolType)
            AddExpression(expr, SnapshotGenerator.ProgramAbstractions);
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
                AddExpression(callExpr, SnapshotGenerator.ProgramAbstractions);
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
        AddExpression(intCompExpr, SnapshotGenerator.ProgramAbstractions);
        intCompExpr = Expression.CreateLess(intExpr1, intExpr2);
        AddExpression(intCompExpr, SnapshotGenerator.ProgramAbstractions);
        intCompExpr = Expression.CreateAtMost(intExpr1, intExpr2);
        AddExpression(intCompExpr, SnapshotGenerator.ProgramAbstractions);
    }

    private void CollectBoolExprFromNullableRef(Expression expr) {
        if (expr is LiteralExpr lExpr && lExpr.Value == null)
            return;
        var nullLiteral = new LiteralExpr(expr.Origin, null) { Type = expr.Type }; // expression should be resolved to avoid errors
        var nullCompExpr = Expression.CreateEq(expr, nullLiteral, expr.Type);
        AddExpression(nullCompExpr, SnapshotGenerator.ProgramAbstractions);
    }

    private void CollectImpliesMutations(BinaryExpr bExpr) { // bExpr = a ==> b
        var negateConsequent = CreateExprComplement(bExpr.E1);
        Expression mutation = Expression.CreateImplies(bExpr.E0, negateConsequent, false);
        AddExpression(mutation, SnapshotGenerator.ProgramAbstractions); // a ==> not b
        mutation = Expression.CreateImplies(bExpr.E1, bExpr.E0, false);
        AddExpression(mutation, SnapshotGenerator.ProgramAbstractions); // b ==> a
    }
    
    private Expression? CreateBinaryExprSymmetric(BinaryExpr bExpr) {
        return bExpr.Op switch {
            BinaryExpr.Opcode.Eq => Expression.CreateEq(bExpr.E1, bExpr.E0, bExpr.E0.Type),
            BinaryExpr.Opcode.Neq => AstUtils.CreateNeq(bExpr.E1, bExpr.E0, bExpr.E0.Type),
            BinaryExpr.Opcode.Iff => new BinaryExpr(bExpr.Origin, bExpr.Op, bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Imp => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Exp, bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Exp => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Imp, bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Lt => AstUtils.CreateGreater(bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Le => AstUtils.CreateAtLeast(bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Gt => Expression.CreateLess(bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.Ge => Expression.CreateAtMost(bExpr.E1, bExpr.E0),
            BinaryExpr.Opcode.And => Expression.CreateAnd(bExpr.E1, bExpr.E0, false),
            BinaryExpr.Opcode.Or => Expression.CreateOr(bExpr.E1, bExpr.E0, false),
            _ => null
        };
    }

    private Expression? CreateBinaryExprComplement(BinaryExpr bExpr) {
        return bExpr.Op switch {
            BinaryExpr.Opcode.Eq => AstUtils.CreateNeq(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.Neq => Expression.CreateEq(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.Lt => AstUtils.CreateAtLeast(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Le => AstUtils.CreateGreater(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Gt => Expression.CreateAtMost(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Ge => Expression.CreateLess(bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.In => AstUtils.CreateIn(bExpr.E0, bExpr.E1, bExpr.E0.Type),
            BinaryExpr.Opcode.NotIn => AstUtils.CreateNotIn(bExpr.E0, bExpr.E1, bExpr.E0.Type),
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

    /// -------------------------
    /// Utils
    /// -------------------------
    private void AddExpression(Expression expr, List<Expression> collection) {
        if (_consultingChildrenExprs || 
            expr is ApplySuffix { ResolvedExpression: FunctionCallExpr fCallExpr } &&
            fCallExpr.Function.IsGhost) return;
        _hasOldExprChild = false;
        _consultingChildrenExprs = true;
        HandleExpression(expr);
        _consultingChildrenExprs = false;
        if (_hasOldExprChild) return;
        
        if (!ExprAlreadyCollected(expr, collection) && !ExprIsRedundant(expr, collection))
            collection.Add(expr);
    }
    
    private bool ExprAlreadyCollected(Expression expr, List<Expression> collection) {
        return collection.Find((e) => e.ToString() == expr.ToString()) != null;
    }
    
    private bool ExprIsRedundant(Expression newExpr, List<Expression> collection) {
        if (newExpr is BinaryExpr bExpr1) {
            var symmetric = CreateBinaryExprSymmetric(bExpr1);
            if (symmetric != null && collection.Select(e => e.ToString()).Contains(symmetric.ToString())) 
                return true;
        }
        
        Expression? complement = null;
        if (newExpr is BinaryExpr bExpr2)
            complement = CreateBinaryExprComplement(bExpr2);
        complement ??= CreateExprComplement(newExpr);
        if (collection.Select(e => e.ToString()).Contains(complement.ToString())) 
            return true;

        if (complement is BinaryExpr bExpr3) {
            Expression? complementSymmetric = CreateBinaryExprSymmetric(bExpr3);
            if (complementSymmetric != null && collection.Select(e => e.ToString()).Contains(complementSymmetric.ToString()))
                return true;
        }
        
        return false;
    }
    
    private static bool AreEquivalent(Context ctx, BoolExpr expr1, BoolExpr expr2) {
        var solver = ctx.MkSolver();
        solver.Add(ctx.MkNot(ctx.MkEq(expr1, expr2)));
        var areEquivalent = solver.Check() == Status.UNSATISFIABLE;
        solver.Dispose();
        return areEquivalent;
    }

    private static bool IsImpliedBy(Context ctx, BoolExpr expr1, BoolExpr expr2) {
        var solver = ctx.MkSolver();
        // Check if expr2 ==> expr1 is a tautology, i.e., Not(expr2 ==> expr1) is unsat
        solver.Add(ctx.MkNot(ctx.MkImplies(expr2, expr1)));
        var isImpliedBy = solver.Check() == Status.UNSATISFIABLE;
        solver.Dispose();
        return isImpliedBy;
    }

    private static HashSet<BoolExpr> ImpliesAny(Context ctx, BoolExpr expr, HashSet<BoolExpr> keptExprs) {
        var uniqueExprs = new HashSet<BoolExpr> { expr };
        
        foreach (var kept in keptExprs) {
            var solver = ctx.MkSolver();
            // Check if expr ==> kept is a tautology, i.e., Not(expr ==> kept) is unsat
            solver.Add(ctx.MkNot(ctx.MkImplies(expr, kept)));
            if (solver.Check() == Status.SATISFIABLE) // does not imply
                uniqueExprs.Add(kept);
            solver.Dispose();
        }
        return uniqueExprs;
    }

    private static void PruneExpressions() {
        var ctx = new Context();
        var uniqueExprs = new HashSet<BoolExpr>();
        var dict = new Dictionary<Expression, BoolExpr>();
        
        foreach (var expr in SnapshotGenerator.ProgramAbstractions) {
            var z3Expr = Z3Parser.Parse(ctx, expr);
            if (z3Expr == null || z3Expr is not BoolExpr z3BoolExpr) continue;
            if (z3Expr.Simplify() == ctx.MkTrue() || z3Expr.Simplify() == ctx.MkFalse())
                continue;
            dict.Add(expr, z3BoolExpr);
            
            var isRedundant = false;
            // Check for equivalence or implication
            foreach (var kept in uniqueExprs) {
                if (AreEquivalent(ctx, z3BoolExpr, kept)) {
                    // we want to keep the simpler expression
                    if (z3BoolExpr.ToString().Length < kept.ToString().Length) {
                        uniqueExprs.Remove(kept);
                        uniqueExprs.Add(z3BoolExpr);
                    }
                    isRedundant = true;
                    break;
                }
                // I have meanwhile concluded that, even if an expression is implied by another,
                // it may still be more accurate at capturing the faulty program state
                // if (IsImpliedBy(ctx, z3BoolExpr, kept)) {
                //     isRedundant = true;
                //     break;
                // }
            }
            if (!isRedundant)
                uniqueExprs.Add(z3BoolExpr);
                // uniqueExprs = ImpliesAny(ctx, z3BoolExpr, uniqueExprs);
        }
        
        SnapshotGenerator.ProgramAbstractions = SnapshotGenerator.ProgramAbstractions
            .Where(expr => !dict.TryGetValue(expr, out var z3Expr) || uniqueExprs.Contains(z3Expr))
            .ToList();
        ctx.Dispose();
    }
}