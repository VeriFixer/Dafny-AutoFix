using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ExpressionTraceBuilder : Visitor
{
    private List<(string, int?, int?)> IdentifierAvailability { get; }
    private readonly List<(Expression, int?, int?)> _exprAvailabilityScope;
    private readonly List<Statement> _newMethodBody = [];
    
    private int? _currentExprAvailabilityScopeStart;
    private int? _currentExprAvailabilityScopeEnd;

    public ExpressionTraceBuilder(List<(string, int?, int?)> identifierAvailability) {
        IdentifierAvailability = identifierAvailability;
        _exprAvailabilityScope = [];
        // identify the scope in which each program abstraction is observable according to the scope in which its subexpressions are defined
        foreach (var expr in SnapshotGenerator.ProgramAbstractions) {
            HandleExpression(expr);
            _exprAvailabilityScope.Add((expr, _currentExprAvailabilityScopeStart, _currentExprAvailabilityScopeEnd));
            _currentExprAvailabilityScopeStart = null;
            _currentExprAvailabilityScopeEnd = null;
        }
    }
    
    public void InstrumentFaultyMethod() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod == null)
            return;
        
        HandleMethod(faultyMethod);
    }
    
    protected override void HandleMethod(Method method) {
        if (method.Body == null) return;
        
        InstrumentLine(method.Body.StartToken);
        foreach (var stmt in method.Body.Body) {
            _newMethodBody.Add(stmt);
            HandleStatement(stmt);
            InstrumentLine(stmt.EndToken);
        }
        method.Body.Body = _newMethodBody;
    }

    protected override void VisitExpression(NameSegment nSegExpr) {
        DetermineIdentifierAvailability(nSegExpr.Name);
    }

    protected override void VisitExpression(IdentifierExpr idExpr) {
        DetermineIdentifierAvailability(idExpr.Name);
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private void InstrumentLine(Token token) {
        var availableExprs = _exprAvailabilityScope.Where(
            expr => 
                (token.pos >= expr.Item2 && token.pos <= expr.Item3) || 
                (expr.Item2 == null && expr.Item3 == null)
        );
        
        foreach (var expr in availableExprs) {
            var posElement = new LiteralExpr(token, token.pos);
            var exprStrElement = new StringLiteralExpr(token, expr.Item1.ToString(), false);
            var delimElement1 = new StringLiteralExpr(token, ",", false);
            var delimElement2 = new StringLiteralExpr(token, "\\n", false);
            List<Expression> printElements = [
                posElement, delimElement1, exprStrElement, delimElement1, expr.Item1, delimElement2
            ];
            var newStmt = new PrintStmt(token, printElements);
            _newMethodBody.Add(newStmt);
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