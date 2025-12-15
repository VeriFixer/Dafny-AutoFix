using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using SnapshotGenerator.Enumeration;
using SnapshotGenerator.InvariantInference;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace SnapshotGenerator;

public class SnapshotGenerator : PluginConfiguration
{
    private int _violationLine;
    private int _violationColumn;
    private bool _enumeration;
    private bool _invariantInference;
    
    public override void ParseArguments(string[] args) {
        if (args.Length < 3) return;
        _violationLine = int.Parse(args[0]);
        _violationColumn = int.Parse(args[1]);
        if (args[2] == "enum") {
            _enumeration = true;
        } else if (args[2] == "inv") { 
            _invariantInference = true;
        }
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _enumeration ? [new Enumerator(reporter, _violationLine, _violationColumn)] : 
            (_invariantInference ? [new InvariantInferrer(reporter, _violationLine, _violationColumn)] : []);
   }
}

public class Enumerator : Rewriter
{
    public static int ViolationLine { get; private set; }
    public static int ViolationColumn { get; private set; }
    public static Method? FaultyMethod { get; set; }
    public static readonly List<Expression> ProgramAbstractions = [];

    private readonly ExpressionScanner _scanner = new();

    public Enumerator(ErrorReporter reporter, int violationLine, int violationColumn) : base(reporter) {
        FaultyMethod = null;
        ViolationLine = violationLine;
        ViolationColumn = violationColumn;
    }

    public override void PreResolve(ModuleDefinition module) {
        _scanner.Visit(module);
    }

    public override void PostResolve(ModuleDefinition module) {
        if (FaultyMethod == null) return;
        // collect additional predicates from faulty method after fully parsing the AST
        _scanner.VisitFaultyMethod();

        // instrument the program for collecting predicates values at runtime
        var expressionTraceBuilder = new ExpressionTraceBuilder(_scanner.IdentifierAvailability, _scanner.Ghosts);
        expressionTraceBuilder.InstrumentFaultyMethod();
    }
    
    public override void PostResolve(Program program) {
        // save instrumented program
        var stringWriter = new StringWriter();
        var printer = new Printer(stringWriter, program.Options, PrintModes.Serialization);
        printer.PrintProgram(program, false);
        var programText = stringWriter.ToString();

        var filename = Path.GetFileNameWithoutExtension(program.Name);
        filename += "_instrumented.dfy";
        File.WriteAllText(filename, programText);
    }
}

public class InvariantInferrer : Rewriter
{
    public static int ViolationLine { get; private set; }
    public static int ViolationColumn { get; private set; }
    public static Method? FaultyMethod { get; set; }
    
    private readonly DaikonInstrumenter _instrumenter = new();

    public InvariantInferrer(ErrorReporter reporter, int violationLine, int violationColumn) : base(reporter) {
        FaultyMethod = null;
        ViolationLine = violationLine;
        ViolationColumn = violationColumn;
    }
    
    public override void PreResolve(ModuleDefinition module) {
        _instrumenter.Instrument(module);
    }
}
