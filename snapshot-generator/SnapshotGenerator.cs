using Microsoft.Dafny;
using Microsoft.Dafny.Plugins;
using PluginConfiguration = Microsoft.Dafny.LanguageServer.Plugins.PluginConfiguration;

namespace SnapshotGenerator;

public class SnapshotGenerator : PluginConfiguration
{ 
   public override Rewriter[] GetRewriters(ErrorReporter reporter) { 
       return [];
   }
}
