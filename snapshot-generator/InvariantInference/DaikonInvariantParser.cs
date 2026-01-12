using System.Text.RegularExpressions;
using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInvariantParser : Visitor
{
    public static readonly List<(string, string)> TypeInfo = ImportTypeInfo(); // (var name, var type)
    private TopLevelDeclWithMembers? _classUnderVisit;
    private string _enclosingClassName = "";
    
    public void Parse(ModuleDefinition module) {
        Visit(module);
        if (InvariantInferrer.FaultyMethod == null)
            return;
        var faultyMethod = InvariantInferrer.FaultyMethod;
        if (faultyMethod.Body == null) return;

        int location = module.StartToken.pos;
        var lines = File.ReadAllLines("inv.inv");
        foreach (var line in lines) {
            if (line.EndsWith(":::ENTER") || line.EndsWith(":::EXIT")) {
                if (line.Contains(faultyMethod.Name) && line.Contains(_enclosingClassName)) {
                    location = line.EndsWith(":::ENTER") ? 
                        faultyMethod.Body.StartToken.pos : 
                        faultyMethod.Body.EndToken.pos;
                } else if (line.Contains("Dummy")) {
                    var locationStartIndex = line.IndexOf("Dummy", StringComparison.Ordinal) + 5;
                    var locationEndIndex = line.EndsWith(":::ENTER") ? 10 : 9;
                    location = int.Parse(line[locationStartIndex..^locationEndIndex]);
                }
            } else {
                var invExpr = ParseInvariant(line);
                if (invExpr == null)
                    continue;
                InstrumentWithInvariant(location, invExpr);
            }
        }
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
        _classUnderVisit = decl;
        base.HandleMemberDecls(decl);
    }
    
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= SnapshotGenerator.ViolationLine &&
              method.EndToken.line >= SnapshotGenerator.ViolationLine))
            return;
        InvariantInferrer.FaultyMethod = method;
        if (_classUnderVisit == null) return;
        _enclosingClassName = _classUnderVisit.Name;
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
            };
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
    private void InstrumentWithInvariant(int location, Expression invariantExpr) {
        var faultyMethod = InvariantInferrer.FaultyMethod;
        if (faultyMethod == null || faultyMethod.Body == null) return;

        if (location == faultyMethod.Body.StartToken.pos) {
            var token = faultyMethod.Body.StartToken;
            faultyMethod.Body.Body.Insert(0, PrintInvariant(token, invariantExpr));
        } else if (location == faultyMethod.Body.EndToken.pos) {
            var token = faultyMethod.Body.EndToken;
            faultyMethod.Body.Body.Add(PrintInvariant(token, invariantExpr));
        } else {
            foreach (var (stmt, i) in faultyMethod.Body.Body.Select((stmt, i) => (stmt, i))) {
                if (location == stmt.EndToken.pos) {
                    var token = stmt.EndToken;
                    faultyMethod.Body.Body.Insert(i + 1, PrintInvariant(token, invariantExpr));
                    break;
                }
            }
        }
    }

    private PrintStmt PrintInvariant(IOrigin token, Expression invariantExpr) {
        var posElement = Expression.CreateIntLiteral(token, token.pos);
        var exprStrElement = AstUtils.CreateStringLiteral(token, invariantExpr.ToString());
        var delimElement1 = AstUtils.CreateStringLiteral(token, ",");
        var delimElement2 = AstUtils.CreateStringLiteral(token, "\\n");
        List<Expression> printElements = [
            posElement, delimElement1, exprStrElement, delimElement1, invariantExpr, delimElement2
        ];
        return new PrintStmt(token, printElements); 
    }
}