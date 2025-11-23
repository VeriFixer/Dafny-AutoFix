using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace SnapshotGenerator;

public class SnapshotGeneratorPlugin : PluginConfiguration
{
    private bool _valid;
    private int _violationLine;
    private int _violationColumn;
    
    public override void ParseArguments(string[] args) {
        if (args.Length < 2) return;
        _valid = true;
        _violationLine = int.Parse(args[0]);
        _violationColumn = int.Parse(args[1]);
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _valid ? [new SnapshotGenerator(reporter, _violationLine, _violationColumn)] : [];
   }
}

public class SnapshotGenerator : Rewriter
{
    public static int ViolationLine { get; private set; }
    public static int ViolationColumn { get; private set; }
    public static Method? FaultyMethod { get; set; }
    public static readonly List<Expression> ProgramAbstractions = [];

    private readonly ExpressionScanner _scanner = new();

    public SnapshotGenerator(ErrorReporter reporter, int violationLine, int violationColumn) : base(reporter) {
        ViolationLine = violationLine;
        ViolationColumn = violationColumn;
    }

    public override void PreResolve(ModuleDefinition module) {
        _scanner.Visit(module);
    }

    public override void PostResolve(ModuleDefinition module) {
        // collect additional predicates from faulty method after fully parsing the AST
        _scanner.VisitFaultyMethod();

        if (FaultyMethod == null) return;
        using StreamWriter sw = File.AppendText("abstractions.txt");
        foreach (var expr in ProgramAbstractions) {
            var line = expr.ToString();
            sw.WriteLine(line);
        }
    }
}
