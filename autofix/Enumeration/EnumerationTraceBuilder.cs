using Microsoft.BaseTypes;
using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace AutoFix.Enumeration;

public sealed class EnumerationTraceBuilder : Visitor
{
    private List<Identifier> IdentifierAvailability { get; }
    private List<string> Ghosts { get; }
    private readonly List<(Expression, int?, int?)> _exprAvailabilityScope;
    public List<Expression> AllEnumPreds => _exprAvailabilityScope.Select(e => e.Item1).ToList();
    
    private List<Statement> _newBlockBody = [];
    private int? _currentExprAvailabilityScopeStart;
    private int? _currentExprAvailabilityScopeEnd;
    private bool _hasGhostChild;
    private bool _hasIdentifierChild;
    
    public EnumerationTraceBuilder(List<Identifier> identifierAvailability, List<string> ghosts) {
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
        var faultyMethod = AutoFix.FaultyMethod;
        if (faultyMethod == null)
            return;
        HandleMethod(faultyMethod);
    }
    
    protected override void HandleBlock(BlockStmt blockStmt) {
        var faultyMethod = AutoFix.FaultyMethod;
        if (faultyMethod == null)
            return;
        
        var prevNewBlock = _newBlockBody;
        _newBlockBody = [];
        InstrumentLine(blockStmt.StartToken, blockStmt);
        foreach (var stmt in blockStmt.Body) {
            _newBlockBody.Add(stmt);
            if (stmt is PrintStmt) continue;
            HandleStatement(stmt);
            InstrumentLine(stmt.EndToken, stmt);
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
    private void InstrumentLine(Token token, Statement placementRefStmt) {
        var availableExprs = _exprAvailabilityScope.Where(
            expr => 
                (token.pos > expr.Item2 && token.pos < expr.Item3) || 
                (expr.Item2 == null && expr.Item3 == null)
        );
        
        foreach (var expr in availableExprs) {
            Expression? seqSafetyCheckedExpr = null;
            Expression? mapSafetyCheckedExpr = null;
            var seqSelectSubExprs = Enumerator.SeqSelectExprs
                .Where(e => expr.ToString().Contains(e.ToString())).ToList();
            if (seqSelectSubExprs.Count > 0)
                seqSafetyCheckedExpr = HandleSeqSelectExpr(seqSelectSubExprs) ?? null;
            var mapSelectSubExprs = Enumerator.MapSelectExprs
                .Where(e => expr.ToString().Contains(e.ToString())).ToList();
            if (seqSelectSubExprs.Count > 0)
                mapSafetyCheckedExpr = HandleMapSelectExpr(mapSelectSubExprs);
            Expression? safetyCheckedExpr = seqSafetyCheckedExpr != null ? mapSafetyCheckedExpr != null ? 
                Expression.CreateAnd(Expression.CreateAnd(seqSafetyCheckedExpr, mapSafetyCheckedExpr), expr.Item1) : 
                Expression.CreateAnd(seqSafetyCheckedExpr, expr.Item1) : 
                mapSafetyCheckedExpr != null ? Expression.CreateAnd(mapSafetyCheckedExpr, expr.Item1) : null;
            
            
            var posElement = Expression.CreateIntLiteral(token, token.pos);
            var exprStrElement = AstUtils.CreateStringLiteral(token, expr.Item1.ToString());
            var snapshotCDep = SnapshotGenerator.CDepAnalyzer?.ComputeCDep(token.pos, placementRefStmt) ?? 0.0;
            var snapshotCDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotCDep}".Replace(',', '.')));
            var snapshotEDep = SnapshotGenerator.EDepAnalyzer?.ComputeEDep(expr.Item1) ?? 0.0;
            var snapshotEDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotEDep}".Replace(',', '.')));
            var delimElement1 = AstUtils.CreateStringLiteral(token, ";");
            var delimElement2 = AstUtils.CreateStringLiteral(token, ";enum\\n");
            List<Expression> printElements = [
                posElement, delimElement1, exprStrElement, delimElement1, safetyCheckedExpr ?? expr.Item1, 
                delimElement1, snapshotCDepElement, delimElement1, snapshotEDepElement, delimElement2
            ];
            var newStmt = new PrintStmt(token, printElements);
            _newBlockBody.Add(newStmt);
        }
    }

    private Expression? HandleSeqSelectExpr(List<Expression> selectExprs) {
        if (selectExprs.Count == 0 || selectExprs[0] is not SeqSelectExpr seqSelExpr) 
            return null;

        var token = seqSelExpr.Seq.Origin;
        Expression lengthExpr = seqSelExpr.Seq.Type.ToString().StartsWith("array<")
            ? new ExprDotName(token, seqSelExpr.Seq, new Name(token, "Length"), null) {
                ResolvedExpression = AstUtils.CreateMemberSelectExpr(
                    token, AstUtils.CreateLengthSpecialField(token), null, seqSelExpr.Seq, Type.Int
                ),
                Type = Type.Int
            }
            : new UnaryOpExpr(token, UnaryOpExpr.Opcode.Cardinality, seqSelExpr.Seq) { Type = Type.Int };

        Expression? firstInBoundsExpr = null;
        Expression? secondInBoundsExpr = null;
        if (seqSelExpr.E0 != null)
            firstInBoundsExpr = Expression.CreateLess(seqSelExpr.E0, lengthExpr);
        if (seqSelExpr.E1 != null)
            secondInBoundsExpr = Expression.CreateLess(seqSelExpr.E1, lengthExpr);
        Expression? inBoundsExpr = firstInBoundsExpr != null ? secondInBoundsExpr != null ? 
            Expression.CreateAnd(firstInBoundsExpr, secondInBoundsExpr) : 
            firstInBoundsExpr : secondInBoundsExpr;
        if (inBoundsExpr == null) return null;

        if (selectExprs.Count > 1) {
            var nextExpr = HandleSeqSelectExpr(selectExprs[1..]);
            if (nextExpr != null)
                return Expression.CreateAnd(inBoundsExpr, nextExpr);
        }
        return inBoundsExpr;
    }
    
    private Expression? HandleMapSelectExpr(List<Expression> selectExprs) {
        if (selectExprs.Count == 0 || selectExprs[0] is not SeqSelectExpr seqSelExpr || seqSelExpr.E0 == null) 
            return null;
        
        var inMapExpr = AstUtils.CreateIn(seqSelExpr.E0, seqSelExpr.Seq, seqSelExpr.Seq.Type);
        if (selectExprs.Count > 1) {
            var nextExpr = HandleMapSelectExpr(selectExprs[1..]);
            if (nextExpr != null)
                return Expression.CreateAnd(inMapExpr, nextExpr);
        }
        return inMapExpr;
    }
    
    private void DetermineIdentifierAvailability(string idName) {
        var identifier = IdentifierAvailability.Find((id) => id.Name == idName);
        var identifierAvailabilityScopeStart = identifier?.AvailabilityStartPos;
        var identifierAvailabilityScopeEnd = identifier?.AvailabilityEndPos;
        
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