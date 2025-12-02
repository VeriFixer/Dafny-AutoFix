using Microsoft.Dafny;
using Type = System.Type;

namespace SnapshotGenerator;

// this is the default implementation of the AST visitor
// other classes that aim to find specific parts of the AST
// can inherit this class and simply override the necessary
// statement/expression handlers
public abstract class Visitor
{
    private readonly Dictionary<Type, Action<Statement>> _statementHandlers;
    private readonly Dictionary<Type, Action<Expression>> _expressionHandlers;
    
    protected Visitor()
    {
        _statementHandlers = new Dictionary<Type, Action<Statement>> {
            {typeof(BlockStmt), stmt => VisitStatement((stmt as BlockStmt)!)},
            {typeof(ConcreteAssignStatement), stmt => VisitStatement((stmt as ConcreteAssignStatement)!)},
            {typeof(AssignStatement), stmt => VisitStatement((stmt as AssignStatement)!)},
            {typeof(AssignSuchThatStmt), stmt => VisitStatement((stmt as AssignSuchThatStmt)!)},
            {typeof(AssignOrReturnStmt), stmt => VisitStatement((stmt as AssignOrReturnStmt)!)},
            {typeof(SingleAssignStmt), stmt => VisitStatement((stmt as SingleAssignStmt)!)},
            {typeof(VarDeclStmt), stmt => VisitStatement((stmt as VarDeclStmt)!)},
            {typeof(VarDeclPattern), stmt => VisitStatement((stmt as VarDeclPattern)!)},
            {typeof(ProduceStmt), stmt => VisitStatement((stmt as ProduceStmt)!)},
            {typeof(IfStmt), stmt => VisitStatement((stmt as IfStmt)!)},
            {typeof(WhileStmt), stmt => VisitStatement((stmt as WhileStmt)!)},
            {typeof(ForLoopStmt), stmt => VisitStatement((stmt as ForLoopStmt)!)},
            {typeof(ForallStmt), stmt => VisitStatement((stmt as ForallStmt)!)},
            {typeof(BreakOrContinueStmt), stmt => VisitStatement((stmt as BreakOrContinueStmt)!)},
            {typeof(AlternativeLoopStmt), stmt => VisitStatement((stmt as AlternativeLoopStmt)!)},
            {typeof(AlternativeStmt), stmt => VisitStatement((stmt as AlternativeStmt)!)},
            {typeof(MatchStmt), stmt => VisitStatement((stmt as MatchStmt)!)},
            {typeof(NestedMatchStmt), stmt => VisitStatement((stmt as NestedMatchStmt)!)},
            {typeof(CallStmt), stmt => VisitStatement((stmt as CallStmt)!)},
            {typeof(ModifyStmt), stmt => VisitStatement((stmt as ModifyStmt)!)},
            {typeof(HideRevealStmt), stmt => VisitStatement((stmt as HideRevealStmt)!)},
            {typeof(BlockByProofStmt), stmt => VisitStatement((stmt as BlockByProofStmt)!)},
            {typeof(SkeletonStatement), stmt => VisitStatement((stmt as SkeletonStatement)!)},
            {typeof(PrintStmt), stmt => VisitStatement((stmt as PrintStmt)!)},
            // spec statements
            {typeof(OpaqueBlock), stmt => VisitStatement((stmt as OpaqueBlock)!)},
            {typeof(PredicateStmt), stmt => VisitStatement((stmt as PredicateStmt)!)},
            {typeof(CalcStmt), stmt => VisitStatement((stmt as CalcStmt)!)},
        };
        _expressionHandlers = new Dictionary<Type, Action<Expression>> {
            {typeof(LiteralExpr), expr => VisitExpression((expr as LiteralExpr)!)},
            {typeof(BinaryExpr), expr => VisitExpression((expr as BinaryExpr)!)},
            {typeof(UnaryExpr), expr => VisitExpression((expr as UnaryExpr)!)},
            {typeof(ParensExpression), expr => VisitExpression((expr as ParensExpression)!)},
            {typeof(NegationExpression), expr => VisitExpression((expr as NegationExpression)!)},
            {typeof(ChainingExpression), expr => VisitExpression((expr as ChainingExpression)!)},
            {typeof(NameSegment), expr => VisitExpression((expr as NameSegment)!)},
            {typeof(IdentifierExpr), expr => VisitExpression((expr as IdentifierExpr)!)},
            {typeof(LetExpr), expr => VisitExpression((expr as LetExpr)!)},
            {typeof(LetOrFailExpr), expr => VisitExpression((expr as LetOrFailExpr)!)},
            {typeof(ApplyExpr), expr => VisitExpression((expr as ApplyExpr)!)},
            {typeof(SuffixExpr), expr => VisitExpression((expr as SuffixExpr)!)},
            {typeof(FunctionCallExpr), expr => VisitExpression((expr as FunctionCallExpr)!)},
            {typeof(MemberSelectExpr), expr => VisitExpression((expr as MemberSelectExpr)!)},
            {typeof(ITEExpr), expr => VisitExpression((expr as ITEExpr)!)},
            {typeof(MatchExpr), expr => VisitExpression((expr as MatchExpr)!)},
            {typeof(NestedMatchExpr), expr => VisitExpression((expr as NestedMatchExpr)!)},
            {typeof(DisplayExpression), expr => VisitExpression((expr as DisplayExpression)!)},
            {typeof(MapDisplayExpr), expr => VisitExpression((expr as MapDisplayExpr)!)},
            {typeof(SeqConstructionExpr), expr => VisitExpression((expr as SeqConstructionExpr)!)},
            {typeof(MultiSetFormingExpr), expr => VisitExpression((expr as MultiSetFormingExpr)!)},
            {typeof(SeqSelectExpr), expr => VisitExpression((expr as SeqSelectExpr)!)},
            {typeof(MultiSelectExpr), expr => VisitExpression((expr as MultiSelectExpr)!)},
            {typeof(SeqUpdateExpr), expr => VisitExpression((expr as SeqUpdateExpr)!)},    
            {typeof(ComprehensionExpr), expr => VisitExpression((expr as ComprehensionExpr)!)},
            {typeof(DatatypeUpdateExpr), expr => VisitExpression((expr as DatatypeUpdateExpr)!)},
            {typeof(DatatypeValue), expr => VisitExpression((expr as DatatypeValue)!)},
            {typeof(StmtExpr), expr => VisitExpression((expr as StmtExpr)!)},
            // spec expressions
            {typeof(OldExpr), expr => VisitExpression((expr as OldExpr)!)},
            {typeof(UnchangedExpr), expr => VisitExpression((expr as UnchangedExpr)!)},
            {typeof(DecreasesToExpr), expr => VisitExpression((expr as DecreasesToExpr)!)},
        };
    }

    /// ---------------------------
    /// Group of top level visitors
    /// ---------------------------
    public void Visit(ModuleDefinition module) {
        HandleDefaultClassDecl(module);
        HandleSourceDecls(module);
    }

    protected virtual void HandleDefaultClassDecl(ModuleDefinition module) {
        if (module.DefaultClass == null) return;
        HandleMemberDecls(module.DefaultClass);
    }

    protected virtual  void HandleSourceDecls(ModuleDefinition module) {
        foreach (var decl in module.SourceDecls) {
            if (decl is TopLevelDeclWithMembers declWithMembers) { // includes class, trait, datatype, etc.
                HandleMemberDecls(declWithMembers);   
            }
            if (decl is IteratorDecl itDecl) {
                HandleBlock(itDecl.Body);
            } else if (decl is NewtypeDecl newTpDecl) {
                HandleExpression(newTpDecl.Constraint);
            } else if (decl is SubsetTypeDecl subTpDecl) {
                HandleExpression(subTpDecl.Constraint);
                if (subTpDecl is NonNullTypeDecl nNullTpDecl) {
                    HandleMemberDecls(nNullTpDecl.Class);
                }
            }
        }
    }

    protected virtual void HandleMemberDecls(TopLevelDeclWithMembers decl) {
        foreach (var member in decl.Members) {
            if (member is Method m) { // includes constructor
                HandleMethod(m);  
            } else if (member is Function func) { // includes predicate
                HandleFunction(func);
            } else if (member is ConstantField cf) {
                if (cf.Rhs == null) continue;
                HandleExpression(cf.Rhs);
            }
        }
    }

    protected virtual void HandleMethod(Method method) {
        if (method.Body == null) return;
        HandleBlock(method.Body);
        
        VisitReqEns(method.Req);
        VisitReqEns(method.Ens);
        VisitDecreases(method.Decreases);
        VisitReadsModifies(method.Reads);
        VisitReadsModifies(method.Mod);
    }

    protected virtual void HandleFunction(Function function) {
        if (function.Body == null) return;
        HandleExpression(function.Body);
    }
    
    /// ---------------------------
    /// Group of statement visitors
    /// ---------------------------
    protected virtual void HandleStatement(Statement stmt) {
        var derivedType = stmt.GetType();
        while (derivedType != typeof(object) && derivedType != null) {
            if (_statementHandlers.TryGetValue(derivedType, out var handler)) {
                handler(stmt);
                return;
            }
            derivedType = derivedType.BaseType;
        }
    }
    
    protected virtual void HandleBlock(BlockStmt blockStmt) {
        if (blockStmt is DividedBlockStmt dBlockStmt) {
            HandleBlock(dBlockStmt.BodyInit);
            HandleBlock(dBlockStmt.BodyProper);
        } else {
            HandleBlock(blockStmt.Body);
        }
    }
    
    protected virtual void HandleBlock(List<Statement> statements) {
        foreach (var stmt in statements) {
            HandleStatement(stmt);
        }
    }
    
    protected virtual void VisitStatement(BlockStmt blockStmt) {
        HandleBlock(blockStmt);
        
        if (blockStmt is DividedBlockStmt dBlockStmt) {
            HandleBlock(dBlockStmt.BodyInit);
            HandleBlock(dBlockStmt.BodyProper);
        }
    }

    protected virtual void VisitStatement(ConcreteAssignStatement cAStmt) {
        HandleExprList(cAStmt.Lhss);
    }

    protected virtual void VisitStatement(AssignStatement aStmt) {
        VisitStatement(aStmt as ConcreteAssignStatement);
        HandleRhsList(aStmt.Rhss); 
        if (aStmt.OriginalInitialLhs == null) return;
        HandleExpression(aStmt.OriginalInitialLhs);
    }
    
    protected virtual void VisitStatement(AssignSuchThatStmt aStStmt) {
        VisitStatement(aStStmt as ConcreteAssignStatement);
        HandleExpression(aStStmt.Expr);
    }
    
    protected virtual void VisitStatement(AssignOrReturnStmt aOrRStmt) {
        VisitStatement(aOrRStmt as ConcreteAssignStatement);
        HandleExpression(aOrRStmt.Rhs.Expr);
        HandleRhsList(aOrRStmt.Rhss);
    }

    protected virtual void VisitStatement(SingleAssignStmt sAStmt) {
        HandleExpression(sAStmt.Lhs);
        HandleRhsList([sAStmt.Rhs]);
    }

    protected virtual void VisitStatement(VarDeclStmt vDeclStmt) {
        if (vDeclStmt.Assign == null) return;
        HandleStatement(vDeclStmt.Assign);
    }

    protected virtual void VisitStatement(VarDeclPattern vDeclPStmt) {
        HandleExpression(vDeclPStmt.RHS);
    }

    // includes ReturnStmt and YieldStmt
    protected virtual void VisitStatement(ProduceStmt pStmt) {
        if (pStmt.Rhss != null) 
            HandleRhsList(pStmt.Rhss);
    }
    
    protected virtual void VisitStatement(IfStmt ifStmt) {
        if (ifStmt.Guard != null) {
            HandleExpression(ifStmt.Guard);
        }
        HandleBlock(ifStmt.Thn);
        if (ifStmt.Els == null) return;
        if (ifStmt.Els is BlockStmt bEls) {
            HandleBlock(bEls);
        } else {
            HandleStatement(ifStmt.Els);
        }
    }

    protected virtual void VisitStatement(LoopStmt loopStmt) {
        VisitReqEns(loopStmt.Invariants);
        VisitDecreases(loopStmt.Decreases);
        VisitReadsModifies(loopStmt.Mod);
    }
    
    protected virtual void VisitStatement(WhileStmt whileStmt) {
        HandleExpression(whileStmt.Guard);
        if (whileStmt.Body != null) 
            HandleBlock(whileStmt.Body);
        VisitStatement(whileStmt as LoopStmt);
    }

    protected virtual void VisitStatement(ForLoopStmt forStmt) {
        HandleExpression(forStmt.Start);
        HandleExpression(forStmt.End);
        HandleBlock(forStmt.Body);
        VisitStatement(forStmt as LoopStmt);
    }

    protected virtual void VisitStatement(ForallStmt forStmt) {
        HandleExpression(forStmt.Range);
        HandleStatement(forStmt.Body);
        VisitReqEns(forStmt.Ens);
    }
    
    protected virtual void VisitStatement(BreakOrContinueStmt bcStmt) { }

    protected virtual void VisitStatement(AlternativeLoopStmt altLStmt) {
        HandleGuardedAlternatives(altLStmt.Alternatives);
        VisitStatement(altLStmt as LoopStmt);
    }

    protected virtual void VisitStatement(AlternativeStmt altStmt) {
        HandleGuardedAlternatives(altStmt.Alternatives);
    }
    
    protected virtual void VisitStatement(MatchStmt matchStmt) {
        HandleExpression(matchStmt.Source);
        foreach (var cs in matchStmt.Cases) {
            HandleBlock(cs.Body);
        }
    }

    protected virtual void VisitStatement(NestedMatchStmt nMatchStmt) {
        HandleExpression(nMatchStmt.Source);
        foreach (var cs in nMatchStmt.Cases) {
            HandleBlock(cs.Body);
        }
    }

    protected virtual void VisitStatement(CallStmt callStmt) {
        HandleExprList(callStmt.Lhs);
        HandleExpression(callStmt.OriginalInitialLhs);
        HandleExpression(callStmt.MethodSelect);
        HandleExpression(callStmt.Receiver);
        HandleMethod(callStmt.Method);
        HandleActualBindings(callStmt.Bindings);
        HandleExprList(callStmt.Args);
    }

    protected virtual void VisitStatement(ModifyStmt mdStmt) {
        if (mdStmt.Body == null) return;
        HandleStatement(mdStmt.Body);
        VisitReadsModifies(mdStmt.Mod);
    }

    protected virtual void VisitStatement(HideRevealStmt hRStmt) {
        HandleExprList(hRStmt.Exprs);
    }

    protected virtual void VisitStatement(BlockByProofStmt bBpStmt) {
        HandleStatement(bBpStmt.Body);
        HandleStatement(bBpStmt.Proof);
    }

    protected virtual void VisitStatement(SkeletonStatement skStmt) {
        if (skStmt.S == null) return;
        HandleStatement(skStmt.S);
    }

    protected virtual void VisitStatement(PrintStmt prtStmt) { }
    
    // statements used specifically in specs
    // by default we don't visit these since we are not mutating them
    protected virtual void VisitStatement(OpaqueBlock opqBlock) {
        VisitReqEns(opqBlock.Ensures);
        VisitReadsModifies(opqBlock.Modifies);
    }
    
    // includes AssertStmt, AssumeStmt, ExpectStmt
    protected virtual void VisitStatement(PredicateStmt predStmt) {
        HandleExpression(predStmt.Expr);
    }

    protected virtual void VisitStatement(CalcStmt calcStmt) {
        HandleExprList(calcStmt.Lines);
        foreach (var stmt in calcStmt.Hints) {
            HandleStatement(stmt);
        }
    }
    
    /// ----------------------------
    /// Group of expression visitors
    /// ----------------------------
    protected virtual void HandleExpression(Expression expr) {
        var derivedType = expr.GetType();
        while (derivedType != typeof(object) && derivedType != null) {
            if (_expressionHandlers.TryGetValue(derivedType, out var handler)) {
                handler(expr);
                return;
            }
            derivedType = derivedType.BaseType;
        }
    }
    
    // no sub-expressions to further visit
    protected virtual void VisitExpression(LiteralExpr litExpr) { }

    protected virtual void VisitExpression(BinaryExpr bExpr) {
        List<Expression> exprs = [bExpr.E0, bExpr.E1];
        HandleExprList(exprs);
    }
    
    protected virtual void VisitExpression(UnaryExpr uExpr) {
        HandleExpression(uExpr.E);
    }
    
    protected virtual void VisitExpression(ParensExpression pExpr) {
        HandleExpression(pExpr.E);
    }
    
    protected virtual void VisitExpression(NegationExpression nExpr) {
        HandleExpression(nExpr.E);
    }

    protected virtual void VisitExpression(ChainingExpression cExpr) {
        if (cExpr.E is BinaryExpr bExpr && bExpr.Op == BinaryExpr.Opcode.And) {
            List<Expression> exprs = [bExpr.E0, bExpr.E1];
            HandleExprList(exprs);
        }
    }
    
    // no sub-expressions to further visit
    protected virtual void VisitExpression(NameSegment nSegExpr) { }

    protected virtual void VisitExpression(IdentifierExpr idExpr) { }
    
    protected virtual void VisitExpression(LetExpr ltExpr) {
        var exprs = Enumerable.Concat([ltExpr.Body], ltExpr.RHSs).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(LetOrFailExpr ltOrFExpr) {
        HandleExprList([ltOrFExpr.Rhs, ltOrFExpr.Body]);
    }

    protected virtual void VisitExpression(ApplyExpr appExpr) {
        var exprs = Enumerable.Concat([appExpr.Function], appExpr.Args).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(SuffixExpr suffixExpr) {
        HandleExpression(suffixExpr.Lhs);
        if (suffixExpr is ApplySuffix appSufExpr) {
            HandleActualBindings(appSufExpr.Bindings);
        }
    }

    protected virtual void VisitExpression(FunctionCallExpr fCallExpr) {
        HandleExpression(fCallExpr.Receiver);
        HandleActualBindings(fCallExpr.Bindings);
    }
    
    protected virtual void VisitExpression(MemberSelectExpr mSelExpr) {
        HandleExpression(mSelExpr.Obj);
    }

    protected virtual void VisitExpression(ITEExpr iteExpr) {
        List<Expression> exprs = [iteExpr.Test, iteExpr.Thn, iteExpr.Els];
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(MatchExpr mExpr) {
        var cases = mExpr.Cases.Select(e => e.Body);
        var exprs = Enumerable.Concat([mExpr.Source], cases).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(NestedMatchExpr nMExpr) {
        var cases = nMExpr.Cases.Select(e => e.Body);
        var exprs = Enumerable.Concat([nMExpr.Source], cases).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(DisplayExpression dExpr) {
        HandleExprList(dExpr.Elements);
    }

    protected virtual void VisitExpression(MapDisplayExpr mDExpr) {
        var keyElements = mDExpr.Elements.Select(e => e.A).ToList();
        var valueElements = mDExpr.Elements.Select(e => e.B).ToList();
        var exprs = Enumerable.Concat(keyElements, valueElements).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(SeqConstructionExpr seqCExpr) {
        List<Expression> exprs = [seqCExpr.N, seqCExpr.Initializer];
        HandleExprList(exprs);
    }
    
    protected virtual void VisitExpression(MultiSetFormingExpr mSetFExpr) {
        HandleExpression(mSetFExpr.E);
    }

    protected virtual void VisitExpression(SeqSelectExpr seqSExpr) {
        List<Expression> exprs = [seqSExpr.Seq];
        if (seqSExpr.E0 != null) exprs.Add(seqSExpr.E0);
        if (seqSExpr.E1 != null) exprs.Add(seqSExpr.E1);
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(MultiSelectExpr mSExpr) {
        var exprs = Enumerable.Concat([mSExpr.Array], mSExpr.Indices).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(SeqUpdateExpr seqUExpr) {
        List<Expression> exprs = [seqUExpr.Seq, seqUExpr.Index, seqUExpr.Value];
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(ComprehensionExpr compExpr) {
        List<Expression> exprs = [compExpr.Term];
        if (compExpr.Range != null) exprs.Add(compExpr.Range);
        HandleExprList(exprs);
        if (compExpr is MapComprehension mCompExpr && mCompExpr.TermLeft != null) {
            HandleExpression(mCompExpr.TermLeft);
        }
        if (compExpr is LambdaExpr lExpr) {
            VisitReadsModifies(lExpr.Reads);
        }
    }

    protected virtual void VisitExpression(DatatypeUpdateExpr dtUExpr) {
        var updates = dtUExpr.Updates.Select(e => e.Item3);
        var exprs = Enumerable.Concat([dtUExpr.Root], updates).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(DatatypeValue dtValue) {
        HandleActualBindings(dtValue.Bindings);
    }

    protected virtual void VisitExpression(StmtExpr stmtExpr) {
        HandleStatement(stmtExpr.S);
        HandleExpression(stmtExpr.E); 
    }

    protected virtual void VisitExpression(OldExpr oldExpr) {
        HandleExpression(oldExpr.E);
    }
    
    protected virtual void VisitExpression(UnchangedExpr unchExpr) {
        var exprs = unchExpr.Frame.Select(e => e.E).ToList();
        HandleExprList(exprs);
    }

    protected virtual void VisitExpression(DecreasesToExpr dToExpr) {
        HandleExprList(dToExpr.OldExpressions.ToList());
        HandleExprList(dToExpr.NewExpressions.ToList());
    }
    
    /// --------------------------
    /// Group of contract visitors
    /// --------------------------
    protected virtual void VisitReqEns(List<AttributedExpression> attExprs) {
        var exprs = attExprs.Select(e => e.E).ToList();
        HandleExprList(exprs);
    }
    
    protected virtual void VisitDecreases(Specification<Expression> expr) {
        if (expr.Expressions == null) return;
        HandleExprList(expr.Expressions);
    }

    protected virtual void VisitReadsModifies(Specification<FrameExpression> expr) {
        if (expr.Expressions == null) return;
        var exprs = expr.Expressions.Select(e => e.E).ToList();
        HandleExprList(exprs);
    }
    
    /// ----------------------
    /// Group of visitor utils
    /// ----------------------
    protected virtual void HandleExprList(List<Expression> exprs) {
        foreach (var expr in exprs) {
            HandleExpression(expr);
        }
    }

    protected virtual void HandleRhsList(List<AssignmentRhs> rhss) {
        foreach (var rhs in rhss) {
            HandleAssignmentRhs(rhs);
        }
    }

    protected virtual void HandleAssignmentRhs(AssignmentRhs aRhs) {
        if (aRhs is ExprRhs exprRhs) {
            HandleExpression(exprRhs.Expr);
        } else if (aRhs is TypeRhs tpRhs) {
            var elInit = tpRhs.ElementInit;
            
            if (tpRhs.ArrayDimensions != null) {
                HandleExprList(tpRhs.ArrayDimensions);
            } if (elInit != null) {
                HandleExpression(elInit);
            } if (tpRhs.InitDisplay != null) {
                HandleExprList(tpRhs.InitDisplay);
            } if (tpRhs.Bindings != null) {
                HandleActualBindings(tpRhs.Bindings);
            }
        }
    }

    protected virtual void HandleGuardedAlternatives(List<GuardedAlternative> alternatives) {
        foreach (var alt in alternatives) {
            HandleExpression(alt.Guard);
            HandleBlock(alt.Body);  
        }
    }

    protected virtual void HandleActualBindings(ActualBindings bindings) {
        foreach (var binding in bindings.ArgumentBindings) {
            HandleExpression(binding.Actual);
        }
    }
}