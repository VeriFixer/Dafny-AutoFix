using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace AutoFix.InvariantInference;

public class DaikonInstrumenter(List<Identifier> identifierAvailability) : Visitor(true)
{
    private TopLevelDeclWithMembers? _currentTopLevelDecl;
    private BlockStmt? _currentBlock;
    private readonly List<Method> _newMethods = [];
    private List<Statement> _newBlockBody = [];
    private readonly List<Statement> _newStmts = [];
    private ReturnStmt? _lastStmtReturn;
    private int _outerLoopCount;
    private bool _invariantInstrumentationComplete;
    
    public void Instrument(ModuleDefinition module) {
        Visit(module);
        if (AutoFix.MainMethod == null || 
            AutoFix.FaultyMethod == null)
            return;
        var mainMethod = AutoFix.MainMethod;
        var faultyMethod = AutoFix.FaultyMethod;
        AdjustTestExecution(mainMethod);
        
        AddHeader();
        AddMethodDeclaration(faultyMethod);
        AddDummyMethodsDeclarations();
        if (mainMethod.Body == null) {
            mainMethod.SetBody(new BlockStmt(mainMethod.Origin, _newStmts));
        } else {
            mainMethod.Body.Body.InsertRange(0, _newStmts);
        }
        AddMethodTracePoints(faultyMethod);
        AddDummyMethodsTracePoints();
        _invariantInstrumentationComplete = true;
        // put max iterations in loops to avoid infinite execution
        HandleMethod(faultyMethod);
    }
    
    protected override void HandleMemberDecls(TopLevelDeclWithMembers decl) {
        _currentTopLevelDecl = decl;
        base.HandleMemberDecls(decl);
        decl.Members.AddRange(_newMethods);
    }
    
    protected override void HandleMethod(MethodOrConstructor methodOrConstructor) {
        if (methodOrConstructor is not Method method) return;
        
        // find Main, i.e., entrypoint, where the faulty method is being exercised
        if (method.Name == "Main")
            AutoFix.MainMethod = method;
        // find the faulty method, i.e., where the violation occurs  
        if (!(method.StartToken.line <= AutoFix.ViolationLine &&
              method.EndToken.line >= AutoFix.ViolationLine))
            return;
        AutoFix.FaultyMethod = method;
        
        // instrument the method with calls to dummy methods for invariant inference
        if (method.Body is { Body.Count: > 0 } && method.Body.Body[^1] is ReturnStmt rStmt)
            _lastStmtReturn = rStmt;
        if (method.Body != null)
            HandleBlock(method.Body);
    }
    
    protected override void VisitStatement(LoopStmt loopStmt) {
        if (_invariantInstrumentationComplete && _currentBlock != null &&
            !DaikonFormatConverter.NewLoops.Contains(loopStmt) && 
            loopStmt is OneBodyLoopStmt oneBodyLoopStmt)
            AstUtils.LimitLoopIterations(oneBodyLoopStmt, _currentBlock, _outerLoopCount);
        _outerLoopCount++;
        base.VisitStatement(loopStmt);
        _outerLoopCount--;
    }
    
    /// -------------------------
    /// Instrumentation
    /// -------------------------
    protected override void HandleBlock(BlockStmt blockStmt) {
        var previousCurrentBlock = _currentBlock;
        _currentBlock = blockStmt;

        if (_invariantInstrumentationComplete) {
            base.HandleBlock(blockStmt);
        } else {
            InstrumentBlock(blockStmt);
        }
        
        _currentBlock = previousCurrentBlock;
    }

    private void InstrumentBlock(BlockStmt blockStmt) {
        var faultyMethod = AutoFix.FaultyMethod;
        var prevNewBlock = _newBlockBody;
        _newBlockBody = [];

        if (blockStmt != faultyMethod?.Body)
            InstrumentLine(blockStmt.StartToken);
        foreach (var (stmt, i) in blockStmt.Body.Select((stmt, i) => (stmt, i))) {
            if (stmt is PrintStmt) continue;
            _newBlockBody.Add(stmt);
            HandleStatement(stmt);
            if (stmt is ReturnStmt || 
                (i < blockStmt.Body.Count -1 && blockStmt.Body[i + 1] == _lastStmtReturn) || 
                (i == blockStmt.Body.Count - 1 && blockStmt == faultyMethod?.Body))
                continue;
            InstrumentLine(stmt.EndToken);
        }
        
        blockStmt.Body.Clear();
        blockStmt.Body.AddRange(_newBlockBody);
        _newBlockBody = prevNewBlock;
    }

    private void InstrumentLine(IOrigin token) {
        if (_currentTopLevelDecl == null) return;
        
        var availableIdentifiers = identifierAvailability.Where(
            id => 
                (token.pos > id.AvailabilityStartPos && token.pos < id.AvailabilityEndPos) || 
                (id.AvailabilityStartPos == null && id.AvailabilityEndPos == null)
        ).DistinctBy(id => id.Name).ToList();
        foreach (var identifier in availableIdentifiers.ToList()) {
            var repType = DaikonFormatConverter.ToType(identifier.Type); 
            var comparability = DaikonFormatConverter.GetComparability(identifier.Type);
            if (repType == "" || comparability == "")
                availableIdentifiers.Remove(identifier);
        }
        
        var newMethod = GenerateDummyMethod(token.pos, availableIdentifiers);
        if (newMethod == null) return;

        List<ActualBinding> arguments = [];
        List<Expression> callArgs = [];
        foreach (var identifier in availableIdentifiers) {
            var argumentName = new NameSegment(token, identifier.Name, null);
            AstUtils.ResolveNameSegment(argumentName, identifier);
            callArgs.Add(argumentName);
            arguments.Add(new ActualBinding(null, argumentName));
        }
        var methodName = new NameSegment(token, $"Dummy{token.pos}", null);
        var methodCall = new ApplySuffix(token, null, methodName, arguments, null);
        var newStmt = new AssignStatement(token, [], [new ExprRhs(token, methodCall)]);
        AstUtils.ResolveCallAssignStatement(newStmt, newMethod, callArgs, _currentTopLevelDecl);
        _newBlockBody.Add(newStmt);
    }

    private Method? GenerateDummyMethod(int dummyMethodID, List<Identifier> availableIdentifiers) {
        if (_currentTopLevelDecl == null) return null;
        var token = _currentTopLevelDecl.EndToken.MakeAutoGenerated();
        
        var methodName = new Name(token, $"Dummy{dummyMethodID}");
        List<Formal> inputs = [];
        foreach (var identifier in availableIdentifiers) {
            inputs.Add(new Formal(
                token, identifier.Name, identifier.Type, true, false, null
            ));
        }

        var newMethod = new Method(
            token, methodName, null, false, false, [], inputs, [], [],
            new Specification<FrameExpression>([], null),
            new Specification<Expression>([], null), [],
            new Specification<FrameExpression>([], null),
            new BlockStmt(token, []), null
        ) { EnclosingClass = _currentTopLevelDecl };
        _newMethods.Add(newMethod);
        return newMethod;
    }
    
    private void AdjustTestExecution(Method method) {
        if (method.Body == null) return;
        foreach (var (stmt, i) in method.Body.Body.Select((stmt, i) => (stmt, i))) {
            if (stmt is not AssignStatement { Rhss: [ExprRhs { Expr: ApplySuffix { Lhs: NameSegment nSegExpr} }] })
                continue;
            if ((!InvariantInferrer.FailingInvariantInference && nSegExpr.Name == "Failing") ||
                (!InvariantInferrer.PassingInvariantInference && nSegExpr.Name == "Passing")) {
                method.Body.Body.RemoveAt(i);
                break;
            }
        }
    }

    /// -------------------------
    /// Daikon
    /// -------------------------
    private void AddHeader() {
        var method = AutoFix.FaultyMethod;
        if (method == null) return;
        
        var headerElement = AstUtils.CreateStringLiteral(method.Origin, "decl-version 2.0\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [headerElement]));
        headerElement = AstUtils.CreateStringLiteral(method.Origin, "input-language Dafny\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [headerElement]));
        var delimElement = AstUtils.CreateStringLiteral(method.Origin, "\\n");
        _newStmts.Add(new PrintStmt(method.Origin, [delimElement]));
    }

    private void AddMethodDeclaration(Method method) {
        var faultyMethod = AutoFix.FaultyMethod;
        var mainMethod = AutoFix.MainMethod;
        if (faultyMethod == null || mainMethod == null) return;
        
        foreach (var type in new List<string> { "ENTER", "EXIT00" }) {
            var programPointDecl = $"ppt {method.FullSanitizedName}():::{type}\\n";
            var declarationElement = AstUtils.CreateStringLiteral(mainMethod.Origin, programPointDecl);
            _newStmts.Add(new PrintStmt(mainMethod.Origin, [declarationElement]));
            var programPointType = type == "ENTER" ? "ppt-type enter\\n" : "ppt-type subexit\\n";
            declarationElement = AstUtils.CreateStringLiteral(mainMethod.Origin, programPointType);
            _newStmts.Add(new PrintStmt(mainMethod.Origin, [declarationElement]));

            if (method == faultyMethod) {
                AddFaultyMethodVariableDeclarations(method, type);
            } else {
                AddDummyMethodVariableDeclarations(method, type == "EXIT00");
            }
            
            var delimElement = AstUtils.CreateStringLiteral(method.Origin, "\\n");
            _newStmts.Add(new PrintStmt(method.Origin, [delimElement]));
        }
    }

    private void AddFaultyMethodVariableDeclarations(Method method, string pointType) {
        var position = pointType.EndsWith("ENTER") ? method.Body?.StartToken.pos : method.Body?.EndToken.pos;
        var availableIdentifiers = identifierAvailability.Where(
            id => 
                (position > id.AvailabilityStartPos && position <= id.AvailabilityEndPos) || 
                (id.AvailabilityStartPos == null && id.AvailabilityEndPos == null)
        ).DistinctBy(id => id.Name).ToList();
        foreach (var identifier in availableIdentifiers)
            AddVariableDeclaration(identifier.Name, identifier.Type, method.Origin);
    }

    private void AddDummyMethodVariableDeclarations(Method method, bool isExit) {
        var mainMethod = AutoFix.MainMethod;
        if (mainMethod == null) return;
        
        foreach (var input in method.Ins.Where(i => !i.IsGhost))
            AddVariableDeclaration(input.DisplayName, input.Type, mainMethod.Origin);
        if (!isExit) return;
        foreach (var output in method.Outs.Where(o => !o.IsGhost))
            AddVariableDeclaration(output.DisplayName, output.Type, mainMethod.Origin);
    }

    private void AddVariableDeclaration(string name, Type t, IOrigin token) {
        var decType = t.ToString().Replace(" ", "");
        var repType = DaikonFormatConverter.ToType(t); 
        var comparability = DaikonFormatConverter.GetComparability(t);
        if (repType == "" || comparability == "") return;

        // arrays require two variable declarations: the array object itself and its contents
        if (DaikonFormatConverter.IsArrayType(t)) { // array object declaration
            AddVariableDeclaration(token, name, decType,
                "hashcode", "9", false);
        }
        // array contents and other variable types declaration
        AddVariableDeclaration(
            token, name, decType, repType, comparability, 
            DaikonFormatConverter.IsArrayType(t)
        );
    }

    private void AddVariableDeclaration(IOrigin token, string varName, string decType, string repType, string comparability, bool isArray) {
        var declarationElement = AstUtils.CreateStringLiteral(token, 
            $"variable {varName}{(isArray ? "[..]" : "")}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        declarationElement = AstUtils.CreateStringLiteral(token, 
            $"\tvar-kind {(isArray ? "array" : "variable")}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        if (isArray) {
            declarationElement = AstUtils.CreateStringLiteral(token, $"\tenclosing-var {varName}\\n");
            _newStmts.Add(new PrintStmt(token, [declarationElement]));
        }
        declarationElement = AstUtils.CreateStringLiteral(token, $"\tdec-type {decType}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        declarationElement = AstUtils.CreateStringLiteral(token, $"\trep-type {repType}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
        declarationElement = AstUtils.CreateStringLiteral(token, $"\tcomparability {comparability}\\n");
        _newStmts.Add(new PrintStmt(token, [declarationElement]));
    }

    private void AddDummyMethodsDeclarations() {
        foreach (var dummyMethod in _newMethods) {
            AddMethodDeclaration(dummyMethod);
        }
    }

    private void AddMethodTracePoints(Method method) {
        // Add points at the method's entrance
        var newStmts = AddMethodTracePoints(method, false);
        if (method.Body == null) {
            method.SetBody(new BlockStmt(method.Origin, newStmts));
        } else {
            method.Body.Body.InsertRange(0, newStmts);
        }
        // Add points at the method's exit(s)
        newStmts = AddMethodTracePoints(method, true);
        if (method != AutoFix.FaultyMethod || _lastStmtReturn == null) {
            method.Body.Body.AddRange(newStmts);
        } else {
            var returnIdx = method.Body.Body.IndexOf(_lastStmtReturn);
            method.Body.Body.InsertRange(returnIdx, newStmts);
        }
    }
    
    private List<Statement> AddMethodTracePoints(Method method, bool isExit) {
        List<Statement> newStmts = [];
        var type = isExit ? "EXIT00" : "ENTER";
        var programPoint = $"{method.FullSanitizedName}():::{type}\\n";
        var declarationElement = AstUtils.CreateStringLiteral(method.Origin, programPoint);
        newStmts.Add(new PrintStmt(method.Origin, [declarationElement]));

        newStmts.AddRange(method == AutoFix.FaultyMethod
            ? AddFaultyMethodVariableTracePoints(method, isExit)
            : AddDummyMethodVariableTracePoints(method, isExit));

        var delimElement = AstUtils.CreateStringLiteral(method.Origin, "\\n");
        newStmts.Add(new PrintStmt(method.Origin, [delimElement]));
        return newStmts;
    }

    private List<Statement> AddFaultyMethodVariableTracePoints(Method method, bool isExit) {
        List<Statement> newStmts = [];
        var position = isExit ? method.Body?.EndToken.pos : method.Body?.StartToken.pos;
        var availableIdentifiers = identifierAvailability.Where(
            id => 
                (position > id.AvailabilityStartPos && position <= id.AvailabilityEndPos) || 
                (id.AvailabilityStartPos == null && id.AvailabilityEndPos == null)
        ).DistinctBy(id => id.Name).ToList();
        foreach (var identifier in availableIdentifiers) {
            newStmts.AddRange(AddVariableTracePoint(method, identifier));
        }
        return newStmts;
    }

    private List<Statement> AddDummyMethodVariableTracePoints(Method method, bool isExit) {
        List<Statement> newStmts = [];
        foreach (var input in method.Ins.Where(i => !i.IsGhost)) {
            var identifier = new Identifier(input, null, input.Type, null, null);
            newStmts.AddRange(AddVariableTracePoint(method, identifier));
        }
        if (!isExit) return newStmts;
        foreach (var output in method.Outs.Where(o => !o.IsGhost)) {
            var identifier = new Identifier(output, null, output.Type, null, null);
            newStmts.AddRange(AddVariableTracePoint(method, identifier));
        }
        return newStmts;
    }

    private List<Statement> AddVariableTracePoint(Method method, Identifier id) {
        var repType = DaikonFormatConverter.ToType(id.Type); 
        var comparability = DaikonFormatConverter.GetComparability(id.Type);
        if (repType == "" || comparability == "") return [];
        var daikonValue = DaikonFormatConverter.ToDaikonValue(method.Origin, id);
        if (daikonValue == null)
            return [];
        
        List<Statement> newStmts = [];
        if (DaikonFormatConverter.IsArrayType(id.Type)) {
            // random hashcode since, for now, we are not interested in invariants related to this
            var hashcodeElem = AstUtils.CreateStringLiteral(method.Origin, "416153648\\n");
            var hashCodePrinter = new PrintStmt(method.Origin, [hashcodeElem]);
            newStmts.AddRange(AddVariableTracePoint(
                method.Origin, id.Name, hashCodePrinter, false
            ));
        }
        newStmts.AddRange(AddVariableTracePoint(method.Origin, id.Name, 
            daikonValue, DaikonFormatConverter.IsArrayType(id.Type)));
        return newStmts;
    }

    private List<Statement> AddVariableTracePoint(IOrigin token, string varName, Statement varValuePrinter, bool isArray) {
        List<Statement> newStmts = [];
        // Print the name of the variable
        var varIdentification = AstUtils.CreateStringLiteral(token, 
            $"{varName}{(isArray ? "[..]" : "")}\\n"
        );
        newStmts.Add(new PrintStmt(token, [varIdentification]));
        // Print the value of the variable
        newStmts.Add(varValuePrinter);
        // Print the modified bit
        var modBit = Expression.CreateIntLiteral(token, 0);
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        newStmts.Add(new PrintStmt(token, [modBit, delimElement]));
        return newStmts;
    }
    
    private void AddDummyMethodsTracePoints() {
        foreach (var dummyMethod in _newMethods) {
            AddMethodTracePoints(dummyMethod);
        }
    }
}