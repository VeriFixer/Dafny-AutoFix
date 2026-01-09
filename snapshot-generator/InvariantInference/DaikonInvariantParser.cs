using System.Text.RegularExpressions;
using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInvariantParser() : Visitor(true)
{
    public static readonly List<(string, string)> TypeInfo = ImportTypeInfo(); // (var name, var type)
    
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
                if (line.Contains(faultyMethod.FullSanitizedName)) {
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
    
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= SnapshotGenerator.ViolationLine &&
              method.EndToken.line >= SnapshotGenerator.ViolationLine))
            return;
        InvariantInferrer.FaultyMethod = method;
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
        return e.ToExpression();
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
            }
            if (word.StartsWith("__orig__")) {
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
        invariant = Regex.Replace(invariant, @"\s+", " ");
        invariant = Regex.Replace(invariant, @"\d+(?:\.\d+)?d0", 
            match => match.Value.Substring(0, match.Value.Length - 2));
        return invariant.Replace("select elems", "selectElems");
    }

    /// -------------------------
    /// Instrumentation
    /// -------------------------
    private void InstrumentWithInvariant(int location, Expression invariantExpr) {
        // TODO: Implement
    }
}