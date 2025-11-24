using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class ExpressionScanner : Visitor
{
    private readonly List<Expression> _defaultClassAbstractions = [];
    private readonly List<Expression> _integerExprs = [];
    private readonly List<(string, string)> _allBoolArgumentlessPreds = [];
    private readonly List<string> _usedObjectNames = [];
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
        if (function.ResultType is BoolType && function.Ins.Count == 0 && function.Body != null) {
            if (!_insideDefaultClass && _insideFaultyTopLevelDecl && 
                !ExprAlreadyCollected(function.Body, SnapshotGenerator.ProgramAbstractions)) 
            {
                SnapshotGenerator.ProgramAbstractions.Add(function.Body);
            } else if (!ExprAlreadyCollected(function.Body, _defaultClassAbstractions)) {
                _defaultClassAbstractions.Add(function.Body);
            }
            // argumentless boolean predicates callable by objects of type _currentTopLevelDecl
            _allBoolArgumentlessPreds.Add((_currentTopLevelDecl, function.Name));
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
    }

    protected override void HandleExpression(Expression expr) {
        CollectExpressions(expr);
        base.HandleExpression(expr);
    }
    
    protected override void VisitExpression(NameSegment nSegExpr) {
        CollectBoolArgumentlessCalls(nSegExpr.Origin, nSegExpr.Type, nSegExpr.Name);
    }

    /// -------------------------
    /// Expression collection
    /// -------------------------
    private void CollectExpressions(Expression expr) {
        if (expr.Type is BoolType && !ExprAlreadyCollected(expr, SnapshotGenerator.ProgramAbstractions))
            SnapshotGenerator.ProgramAbstractions.Add(expr);
        if ((expr.Type is IntType || (expr.Type is UserDefinedType intUType && intUType.Name == "nat")) 
            && !ExprAlreadyCollected(expr, _integerExprs))
            _integerExprs.Add(expr);
        if (expr.Type is UserDefinedType uType && uType.Name[^1] == '?') {
            if (expr is LiteralExpr lExpr && lExpr.Value == null)
                return;
            var nullLiteral = new LiteralExpr(expr.Origin, null);
            var nullCompExpr = new BinaryExpr(expr.Origin, BinaryExpr.Opcode.Eq, expr, nullLiteral);
            if (!ExprAlreadyCollected(nullCompExpr, SnapshotGenerator.ProgramAbstractions))
                SnapshotGenerator.ProgramAbstractions.Add(nullCompExpr);
        }
    }
    
    private void CollectBoolArgumentlessCalls(IOrigin origin, Type type, string objectName) {
        if (!(type is UserDefinedType) || _usedObjectNames.Contains(objectName)) 
            return;
        var applicablePreds = _allBoolArgumentlessPreds.Where(
            (pred) => pred.Item1 == type.ToString()
        );
        foreach (var pred in applicablePreds) {
            var lhs = new NameSegment(origin, objectName, null);
            var suffixName = new Name(pred.Item2);
            var callExpr = new ExprDotName(origin, lhs, suffixName, null);
            if (!ExprAlreadyCollected(callExpr, SnapshotGenerator.ProgramAbstractions))
                SnapshotGenerator.ProgramAbstractions.Add(callExpr);
        }
        _usedObjectNames.Add(objectName);
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
                    if (!ExprAlreadyCollected(intCompExpr, SnapshotGenerator.ProgramAbstractions))
                        SnapshotGenerator.ProgramAbstractions.Add(intCompExpr);
                });
            }
            
            if (containsZeroLiteral) continue;
            ops.ForEach((o) => {
                var intCompExpr = new BinaryExpr(intExpr1.Origin, o, intExpr1, new LiteralExpr(intExpr1.Origin, 0));
                if (!ExprAlreadyCollected(intCompExpr, SnapshotGenerator.ProgramAbstractions))
                    SnapshotGenerator.ProgramAbstractions.Add(intCompExpr);
            });
        }
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private bool ExprAlreadyCollected(Expression expr, List<Expression> collection) {
        return collection.Find((e) => e.ToString() == expr.ToString()) != null;
    }
}