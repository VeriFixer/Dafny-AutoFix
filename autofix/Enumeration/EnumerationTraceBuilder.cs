using Microsoft.BaseTypes;
using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace AutoFix.Enumeration;

public sealed class EnumerationTraceBuilder : Visitor
{
    private List<Identifier> IdentifierAvailability { get; }
    private List<string> Ghosts { get; }
    private readonly Dictionary<Expression, List<(Expression, string)>> _exprIdentifiers;
    public List<Expression> AllEnumPreds => _exprIdentifiers.Keys.ToList();

    private Expression? _exprUnderVisit;
    private List<Statement> _newBlockBody = [];
    
    public EnumerationTraceBuilder(List<Identifier> identifierAvailability, List<string> ghosts) {
        IdentifierAvailability = identifierAvailability;
        Ghosts = ghosts;
        _exprIdentifiers = new Dictionary<Expression, List<(Expression, string)>>();
        // identify the scope in which each program abstraction is observable according to the scope in which its subexpressions are defined
        foreach (var expr in SnapshotGenerator.ProgramAbstractions) {
            _exprIdentifiers[expr] = [];
            _exprUnderVisit = expr;
            HandleExpression(expr);
            _exprUnderVisit = null;
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
            if (stmt is PrintStmt) continue;
            _newBlockBody.Add(stmt);
            HandleStatement(stmt);
            InstrumentLine(stmt.EndToken, stmt);
        }
        blockStmt.Body.Clear();
        blockStmt.Body.AddRange(_newBlockBody);
        _newBlockBody = prevNewBlock;
    }

    protected override void VisitExpression(NameSegment nSegExpr) {
        if (_exprUnderVisit != null)
            _exprIdentifiers[_exprUnderVisit].Add((nSegExpr, nSegExpr.Name));
    }

    protected override void VisitExpression(IdentifierExpr idExpr) {
        if (_exprUnderVisit != null)
            _exprIdentifiers[_exprUnderVisit].Add((idExpr, idExpr.Name));
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private void InstrumentLine(Token token, Statement placementRefStmt) {
        var availableExprs = DetermineExpressionAvailability(token.pos);
        foreach (var expr in availableExprs) {
            Expression? seqSafetyCheckedExpr = null;
            Expression? mapSafetyCheckedExpr = null;
            var seqSelectSubExprs = Enumerator.SeqSelectExprs
                .Where(e => expr.ToString().Contains(e.ToString())).ToList();
            if (seqSelectSubExprs.Count > 0)
                seqSafetyCheckedExpr = HandleSeqSelectExpr(seqSelectSubExprs);
            var mapSelectSubExprs = Enumerator.MapSelectExprs
                .Where(e => expr.ToString().Contains(e.ToString())).ToList();
            if (mapSelectSubExprs.Count > 0)
                mapSafetyCheckedExpr = HandleMapSelectExpr(mapSelectSubExprs);
            Expression? safetyCheckedExpr = seqSafetyCheckedExpr != null ? mapSafetyCheckedExpr != null ? 
                Expression.CreateAnd(Expression.CreateAnd(seqSafetyCheckedExpr, mapSafetyCheckedExpr), expr) : 
                Expression.CreateAnd(seqSafetyCheckedExpr, expr) : 
                mapSafetyCheckedExpr != null ? Expression.CreateAnd(mapSafetyCheckedExpr, expr) : null;
            
            
            var lineElement = Expression.CreateIntLiteral(token, token.line);
            var posElement = Expression.CreateIntLiteral(token, token.pos);
            var exprStrElement = AstUtils.CreateStringLiteral(token, expr.ToString());
            var snapshotCDep = SnapshotGenerator.CDepAnalyzer?.ComputeCDep(token.pos, placementRefStmt) ?? 0.0;
            var snapshotCDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotCDep}".Replace(',', '.')));
            var snapshotEDep = SnapshotGenerator.EDepAnalyzer?.ComputeEDep(expr) ?? 0.0;
            var snapshotEDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotEDep}".Replace(',', '.')));
            var delimElement1 = AstUtils.CreateStringLiteral(token, ";");
            var delimElement2 = AstUtils.CreateStringLiteral(token, ";enum\\n");
            List<Expression> printElements = [
                lineElement, delimElement1, posElement, delimElement1, 
                exprStrElement, delimElement1, safetyCheckedExpr ?? expr, delimElement1, 
                snapshotCDepElement, delimElement1, snapshotEDepElement, delimElement2
            ];
            var newStmt = new PrintStmt(token, printElements);
            _newBlockBody.Add(newStmt);
        }
    }

    private Expression? HandleSeqSelectExpr(List<Expression> selectExprs) {
        if (selectExprs.Count == 0 || selectExprs[0] is not SeqSelectExpr seqSelExpr)
            return null;

        var token = seqSelExpr.Seq.Origin;
        var zeroExpr = Expression.CreateIntLiteral(token, 0);
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
        Expression? lowLeHigher = null;
        if (seqSelExpr.E0 != null)
            firstInBoundsExpr = Expression.CreateAnd(
                Expression.CreateAtMost(zeroExpr, seqSelExpr.E0),
                Expression.CreateLess(seqSelExpr.E0, lengthExpr));
        if (seqSelExpr.E1 != null)
            secondInBoundsExpr = Expression.CreateAnd(
                Expression.CreateAtMost(zeroExpr, seqSelExpr.E1),
                Expression.CreateLess(seqSelExpr.E1, lengthExpr));
        if (firstInBoundsExpr != null && secondInBoundsExpr != null)
            lowLeHigher = Expression.CreateAtMost(seqSelExpr.E0, seqSelExpr.E1);
        Expression? inBoundsExpr = firstInBoundsExpr != null ? secondInBoundsExpr != null ? 
            Expression.CreateAnd(Expression.CreateAnd(firstInBoundsExpr, secondInBoundsExpr), lowLeHigher) : 
            firstInBoundsExpr : secondInBoundsExpr;

        if (selectExprs.Count > 1) {
            var nextExpr = HandleSeqSelectExpr(selectExprs[1..]);
            if (nextExpr != null)
                return inBoundsExpr != null ? Expression.CreateAnd(inBoundsExpr, nextExpr) : nextExpr;
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
    
    private List<Expression> DetermineExpressionAvailability(int pos) {
        List<Expression> availableExprs = [];
        foreach (var expr in SnapshotGenerator.ProgramAbstractions) {
            if (!_exprIdentifiers.TryGetValue(expr, out var exprIdentifiers) || exprIdentifiers.Count == 0)
                continue;
            
            var allIdentifiersAreAvailable = true;
            var identifierVars = new Dictionary<string, Identifier?>();
            foreach (var id in exprIdentifiers) {
                if (Ghosts.Contains(id.Item2)) {
                    allIdentifiersAreAvailable = false;
                    break;
                }
                var identifiers = IdentifierAvailability.FindAll(i => i.Name == id.Item2);
                var identifier = identifiers.FirstOrDefault(i =>
                    (pos > i.AvailabilityStartPos && pos < i.AvailabilityEndPos) ||
                    (i.AvailabilityStartPos == null && i.AvailabilityEndPos == null));
                if (identifier == null) {
                    allIdentifiersAreAvailable = false;
                    break;
                }
                identifierVars.TryAdd(identifier.Name, identifier);
            }
            if (!allIdentifiersAreAvailable) continue;
            var updatedExpr = EnsureSubExpressionIVarCompatibility(expr, identifierVars);
            availableExprs.Add(updatedExpr ?? expr);
        }
        return availableExprs;
    }
    
    private Expression? EnsureSubExpressionIVarCompatibility(Expression expr, Dictionary<string, Identifier?> ids) {
        var cloner = new Cloner(false, true);
        var updatedExpr = cloner.CloneExpr(expr);
        _exprIdentifiers[updatedExpr] = [];
        _exprUnderVisit = updatedExpr;
        HandleExpression(updatedExpr);
        _exprUnderVisit = null;
        
        if (!_exprIdentifiers.TryGetValue(updatedExpr, out var exprIdentifiers) || exprIdentifiers.Count == 0)
            return null;
        
        foreach (var subExpr in exprIdentifiers) {
            var idName = subExpr.Item2;
            if (!ids.TryGetValue(idName, out var id) || id == null)
                continue;
            
            if (subExpr.Item1 is NameSegment nSegExpr && 
                nSegExpr.ResolvedExpression is IdentifierExpr idExpr1) {
                idExpr1.Var = id.Var;
            } else if (subExpr.Item1 is IdentifierExpr idExpr2) {
                idExpr2.Var = id.Var;
            }
        }
        return updatedExpr;
    }
}