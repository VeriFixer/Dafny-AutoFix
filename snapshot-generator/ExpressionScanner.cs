using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class ExpressionScanner : Visitor
{
    private readonly List<Expression> _defaultClassAbstractions = [];
    private readonly List<Expression> _integerExprs = [];
    private readonly List<(string, Type, string)> _allArgumentlessPreds = []; // (scope, return type, predicate name)
    private bool _insideDefaultClass;
    private bool _insideFaultyTopLevelDecl;
    private string _currentTopLevelDecl = "";
    
    /// -------------------------
    /// General AST node visitors
    /// -------------------------
    protected override void HandleDefaultClassDecl(ModuleDefinition module) {
        if (module.DefaultClass == null) return;
        
        var faultyMethodFound = SnapshotGenerator.FaultyMethod != null;
        _insideDefaultClass = true;
        HandleMemberDecls(module.DefaultClass); // requires visit to determine if it includes faulty method
        _insideDefaultClass = false;

        if (!faultyMethodFound && SnapshotGenerator.FaultyMethod != null) // includes faulty method
            SnapshotGenerator.ProgramAbstractions.AddRange(_defaultClassAbstractions);
    }
    
    protected override void HandleSourceDecls(ModuleDefinition module) {
        foreach (var decl in module.SourceDecls) {
            if (decl is not TopLevelDeclWithMembers declWithMembers) // includes class, trait, datatype, etc.
                continue;
            HandleMemberDecls(declWithMembers);
        }
    }
    
    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {  
        _currentTopLevelDecl = decl.Name;
        if (decl.StartToken.line <= SnapshotGenerator.ViolationLine && 
            decl.EndToken.line >= SnapshotGenerator.ViolationLine)
            _insideFaultyTopLevelDecl = true;
        
        foreach (var member in decl.Members) {
            if (member is Method m) { // includes constructor
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            }
        }
        _insideFaultyTopLevelDecl = false;
    }
    
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (method.StartToken.line <= SnapshotGenerator.ViolationLine &&
            method.EndToken.line >= SnapshotGenerator.ViolationLine)
            SnapshotGenerator.FaultyMethod = method;
    }
    
    protected override void HandleFunction(Function function) {
        // collect argumentless boolean predicates
        if (function.Ins.Count == 0 && function.Body != null) {
            if (function.ResultType is BoolType) {
                if (!_insideDefaultClass && _insideFaultyTopLevelDecl) {
                    AddExpression(function.Body, SnapshotGenerator.ProgramAbstractions);
                } else {
                    AddExpression(function.Body, _defaultClassAbstractions);
                }
            }
            
            // argumentless predicates callable by objects of type _currentTopLevelDecl
            _allArgumentlessPreds.Add((_currentTopLevelDecl, function.ResultType, function.Name));
        }
    }
    
    /// -------------------------
    /// Faulty method visit
    /// -------------------------
    public void VisitFaultyMethod() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
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
            var callExpr = new ExprDotName(expr.Origin, expr, suffixName, null);
            
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
        
        List<BinaryExpr.Opcode> ops = [BinaryExpr.Opcode.Eq, BinaryExpr.Opcode.Lt, BinaryExpr.Opcode.Le];
        foreach (var intExpr1 in _integerExprs) {
            foreach (var intExpr2 in _integerExprs) {
                if (intExpr1 == intExpr2) continue;
                ops.ForEach((o) => {
                    var intCompExpr = new BinaryExpr(intExpr1.Origin, o, intExpr1, intExpr2);
                    AddExpression(intCompExpr, SnapshotGenerator.ProgramAbstractions);
                });
            }
            
            if (containsZeroLiteral) continue;
            ops.ForEach((o) => {
                var intCompExpr = new BinaryExpr(intExpr1.Origin, o, intExpr1, new LiteralExpr(intExpr1.Origin, 0));
                AddExpression(intCompExpr, SnapshotGenerator.ProgramAbstractions);
            });
        }
    }

    private void CollectBoolExprFromNullableRef(Expression expr) {
        if (expr is LiteralExpr lExpr && lExpr.Value == null)
            return;
        var nullLiteral = new LiteralExpr(expr.Origin, null);
        var nullCompExpr = new BinaryExpr(expr.Origin, BinaryExpr.Opcode.Eq, expr, nullLiteral);
        AddExpression(nullCompExpr, SnapshotGenerator.ProgramAbstractions);
    }

    private void CollectImpliesMutations(BinaryExpr bExpr) { // bExpr = a ==> b
        Expression mutation = new UnaryOpExpr(bExpr.Origin, UnaryOpExpr.Opcode.Not, bExpr); 
        AddExpression(mutation, SnapshotGenerator.ProgramAbstractions); // not a ==> b
        var negateConsequent = new UnaryOpExpr(bExpr.E1.Origin, UnaryOpExpr.Opcode.Not, bExpr.E1);
        mutation = new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Imp, bExpr.E0, negateConsequent);
        AddExpression(mutation, SnapshotGenerator.ProgramAbstractions); // a ==> not b
        mutation = new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Imp, bExpr.E1, bExpr.E0);
        AddExpression(mutation, SnapshotGenerator.ProgramAbstractions); // b ==> a
    }

    private void CollectExprsComplements() {
        foreach (var expr in SnapshotGenerator.ProgramAbstractions.ToList()) {
            Expression? complement = null;
            if (expr is BinaryExpr bExpr)
                complement = CollectBinaryExprComplement(bExpr);
            complement ??= CollectExprComplement(expr);
            AddExpression(complement, SnapshotGenerator.ProgramAbstractions);
        }
    }

    private BinaryExpr? CollectBinaryExprComplement(BinaryExpr bExpr) {
        return bExpr.Op switch {
            BinaryExpr.Opcode.Eq => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Neq, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Neq => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Eq, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Lt => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Ge, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Le => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Gt, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Gt => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Le, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.Ge => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.Lt, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.In => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.NotIn, bExpr.E0, bExpr.E1),
            BinaryExpr.Opcode.NotIn => new BinaryExpr(bExpr.Origin, BinaryExpr.Opcode.In, bExpr.E0, bExpr.E1),
            _ => null
        };
    }
    
    private Expression CollectExprComplement(Expression expr) {
        if (expr is UnaryOpExpr uOpExpr && uOpExpr.Op == UnaryOpExpr.Opcode.Not)
            return uOpExpr.E;
        return new UnaryOpExpr(expr.Origin, UnaryOpExpr.Opcode.Not, expr);
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private void AddExpression(Expression expr, List<Expression> collection) {
        if (!ExprAlreadyCollected(expr, collection))
            collection.Add(expr);
    }
    
    private bool ExprAlreadyCollected(Expression expr, List<Expression> collection) {
        return collection.Find((e) => e.ToString() == expr.ToString()) != null;
    }
}