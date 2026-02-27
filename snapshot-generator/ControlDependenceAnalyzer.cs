using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ControlDependenceAnalyzer : Visitor
{
    public Dictionary<Statement, int> CDists = [];
    private Statement? _violationStmt;
    private readonly bool _firstVisit = true;
    private bool _visitingViolationBlockStmt;
    private bool _visitingBlockWithViolationStmt;
    private bool _visitingOutsideViolationBlockStmt;
    private bool _beforeViolationStmt;
    private Statement? _prevStmt;
    private BlockStmt? _outerBlockWithViolationStmt;
    private readonly Method? _faultyMethodClone;

    public ControlDependenceAnalyzer() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod?.Body == null) return;
        
        // find faulty location
        if (SnapshotGenerator.ViolationLine <= faultyMethod.Body.StartToken.line)
            _violationStmt = faultyMethod.Body;
        HandleMethod(faultyMethod);
        _firstVisit = false;
        if (_violationStmt == null) return;
        // compute the control distance from each method location to the violation location
        CDists.Add(_violationStmt, 0);
        HandleMethod(faultyMethod);
    }

    private void CheckForViolationStatement(Statement stmt) {
        if (stmt.StartToken.line == SnapshotGenerator.ViolationLine &&
            stmt.EndToken.line == SnapshotGenerator.ViolationLine) 
        {
            _violationStmt = stmt;
        }
        else if (stmt.StartToken.line <= SnapshotGenerator.ViolationLine &&
                 stmt.EndToken.line >= SnapshotGenerator.ViolationLine) 
        { // the violation location may be an entire block (e.g., a loop with a violated invariant)
            _violationStmt ??= stmt;
        }
        base.HandleStatement(stmt);
    }


    protected override void HandleStatement(Statement stmt) {
        if (_firstVisit) {
            CheckForViolationStatement(stmt);
        } else {
            base.HandleStatement(stmt);
        }
    }

    protected override void HandleBlock(BlockStmt blockStmt) {
        if (_firstVisit) {
            base.HandleBlock(blockStmt);
            return;
        }

        if (_violationStmt == null) return;
        var prevVisitingViolationBlockStmt = _visitingViolationBlockStmt;
        var prevVisitingBlockWithViolationStmt = _visitingBlockWithViolationStmt;
        var prevVisitingOutsideViolationBlockStmt = _visitingOutsideViolationBlockStmt;
        
        // blockStmt is, in its entirety, the violation location
        if (_violationStmt == blockStmt || _violationStmt.SubStatements.Contains(blockStmt))
            _visitingViolationBlockStmt = true;
        if (_visitingViolationBlockStmt)
            InitializeViolationBlockCDists(blockStmt.Body);
        
        if (_visitingBlockWithViolationStmt) { // inside sub-blocks of the violation block
            ComputeCDists(blockStmt.Body, _beforeViolationStmt);
            ComputeSubBlockCDists(blockStmt, _beforeViolationStmt ? blockStmt.Body.Count + 1 : -1);
        }
        
        var violationStmtIdx = blockStmt.Body.IndexOf(_violationStmt);
        if (violationStmtIdx != -1) { // blockStmt contains the violation location
            _outerBlockWithViolationStmt = blockStmt;
            _visitingBlockWithViolationStmt = true;
            ComputeCDists(blockStmt.Body, violationStmtIdx);
            ComputeSubBlockCDists(blockStmt, violationStmtIdx);
        }
        
        if (!_visitingViolationBlockStmt && !_visitingBlockWithViolationStmt && violationStmtIdx == -1) { // outside of block containing violation location
            base.HandleBlock(blockStmt);
            _visitingOutsideViolationBlockStmt = true;
            var violationBlockIdx = _outerBlockWithViolationStmt != null ?
                blockStmt.Body.FindIndex(stmt => stmt.SubStatements.Contains(_outerBlockWithViolationStmt)) : -1;
            if (violationBlockIdx != -1) {
                ComputeCDists(blockStmt.Body, violationBlockIdx);
            } else {
                ComputeCDists(blockStmt.Body, _beforeViolationStmt);
            }
            ComputeSubBlockCDists(blockStmt, 
                violationBlockIdx != -1 ? violationBlockIdx : 
                _beforeViolationStmt ? blockStmt.Body.Count + 1 : -1);
            if (violationBlockIdx != -1)
                _outerBlockWithViolationStmt = blockStmt;
        }
        
        _visitingViolationBlockStmt = prevVisitingViolationBlockStmt;
        _visitingBlockWithViolationStmt = prevVisitingBlockWithViolationStmt;
        _visitingOutsideViolationBlockStmt = prevVisitingOutsideViolationBlockStmt;
    }

    private void InitializeViolationBlockCDists(List<Statement> blockBody) {
        foreach (var stmt in blockBody) {
            CDists.Add(stmt, 0);
            HandleStatement(stmt);
        }
    }

    private void ComputeCDists(List<Statement> blockBody, bool beforeViolationStmt) {
        var startIdx = beforeViolationStmt ? blockBody.Count - 1 : 0;
        Predicate<int> endCondition = beforeViolationStmt ? i => i >= 0 : i => i < blockBody.Count;

        for (var i = startIdx; endCondition(i); ) {
            var currentStmt = blockBody[i];
            var prevStmtIdx = beforeViolationStmt ? i + 1 : i - 1;
            var prevStmt = prevStmtIdx >= 0 && prevStmtIdx < blockBody.Count ? blockBody[prevStmtIdx] : _prevStmt;
            i = beforeViolationStmt ? i - 1 : i + 1;
            if (CDists.ContainsKey(currentStmt) || prevStmt == null || !CDists.ContainsKey(prevStmt))
                continue;
            var prevStmtDist = CDists.First(stmt => stmt.Key == prevStmt).Value;
            CDists.Add(currentStmt, prevStmtDist + 1);
        }
    }

    private void ComputeCDists(List<Statement> blockBody, int violationLocationIdx) {
        var blockDivider = violationLocationIdx + 1;
        if (_visitingOutsideViolationBlockStmt && blockBody[violationLocationIdx].SubStatements.Contains(_outerBlockWithViolationStmt)) {
            blockDivider = violationLocationIdx;
            _prevStmt = DeterminePrevStmt(_outerBlockWithViolationStmt?.Body[0], true);
        }
        ComputeCDists(blockBody[..blockDivider], true);
        
        blockDivider = violationLocationIdx;
        if (_visitingOutsideViolationBlockStmt && blockBody[violationLocationIdx].SubStatements.Contains(_outerBlockWithViolationStmt)) {
            blockDivider = violationLocationIdx + 1;
            _prevStmt = DeterminePrevStmt(_outerBlockWithViolationStmt?.Body[^1], false);
        }
        ComputeCDists(blockBody[blockDivider..], false);
    }
    
    private void ComputeSubBlockCDists(BlockStmt blockStmt, int violationStmtIdx) {
        foreach (var (stmt, i) in blockStmt.Body.Select((stmt, i) => (stmt, i))) {
            if (!(stmt is BlockStmt || stmt is IfStmt || stmt is LoopStmt || stmt is AlternativeStmt || 
                  stmt is MatchStmt || stmt is NestedMatchStmt) || stmt.SubStatements.Contains(_outerBlockWithViolationStmt))
                continue;
            _beforeViolationStmt = i < violationStmtIdx;
            _prevStmt = _beforeViolationStmt ? 
                i < blockStmt.Body.Count - 1 ? 
                    DeterminePrevStmt(blockStmt.Body[i + 1], _beforeViolationStmt) : stmt :
                i > 0 ? DeterminePrevStmt(blockStmt.Body[i - 1], _beforeViolationStmt) : stmt;
            HandleStatement(stmt);
        }
        if (!CDists.ContainsKey(blockStmt) && CDists.TryGetValue(blockStmt.Body[0], out var value))
            CDists.Add(blockStmt, value);
    }

    private Statement? DeterminePrevStmt(Statement? prevStmt, bool before) {
        if (prevStmt == null) return null;
        if (CDists.ContainsKey(prevStmt))
            return prevStmt;
        if (!(prevStmt is BlockStmt || prevStmt is IfStmt || prevStmt is LoopStmt ||
              prevStmt is AlternativeStmt || prevStmt is MatchStmt || prevStmt is NestedMatchStmt))
            return null;

        Statement? newPrevStmt = null;
        if (prevStmt is BlockStmt bStmt1) {
            newPrevStmt = DeterminePrevStmt([bStmt1.Body], before);
        } else if (prevStmt is IfStmt ifStmt) {
            List<List<Statement>> blocks = ifStmt.Els is BlockStmt bStmt ? 
                [ifStmt.Thn.Body, bStmt.Body] : [ifStmt.Thn.Body];
            newPrevStmt = DeterminePrevStmt(blocks, before);
        } else if (prevStmt is OneBodyLoopStmt loopStmt) {
            newPrevStmt = DeterminePrevStmt([loopStmt.Body.Body], before);
        } else if (prevStmt is AlternativeLoopStmt altLStmt) {
            newPrevStmt = DeterminePrevStmt(altLStmt.Alternatives
                .Select(a => a.Body).ToList(), before);
        } else if (prevStmt is AlternativeStmt altStmt) {
            newPrevStmt = DeterminePrevStmt(altStmt.Alternatives
                .Select(a => a.Body).ToList(), before);
        } else if (prevStmt is MatchStmt matchStmt) {
            newPrevStmt = DeterminePrevStmt(matchStmt.Cases
                .Select(cs => cs.Body).ToList(), before);
        } else if (prevStmt is NestedMatchStmt nestedStmt) {
            newPrevStmt = DeterminePrevStmt(nestedStmt.Cases
                .Select(cs => cs.Body).ToList(), before);
        }
        return newPrevStmt;
    }

    private Statement? DeterminePrevStmt(List<List<Statement>> stmts, bool before) {
        Statement? newPrevStmt = null;
        var stmtsByIncreasingBlockSize = stmts.OrderBy(l => l.Count).ToList();
        foreach (var stmt in stmtsByIncreasingBlockSize) {
            newPrevStmt = DeterminePrevStmt(stmt[before ? 0 : stmt.Count - 1], before);
            if (newPrevStmt != null) break;
        }
        return newPrevStmt;
    }
}