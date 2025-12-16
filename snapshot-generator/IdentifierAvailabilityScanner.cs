using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator;

public class IdentifierAvailabilityScanner(bool multipleModule = false) : Visitor(multipleModule)
{
    public List<(string, Type, int?, int?)> IdentifierAvailability { get; } = []; // (name, position where availability starts, position where availability ends)
    public List<string> Ghosts { get; } = [];
    
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
        if (decl.StartToken.line <= Enumerator.ViolationLine && 
            decl.EndToken.line >= Enumerator.ViolationLine)
            InsideFaultyTopLevelDecl = true;
        
        foreach (var member in decl.Members) {
            if (member is Method m) { // includes constructor
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            } else if (member is Field f) {
                if (InsideDefaultClass || InsideFaultyTopLevelDecl)
                    IdentifierAvailability.Add((f.Name, f.Type, null, null));
                if (f.IsGhost)
                    Ghosts.Add(f.Name);
            }
        }
        InsideFaultyTopLevelDecl = false;
    }
    
    protected override void HandleMethod(Method method) {
        foreach (var input in method.Ins)
            IdentifierAvailability.Add((input.Name, input.Type, method.StartToken.pos, method.EndToken.pos));
        base.HandleMethod(method);
    }
    
    protected override void HandleBlock(BlockStmt blockStmt) {
        var prevScope = _currentScopeLimit;
        _currentScopeLimit = blockStmt.EndToken.pos;
        base.HandleBlock(blockStmt);
        _currentScopeLimit = prevScope;
    }
    
    protected override void VisitStatement(ConcreteAssignStatement cAStmt) {
        foreach (var lhs in cAStmt.Lhss) {
            if (IdentifierAvailability.Count(id => id.Item1 == lhs.ToString()) > 0)
                continue;
            IdentifierAvailability.Add((lhs.ToString(), lhs.Type, lhs.EndToken.pos, _currentScopeLimit));
        }
        base.VisitStatement(cAStmt);
    }
    
    protected override void VisitStatement(VarDeclStmt vDeclStmt) {
        foreach (var lhs in vDeclStmt.Locals) {
            IdentifierAvailability.Add((lhs.Name, lhs.Type, lhs.EndToken.pos, _currentScopeLimit));
            if (vDeclStmt.IsGhost)
                Ghosts.Add(lhs.Name);
        }
        base.VisitStatement(vDeclStmt);
    }
}