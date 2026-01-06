using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInvariantParser() : Visitor(true)
{
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
    
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= SnapshotGenerator.ViolationLine &&
              method.EndToken.line >= SnapshotGenerator.ViolationLine))
            return;
        InvariantInferrer.FaultyMethod = method;
    }

    /// -------------------------
    /// Invariants
    /// -------------------------
    private Stack<SimplifyToken> _stack = new();
    private bool _invalidInvariant;
    
    private Expression? ParseInvariant(String invariant) {
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
                [new SimplifyToken(SimplifyToken.SimplifyTokenType.OPEN_PAREN)],
                LexInvariantWord(word[1..])
            ).ToList();
        if (word.EndsWith(')')) {
            _stack.Push(new SimplifyToken(SimplifyToken.SimplifyTokenType.CLOSE_PAREN));
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

    private void InstrumentWithInvariant(int location, Expression invariantExpr) {
        // TODO: Implement
    }
}