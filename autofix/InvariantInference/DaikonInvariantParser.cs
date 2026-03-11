using System.Text.RegularExpressions;
using Microsoft.BaseTypes;
using Microsoft.Dafny;

namespace AutoFix.InvariantInference;

public class DaikonInvariantParser : Visitor
{
    public static readonly List<(string, string)> TypeInfo = ImportTypeInfo(); // (var name, var type)
    private readonly List<(Expression, int, Statement)> _invariantsPlacement = []; // (invariant, location, statement after which invariant should be inserted)
    public List<Expression> AllInvariants => _invariantsPlacement.Select(i => i.Item1).ToList();
    
    private readonly List<PrintStmt> _newPrintStmts = [];
    private string _enclosingClassName = "";
    private BlockStmt? _currentBlock;
    private (ReturnStmt?, int) _lastStmtReturn;
    private Predicate<Statement>? _findStmtPred;
    private Statement? _prevStmt;
    private (BlockStmt?, int) _targetStmt = (null, -1);

    private ControlDependenceAnalyzer? _cDepAnalyzer;
    private ExpressionDependenceAnalyzer? _eDepAnalyzer;
    
    public void Parse(ModuleDefinition module) {
        Visit(module);
        if (AutoFix.FaultyMethod == null || SnapshotGenerator.InvariantsAlreadyParsed)
            return;
        var faultyMethod = AutoFix.FaultyMethod;
        if (faultyMethod.Body == null) return;

        int location = module.StartToken.pos;
        var lines = File.ReadAllLines("inv.inv");
        foreach (var line in lines) {
            if (line.EndsWith(":::ENTER") || line.Contains(":::EXIT")) {
                if (line.Contains(faultyMethod.Name) && line.Contains(_enclosingClassName)) {
                    location = line.EndsWith(":::ENTER") ? faultyMethod.Body.StartToken.pos : 
                        _lastStmtReturn.Item1 == null ? faultyMethod.Body.EndToken.pos : _lastStmtReturn.Item2;
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
        
        _cDepAnalyzer = new ControlDependenceAnalyzer();
        InstrumentWithInvariants();
        SnapshotGenerator.InvariantsAlreadyParsed = true;
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
    }
    
    protected override void HandleMethod(Method method) {
        // distinguish passing from failing test execution
        if (_enclosingClassName == "_default" && method.Name == "Passing")
            AutoFix.PassingTestsMethod = method;
        if (_enclosingClassName == "_default" && method.Name == "Failing")
            AutoFix.FailingTestsMethod = method;
            
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= AutoFix.ViolationLine &&
              method.EndToken.line >= AutoFix.ViolationLine))
            return;
        AutoFix.FaultyMethod = method;
        if (method.Body is { Body.Count: > 0 } && method.Body.Body[^1] is ReturnStmt rStmt)
            _lastStmtReturn = (rStmt, -1);
        base.HandleMethod(method);
    }
    
    protected override void VisitStatement(ProduceStmt pStmt) {
        if (pStmt is ReturnStmt retStmt && _lastStmtReturn.Item1 != null && _lastStmtReturn.Item1 == retStmt) {
            if (_prevStmt != null) {
                _lastStmtReturn.Item2 = _prevStmt.EndToken.pos;
            } else if (_currentBlock != null) {
                _lastStmtReturn.Item2 = _currentBlock.StartToken.pos;
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
        var faultyMethod = AutoFix.FaultyMethod;
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
        var faultyMethod = AutoFix.FaultyMethod;
        if (faultyMethod == null || faultyMethod.Body == null) return;

        foreach (var (inv, location, placement) in _invariantsPlacement) {
            if (ReferenceEquals(placement, faultyMethod.Body)) {
                if (location == faultyMethod.Body.StartToken.pos) {
                    faultyMethod.Body.Body.Insert(0, PrintInvariant(inv, location, placement));
                } else {
                    faultyMethod.Body.Body.Add(PrintInvariant(inv, location, placement));
                }
            } else {
                _findStmtPred = stmt => ReferenceEquals(stmt, placement);
                HandleMethod(faultyMethod);
                if (_targetStmt.Item1 == null)
                    continue;
                _targetStmt.Item1.Body.Insert(_targetStmt.Item2 + 1, PrintInvariant(inv, location, placement));
                _findStmtPred = null;
                _targetStmt = (null, -1);
            }
        }
    }

    private PrintStmt PrintInvariant(Expression invariantExpr, int location, Statement placementRefStmt) {
        var posElement = Expression.CreateIntLiteral(null, location);
        var exprStrElement = AstUtils.CreateStringLiteral(null, invariantExpr.ToString());
        var snapshotCDep = _cDepAnalyzer?.ComputeCDep(location, placementRefStmt) ?? 0.0;
        var snapshotCDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotCDep}".Replace(',', '.')));
        var delimElement1 = AstUtils.CreateStringLiteral(null, ";");
        var delimElement2 = AstUtils.CreateStringLiteral(null, "\\n");
        List<Expression> printElements = [
            posElement, delimElement1, exprStrElement, delimElement1, 
            invariantExpr, delimElement1, snapshotCDepElement, delimElement2
        ];
        
        var printStmt = new PrintStmt(null, printElements);
        _newPrintStmts.Add(printStmt);
        return printStmt; 
    }

    public void AddEDepToInvariantPrints() {
        _eDepAnalyzer = new ExpressionDependenceAnalyzer(SnapshotGenerator.AllPredicates);

        foreach (var printStmt in _newPrintStmts) {
            var invariantExpr = printStmt.Args[4];
            if (invariantExpr == null) continue;
            var snapshotEDep = _eDepAnalyzer?.ComputeEDep(invariantExpr) ?? 0.0;
            var snapshotEDepElement = Expression.CreateRealLiteral(null, BigDec.FromString($"{snapshotEDep}".Replace(',', '.')));
            var delimElement = AstUtils.CreateStringLiteral(null, ";");
            printStmt.Args.InsertRange(7, [delimElement, snapshotEDepElement]);
        }
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