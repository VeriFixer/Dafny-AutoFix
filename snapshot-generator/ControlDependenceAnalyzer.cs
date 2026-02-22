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
            ComputeSubBlockCDists(blockStmt.Body, _beforeViolationStmt ? blockStmt.Body.Count + 1 : -1);
        }
        
        var violationStmtIdx = blockStmt.Body.IndexOf(_violationStmt);
        if (violationStmtIdx != -1) { // blockStmt contains the violation location
            _outerBlockWithViolationStmt = blockStmt;
            _visitingBlockWithViolationStmt = true;
            ComputeCDists(blockStmt.Body, violationStmtIdx);
            ComputeSubBlockCDists(blockStmt.Body, violationStmtIdx);
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
            ComputeSubBlockCDists(blockStmt.Body, 
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
            if (stmt is BlockStmt bStmt1) {
                InitializeViolationBlockCDists(bStmt1.Body);
            } else if (stmt is IfStmt ifStmt) {
                InitializeViolationBlockCDists(ifStmt.Thn.Body);
                if (ifStmt.Els is BlockStmt bStmt2)
                    InitializeViolationBlockCDists(bStmt2.Body);
            } else if (stmt is OneBodyLoopStmt loopStmt) {
                InitializeViolationBlockCDists(loopStmt.Body.Body);
            } else if (stmt is AlternativeLoopStmt altLStmt) {
                InitializeViolationBlockCDists(altLStmt.Alternatives
                    .Select(a => a.Body).SelectMany(l => l).ToList());
            } else if (stmt is AlternativeStmt altStmt) {
                InitializeViolationBlockCDists(altStmt.Alternatives
                    .Select(aStmt => aStmt.Body).SelectMany(l => l).ToList());
            } else if (stmt is MatchStmt matchStmt) {
                InitializeViolationBlockCDists(matchStmt.Cases
                    .Select(cs => cs.Body).SelectMany(l => l).ToList());
            } else if (stmt is NestedMatchStmt nestedStmt) {
                InitializeViolationBlockCDists(nestedStmt.Cases
                    .Select(cs => cs.Body).SelectMany(l => l).ToList());
            }
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
            _prevStmt = _outerBlockWithViolationStmt?.Body[0];
            blockDivider = violationLocationIdx;
        }
        ComputeCDists(blockBody[..blockDivider], true);
        
        blockDivider = violationLocationIdx;
        if (_visitingOutsideViolationBlockStmt && blockBody[violationLocationIdx].SubStatements.Contains(_outerBlockWithViolationStmt)) {
            _prevStmt = _outerBlockWithViolationStmt?.Body[^1];
            blockDivider = violationLocationIdx + 1;
        }
        ComputeCDists(blockBody[blockDivider..], false);
    }
    
    private void ComputeSubBlockCDists(List<Statement> blockBody, int violationStmtIdx) {
        foreach (var (stmt, i) in blockBody.Select((stmt, i) => (stmt, i))) {
            if (!(stmt is BlockStmt || stmt is IfStmt || stmt is LoopStmt || stmt is AlternativeStmt || 
                  stmt is MatchStmt || stmt is NestedMatchStmt) || stmt.SubStatements.Contains(_outerBlockWithViolationStmt))
                continue;
            _beforeViolationStmt = i < violationStmtIdx;
            _prevStmt = _beforeViolationStmt && i < blockBody.Count - 1 ? blockBody[i + 1] : 
                i > 0 ? blockBody[i - 1] : null;
            HandleStatement(stmt);
        }
    }
}