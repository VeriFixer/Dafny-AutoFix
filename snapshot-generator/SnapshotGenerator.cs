using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using SnapshotGenerator.Enumeration;
using SnapshotGenerator.InvariantInference;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace SnapshotGenerator;

public class SnapshotGenerator : PluginConfiguration
{
    public static int ViolationLine { get; private set; }
    public static int ViolationColumn { get; private set; }
    private bool _enumeration;
    private bool _invariantInference;
    
    public override void ParseArguments(string[] args) {
        if (args.Length < 3) return;
        ViolationLine = int.Parse(args[0]);
        ViolationColumn = int.Parse(args[1]);
        if (args[2] == "enum") {
            _enumeration = true;
        } else if (args[2] == "inv") { 
            _invariantInference = true;
        }
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _enumeration ? [new Enumerator(reporter)] : 
            (_invariantInference ? [new InvariantInferrer(reporter)] : []);
   }
}

public class Enumerator(ErrorReporter reporter) : Rewriter(reporter)
{
    public static Method? FaultyMethod { get; set; }
    public static readonly List<Expression> ProgramAbstractions = [];

    private readonly ExpressionScanner _scanner = new();

    public override void PostResolve(ModuleDefinition module) {
        if (module.Name != "_module") 
            return;
        
        _scanner.Visit(module);
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

public class InvariantInferrer(ErrorReporter reporter) : Rewriter(reporter)
{
    public static Method? FaultyMethod { get; set; }
    public static Method? MainMethod { get; set; }
    
    public override void PostResolve(ModuleDefinition module) {
        if (module.Name != "_module") 
            return;
        
        IdentifierAvailabilityScanner scanner = new(true);
        scanner.Visit(module);
        var traceIdentifiers = scanner.IdentifierAvailability.Where(
            expr => !scanner.Ghosts.Contains(expr.Item3)
        ).ToList();
        DaikonInstrumenter instrumenter = new(traceIdentifiers);
        instrumenter.Instrument(module);
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
