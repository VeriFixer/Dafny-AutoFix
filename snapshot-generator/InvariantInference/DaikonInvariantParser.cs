using System.Text.RegularExpressions;
using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInvariantParser : Visitor
{
    public static readonly List<(string, string)> TypeInfo = ImportTypeInfo(); // (var name, var type)
    private readonly List<(Expression, int, Statement)> _invariantsPlacement = []; // (invariant, location, statement after which invariant should be inserted)
    private string _enclosingClassName = "";
    private BlockStmt? _currentBlock;
    private readonly List<(ReturnStmt, int)> _returnStmts = []; // (return stmt, location)
    private bool _isLastStmtReturn;
    private Predicate<Statement>? _findStmtPred;
    private Statement? _prevStmt;
    private (BlockStmt?, int) _targetStmt = (null, -1);
    
    private ControlDependenceAnalyzer? _cDepAnalyzer;
    
    public void Parse(ModuleDefinition module) {
        Visit(module);
        if (SnapshotGenerator.FaultyMethod == null || InvariantParser.InvariantsAlreadyParsed)
            return;
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod.Body == null) return;
        _cDepAnalyzer = new ControlDependenceAnalyzer(faultyMethod);

        int location = module.StartToken.pos;
        var lines = File.ReadAllLines("inv.inv");
        foreach (var line in lines) {
            if (line.EndsWith(":::ENTER") || line.Contains(":::EXIT")) {
                if (line.Contains(faultyMethod.Name) && line.Contains(_enclosingClassName)) {
                    var locationStartIndex = line.IndexOf(":::EXIT", StringComparison.Ordinal) + 7;
                    var returnIdx = int.TryParse(line[locationStartIndex..], out var intVal) ? intVal - 1 : _returnStmts.Count - 1;
                    location = line.EndsWith(":::ENTER") ? faultyMethod.Body.StartToken.pos : 
                        (line.EndsWith(":::EXIT") || line.EndsWith(":::EXIT00")) && !_isLastStmtReturn ?
                        faultyMethod.Body.EndToken.pos : _returnStmts[returnIdx].Item2;
                } else if (line.Contains("Dummy")) {
                    var locationStartIndex = line.IndexOf("Dummy", StringComparison.Ordinal) + 5;
                    var locationEndIndex = line.EndsWith(":::ENTER") ? 10 : 9;
                    location = int.Parse(line[locationStartIndex..^locationEndIndex]);
                }
            } else {
                var invExpr = ParseInvariant(line);
                if (invExpr == null)
                    continue;
                FindInvariantPlacement(location, invExpr);
            }
        }
        InstrumentWithInvariants();
        InvariantParser.InvariantsAlreadyParsed = true;
    }

    private static List<(string, string)> ImportTypeInfo() {
        var typeInfo = new List<(string, string)>();
        var currentVar = "";
        // both traces will contain the same var declarations
        var lines = File.ReadAllLines("trace-pass.dtrace");
        foreach (var line in lines) {
            if (line.StartsWith("variable")) {
                currentVar = line.Split(" ")[1];
            } else if (line.StartsWith("\tdec-type")) {
                if (typeInfo.All(t => t.Item1 != currentVar))
                    typeInfo.Add((currentVar, line[10..]));
                currentVar = "";
            }
        }
        return typeInfo;
    }

    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {
        _enclosingClassName = decl.Name;
        base.HandleMemberDecls(decl);
        
        if (SnapshotGenerator.FaultyMethod == null ||
            SnapshotGenerator.PassingTestsMethod == null ||
            SnapshotGenerator.FailingTestsMethod == null) 
            return;
        AstUtils.PrintTestCases(SnapshotGenerator.PassingTestsMethod);
        AstUtils.PrintTestCases(SnapshotGenerator.FailingTestsMethod);
    }
    
    protected override void HandleMethod(Method method) {
        // distinguish passing from failing test execution
        if (_enclosingClassName == "_default" && method.Name == "Passing") {
            SnapshotGenerator.PassingTestsMethod = method;
            AstUtils.PrintTestType(method, true);
        }
        if (_enclosingClassName == "_default" && method.Name == "Failing") {
            SnapshotGenerator.FailingTestsMethod = method;
            AstUtils.PrintTestType(method, false);
        }
            
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= SnapshotGenerator.ViolationLine &&
              method.EndToken.line >= SnapshotGenerator.ViolationLine))
            return;
        SnapshotGenerator.FaultyMethod = method;
        base.HandleMethod(method);
        if (method.Body is { Body.Count: > 0 } && method.Body.Body[^1] is ReturnStmt)
            _isLastStmtReturn = true;
    }
    
    protected override void VisitStatement(ProduceStmt pStmt) {
        if (pStmt is ReturnStmt retStmt && _returnStmts.All(rStmt => rStmt.Item1 != retStmt)) {
            if (_prevStmt != null) {
                _returnStmts.Add((retStmt, _prevStmt.EndToken.pos));
            } else if (_currentBlock != null) {
                _returnStmts.Add((retStmt, _currentBlock.StartToken.pos));
            }
        }
        base.VisitStatement(pStmt);
    }

    /// -------------------------
    /// Invariant parsing
    /// -------------------------
    private Stack<SimplifyToken> _stack = new();
    private bool _invalidInvariant;
    
    private Expression? ParseInvariant(String invariant) {
        invariant = HandleFormatExceptions(invariant);
        _invalidInvariant = false;
        // Lexing
        var words = invariant.Split(" ").ToList();
        var tokens = new List<SimplifyToken>();
        foreach (var word in words) {
            _stack = new Stack<SimplifyToken>();
            tokens.AddRange(LexInvariantWord(word));
            tokens.AddRange(_stack);
            if (_invalidInvariant) return null;
        }
        // Parsing
        var (e, _) = SimplifyExpression.Parse(tokens);
        return e.ToExpression()[0];
    }

    private List<SimplifyToken> LexInvariantWord(String word) {
        if (word.StartsWith('('))
            return Enumerable.Concat(
                [new SimplifyToken(SimplifyToken.SimplifyTokenType.OpenParen)],
                LexInvariantWord(word[1..])
            ).ToList();
        if (word.EndsWith(')')) {
            _stack.Push(new SimplifyToken(SimplifyToken.SimplifyTokenType.CloseParen));
            return LexInvariantWord(word.Remove(word.Length - 1));
        } 
        if (word.StartsWith('|')) {
            word = word.Trim('|');
            if (word.StartsWith("@")) {
                var constantToken = SimplifyToken.GetSimplifyToken(word[1..]);
                if (constantToken == null) _invalidInvariant = true;
                return constantToken != null ? [constantToken] : [];
            } if (word.StartsWith("_string_")) {
                return [new StringSimplifyToken(word[8..])];
            } if (word.StartsWith("__orig__")) {
                _invalidInvariant = true;
                return [];
            }
            return [new VarSimplifyToken(word)];
        }
        var token = SimplifyToken.GetSimplifyToken(word);
        if (token == null) _invalidInvariant = true;
        return token != null ? [token] : [];
    }

    private string HandleFormatExceptions(String invariant) {
        invariant = Regex.Replace(invariant, @"\s+", " "); // remove multiple blank spaces
        invariant = Regex.Replace(invariant, @"\d+(?:\.\d+)?d0", // remove the d0 in a double representation 
            match => match.Value.Substring(0, match.Value.Length - 2));
        // since we parse the invariant's words using a blank space as delimiter
        // we need to temporarily hide the blank spaces in a string
        invariant = Regex.Replace(invariant, @"\|_string_(.*?)\|",
            match => match.Value.Replace(" ", "_string_space_"));
        return invariant.Replace("select elems", "selectElems");
    }

    /// -------------------------
    /// Instrumentation
    /// -------------------------
    private void FindInvariantPlacement(int location, Expression invariantExpr) {
        if (_invariantsPlacement.Any(i => i.Item2 == location && i.Item1.ToString() == invariantExpr.ToString()))
            return;
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod == null || faultyMethod.Body == null) return;
        
        if (location == faultyMethod.Body.StartToken.pos) {
            _invariantsPlacement.Add((invariantExpr, location, faultyMethod.Body));
        } else if (location == faultyMethod.Body.EndToken.pos) {
            _invariantsPlacement.Add((invariantExpr, location, faultyMethod.Body));
        } else {
            _findStmtPred = stmt => stmt.EndToken.pos == location || (stmt is BlockStmt bStmt && bStmt.StartToken.pos == location);
            HandleMethod(faultyMethod);
            var refStmt = _targetStmt.Item2 == -1 ? 
                _targetStmt.Item1 : _targetStmt.Item1?.Body[_targetStmt.Item2];
            if (refStmt != null)
                _invariantsPlacement.Add((invariantExpr, location, refStmt));
            _findStmtPred = null;
            _targetStmt = (null, -1);
        }
    }
    
    private void InstrumentWithInvariants() {
        var faultyMethod = SnapshotGenerator.FaultyMethod;
        if (faultyMethod == null || faultyMethod.Body == null) return;

        foreach (var (inv, location, placement) in _invariantsPlacement) {
            if (ReferenceEquals(placement, faultyMethod.Body)) {
                if (location == faultyMethod.Body.StartToken.pos) {
                    faultyMethod.Body.Body.Insert(0, PrintInvariant(inv, location));
                } else {
                    faultyMethod.Body.Body.Add(PrintInvariant(inv, location));
                }
            } else {
                _findStmtPred = stmt => ReferenceEquals(stmt, placement);
                HandleMethod(faultyMethod);
                if (_targetStmt.Item1 == null)
                    continue;
                _targetStmt.Item1.Body.Insert(_targetStmt.Item2 + 1, PrintInvariant(inv, location));
                _findStmtPred = null;
                _targetStmt = (null, -1);
            }
        }
    }

    private PrintStmt PrintInvariant(Expression invariantExpr, int location) {
        var posElement = Expression.CreateIntLiteral(null, location);
        var exprStrElement = AstUtils.CreateStringLiteral(null, invariantExpr.ToString());
        var delimElement1 = AstUtils.CreateStringLiteral(null, ",");
        var delimElement2 = AstUtils.CreateStringLiteral(null, "\\n");
        List<Expression> printElements = [
            posElement, delimElement1, exprStrElement, delimElement1, invariantExpr, delimElement2
        ];
        return new PrintStmt(null, printElements); 
    }

    protected override void HandleBlock(BlockStmt blockStmt) {
        _currentBlock = blockStmt;
        
        if (_findStmtPred != null && _findStmtPred(blockStmt)) {
            _targetStmt = (blockStmt, -1);
            _prevStmt = null;
            return;
        }
        
        foreach (var (stmt, i) in blockStmt.Body.Select((stmt, i) => (stmt, i))) {
            if (_findStmtPred != null && _findStmtPred(stmt)) {
                _targetStmt = (blockStmt, i);
                return;
            }
            HandleStatement(stmt);
            _currentBlock = blockStmt;
            _prevStmt = stmt;
        }
        
        _prevStmt = null;
    }
}