using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;

namespace SnapshotGenerator;

public class Scanner(ErrorReporter reporter) : Rewriter(reporter)
{
    private readonly List<Expression> _programAbstractions = [];
    private readonly List<Expression> _defaultClassAbstractions = [];
    private bool _insideDefaultClass;
    
    public override void PreResolve(ModuleDefinition module) {
        Scan(module);
        
        if (SnapshotGenerator.FaultyMethod == null) return;
        using StreamWriter sw = File.CreateText("abstractions.txt");
        foreach (var expr in _programAbstractions) {
            var line = expr.ToString();
            sw.WriteLine(line);
        }
    }

    private void Scan(ModuleDefinition module) {
        HandleDefaultClassDecl(module);
        HandleSourceDecls(module);
    }

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
            // visit if faulty method belongs to declaration
            if (!(decl.StartToken.line <= SnapshotGenerator.ViolationLine &&
                  decl.EndToken.line >= SnapshotGenerator.ViolationLine))
                return;
            HandleMemberDecls(declWithMembers);
        }
    }
    
    private void HandleMemberDecls(TopLevelDeclWithMembers decl) {
        foreach (var member in decl.Members) {
            if (member is Method m) { // includes constructor
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            }
        }
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
            if (!_insideDefaultClass) {
                _programAbstractions.Add(function.Body);
            } else {
                _defaultClassAbstractions.Add(function.Body);
            }
        }
    }
}