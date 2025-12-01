using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ExpressionTraceBuilder : Visitor
{
    public List<(string, int?, int?)> IdentifierAvailability { get; }
    private readonly List<(Expression, int?, int?)> _exprAvailabilityScope;
    
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

    protected override void VisitExpression(NameSegment nSegExpr) {
        var identifier = IdentifierAvailability.Find((id) => id.Item1 == nSegExpr.Name);
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