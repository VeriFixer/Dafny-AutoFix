using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class ExpressionScanner : Visitor
{
    private readonly List<Expression> _defaultClassAbstractions = [];
    private readonly List<(string, string)> _allBoolArgumentlessPreds = [];
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
            if (!_insideDefaultClass && _insideFaultyTopLevelDecl) {
                SnapshotGenerator.ProgramAbstractions.Add(function.Body);
            } else {
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

        CollectBoolArgumentlessCalls(faultyMethod);
        base.HandleMethod(faultyMethod);
    }
    
    protected override void VisitStatement(VarDeclStmt vDeclStmt) {
        CollectBoolArgumentlessCalls(vDeclStmt);
        base.VisitStatement(vDeclStmt);
    }

    /// -------------------------
    /// Expression collection
    /// -------------------------
    private void CollectBoolArgumentlessCalls(Method faultyMethod) {
        foreach (var input in faultyMethod.Ins) {
            CollectBoolArgumentlessCalls(input.Origin, input.Type, input.Name);
        }
    }

    private void CollectBoolArgumentlessCalls(VarDeclStmt vDeclStmt) {
        foreach (var lhs in vDeclStmt.Locals) {
            CollectBoolArgumentlessCalls(lhs.Origin, lhs.Type, lhs.Name);
        }
    }

    private void CollectBoolArgumentlessCalls(IOrigin origin, Type type, string objectName) {
        if (!(type is UserDefinedType)) return;
        var applicablePreds = _allBoolArgumentlessPreds.Where(
            (pred) => pred.Item1 == type.ToString()
        );
        foreach (var pred in applicablePreds) {
            var lhs = new NameSegment(origin, objectName, null);
            var suffixName = new Name(pred.Item2);
            var callExpr = new ExprDotName(origin, lhs, suffixName, null);
            SnapshotGenerator.ProgramAbstractions.Add(callExpr);
        }
    }
}