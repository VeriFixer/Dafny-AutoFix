using Microsoft.Dafny;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInstrumenter : Visitor
{
    public void Instrument(ModuleDefinition module) {
        Visit(module);
        if (InvariantInferrer.FaultyMethod == null)
            return;
        // TODO: start instrumenting
    }
    
    // TODO: get all variables available at each location of the faulty method
    // TODO: insert dummy method calls that take as input the variables available at given location
    protected override void HandleMethod(Method method) {
        // find the faulty method, i.e., where the violation occurs  
        if (method.StartToken.line <= InvariantInferrer.ViolationLine &&
            method.EndToken.line >= InvariantInferrer.ViolationLine)
            InvariantInferrer.FaultyMethod = method;
        // this is the point at which instrumentation begins
    }
}