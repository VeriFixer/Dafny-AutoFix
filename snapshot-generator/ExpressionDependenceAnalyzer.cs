using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ExpressionDependenceAnalyzer : Visitor
{
    private readonly Dictionary<string, int> _eProxs = [];
    private Expression? _violatedPredicate;
    private readonly HashSet<string> _violatedPredicateSubExprs = [];
    private readonly int _violationLocation = SnapshotGenerator.RelatedLocationLine ?? SnapshotGenerator.ViolationLine;
    
    public ExpressionDependenceAnalyzer(ModuleDefinition module, List<Expression> predicates) {
        // find violated predicate
        Visit(module);
        if (_violatedPredicate == null) return;
        _violatedPredicateSubExprs = GetSubExpressions(_violatedPredicate);
        
        // compute the expression proximity between each snapshot predicate and the violated predicate
        foreach (var pred in predicates)
            _eProxs.TryAdd(pred.ToString(), ComputeEProx(pred));
    }
    
    protected override void VisitReqEns(List<AttributedExpression> attExprs) {
        foreach (var attExpr in attExprs) {
            if (!(attExpr.StartToken.line == _violationLocation &&
                 attExpr.EndToken.line == _violationLocation))
                continue;
            _violatedPredicate = attExpr.E;
            break;
        }
        base.VisitReqEns(attExprs);
    }
    
    protected override void VisitStatement(PredicateStmt predStmt) {
        if (predStmt.StartToken.line == _violationLocation &&
            predStmt.EndToken.line == _violationLocation)
            _violatedPredicate = predStmt.Expr;
        base.VisitStatement(predStmt);
    }
    
    private int ComputeEProx(Expression pred) {
        var predSubExprs = GetSubExpressions(pred);
        var sharedExprs = new HashSet<string>(_violatedPredicateSubExprs.Select(e => e.ToString()));
        sharedExprs.IntersectWith(predSubExprs.Select(e => e.ToString()));
        return sharedExprs.Count;
    }

    private HashSet<string> GetSubExpressions(Expression pred) {
        var subExprs = new HashSet<string>();
        foreach (var expr in pred.SubExpressions) {
            subExprs.Add(expr.ToString());
            subExprs.UnionWith(GetSubExpressions(expr));
        }
        return subExprs;
    }
}