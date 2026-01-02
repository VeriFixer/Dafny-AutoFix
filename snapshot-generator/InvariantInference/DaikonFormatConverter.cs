using System.Text;
using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator.InvariantInference;

public static class DaikonFormatConverter
{
    private static int _outerLoopCount;
    private static bool _shouldEscapeQuotes;
    
    /// -------------------------
    /// Values
    /// -------------------------
    public static Statement? ToDaikonValue(IOrigin token, Formal f, Expression? expr = null, Expression? delim = null) {
        return (expr != null ? expr.Type.ToString() : f.Type.ToString()) switch {
            "int" or "nat" or "real" => CreatePrintStmt(token, ToNumericalValue(f, expr), delim),
            "bool" => CreatePrintStmt(token, ToBooleanValue(token, f, expr), delim),
            "char" => CreatePrintStmt(token, ToCharValue(token, f, expr), delim),
            "string" => CreatePrintStmt(token, ToStringValue(token, f, expr), delim),
            var t when t.StartsWith("seq<") => ToSequenceValue(token, f, expr),
            var t when t.StartsWith("array<") => ToArrayValue(token, f, expr),
            var t when t.StartsWith("array") => ToMultiDimArrayValue(token, f, expr),
            var t when t.StartsWith("set<") => ToSetValue(token, f, expr),
            var t when t.StartsWith("multiset<") => ToSetValue(token, f, expr),
            var t when t.StartsWith("map<") => ToMapValue(token, f, expr),
            _ => null
        };
    }

    private static List<Expression> ToNumericalValue(Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        return [expr ?? varValue];
    }

    private static List<Expression> ToBooleanValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        var trueValue = Expression.CreateIntLiteral(token, 1);
        var falseValue = Expression.CreateIntLiteral(token, 0);
        var iteExpr = Expression.CreateITE(expr ?? varValue, trueValue, falseValue);
        return [iteExpr];
    }

    private static List<Expression> ToCharValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        var seqDisplay = new SeqDisplayExpr(f.Origin, [expr ?? varValue]) {
            Type = Type.ResolvedString()
        }; // string is seq<char>
        var quoteElement = AstUtils.CreateStringLiteral(token, $"{(_shouldEscapeQuotes ? "\\\\" : "")}\\\"");
        return [quoteElement, seqDisplay, quoteElement];
    }

    private static List<Expression> ToStringValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        var quoteElement = AstUtils.CreateStringLiteral(token, $"{(_shouldEscapeQuotes ? "\\\\" : "")}\\\"");
        return [quoteElement, expr ?? varValue, quoteElement];
    }

    private static BlockStmt ToSequenceValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(token, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        
        var seqLengthExpr = new UnaryOpExpr(token, UnaryOpExpr.Opcode.Cardinality, expr ?? varValue) {
            Type = Type.Int
        };
        return ToListValue(token, f, expr ?? varValue, seqLengthExpr);
    }

    private static BlockStmt ToArrayValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);

        var lengthMethod = new Name(token, "Length");
        var arrLengthExpr = new ExprDotName(token, varValue, lengthMethod, null) {
            ResolvedExpression = AstUtils.CreateMemberSelectExpr(
                token, AstUtils.CreateLengthSpecialField(token), null, varValue
            )
        };
        return ToListValue(token, f, expr ?? varValue, arrLengthExpr);
    }

    // applies to both Dafny sequences and arrays
    private static BlockStmt ToListValue(IOrigin token, Formal f, Expression listExpr, Expression listLengthExpr) {
        var loopBoundVar = new BoundVar(token, $"{Convert.ToChar(105 + _outerLoopCount)}", Type.Int);
        var loopBound = new NameSegment(token, $"{Convert.ToChar(105 + _outerLoopCount)}", null);
        AstUtils.ResolveNameSegment(loopBound, Type.Int, loopBoundVar, null);
        
        var listIndexSelector = new SeqSelectExpr(token, true, listExpr, loopBound, null);
        if (listExpr.Type is SeqType seqType) {
            listIndexSelector.Type = seqType.Arg;
        } else if (listExpr.Type is UserDefinedType uType) {
            listIndexSelector.Type = uType.TypeArgs[0];
        }
        var delimElement = AstUtils.CreateStringLiteral(token, " ");
        _outerLoopCount++;
        var listIndexPrinter = ToDaikonValue(token, f, listIndexSelector, delimElement);
        _outerLoopCount--;
        var loop = new ForLoopStmt(token, loopBoundVar,
            Expression.CreateIntLiteral(token, 0), listLengthExpr,
            true, [], new Specification<Expression>(),
            new Specification<FrameExpression>(),
            new BlockStmt(token, listIndexPrinter != null ? [listIndexPrinter] : []), null);
        
        var openArrayElem = AstUtils.CreateStringLiteral(token, "[ ");
        var printOpenArray = new PrintStmt(token, [openArrayElem]);
        var closeArrayElem = AstUtils.CreateStringLiteral(token, $"]{(_outerLoopCount == 0 ? "\\n" : " ")}");
        var printCloseArray = new PrintStmt(token, [closeArrayElem]);
        return new BlockStmt(token, [printOpenArray, loop, printCloseArray]);
    }
    
    private static BlockStmt ToMultiDimArrayValue(IOrigin token, Formal f, Expression? expr = null) {
        var arrayDims = expr != null ? expr.Type.AsArrayType.Dims : f.Type.AsArrayType.Dims;
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);

        List<BoundVar> boundVars = [];
        List<Expression> bounds = [];
        List<ExprDotName> arrLengthExprs = [];
        for (var i = 0; i < arrayDims; i++) {
            var loopBoundVar = new BoundVar(token, $"{Convert.ToChar(105 + i + _outerLoopCount)}", Type.Int);
            boundVars.Add(loopBoundVar);
            var loopBound = new NameSegment(token, $"{Convert.ToChar(105 + i + _outerLoopCount)}", null);
            AstUtils.ResolveNameSegment(loopBound, Type.Int, loopBoundVar, null);
            bounds.Add(loopBound);
            var lengthMethod = new Name(token, "Length");
            var arrLengthExpr = new ExprDotName(token, expr ?? varValue, lengthMethod, null) {
                ResolvedExpression = AstUtils.CreateMemberSelectExpr(
                    token, AstUtils.CreateLengthSpecialField(token, i), null, expr ??varValue
                )
            };
            arrLengthExprs.Add(arrLengthExpr);
        }
        
        var listIndexSelector = new MultiSelectExpr(token, expr ?? varValue, bounds) {
            Type = expr != null ? ((UserDefinedType)expr.Type).TypeArgs[0] : ((UserDefinedType)f.Type).TypeArgs[0]
        };
        var delimElement = AstUtils.CreateStringLiteral(token, " ");
        _outerLoopCount += arrayDims;
        var listIndexPrinter = ToDaikonValue(token, f, listIndexSelector, delimElement);
        _outerLoopCount -= arrayDims;
        var openArrayElem = AstUtils.CreateStringLiteral(token, "[ ");
        var printOpenArray = new PrintStmt(token, [openArrayElem]);
        var closeArrayElem = AstUtils.CreateStringLiteral(token, $"] ");
        var printCloseArray = new PrintStmt(token, [closeArrayElem]);
        List<Statement> loopBody = listIndexPrinter != null ? [listIndexPrinter] : [];
        for (var i = arrayDims - 1; i >= 0; i--) {
            var loop = new ForLoopStmt(token, boundVars[i],
                Expression.CreateIntLiteral(token, 0), arrLengthExprs[i],
                true, [], new Specification<Expression>(),
                new Specification<FrameExpression>(),
                new BlockStmt(token, loopBody), null);
            loopBody = [printOpenArray, loop, printCloseArray];
        }
        
        delimElement = AstUtils.CreateStringLiteral(token, $"{(_outerLoopCount == 0 ? "\\n" : "")}");
        var printDelim = new PrintStmt(token, [delimElement]);
        loopBody.Add(printDelim);
        return new BlockStmt(token, loopBody);
    }

    private static BlockStmt ToSetValue(IOrigin token, Formal f, Expression? expr = null, Type? mapRange = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);
        var setType = (expr ?? varValue).Type;
        var setArgType = setType switch {
            SetType type => type.Arg,
            MultiSetType mType => mType.Arg,
            _ => ((MapType)setType).Domain
        };
        var setClone = Statement.CreateLocalVariable(token, $"{f.Name}{_outerLoopCount}'", expr ?? varValue);
        var cloneVarValue = new NameSegment(f.Origin, $"{f.Name}{_outerLoopCount}'", null);
        AstUtils.ResolveNameSegment(cloneVarValue, setType, setClone.Locals[0], null);

        var emptySet = CreateSetDisplay(token, setType, []);
        var loopGuard = AstUtils.CreateNeq(cloneVarValue, emptySet, setType);
        // var e :| e in my_set';
        var setElemSelector = Statement.CreateLocalVariable(token, $"{f.Name}{_outerLoopCount}_elem", setArgType);
        var setElemVarValue = new NameSegment(token, $"{f.Name}{_outerLoopCount}_elem", null);
        AstUtils.ResolveNameSegment(setElemVarValue, setArgType, setElemSelector.Locals[0], null);
        var setElemSelectorExpr = AstUtils.CreateIn(setElemVarValue, cloneVarValue, setType);
        setElemSelector.Assign = new AssignSuchThatStmt(token, [setElemVarValue], setElemSelectorExpr, null, null);
        AstUtils.ResolveAssignSuchThatStatement((AssignSuchThatStmt)setElemSelector.Assign);
        // print e, " ";
        var delimElement = AstUtils.CreateStringLiteral(token, " ");
        _outerLoopCount++;
        var setElemPrinter = ToDaikonValue(token, f, setElemVarValue, delimElement);
        if (mapRange != null && expr != null)
            setElemPrinter = ToMapElementValue(token, f, ((ExprDotName)expr).Lhs, setElemVarValue, mapRange, setElemPrinter);
        _outerLoopCount--;
        // sett' := sett' - { e };
        var elemSubset = CreateSetDisplay(token, setType, [setElemVarValue]);
        var setElemRemoverExpr = new ExprRhs(Expression.CreateSetDifference(cloneVarValue, elemSubset));
        var setElemRemover = new AssignStatement(token, [cloneVarValue], [setElemRemoverExpr], false);
        AstUtils.ResolveNormalAssignStatement(setElemRemover);

        List<Statement> loopBody = setElemPrinter != null ? 
            [setElemSelector, setElemPrinter, setElemRemover] : 
            [setElemSelector, setElemRemover];
        var loop = new WhileStmt(token, loopGuard, [], 
            new Specification<Expression>(),
            new Specification<FrameExpression>(), 
            new BlockStmt(token, loopBody)
        );
        
        var openArrayElem = AstUtils.CreateStringLiteral(token, "[ ");
        var printOpenArray = new PrintStmt(token, [openArrayElem]);
        var closeArrayElem = AstUtils.CreateStringLiteral(
            token, $"]{(_outerLoopCount == 0 ? (mapRange == null ? "\\n" : "") : " ")}");
        var printCloseArray = new PrintStmt(token, [closeArrayElem]);
        return new BlockStmt(token, [setClone, printOpenArray, loop, printCloseArray]);
    }

    private static BlockStmt ToMapValue(IOrigin token, Formal f, Expression? expr = null) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null);

        var mapDomainType = ((MapType)(expr ?? varValue).Type).Arg;
        var keysMethod = new Name(token, "Keys");
        var mapKeysExpr = new ExprDotName(token, expr ?? varValue, keysMethod, null) {
            Type = new SetType(true, mapDomainType),
            ResolvedExpression = AstUtils.CreateMemberSelectExpr(
                token, AstUtils.CreateKeysSpecialField(token, mapDomainType), null, varValue
            )
        };
        if (mapKeysExpr.ResolvedExpression != null)
            mapKeysExpr.ResolvedExpression.Type = new SetType(true, mapDomainType);
        var prevShouldEscapeQuotes = _shouldEscapeQuotes;
        _shouldEscapeQuotes = true;
        var mapPrinter = ToSetValue(token, f, mapKeysExpr, (expr ?? varValue).Type.AsMapType.Range);
        _shouldEscapeQuotes = prevShouldEscapeQuotes;
        
        var stringDelimElem = AstUtils.CreateStringLiteral(token, "\\\"");
        var printStringDelim = new PrintStmt(token, [stringDelimElem]);
        mapPrinter.Body.Insert(0, printStringDelim);
        stringDelimElem = AstUtils.CreateStringLiteral(token, $"{(_outerLoopCount == 0 ? "\\\"\\n" : "\\\"")}");
        printStringDelim = new PrintStmt(token, [stringDelimElem]);
        mapPrinter.Body.Add(printStringDelim);
        return mapPrinter;
    }

    private static BlockStmt ToMapElementValue(IOrigin token, Formal f, Expression mapVarValue, Expression mapKeyVarValue, Type mapKeysType, Statement? mapKeyPrinter) {
        var openArrayElem = AstUtils.CreateStringLiteral(token, "[ ");
        var printOpenArray = new PrintStmt(token, [openArrayElem]);
        var closeArrayElem = AstUtils.CreateStringLiteral(token, "] ");
        var printCloseArray = new PrintStmt(token, [closeArrayElem]);
        var delimElement = AstUtils.CreateStringLiteral(token, " ");

        var mapValueSelector = new SeqSelectExpr(token, true, mapVarValue, mapKeyVarValue, null) {
            Type = mapKeysType
        };
        var mapValuePrinter = ToDaikonValue(token, f, mapValueSelector, delimElement);

        List<Statement> blockBody = [printOpenArray];
        if (mapKeyPrinter != null) blockBody.Add(mapKeyPrinter);
        if (mapValuePrinter != null) blockBody.Add(mapValuePrinter);
        blockBody.Add(printCloseArray);
        return new BlockStmt(token, blockBody);
    }

    /// -------------------------
    /// Types
    /// -------------------------
    public static String ToType(Type type) {
        return type.ToString() switch {
            "int" or "nat" => "int",
            "real" => "double",
            "bool" => "boolean",
            "char" or "string" => "java.lang.String",
            var t when t.StartsWith("seq<") ||
               t.StartsWith("set<") || t.StartsWith("multiset<") ||
               t.StartsWith("array") => ToArrayType(type),
            var t when t.StartsWith("map<") => "java.lang.String", // TODO: is there a better way to represent it?
            _ => ""
        };
    }
    
    private static String ToArrayType(Type type) { 
        var arrayType = type switch {
            SeqType seqType => ToType(seqType.Arg),
            SetType setType => ToType(setType.Arg),
            MultiSetType msetType => ToType(msetType.Arg),
            UserDefinedType uType => ToType(uType.TypeArgs[0]) + 
                new StringBuilder(2 * (type.AsArrayType.Dims - 1))
                    .Insert(0, "[]", type.AsArrayType.Dims - 1),
            _ => ""
        };
        if (arrayType == "") return "";
        return arrayType + "[]";
    }
    
    public static String GetComparability(Type type) {
        return type.ToString() switch {
            "bool" => "1",
            "int" or "nat" or "real" => "2",
            "char" or "string" => "3",
            var t when t.StartsWith("seq<") ||
               t.StartsWith("set<") || t.StartsWith("multiset<") ||
               t.StartsWith("array") => GetArrayComparability(type),
            var t when t.StartsWith("map<") => "4",
            _ => ""
        };
    }
    
    private static String GetArrayComparability(Type type) {
        var arrayElementComp = type switch {
            SeqType seqType => GetComparability(seqType.Arg),
            SetType setType => GetComparability(setType.Arg),
            MultiSetType msetType => GetComparability(msetType.Arg),
            UserDefinedType uType => GetComparability(uType.TypeArgs[0]),
            _ => ""
        };
        if (arrayElementComp == "") return "";
        var numDimensions = NumDimensions(type);
        if (numDimensions > 1) {
            var compParts = arrayElementComp.Split("[");
            return (10 * numDimensions) + "[" + compParts[^1][0] + "]";
        }
        return "10[" + arrayElementComp + "]";
    }

    public static bool IsArrayType(Type type) {
        var t = type.ToString();
        return t.StartsWith("seq<") || t.StartsWith("set<") || 
               t.StartsWith("multiset<") || t.StartsWith("array");
    }

    private static int NumDimensions(Type type) {
        var numDimensions = 0;
        numDimensions += type.ToString().Count(x => x == '<');
        if (type.ToString().Contains("array")) {
            var arrayElems = type.ToString().Split("array");
            foreach (var elem in arrayElems){
                if (elem.Length == 0) continue;
                if (int.TryParse($"{elem[0]}", out var dim))
                    numDimensions += dim - 1;
            }
        }
        return numDimensions;
    }

    /// -------------------------
    /// Utils
    /// -------------------------
    private static PrintStmt CreatePrintStmt(IOrigin token, List<Expression> args, Expression? delim = null) {
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        args.Add(delim ?? delimElement);
        return new PrintStmt(token, args);
    }

    private static DisplayExpression CreateSetDisplay(IOrigin token, Type type, List<Expression> elements) {
        if (type is SetType)
            return new SetDisplayExpr(token, true, elements) { Type = type };
        return new MultiSetDisplayExpr(token, elements) { Type = type };
    }
}