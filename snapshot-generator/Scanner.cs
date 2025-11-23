using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class Scanner(ErrorReporter reporter) : Rewriter(reporter)
{
    private readonly List<Expression> _programAbstractions = [];
    private readonly List<Expression> _defaultClassAbstractions = [];
    private readonly List<(string, string)> _allBoolArgumentlessPreds = [];
    private bool _insideDefaultClass;
    private bool _insideFaultyTopLevelDecl;
    private string _currentTopLevelDecl = "";
    
    public override void PreResolve(ModuleDefinition module) {
        Scan(module);
    }

    public override void PostResolve(ModuleDefinition module) {
        // collect additional predicates from faulty method after fully parsing the AST
        VisitFaultyMethod();
        
        if (SnapshotGenerator.FaultyMethod == null) return;
        using StreamWriter sw = File.AppendText("abstractions.txt");
        foreach (var expr in _programAbstractions) {
            var line = expr.ToString();
            sw.WriteLine(line);
        }
    }

    private void Scan(ModuleDefinition module) {
        // first AST parse
        HandleDefaultClassDecl(module);
        HandleSourceDecls(module);
    }

    /// --------------------------
    /// Group of AST node visitors
    /// --------------------------
    private void HandleDefaultClassDecl(ModuleDefinition module) {
        if (module.DefaultClass == null) return;
        
        var faultyMethodFound = SnapshotGenerator.FaultyMethod != null;
        _insideDefaultClass = true;
        HandleMemberDecls(module.DefaultClass); // requires visit to determine if it includes faulty method
        _insideDefaultClass = false;

        if (!faultyMethodFound && SnapshotGenerator.FaultyMethod != null) // includes faulty method
            _programAbstractions.AddRange(_defaultClassAbstractions);
    }
    
    private void HandleSourceDecls(ModuleDefinition module) {
        foreach (var decl in module.SourceDecls) {
            if (decl is not TopLevelDeclWithMembers declWithMembers) // includes class, trait, datatype, etc.
                continue;
            HandleMemberDecls(declWithMembers);
        }
    }
    
    private void HandleMemberDecls(TopLevelDeclWithMembers decl) {       
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
    
    private void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (method.StartToken.line <= SnapshotGenerator.ViolationLine &&
            method.EndToken.line >= SnapshotGenerator.ViolationLine)
            SnapshotGenerator.FaultyMethod = method;
    }
    
    private void HandleFunction(Function function) {
        // collect argumentless boolean predicates
        if (function.ResultType is BoolType && function.Ins.Count == 0 && function.Body != null) {
            if (!_insideDefaultClass && _insideFaultyTopLevelDecl) {
                _programAbstractions.Add(function.Body);
            } else {
                _defaultClassAbstractions.Add(function.Body);
            }
            // argumentless boolean predicates callable by objects of type _currentTopLevelDecl
            _allBoolArgumentlessPreds.Add((_currentTopLevelDecl, function.Name));
        }
    }
    
    /// --------------------------
    /// Faulty method
    /// --------------------------
    private void VisitFaultyMethod() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod == null || faultyMethod.Body == null)
            return;

        CollectBoolArgumentlessCalls(faultyMethod);
        foreach (var stmt in faultyMethod.Body.Body) {
            if (stmt is VarDeclStmt vDeclStmt) 
                CollectBoolArgumentlessCalls(vDeclStmt);
        }
    }

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
            _programAbstractions.Add(callExpr);
        }
    }
}