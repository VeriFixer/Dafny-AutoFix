using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class IdentifierAvailabilityScanner(bool multipleModule = false) : Visitor(multipleModule)
{
    public List<(IVariable?, MemberDecl?, string, Type, int?, int?)> IdentifierAvailability { get; private set; } = []; // (name, position where availability starts, position where availability ends)
    public List<string> Ghosts { get; } = [];

    private List<Formal> _currentMethodOutputs = [];
    private bool _foundFaultyMethod;
    protected bool InsideDefaultClass;
    protected bool InsideFaultyTopLevelDecl;
    private int _currentScopeLimit;
    
    protected override void HandleDefaultClassDecl(ModuleDefinition module) {
        if (module.DefaultClass == null) return;
        
        InsideDefaultClass = true;
        HandleMemberDecls(module.DefaultClass); // requires visit to determine if it includes faulty method
        InsideDefaultClass = false;
    }
    
    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {
        List<(IVariable?, MemberDecl?, string, Type, int?, int?)> prevIdentifierAvailability = IdentifierAvailability.ToList();
        if (decl.StartToken.line <= SnapshotGenerator.ViolationLine && 
            decl.EndToken.line >= SnapshotGenerator.ViolationLine) {
            InsideFaultyTopLevelDecl = true;
        } else if (!InsideDefaultClass) {
            return;
        }
        
        foreach (var member in decl.Members) {
            if (member is Method m) { // includes constructor
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            } else if (member is Field f) {
                if (InsideDefaultClass || InsideFaultyTopLevelDecl)
                    IdentifierAvailability.Add((null, f, f.Name, f.Type, null, null));
                if (f.IsGhost)
                    Ghosts.Add(f.Name);
            }
        }
        
        if (!_foundFaultyMethod)
            IdentifierAvailability = prevIdentifierAvailability;
        _foundFaultyMethod = false;
        InsideFaultyTopLevelDecl = false;
    }
    
    protected override void HandleMethod(Method method) {
        if (!(method.StartToken.line <= SnapshotGenerator.ViolationLine &&
              method.EndToken.line >= SnapshotGenerator.ViolationLine))
            return;
        _foundFaultyMethod = true;
        
        foreach (var input in method.Ins)
            IdentifierAvailability.Add((
                input, null, input.Name, input.Type, 
                method.StartToken.pos, method.EndToken.pos
            ));
        _currentMethodOutputs = method.Outs;
        base.HandleMethod(method);
        _currentMethodOutputs = [];
    }
    
    protected override void HandleBlock(BlockStmt blockStmt) {
        var prevScope = _currentScopeLimit;
        _currentScopeLimit = blockStmt.EndToken.pos;
        base.HandleBlock(blockStmt);
        _currentScopeLimit = prevScope;
    }
    
    protected override void VisitStatement(ConcreteAssignStatement cAStmt) {
        foreach (var lhs in cAStmt.Lhss) {
            if (IdentifierAvailability.Count(id => id.Item3 == lhs.ToString()) > 0)
                continue;
            var output = _currentMethodOutputs.Find(output => output.Name == lhs.ToString());
            if (output == null)
                continue;
            IdentifierAvailability.Add((
                output, null, lhs.ToString(), 
                lhs.Type, lhs.EndToken.pos, _currentScopeLimit
            ));
        }
        base.VisitStatement(cAStmt);
    }
    
    protected override void VisitStatement(VarDeclStmt vDeclStmt) {
        foreach (var lhs in vDeclStmt.Locals) {
            IdentifierAvailability.Add((
                lhs, null, lhs.Name, lhs.Type, 
                lhs.EndToken.pos, _currentScopeLimit
            ));
            if (vDeclStmt.IsGhost)
                Ghosts.Add(lhs.Name);
        }
        base.VisitStatement(vDeclStmt);
    }
}