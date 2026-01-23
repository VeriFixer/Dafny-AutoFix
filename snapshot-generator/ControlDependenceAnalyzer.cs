using Microsoft.Dafny;

namespace SnapshotGenerator;

public sealed class ControlDependenceAnalyzer : Visitor
{
    // (statement, in case statement is the method's body itself whether it's its start or not,
    // whether the statement includes the faulty statement, whether the statement appears before the faulty statement,
    // depth, control distance to the faulty location)
    public static List<(Statement, bool, bool, bool, int)> CDists = [];
    private Statement? _faultyLocation;
    private readonly bool _firstVisit = true;
    private List<Statement> _beforeFaultCurrentScope = [];
    private List<Statement> _afterFaultCurrentScope = [];

    public ControlDependenceAnalyzer(Method faultyMethod) {
        if (faultyMethod.Body == null) return;
        
        // find faulty location
        if (SnapshotGenerator.ViolationLine <= faultyMethod.Body.StartToken.line)
            _faultyLocation = faultyMethod.Body;
        HandleMethod(faultyMethod);
        _firstVisit = false;
        if (_faultyLocation == null) return;
        
        // compute the control distance from each method location to the faulty location
        // ComputeCDist(faultyMethod.Body, true); // TODO
        HandleMethod(faultyMethod);
        using (StreamWriter sw = File.AppendText("debug.txt")) {
            TextWriter syncWriter = TextWriter.Synchronized(sw);
            syncWriter.WriteLine(String.Join("\n", CDists));
        }
    }

    protected override void HandleStatement(Statement stmt) {
        if (_firstVisit) {
            CheckForFaultyStatement(stmt);
        } else {
            ComputeCDist(stmt);
            if (stmt is IfStmt || stmt is LoopStmt || stmt is AlternativeStmt ||
                stmt is MatchStmt || stmt is NestedMatchStmt)
                base.HandleStatement(stmt);
        }
    }
    
    protected override void HandleBlock(BlockStmt blockStmt) {
        // using (StreamWriter sw = File.AppendText("debug2.txt")) {
        //     TextWriter syncWriter = TextWriter.Synchronized(sw);
        //     syncWriter.WriteLine("\nVisit: " + blockStmt);
        //     syncWriter.WriteLine("Entry: " + String.Join(" ", _beforeFaultCurrentScope));
        // }
        var prevScope = _beforeFaultCurrentScope;
        _beforeFaultCurrentScope = blockStmt.Body.ToList();
        _afterFaultCurrentScope = prevScope.Concat(_beforeFaultCurrentScope).ToList();
        base.HandleBlock(blockStmt);
        // using (StreamWriter sw = File.AppendText("debug2.txt")) {
        //     TextWriter syncWriter = TextWriter.Synchronized(sw);
        //     syncWriter.WriteLine("After children visit: " + String.Join(" ", _beforeFaultCurrentScope));
        // }
        // using (StreamWriter sw = File.AppendText("debug2.txt")) {
        //     TextWriter syncWriter = TextWriter.Synchronized(sw);
            // syncWriter.WriteLine("Hello");
            // syncWriter.WriteLine("prevScope: " + String.Join(" ", prevScope));
            // syncWriter.WriteLine("_currScope: " + String.Join(" ", _currentScope));
            // foreach (var (stmt, i) in prevScope.Select((stmt, i) => (stmt, i))) 
            // {
            //     syncWriter.WriteLine(i + ": " + stmt);
            // }
            // syncWriter.Flush();
        // }
        _beforeFaultCurrentScope.AddRange(prevScope);
        // using (StreamWriter sw = File.AppendText("debug2.txt")) {
        //     TextWriter syncWriter = TextWriter.Synchronized(sw);
        //     syncWriter.WriteLine("Exit: " + String.Join(" ", _beforeFaultCurrentScope));
        // }        
        // using (StreamWriter sw = File.AppendText("debug2.txt")) {
        //     TextWriter syncWriter = TextWriter.Synchronized(sw);
        //     syncWriter.WriteLine("Finish visiting: " + blockStmt + "\n");
        // }
    }

    private void CheckForFaultyStatement(Statement stmt) {
        if (stmt.StartToken.line == SnapshotGenerator.ViolationLine &&
            stmt.EndToken.line == SnapshotGenerator.ViolationLine &&
            stmt.StartToken.col <= SnapshotGenerator.ViolationColumn &&
            stmt.EndToken.col >= SnapshotGenerator.ViolationColumn) 
        {
            _faultyLocation = stmt;
        }
        else if (stmt.StartToken.line <= SnapshotGenerator.ViolationLine &&
                 stmt.EndToken.line >= SnapshotGenerator.ViolationLine) 
        {
            _faultyLocation ??= stmt;
        } else if (_faultyLocation != null) {
            if (CDists.All(s => s.Item1 != stmt))
                CDists.Add((stmt, false, false, false, 0));
        }
        base.HandleStatement(stmt);
    }

    private void ComputeCDist(Statement stmt) {
        using (StreamWriter sw = File.AppendText("debug1.txt")) {
            TextWriter syncWriter = TextWriter.Synchronized(sw);
            syncWriter.WriteLine("Visit: " + stmt);
            syncWriter.WriteLine("Position: " + stmt.StartToken.pos + "-" + stmt.EndToken.pos);
            syncWriter.WriteLine("CurrentScope: " + String.Join(" ", _beforeFaultCurrentScope) + "\n");
        }
        if (_faultyLocation == null) return;
        var includes = stmt.StartToken.pos <= _faultyLocation.StartToken.pos &&
            stmt.EndToken.pos >= _faultyLocation.EndToken.pos;
        var before = stmt.EndToken.pos <= _faultyLocation.StartToken.pos;
        
        if ((includes && stmt != _faultyLocation) || stmt == _faultyLocation) return;
        if (before) { // increment distance for statements located before the faulty statement
            if (CDists.All(s => s.Item1 != stmt))
                CDists.Add((stmt, false, includes, before, 0));
            using (StreamWriter sw = File.AppendText("debug1.txt")) {
                TextWriter syncWriter = TextWriter.Synchronized(sw);
                syncWriter.WriteLine("Before faulty statement");
            }
            var matches = CDists.Where(s => s.Item4 && _beforeFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString()));
            using (StreamWriter sw = File.AppendText("debug1.txt")) {
                TextWriter syncWriter = TextWriter.Synchronized(sw);
                syncWriter.WriteLine("Will increase: " + String.Join(" ", matches));
            }
            foreach (var s in CDists) {
                using (StreamWriter sw = File.AppendText("debug1.txt")) {
                    TextWriter syncWriter = TextWriter.Synchronized(sw);
                    syncWriter.WriteLine("\t" + s.Item1);
                    syncWriter.WriteLine("\t" + s.Item4);
                    syncWriter.WriteLine("\t" + _beforeFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString()));
                }
            }
            CDists = CDists.Select(s =>
                s.Item4 && _beforeFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString())
                    ? (s.Item1, s.Item2, s.Item3, s.Item4, s.Item5 + 1) : s
            ).ToList();
        } else {
            using (StreamWriter sw = File.AppendText("debug1.txt")) {
                TextWriter syncWriter = TextWriter.Synchronized(sw);
                syncWriter.WriteLine("After faulty statement");
            }
            var matches = CDists.Where(s => !s.Item4 && (_beforeFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString())) && (s.Item1.StartToken.pos >= stmt.EndToken.pos || s.Item1 == stmt));
            using (StreamWriter sw = File.AppendText("debug1.txt")) {
                TextWriter syncWriter = TextWriter.Synchronized(sw);
                syncWriter.WriteLine("Will increase: " + String.Join(" ", matches));
            }
            foreach (var s in CDists) {
                using (StreamWriter sw = File.AppendText("debug1.txt")) {
                    TextWriter syncWriter = TextWriter.Synchronized(sw);
                    syncWriter.WriteLine("\t" + s.Item1);
                    syncWriter.WriteLine("\t" + s.Item1.StartToken.pos);
                    syncWriter.WriteLine("\t" + s.Item4);
                    syncWriter.WriteLine("\t" + _afterFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString()));
                }
            }
            CDists = CDists.Select(s =>
                !s.Item4 && (_afterFaultCurrentScope.Select(s1 => s1.ToString()).Contains(s.Item1.ToString())) && (s.Item1.StartToken.pos >= stmt.EndToken.pos || s.Item1 == stmt)
                    ? (s.Item1, s.Item2, s.Item3, s.Item4, s.Item5 + 1) : s
            ).ToList();
        }
    }
}