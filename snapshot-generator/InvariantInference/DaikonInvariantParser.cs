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
    private Expression ParseInvariant(String invariant) {
        return null; // TODO: Implement
    }

    private void InstrumentWithInvariant(int location, Expression invariantExpr) {
        // TODO: Implement
    }
}