using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace SnapshotGenerator.InvariantInference;

public static class DaikonFormatConverter
{
    public static Statement? ToDaikonValue(IOrigin token, Formal f, TopLevelDeclWithMembers enclosingClass) {
        return f.Type.ToString() switch {
            "int" or "nat" or "real" => ToNumericalValue(token, f, enclosingClass),
            "bool" => ToBooleanValue(token, f, enclosingClass),
            "char" => ToCharValue(token, f, enclosingClass),
            "string" => ToStringValue(token, f, enclosingClass),
            _ => null
        };
    }

    private static PrintStmt ToNumericalValue(IOrigin token, Formal f, TopLevelDeclWithMembers enclosingClass) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null, enclosingClass);
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        return new PrintStmt(f.Origin, [varValue, delimElement]);
    }

    private static PrintStmt ToBooleanValue(IOrigin token, Formal f, TopLevelDeclWithMembers enclosingClass) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null, enclosingClass);
        var trueValue = Expression.CreateIntLiteral(token, 1);
        var falseValue = Expression.CreateIntLiteral(token, 0);
        var iteExpr = Expression.CreateITE(varValue, trueValue, falseValue);
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        return new PrintStmt(f.Origin, [iteExpr, delimElement]);
    }

    private static PrintStmt ToCharValue(IOrigin token, Formal f, TopLevelDeclWithMembers enclosingClass) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null, enclosingClass);
        var seqDisplay = new SeqDisplayExpr(f.Origin, [varValue]) {
            Type = Type.ResolvedString()
        }; // string is seq<char>
        var quoteElement = AstUtils.CreateStringLiteral(token, "\\\"");
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        return new PrintStmt(f.Origin, [quoteElement, seqDisplay, quoteElement, delimElement]);
    }

    private static PrintStmt ToStringValue(IOrigin token, Formal f, TopLevelDeclWithMembers enclosingClass) {
        var varValue = new NameSegment(f.Origin, f.Name, null);
        AstUtils.ResolveNameSegment(varValue, f.Type, f, null, enclosingClass);
        var quoteElement = AstUtils.CreateStringLiteral(token, "\\\"");
        var delimElement = AstUtils.CreateStringLiteral(token, "\\n");
        return new PrintStmt(f.Origin, [quoteElement, varValue, quoteElement, delimElement]);
    }
}