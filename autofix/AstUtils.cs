using Microsoft.Dafny;
using IdentifierExpr = Microsoft.Dafny.IdentifierExpr;
using MapType = Microsoft.Dafny.MapType;
using Type = Microsoft.Dafny.Type;

namespace AutoFix;

public static class AstUtils
{
    public static Dictionary<string, IVariable> IterCounterVars = [];
    /// -----------------------------------------------------
    /// My version of the (missing) static methods of Dafny's
    /// Expression class for creating resolved expressions
    /// -----------------------------------------------------
    public static Expression CreateNeq(Expression e0, Expression e1, Type ty) {
        var neq = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Neq, e0, e1);
        if (ty is SetType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.SetNeq;
        } else if (ty is SeqType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.SeqNeq;
        } else if (ty is MultiSetType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.MultiSetNeq;
        } else if (ty is MapType) {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.MapNeq;
        } else {
            neq.ResolvedOp = BinaryExpr.ResolvedOpcode.NeqCommon;
        }
        neq.Type = Type.Bool;
        return neq;
    }
    
    public static Expression CreateGreater(Expression e0, Expression e1) {
        return new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Gt, e0, e1) {
            ResolvedOp = e0.Type.IsCharType ? BinaryExpr.ResolvedOpcode.GtChar : BinaryExpr.ResolvedOpcode.Gt,
            Type = Type.Bool
        };
    }
    
    public static Expression CreateAtLeast(Expression e0, Expression e1) {
        return new BinaryExpr(e0.Origin, BinaryExpr.Opcode.Ge, e0, e1) {
            ResolvedOp = e0.Type.IsCharType ? BinaryExpr.ResolvedOpcode.GeChar : BinaryExpr.ResolvedOpcode.Ge,
            Type = Type.Bool
        };
    }
    
    public static Expression CreateIn(Expression e0, Expression e1, Type ty) {
        var inExpr = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.In, e0, e1);
        if (ty is SetType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InSet;
        } else if (ty is SeqType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InSeq;
        } else if (ty is MultiSetType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InMultiSet;
        } else if (ty is MapType) {
            inExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.InMap;
        }
        inExpr.Type = Type.Bool;
        return inExpr;
    }
    
    public static Expression CreateNotIn(Expression e0, Expression e1, Type ty) {
        var notInExpr = new BinaryExpr(e0.Origin, BinaryExpr.Opcode.NotIn, e0, e1);
        if (ty is SetType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInSet;
        } else if (ty is SeqType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInSeq;
        } else if (ty is MultiSetType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInMultiSet;
        } else if (ty is MapType) {
            notInExpr.ResolvedOp = BinaryExpr.ResolvedOpcode.NotInMap;
        }
        notInExpr.Type = Type.Bool;
        return notInExpr;
    }

    public static Expression CreateStringLiteral(IOrigin token, string s) {
        var stringLit = (StringLiteralExpr)Expression.CreateStringLiteral(token, s);
        stringLit.IsVerbatim = false;
        return stringLit;
    } 
    
    /// ----------------------------------------------------
    /// New methods for the creation of resolved expressions
    /// ----------------------------------------------------
    public static void ResolveNameSegment(NameSegment nSegExpr, Identifier id, Type? type = null) {
        nSegExpr.Type = type ?? id.Type;
        Expression resolvedExpr;
        if (id.Var != null) {
            resolvedExpr = new IdentifierExpr(nSegExpr.Origin, id.Var);
        } else if (id.Field != null && id.EnclosingClass != null) {
            resolvedExpr = CreateMemberSelectExpr(nSegExpr.Origin, id.Field, id.EnclosingClass);
        } else {
            return;
        }
        nSegExpr.ResolvedExpression = resolvedExpr;
    }

    public static MemberSelectExpr? CreateMemberSelectExpr(IOrigin token, MemberDecl call, TopLevelDeclWithMembers? enclosingClass, Expression? obj = null, Type? type = null) {
        Expression? myObj = null;
        if (enclosingClass != null) {
            if (enclosingClass is DefaultClassDecl) {
                myObj = new StaticReceiverExpr(token, enclosingClass, true);
            } else {
                myObj = new ImplicitThisExpr(token);
                var className = new NameSegment(token, enclosingClass.Name, []);
                myObj.Type = new UserDefinedType(token, enclosingClass.Name, enclosingClass, [], className);
            } 
        }

        if (myObj == null && obj == null) return null;
        var expr = new MemberSelectExpr(token, obj ?? myObj, call.NameNode) {
            Member = call,
            TypeApplicationJustMember = []
        };
        if (type != null)
            expr.Type = type;
        return expr;
    }

    public static SpecialField CreateLengthSpecialField(IOrigin token, int? param = null) {
        return new SpecialField(
            token, "Length", SpecialField.ID.ArrayLength, param, 
            false, false, false, Type.Int, null
        );
    }

    public static SpecialField CreateKeysSpecialField(IOrigin token, Type argType) {
        return new SpecialField(
            token, "Keys", SpecialField.ID.Keys, null,
            false, false, false, new SetType(true, argType), null
        );
    }

    /// ---------------------------------------------------
    /// New methods for the creation of resolved statements
    /// ---------------------------------------------------
    public static void ResolveNormalAssignStatement(AssignStatement aStmt) {
        aStmt.ResolvedStatements = [];
        for (var i = 0; i < aStmt.Lhss.Count; i++) {
          var resolvedAssign = new SingleAssignStmt(aStmt.Origin, aStmt.Lhss[i].Resolved, aStmt.Rhss[i]);
          aStmt.ResolvedStatements.Add(resolvedAssign);
        }
    }
    
    public static void ResolveCallAssignStatement(AssignStatement aStmt, Method call, List<Expression> args, TopLevelDeclWithMembers enclosingClass) {
        if (!(aStmt.Rhss.Count == 1 && aStmt.Rhss[0] is ExprRhs exprRhs && 
              exprRhs.Expr is ApplySuffix appSufExpr))
            return;
        
        var memberSelectExpr = CreateMemberSelectExpr(appSufExpr.Origin, call, enclosingClass);
        var resolvedStmt = new CallStmt(aStmt.Origin, [], memberSelectExpr, args);
        aStmt.ResolvedStatements = [resolvedStmt];
    }

    public static void ResolveAssignSuchThatStatement(AssignSuchThatStmt aStStmt) {
        var varLhss = new List<IVariable>();
        foreach (var lhs in aStStmt.Lhss) {
            var ide = (IdentifierExpr)lhs.Resolved;
            varLhss.Add(ide.Var);
        } 
        aStStmt.Bounds = ModuleResolver.DiscoverBestBounds_MultipleVars(varLhss, aStStmt.Expr, true);
    }
    
    /// -----
    /// Other
    /// -----
    public static void PrintTestType(Method method, bool isPassing) {
        if (method.Body == null) return;
        
        var testType = CreateStringLiteral(method.StartToken, 
            isPassing ? "Running passing tests\\n" : "Running failing tests\\n");
        var printStmt = new PrintStmt(method.StartToken, [testType]);
        method.Body.Body.Insert(0, printStmt);
    }

    public static void PrintTestCases(Method method) {
        if (method.Body == null) return;
        PrintTestCases(method.Body);
    }

    private static void PrintTestCases(BlockStmt bStmt) {
        var testCaseStr = CreateStringLiteral(bStmt.StartToken, "Running test case\\n");
        var printStmt = new PrintStmt(bStmt.StartToken, [testCaseStr]);

        foreach (var stmt in bStmt.Body.ToList()) {
            if (stmt is BlockStmt innerBStmt) {
                PrintTestCases(innerBStmt);
                continue;
            }
            
            var isVarDecl = stmt is VarDeclStmt { Assign: AssignStatement { Rhss: 
                                [ExprRhs { Expr: ApplySuffix appSufExpr1 } ] } } &&
                            (appSufExpr1.Lhs is NameSegment nSegExpr1 && nSegExpr1.Name == AutoFix.FaultyMethod?.Name || 
                             appSufExpr1.Lhs is ExprDotName exprDotName1 && exprDotName1.SuffixName == AutoFix.FaultyMethod?.Name);
            var isAssign = stmt is AssignStatement { Rhss: 
                               [ExprRhs { Expr: ApplySuffix appSufExpr2 } ] } &&
                           (appSufExpr2.Lhs is NameSegment nSegExpr2 && nSegExpr2.Name == AutoFix.FaultyMethod?.Name || 
                            appSufExpr2.Lhs is ExprDotName exprDotName2 && exprDotName2.SuffixName == AutoFix.FaultyMethod?.Name);
            
            if (isVarDecl || isAssign) {
                var idx = bStmt.Body.IndexOf(stmt);
                bStmt.Body.Insert(idx, printStmt);
            }
        }
    }

    public static void LimitLoopIterations(OneBodyLoopStmt loopStmt, BlockStmt enclosingBlock, int outerLoopCount) {
        var stmtPositionInCurrentBlock = enclosingBlock.Body.IndexOf(loopStmt);
        if (stmtPositionInCurrentBlock == -1) return;
        
        var token = enclosingBlock.Body[stmtPositionInCurrentBlock].StartToken;
        var varName = $"iter_counter{outerLoopCount}'";
        var iterationCounterInit = InitializeIterCounter(token, varName, outerLoopCount);
        enclosingBlock.Body.Insert(stmtPositionInCurrentBlock, iterationCounterInit);
        var iterationCounter = new NameSegment(token, varName, null);
        ResolveNameSegment(iterationCounter, Identifier.ToIdentifier(IterCounterVars[varName]));

        token = loopStmt.StartToken;
        var maxIterationsReached = CreateAtLeast(iterationCounter, Expression.CreateIntLiteral(token, 50));
        var breakStmt = new BreakOrContinueStmt(token, outerLoopCount + 1, false);
        breakStmt.TargetStmt = loopStmt;
        var ifMaxIterBreakStmt = new IfStmt(
            token, false, maxIterationsReached, 
            new BlockStmt(token, [breakStmt]), null);
        
        var incrementIterationCounterExpr = new ExprRhs(
            Expression.CreateAdd(iterationCounter, Expression.CreateIntLiteral(token, 1)));
        var incrementIterationCounter = new AssignStatement(token, [iterationCounter], [incrementIterationCounterExpr], false);
        ResolveNormalAssignStatement(incrementIterationCounter);
        
        loopStmt.Labels.Add(new Label(new SourceOrigin(loopStmt.StartToken, loopStmt.EndToken), null));
        loopStmt.Body?.Body.InsertRange(0, [ifMaxIterBreakStmt, incrementIterationCounter]);
    }

    private static Statement InitializeIterCounter(Token token, string varName, int outerLoopCount) {
        if (!IterCounterVars.ContainsKey(varName)) {
            var iterationCounterVar = Statement.CreateLocalVariable(token, 
                $"iter_counter{outerLoopCount}'", 
                Expression.CreateIntLiteral(token, 0));
            IterCounterVars.Add(varName, iterationCounterVar.Locals[0]);
            return iterationCounterVar;
        }
        
        var iterationCounter = new NameSegment(token, $"iter_counter{outerLoopCount}'", null);
        ResolveNameSegment(iterationCounter, Identifier.ToIdentifier(IterCounterVars[varName]));
        var incrementIterationCounterExpr = new ExprRhs(Expression.CreateIntLiteral(token, 0));
        var incrementIterationCounter = new AssignStatement(token, [iterationCounter], [incrementIterationCounterExpr], false);
        ResolveNormalAssignStatement(incrementIterationCounter);
        return incrementIterationCounter;
    }
}

public class Identifier(IVariable? var, MemberDecl? field, Type type, int? availabilityStartPos, int? availabilityEndPos)
{
    public IVariable? Var { get; private set; } = var;
    public MemberDecl? Field { get; private set; } = field;
    public TopLevelDeclWithMembers? EnclosingClass => Field is { EnclosingClass: TopLevelDeclWithMembers encClass } ? encClass : null;
    public string Name => Var != null ? Var.DisplayName : Field != null ? Field.Name : "";
    public Type Type { get; private set; } = type;
    public IOrigin Origin => Var?.Origin ?? Field?.Origin ?? new AutoGeneratedOrigin(null);
    public int? AvailabilityStartPos { get; private set; } = availabilityStartPos;
    public int? AvailabilityEndPos { get; private set; } = availabilityEndPos;

    public static Identifier ToIdentifier(IVariable v) {
        return new Identifier(v, null, v.Type, null, null);
    }
}