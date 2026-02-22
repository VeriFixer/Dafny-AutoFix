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
    public static bool DebugMode { get; private set; }
    private bool _enumeration;
    private bool _invariantInference;
    private bool _invariantParsing;
    private bool _passingInvariantInference;
    private bool _failingInvariantInference;
    
    public static Method? FaultyMethod { get; set; }
    public static Method? MainMethod { get; set; }
    public static Method? PassingTestsMethod { get; set; }
    public static Method? FailingTestsMethod { get; set; }
    
    public override void ParseArguments(string[] args) {
        if (args.Length < 3) return;
        ViolationLine = int.Parse(args[0]);
        ViolationColumn = int.Parse(args[1]);
        if (args[2] == "enum") {
            _enumeration = true;
        } else if (args[2] == "inv_pass" || args[2] == "inv_fail" || args[2] == "inv_all") { 
            _invariantInference = true;
            if (args[2] == "inv_pass")
                _passingInvariantInference = true;
            if (args[2] == "inv_fail")
                _failingInvariantInference = true;
            if (args[2] == "inv_all") {
                _passingInvariantInference = true;
                _failingInvariantInference = true;
            }
        } else if (args[2] == "inv") {
            _invariantParsing = true;
        }
        
        if (args.Length == 4 && args[3] == "debug")
            DebugMode = true;
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _enumeration ? [new Enumerator(reporter)] : 
            (_invariantInference ? [new InvariantInferrer(reporter, _passingInvariantInference, _failingInvariantInference)] : 
            (_invariantParsing ? [new InvariantParser(reporter)] : []));
   }

   public static void SaveInstrumentedProgram(Program program) {
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

public class Enumerator(ErrorReporter reporter) : Rewriter(reporter)
{
    public static readonly List<Expression> ProgramAbstractions = [];

    public override void PostResolve(ModuleDefinition module) {
        if (module.Name != "_module") 
            return;

        ExpressionScanner scanner = new();
        scanner.Visit(module);
        if (SnapshotGenerator.FaultyMethod == null) return;
        // collect additional predicates from faulty method after fully parsing the AST
        scanner.VisitFaultyMethod();

        // instrument the program for collecting predicates values at runtime
        var expressionTraceBuilder = new ExpressionTraceBuilder(scanner.IdentifierAvailability, scanner.Ghosts);
        expressionTraceBuilder.InstrumentFaultyMethod();
    }
    
    public override void PostResolve(Program program) {
        if (SnapshotGenerator.DebugMode)
            SnapshotGenerator.SaveInstrumentedProgram(program);
    }
}

public class InvariantInferrer : Rewriter
{
    public static bool PassingInvariantInference { get; private set; }
    public static bool FailingInvariantInference { get; private set; }
    
    public InvariantInferrer(ErrorReporter reporter, bool passingInvariantInference, bool failingInvariantInference) 
        : base(reporter) 
    {
        PassingInvariantInference = passingInvariantInference;
        FailingInvariantInference = failingInvariantInference;
    }
    
    public override void PostResolve(ModuleDefinition module) {
        if (module.Name != "_module") 
            return;
        
        IdentifierAvailabilityScanner scanner = new(true);
        scanner.Visit(module);
        var traceIdentifiers = scanner.IdentifierAvailability.Where(
            expr => !scanner.Ghosts.Contains(expr.Name)
        ).ToList();
        DaikonInstrumenter instrumenter = new(traceIdentifiers);
        instrumenter.Instrument(module);
    }

    public override void PostResolve(Program program) {
        if (SnapshotGenerator.DebugMode)
            SnapshotGenerator.SaveInstrumentedProgram(program);
    }
}

public class InvariantParser(ErrorReporter reporter) : Rewriter(reporter)
{
    public static bool InvariantsAlreadyParsed = false;
    
    public override void PreResolve(ModuleDefinition module) {
        DaikonInvariantParser invariantParser = new();
        invariantParser.Parse(module);
        
        var cDepAnalyzer = new ControlDependenceAnalyzer();
        using (StreamWriter sw = File.AppendText("debug.txt")) {
            TextWriter syncWriter = TextWriter.Synchronized(sw);
            syncWriter.WriteLine(String.Join("\n", cDepAnalyzer.CDists));
        }
    }
    
    public override void PostResolve(Program program) {
        if (SnapshotGenerator.DebugMode)
            SnapshotGenerator.SaveInstrumentedProgram(program);
    }
}
