using Microsoft.Dafny;

namespace AutoFix;

public class IdentifierAvailabilityScanner(bool multipleModule = false) : Visitor(multipleModule)
{
    public List<Identifier> IdentifierAvailability { get; private set; } = [];
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
        List<Identifier> prevIdentifierAvailability = IdentifierAvailability.ToList();
        if (decl.StartToken.line <= AutoFix.ViolationLine && 
            decl.EndToken.line >= AutoFix.ViolationLine) {
            InsideFaultyTopLevelDecl = true;
        } else if (!InsideDefaultClass) {
            return;
        }
        
        foreach (var member in decl.Members) {
            if (AutoFix.TargetURI != "" && !member.Origin.Uri.LocalPath.Contains(AutoFix.TargetURI))
                continue;
            
            if (member is Method m) {
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            } else if (member is Field f) {
                if ((InsideDefaultClass || InsideFaultyTopLevelDecl) && 
                    !IdentifierAvailability.Any(id =>
                        id.Name == f.Name && id.AvailabilityStartPos == null &&
                        id.AvailabilityEndPos == null)) 
                {
                    var identifier = new Identifier(null, f, f.Type, null, null);
                    IdentifierAvailability.Add(identifier);
                }
                if (f.IsGhost)
                    Ghosts.Add(f.Name);
            }
        }
        
        if (!_foundFaultyMethod)
            IdentifierAvailability = prevIdentifierAvailability;
        _foundFaultyMethod = false;
        InsideFaultyTopLevelDecl = false;
    }
    
    protected override void HandleMethod(MethodOrConstructor method) {
        if (!(method.StartToken.line <= AutoFix.ViolationLine &&
              method.EndToken.line >= AutoFix.ViolationLine))
            return;
        _foundFaultyMethod = true;
        
        foreach (var input in method.Ins) {
            if (IdentifierAvailability.Any(id =>
                    id.Name == input.Name && id.AvailabilityStartPos == method.StartToken.pos &&
                    id.AvailabilityEndPos == method.EndToken.pos)) 
                continue;
            var identifier = new Identifier(input, null, input.Type, method.StartToken.pos, method.EndToken.pos);
            IdentifierAvailability.Add(identifier);
            if (input.IsGhost)
                Ghosts.Add(input.Name);
        }
        foreach (var output in method.Outs) {
            if (output.IsGhost)
                Ghosts.Add(output.Name);
        }
        
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
            if (IdentifierAvailability.Any(id =>
                    id.Name == lhs.ToString() && id.AvailabilityStartPos == lhs.EndToken.pos &&
                    id.AvailabilityEndPos == _currentScopeLimit)) 
                continue;
            var output = _currentMethodOutputs.Find(output => output.Name == lhs.ToString());
            if (output == null)
                continue;
            var identifier = new Identifier(output, null, lhs.Type, lhs.EndToken.pos, _currentScopeLimit);
            IdentifierAvailability.Add(identifier);
        }
        base.VisitStatement(cAStmt);
    }
    
    protected override void VisitStatement(VarDeclStmt vDeclStmt) {
        foreach (var lhs in vDeclStmt.Locals) {
            if (IdentifierAvailability.Any(id =>
                    id.Name == lhs.ToString() && id.AvailabilityStartPos == lhs.EndToken.pos &&
                    id.AvailabilityEndPos == _currentScopeLimit)) 
                continue;
            var identifier = new Identifier(lhs, null, lhs.Type, lhs.EndToken.pos, _currentScopeLimit);
            IdentifierAvailability.Add(identifier);
            if (vDeclStmt.IsGhost)
                Ghosts.Add(lhs.Name);
        }
        base.VisitStatement(vDeclStmt);
    }
}