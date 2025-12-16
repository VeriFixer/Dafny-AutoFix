using Microsoft.Dafny;
using SnapshotGenerator.Enumeration;

namespace SnapshotGenerator.InvariantInference;

public class DaikonInstrumenter() : IdentifierAvailabilityScanner(true)
{
    private List<Statement> _newStmts = [];
    
    public void Instrument(ModuleDefinition module) {
        Visit(module);
        if (InvariantInferrer.MainMethod == null || 
            InvariantInferrer.FaultyMethod == null)
            return;
        var mainMethod = InvariantInferrer.MainMethod;
        var faultyMethod = InvariantInferrer.FaultyMethod;
        
        AddHeader();
        AddMethodDeclaration(faultyMethod);
        // TODO: dummy methods declarations
        if (mainMethod.Body == null) {
            mainMethod.Body = new BlockStmt(mainMethod.Origin, _newStmts);
        } else {
            mainMethod.Body.Body = _newStmts.Concat(mainMethod.Body.Body).ToList();  
        }
    }
    
    // TODO: get all variables available at each location of the faulty method
    // TODO: insert dummy method calls that take as input the variables available at given location
    
    protected override void HandleMethod(Method method) {
        // find Main, i.e., entrypoint, where the faulty method is being exercised
        if (method.Name == "Main")
            InvariantInferrer.MainMethod = method;
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= InvariantInferrer.ViolationLine &&
              method.EndToken.line >= InvariantInferrer.ViolationLine)) {
            HandleMethodBase(method); // keep the visit going without collecting identifiers
            return;
        }
        InvariantInferrer.FaultyMethod = method;
        // first, we get the identifiers availability
        base.HandleMethod(method);
    }

    /// -------------------------
    /// Daikon
    /// -------------------------
    private void AddHeader() {
        var method = InvariantInferrer.FaultyMethod;
        if (method == null) return;
        
        var headerElement = ExpressionScanner.CreateStringLiteral(method.Origin, "decl-version 2.0\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [headerElement]));
        headerElement = ExpressionScanner.CreateStringLiteral(method.Origin, "input-language Dafny\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [headerElement]));
        var delimElement = ExpressionScanner.CreateStringLiteral(method.Origin, "\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [delimElement]));
    }

    private void AddMethodDeclaration(Method method) {
        var mainMethod = InvariantInferrer.FaultyMethod;
        if (mainMethod == null) return;
        
        foreach (var type in new List<string> { "ENTER", "EXIT00" }) {
            var programPointDecl = $"ppt {mainMethod.FullSanitizedName}():::{type}\\n";
            var declarationElement = ExpressionScanner.CreateStringLiteral(mainMethod.Origin, programPointDecl);
            _newStmts.Add(new PrintStmt(mainMethod.Origin, [declarationElement]));
            var programPointType = type == "ENTER" ? "ppt-type enter" : "ppt-type subexit\\n";
            declarationElement = ExpressionScanner.CreateStringLiteral(mainMethod.Origin, programPointType);
            _newStmts.Add(new PrintStmt(mainMethod.Origin, [declarationElement]));


            foreach (var input in method.Ins)
                AddVariableDeclaration(input, mainMethod.Origin);
            if (type == "EXIT00") {
                foreach (var output in method.Outs)
                    AddVariableDeclaration(output, mainMethod.Origin);
            }
            
            var delimElement = ExpressionScanner.CreateStringLiteral(method.Origin, "\\n");
            _newStmts.Add(new PrintStmt(method.Origin, [delimElement]));
        }
    }

    private void AddVariableDeclaration(Formal f, IOrigin token) {
        var declarationElement = ExpressionScanner.CreateStringLiteral(token, $"variable {f.CompileName}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        declarationElement = ExpressionScanner.CreateStringLiteral(token, "\tvar-kind variable\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        declarationElement = ExpressionScanner.CreateStringLiteral(token, $"\tdec-type {f.Type}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        var repType = "\trep-type " + f.Type.ToString() switch {
            "int" => "int\\n",
            "real" => "double\\n",
            _ => "hashcode\\n"
        };
        declarationElement = ExpressionScanner.CreateStringLiteral(token, repType);
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        var comparability = "\tcomparability " + f.Type.ToString() switch {
            "bool" => "1\\n",
            "int" => "2\\n",
            _ => "20\\n"
        };
        declarationElement = ExpressionScanner.CreateStringLiteral(token, comparability);
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
    }
}