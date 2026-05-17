using System.Globalization;
using MavNet.CodeGen;
using MavNet.CodeGen.Emitters;

// A code generator MUST emit byte-identical output regardless of the host
// machine's locale (the CI codegen-drift gate depends on it). Pin the whole
// process to the invariant culture up front so every number/string format in
// the emitters is deterministic — present and future — without threading an
// IFormatProvider through ~30 call sites.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

// CLI: --spec <root.xml> --allowlist <file> --generated-out <dir> --registry-out <dir>
// Defaults assume the repo layout: specs/ at the root, generator output split between
//   src/MavNet.Protocol.Generated/{Messages,Enums,Commands}/   (record structs + enums)
//   src/MavNet.Protocol/Generated/MessageRegistry.cs           (parser dependency)
// MessageRegistry lives in the Protocol assembly so MavlinkFrame can reach it without
// pulling MavNet.Protocol.Generated as a project reference (which would be circular).

var here = AppContext.BaseDirectory;
// AppContext.BaseDirectory = tools/MavNet.CodeGen/bin/<cfg>/<tfm>/. Walk up to repo root.
var toolRoot = new DirectoryInfo(here).Parent?.Parent?.Parent?.FullName
    ?? throw new InvalidOperationException("Couldn't locate tools/MavNet.CodeGen/ from " + here);
var repoRoot = new DirectoryInfo(toolRoot).Parent?.Parent?.FullName
    ?? throw new InvalidOperationException("Couldn't locate repo root from " + toolRoot);

var spec         = Path.Combine(repoRoot, "specs", "common.xml");
var allowlist    = Path.Combine(toolRoot, "allowlist.txt");
var generatedOut = Path.Combine(repoRoot, "src", "MavNet.Protocol.Generated");
var registryOut  = Path.Combine(repoRoot, "src", "MavNet.Protocol", "Generated");

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--spec":          spec = args[i + 1]; break;
        case "--allowlist":     allowlist = args[i + 1]; break;
        case "--generated-out": generatedOut = args[i + 1]; break;
        case "--registry-out":  registryOut = args[i + 1]; break;
    }
}

Console.WriteLine($"Spec          : {spec}");
Console.WriteLine($"Allowlist     : {allowlist}");
Console.WriteLine($"Generated out : {generatedOut}");
Console.WriteLine($"Registry out  : {registryOut}");
Console.WriteLine();

var parsed = XmlSpecParser.Load(spec);
Console.WriteLine($"Parsed {parsed.Messages.Count} messages, {parsed.Enums.Count} enums.");

CrcExtraComputer.SelfTest(parsed);
Console.WriteLine("CRC_EXTRA self-test passed.");

var allowed = File.ReadAllLines(allowlist)
    .Select(l => l.Trim())
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .ToHashSet(StringComparer.Ordinal);
Console.WriteLine($"Allowlist     : {allowed.Count} messages [{string.Join(", ", allowed)}]");
Console.WriteLine();

// Clean previous output so deletions stick.
CleanGeneratedDir(Path.Combine(generatedOut, "Messages"));
CleanGeneratedDir(Path.Combine(generatedOut, "Enums"));
CleanGeneratedDir(Path.Combine(generatedOut, "Commands"));
CleanGeneratedDir(registryOut);
// Also clean stray flat-layout .cs files from the prior generator (Messages.Generated lived at the project root).
if (Directory.Exists(generatedOut))
{
    foreach (var f in Directory.EnumerateFiles(generatedOut, "*.cs", SearchOption.TopDirectoryOnly))
        File.Delete(f);
}

EnumEmitter.EmitAll(parsed, Path.Combine(generatedOut, "Enums"));
MessageEmitter.EmitAll(parsed, allowed, generatedOut);
CommandEmitter.EmitAll(parsed, generatedOut);
RegistryEmitter.Emit(parsed, registryOut);

Console.WriteLine("Emitted:");
foreach (var f in Directory.EnumerateFiles(generatedOut, "*.cs", SearchOption.AllDirectories)
             .Concat(Directory.EnumerateFiles(registryOut, "*.cs", SearchOption.AllDirectories))
             .OrderBy(x => x))
    Console.WriteLine($"  {Path.GetRelativePath(repoRoot, f)}");
return 0;

static void CleanGeneratedDir(string dir)
{
    if (!Directory.Exists(dir)) return;
    foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        File.Delete(f);
}
