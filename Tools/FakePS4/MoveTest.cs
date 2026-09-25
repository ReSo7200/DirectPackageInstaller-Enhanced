using System; using System.IO; using System.Linq; using System.Reflection; using System.Threading.Tasks;
using DirectPackageInstaller; using DirectPackageInstaller.Services;

var Root = args[0];
bool ToExtended = args.Length < 2 || args[1] != "system";
bool FailFirst = args.Contains("failfirst");
App.Config.PSIP = "127.0.0.1"; App.Config.PCIP = "127.0.0.1"; App.Config.ExperimentalPayload = true;

void Tree(string Label)
{
    Console.WriteLine("-- " + Label);
    foreach (var F in Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Where(x => !x.EndsWith(".fail")).OrderBy(x => x))
        Console.WriteLine("   " + F.Substring(Root.Length).Replace(Path.DirectorySeparatorChar, '/') + "  " + new FileInfo(F).Length);
}

var T = typeof(ConsoleMove);
async Task<bool> Step(string Name, MoveJob Job)
{
    var M = T.GetMethod(Name, BindingFlags.NonPublic | BindingFlags.Static)!;
    try { await (Task)M.Invoke(null, new object[] { "127.0.0.1", Job })!; Console.WriteLine($"{Name}: ok"); return true; }
    catch (Exception E) { Console.WriteLine($"{Name}: FAILED {E.GetType().Name}: {E.Message}"); return false; }
}

var Job = new MoveJob { TitleId = "SPSX14001", Title = "Test title", Category = "gd", ToExtended = ToExtended };
Tree("before");
if (!await Step("PrepareAsync", Job)) return;
Console.WriteLine("parts: " + string.Join(" | ", Job.Parts.Select(p => $"{p.Kind} {p.HeaderType} {p.Size}")));
if (!await Step("UninstallAsync", Job)) return;

if (FailFirst)
    File.WriteAllText(Path.Combine(Root, ".fail"), "");
if (!await Step("InstallAsync", Job))
{
    Tree("after the refused install (held copies must still be there)");
    Console.WriteLine("job: Uninstalled=" + Job.Uninstalled + ", record on the console: "
        + File.Exists(Path.Combine(Root, "user", "dpi_move", "SPSX14001", "job.json")));
    File.Delete(Path.Combine(Root, ".fail"));
    Console.WriteLine("== Retry");
    await Step("InstallAsync", Job);
}
Tree("after install");
