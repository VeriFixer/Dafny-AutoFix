using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using AutoFix.Enumeration;
using AutoFix.InvariantInference;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace AutoFix;

public class AutoFix : PluginConfiguration
{
    public static int ViolationLine { get; private set; }
    public static int? RelatedLocationLine { get; private set; }
    public static bool DebugMode { get; private set; }
    private bool _snapshotGeneration;
    private bool _invariantInference;
    private bool _passingInvariantInference;
    private bool _failingInvariantInference;
    
    public static Method? FaultyMethod { get; set; }
    public static Method? MainMethod { get; set; }
    public static Method? PassingTestsMethod { get; set; }
    public static Method? FailingTestsMethod { get; set; }
    public static ModuleDefinition? FaultyModule { get; set; }
    
    public override void ParseArguments(string[] args) {
        if (args.Length < 2) return;
        if (args[0] == "snap") {
            _snapshotGeneration = true;
        } else if (args[0] == "inv_pass" || args[0] == "inv_fail" || args[0] == "inv_all") { 
            _invariantInference = true;
            if (args[0] == "inv_pass")
                _passingInvariantInference = true;
            if (args[0] == "inv_fail")
                _failingInvariantInference = true;
            if (args[0] == "inv_all") {
                _passingInvariantInference = true;
                _failingInvariantInference = true;
            }
        }
        
        ViolationLine = int.Parse(args[1]);
        if (args.Length > 2 && int.TryParse(args[2], out var arg))
            RelatedLocationLine = arg;
        
        if (args is [_, _, "debug"] or [_, _, _, "debug"])
            DebugMode = true;
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _snapshotGeneration ? [new SnapshotGenerator(reporter)] : 
            _invariantInference ? [new InvariantInferrer(reporter, _passingInvariantInference, _failingInvariantInference)] : [];
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

public class SnapshotGenerator(ErrorReporter reporter) : Rewriter(reporter)
{
    public static readonly List<Expression> AllPredicates = [];
    
    /// -------------------------
    /// Invariants
    /// -------------------------
    private readonly DaikonInvariantParser _invariantParser = new();
    public static bool InvariantsAlreadyParsed = false;
    
    public override void PreResolve(ModuleDefinition module) {
        _invariantParser.Parse(module);
        AllPredicates.AddRange(_invariantParser.AllInvariants);
    }
    
    /// -------------------------
    /// Enumeration
    /// -------------------------
    public static readonly List<Expression> ProgramAbstractions = [];

    public override void PostResolve(ModuleDefinition module) {
        if (module.Name != "_module")  // only visits the default module, and all the other one from there
            return;

        Enumerator enumerator = new();
        enumerator.Visit(module);
        if (AutoFix.FaultyMethod == null) return;
        // enumerate additional predicates from faulty method after fully parsing the AST
        enumerator.VisitFaultyMethod();

        // instrument the program for collecting enumeration predicates values at runtime
        var enumerationTraceBuilder = new EnumerationTraceBuilder(enumerator.IdentifierAvailability, enumerator.Ghosts);
        AllPredicates.AddRange(enumerationTraceBuilder.AllEnumPreds);
        enumerationTraceBuilder.InstrumentFaultyMethod();
        
        _invariantParser.AddEDepToInvariantPrints();
        
        // distinguish passing from failing test execution
        if (AutoFix.PassingTestsMethod != null) {
            AstUtils.PrintTestType(AutoFix.PassingTestsMethod, true);
            AstUtils.PrintTestCases(AutoFix.PassingTestsMethod);
        }
        if (AutoFix.FailingTestsMethod != null) {
            AstUtils.PrintTestType(AutoFix.FailingTestsMethod, false);
            AstUtils.PrintTestCases(AutoFix.FailingTestsMethod);
        }
    }
   
    
    public override void PostResolve(Program program) {
        if (AutoFix.DebugMode)
            AutoFix.SaveInstrumentedProgram(program);
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
        if (AutoFix.DebugMode)
            AutoFix.SaveInstrumentedProgram(program);
    }
}
