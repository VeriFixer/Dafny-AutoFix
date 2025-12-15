using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ExpressionTraceBuilder : Visitor
{
    private List<(string, int?, int?)> IdentifierAvailability { get; }
    public List<string> Ghosts { get; } = [];
    private readonly List<(Expression, int?, int?)> _exprAvailabilityScope;
    private List<Statement> _newBlockBody = [];
    
    private int? _currentExprAvailabilityScopeStart;
    private int? _currentExprAvailabilityScopeEnd;
    private bool _hasGhostChild;
    private bool _hasIdentifierChild;

    public ExpressionTraceBuilder(List<(string, int?, int?)> identifierAvailability, List<string> ghosts) {
        IdentifierAvailability = identifierAvailability;
        Ghosts = ghosts;
        _exprAvailabilityScope = [];
        // identify the scope in which each program abstraction is observable according to the scope in which its subexpressions are defined
        foreach (var expr in SnapshotGenerator.ProgramAbstractions) {
            HandleExpression(expr);
            if (!_hasGhostChild && _hasIdentifierChild) // predicates involving only literals aren't relevant since they don't abstract program state
                _exprAvailabilityScope.Add((expr, _currentExprAvailabilityScopeStart, _currentExprAvailabilityScopeEnd));
            _currentExprAvailabilityScopeStart = null;
            _currentExprAvailabilityScopeEnd = null;
            _hasGhostChild = false;
            _hasIdentifierChild = false;
        }
    }
    
    public void InstrumentFaultyMethod() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod == null)
            return;
        
        HandleMethod(faultyMethod);
    }
    
    protected override void HandleBlock(BlockStmt blockStmt) {
        var prevNewBlock = _newBlockBody;
        _newBlockBody = [];
        InstrumentLine(blockStmt.StartToken);
        foreach (var stmt in blockStmt.Body) {
            _newBlockBody.Add(stmt);
            HandleStatement(stmt);
            InstrumentLine(stmt.EndToken);
        }
        blockStmt.Body = _newBlockBody;
        _newBlockBody = prevNewBlock;
    }

    protected override void VisitExpression(NameSegment nSegExpr) {
        DetermineIdentifierAvailability(nSegExpr.Name);
        _hasIdentifierChild = true;
        if (Ghosts.Contains(nSegExpr.Name))
            _hasGhostChild = true;
    }

    protected override void VisitExpression(IdentifierExpr idExpr) {
        DetermineIdentifierAvailability(idExpr.Name);
        _hasIdentifierChild = true;
        if (Ghosts.Contains(idExpr.Name))
            _hasGhostChild = true;
    }
    
    protected override void VisitExpression(SuffixExpr suffixExpr) {
        if (suffixExpr is ExprDotName)
            _hasIdentifierChild = true;
        base.VisitExpression(suffixExpr);
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private void InstrumentLine(Token token) {
        var availableExprs = _exprAvailabilityScope.Where(
            expr => 
                (token.pos > expr.Item2 && token.pos < expr.Item3) || 
                (expr.Item2 == null && expr.Item3 == null)
        );
        
        foreach (var expr in availableExprs) {
            var posElement = Expression.CreateIntLiteral(token, token.pos);
            var exprStrElement = ExpressionScanner.CreateStringLiteral(token, expr.Item1.ToString());
            var delimElement1 = ExpressionScanner.CreateStringLiteral(token, ",");
            var delimElement2 = ExpressionScanner.CreateStringLiteral(token, "\\n");
            List<Expression> printElements = [
                posElement, delimElement1, exprStrElement, delimElement1, expr.Item1, delimElement2
            ];
            var newStmt = new PrintStmt(token, printElements);
            _newBlockBody.Add(newStmt);
        }
    }
    
    private void DetermineIdentifierAvailability(string idName) {
        var identifier = IdentifierAvailability.Find((id) => id.Item1 == idName);
        var identifierAvailabilityScopeStart = identifier.Item2;
        var identifierAvailabilityScopeEnd = identifier.Item3;
        
        if (_currentExprAvailabilityScopeStart == null ||
            (identifierAvailabilityScopeStart != null && 
             identifierAvailabilityScopeStart > _currentExprAvailabilityScopeStart))
            _currentExprAvailabilityScopeStart = identifierAvailabilityScopeStart;
        if (_currentExprAvailabilityScopeEnd == null ||
            (identifierAvailabilityScopeEnd != null && 
             identifierAvailabilityScopeEnd < _currentExprAvailabilityScopeEnd))
            _currentExprAvailabilityScopeEnd = identifierAvailabilityScopeEnd;
    }
}