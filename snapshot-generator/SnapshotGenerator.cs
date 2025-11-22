using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace SnapshotGenerator;

public class SnapshotGenerator : PluginConfiguration
{
    public static int ViolationLine { get; private set; }
    public static int ViolationColumn { get; private set; }
    public static Method? FaultyMethod { get; set; }
    private bool _valid;
    public override void ParseArguments(string[] args) {
        if (args.Length < 2) return;
        _valid = true;
        ViolationLine = int.Parse(args[0]);
        ViolationColumn = int.Parse(args[1]);
    }
    
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return _valid ? [new Scanner(reporter)] : [];
   }
}
