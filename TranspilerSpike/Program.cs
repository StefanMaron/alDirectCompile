using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Microsoft.Dynamics.Nav.CodeAnalysis.Emit;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

Console.WriteLine("=== AL Compiler Spike - Multi-Object ===\n");

// ─────────────────────────────────────────────
// AL Source: table + logic codeunit + test codeunit
// ─────────────────────────────────────────────

var tableSource = @"
table 50100 ""Spike Item""
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; ""No.""; Code[20])
        {
        }
        field(2; Description; Text[100])
        {
        }
        field(3; ""Unit Price""; Decimal)
        {
        }
    }

    keys
    {
        key(PK; ""No."")
        {
            Clustered = true;
        }
    }
}
";

var codeunitSource = @"
codeunit 50100 ""Spike Logic""
{
    procedure ApplyDiscount(var SpikeItem: Record ""Spike Item""; Pct: Decimal)
    begin
        SpikeItem.""Unit Price"" := SpikeItem.""Unit Price"" * (1 - Pct / 100);
        SpikeItem.Modify(false);
    end;
}
";

var testSource = @"
codeunit 50200 ""Spike Tests""
{
    Subtype = Test;

    [Test]
    procedure TestApplyDiscount()
    var
        SpikeItem: Record ""Spike Item"";
        SpikeLogic: Codeunit ""Spike Logic"";
    begin
        // Setup
        SpikeItem.Init();
        SpikeItem.""No."" := 'ITEM1';
        SpikeItem.Description := 'Test Item';
        SpikeItem.""Unit Price"" := 100;
        SpikeItem.Insert(false);

        // Exercise
        SpikeLogic.ApplyDiscount(SpikeItem, 10);

        // Verify
        SpikeItem.Get('ITEM1');
        if SpikeItem.""Unit Price"" <> 90 then
            Error('Expected 90, got %1', SpikeItem.""Unit Price"");
    end;
}
";

// Also try all-in-one source
var allInOneSource = tableSource + codeunitSource + testSource;

// ─────────────────────────────────────────────
// PHASE 0: Discover Compilation.Create overloads via reflection
// ─────────────────────────────────────────────
Console.WriteLine("--- Phase 0: Discovering Compilation API ---\n");

var compilationType = typeof(Compilation);
foreach (var m in compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    if (m.Name == "Create")
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  Compilation.Create({parms})");
    }
}

// Look for AddReferences or WithReferences methods
foreach (var m in compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
{
    if (m.Name.Contains("Reference") || m.Name.Contains("Symbol") || m.Name.Contains("Package"))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  Compilation.{m.Name}({parms}) -> {m.ReturnType.Name}");
    }
}

// ─────────────────────────────────────────────
// PHASE 1: Parse AL sources to syntax trees
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 1: Parsing AL sources ---\n");

var sources = new Dictionary<string, string>
{
    ["Table 50100"] = tableSource,
    ["Codeunit 50100"] = codeunitSource,
    ["Codeunit 50200 (Test)"] = testSource,
};

// First, try parsing all-in-one
Console.WriteLine("Attempt 1: Parse all objects in one call...");
var allTrees = new List<SyntaxTree>();
try
{
    var tree = SyntaxTree.ParseObjectText(allInOneSource);
    var diags = tree.GetDiagnostics().ToList();
    Console.WriteLine($"  [OK] Single parse: {diags.Count} diagnostics");
    foreach (var d in diags.Take(5))
        Console.WriteLine($"    {d}");

    // Check how many top-level objects we got
    var root = tree.GetCompilationUnitRoot();
    var topLevel = root.ChildNodes().ToList();
    Console.WriteLine($"  Top-level nodes: {topLevel.Count}");
    foreach (var n in topLevel)
        Console.WriteLine($"    {n.Kind} [{n.GetType().Name}]");

    if (topLevel.Count >= 3 && diags.Count == 0)
    {
        Console.WriteLine("  -> All-in-one parse looks good, using single tree");
        allTrees.Add(tree);
    }
    else
    {
        Console.WriteLine("  -> All-in-one parse incomplete, falling back to per-object parsing");
        allTrees.Clear();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  [FAIL] {ex.GetType().Name}: {ex.Message}");
}

// If all-in-one didn't work, parse each separately
if (allTrees.Count == 0)
{
    Console.WriteLine("\nAttempt 2: Parse each object separately...");
    foreach (var (name, src) in sources)
    {
        try
        {
            var tree = SyntaxTree.ParseObjectText(src);
            var diags = tree.GetDiagnostics().ToList();
            Console.WriteLine($"  [{name}] Parse: {diags.Count} diagnostics");
            foreach (var d in diags.Take(3))
                Console.WriteLine($"    {d}");
            allTrees.Add(tree);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{name}] FAIL: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

Console.WriteLine($"\nTotal syntax trees: {allTrees.Count}");

// Print syntax tree structure for each
foreach (var (tree, idx) in allTrees.Select((t, i) => (t, i)))
{
    var root = tree.GetCompilationUnitRoot();
    Console.WriteLine($"\n  Tree {idx} structure:");
    PrintNode(root, 2, 0, 3);
}

// ─────────────────────────────────────────────
// PHASE 1.5: Try loading System.app as symbol reference
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 1.5: Exploring symbol reference loading ---\n");

var systemAppPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Documents/Repos/community/alDirectCompile/artifacts/onprem/27.5.46862.0/platform/ModernDev/pfiles/microsoft dynamics nav/270/al development environment/System.app");

Console.WriteLine($"  System.app exists: {File.Exists(systemAppPath)}");
if (File.Exists(systemAppPath))
{
    Console.WriteLine($"  System.app size: {new FileInfo(systemAppPath).Length} bytes");
}

// Look for SymbolReference type or PackageCacheScope or similar
var codeAnalysisAsm = typeof(Compilation).Assembly;
var allTypes = codeAnalysisAsm.GetExportedTypes();
var referenceTypes = allTypes
    .Where(t => t.Name.Contains("Reference") || t.Name.Contains("Package") || t.Name.Contains("SymbolLoader"))
    .OrderBy(t => t.Name)
    .ToList();

Console.WriteLine($"\n  Reference-related types in CodeAnalysis assembly:");
foreach (var t in referenceTypes.Take(20))
    Console.WriteLine($"    {t.FullName}");

// Look for types related to app loading
var appTypes = allTypes
    .Where(t => t.Name.Contains("AppPackage") || t.Name.Contains("AppSymbol") || t.Name.Contains("NavApp"))
    .OrderBy(t => t.Name)
    .ToList();

Console.WriteLine($"\n  App-related types:");
foreach (var t in appTypes.Take(20))
    Console.WriteLine($"    {t.FullName}");

// ─────────────────────────────────────────────
// PHASE 2: Create a Compilation
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 2: Creating Compilation ---\n");

Compilation compilation;
try
{
    compilation = Compilation.Create(
        moduleName: "SpikeApp",
        publisher: "TestPublisher",
        version: new Version("1.0.0.0"),
        appId: Guid.NewGuid(),
        syntaxTrees: allTrees.ToArray(),
        options: new CompilationOptions(
            continueBuildOnError: true,
            target: CompilationTarget.OnPrem,
            generateOptions: CompilationGenerationOptions.All
        )
    );
    Console.WriteLine("[OK] Compilation.Create succeeded");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] Compilation.Create threw: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  Stack: {ex.StackTrace}");
    return;
}

// ─────────────────────────────────────────────
// PHASE 2.1: Try adding symbol references
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 2.1: Attempting to add symbol references ---\n");

// Check for AddReferences method or similar
var addRefMethods = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
    .Where(m => m.Name.Contains("Reference") || m.Name.Contains("Package"))
    .ToList();

Console.WriteLine("  Methods related to references:");
foreach (var m in addRefMethods)
{
    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"    {m.Name}({parms}) -> {m.ReturnType.Name}");
}

// Try to find and use a SymbolReference.Load or similar
try
{
    // Look for static factory methods on reference-related types
    foreach (var refType in referenceTypes.Where(t => !t.IsInterface && !t.IsAbstract))
    {
        var staticMethods = refType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.Contains("Load") || m.Name.Contains("Create") || m.Name.Contains("From") || m.Name.Contains("Open"))
            .ToList();
        if (staticMethods.Count > 0)
        {
            Console.WriteLine($"\n  {refType.Name} factory methods:");
            foreach (var m in staticMethods)
            {
                var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"    {m.Name}({parms}) -> {m.ReturnType.Name}");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  Reflection error: {ex.Message}");
}

// ─────────────────────────────────────────────
// PHASE 2.5: Get all diagnostics
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 2.5: Diagnostics ---\n");

try
{
    var parseDiags = compilation.GetParseDiagnostics();
    Console.WriteLine($"  Parse diagnostics: {parseDiags.Length}");
    foreach (var d in parseDiags.Take(10))
        Console.WriteLine($"    [{d.Severity}] {d.Id}: {d.GetMessage()}");

    var declDiags = compilation.GetDeclarationDiagnostics();
    Console.WriteLine($"  Declaration diagnostics: {declDiags.Length}");
    foreach (var d in declDiags.Take(20))
        Console.WriteLine($"    [{d.Severity}] {d.Id}: {d.GetMessage()}");

    var bodyDiags = compilation.GetMethodBodyDiagnostics();
    Console.WriteLine($"  Method body diagnostics: {bodyDiags.Length}");
    foreach (var d in bodyDiags.Take(20))
        Console.WriteLine($"    [{d.Severity}] {d.Id}: {d.GetMessage()}");

    var allDiags = compilation.GetDiagnostics().ToList();
    Console.WriteLine($"\n  ALL diagnostics: {allDiags.Count}");
    foreach (var d in allDiags)
    {
        Console.WriteLine($"    [{d.Severity}] {d.Id}: {d.GetMessage()}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] GetDiagnostics threw: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  Stack: {ex.StackTrace}");
}

// ─────────────────────────────────────────────
// PHASE 3: Emit and capture C# for each object
// ─────────────────────────────────────────────
Console.WriteLine("\n--- Phase 3: Attempting Emit (C# capture) ---\n");

var outputDir = "/home/stefan/Documents/Repos/community/alDirectCompile/TranspilerSpike";

try
{
    var captureOutputter = new CSharpCaptureOutputter();
    var emitOptions = new EmitOptions();
    var emitResult = compilation.Emit(emitOptions, captureOutputter);

    Console.WriteLine($"  Emit success: {emitResult.Success}");
    Console.WriteLine($"  Emit diagnostics ({emitResult.Diagnostics.Length}):");
    int shown = 0;
    foreach (var d in emitResult.Diagnostics)
    {
        Console.WriteLine($"    [{d.Severity}] {d.Id}: {d.GetMessage()}");
        if (++shown >= 40) { Console.WriteLine("    ... (truncated)"); break; }
    }

    if (captureOutputter.CapturedObjects.Count > 0)
    {
        Console.WriteLine($"\n  Captured {captureOutputter.CapturedObjects.Count} application objects:");
        foreach (var obj in captureOutputter.CapturedObjects)
        {
            Console.WriteLine($"\n  === Object: {obj.SymbolName} ===");
            Console.WriteLine($"  C# code length: {obj.CSharpCode?.Length ?? 0} bytes");
            Console.WriteLine($"  Metadata length: {obj.Metadata?.Length ?? 0} chars");
            Console.WriteLine($"  AL debug code length: {obj.DebugCode?.Length ?? 0} chars");

            if (obj.CSharpCode != null && obj.CSharpCode.Length > 0)
            {
                var csharp = Encoding.UTF8.GetString(obj.CSharpCode);
                Console.WriteLine($"\n  --- Generated C# ---");
                Console.WriteLine(csharp);
                Console.WriteLine("  --- End C# ---");

                // Determine output filename based on object name
                var fileName = obj.SymbolName switch
                {
                    var n when n.Contains("Item") || n.Contains("Table") || n.Contains("50100") && !n.Contains("Logic") => "GeneratedTable50100.cs",
                    var n when n.Contains("Logic") => "GeneratedCodeunit50100.cs",
                    var n when n.Contains("Test") || n.Contains("50200") => "GeneratedCodeunit50200.cs",
                    _ => $"Generated_{obj.SymbolName.Replace(" ", "")}.cs"
                };

                var outputPath = Path.Combine(outputDir, fileName);
                File.WriteAllText(outputPath, csharp);
                Console.WriteLine($"  [Saved to {outputPath}]");
            }

            if (obj.Metadata != null)
            {
                Console.WriteLine($"\n  --- Metadata (first 500 chars) ---");
                Console.WriteLine(obj.Metadata.Length > 500 ? obj.Metadata[..500] + "..." : obj.Metadata);
                Console.WriteLine("  --- End Metadata ---");
            }
        }
    }
    else
    {
        Console.WriteLine("  No application objects captured.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] Emit threw: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  Stack: {ex.StackTrace}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}

Console.WriteLine("\n=== Spike Complete ===");

// ─────────────────────────────────────────────
// Helper: print syntax tree
// ─────────────────────────────────────────────
static void PrintNode(SyntaxNode node, int indent, int depth, int maxDepth)
{
    if (depth >= maxDepth) return;
    Console.WriteLine(new string(' ', indent * 2) + node.Kind + " [" + node.GetType().Name + "]");
    foreach (var child in node.ChildNodes())
        PrintNode(child, indent + 1, depth + 1, maxDepth);
}

// ─────────────────────────────────────────────
// Custom CodeModuleOutputter that captures C#
// ─────────────────────────────────────────────
public record CapturedObject(string SymbolName, byte[]? CSharpCode, string? Metadata, string? DebugCode);

public class CSharpCaptureOutputter : CodeModuleOutputter
{
    public List<CapturedObject> CapturedObjects { get; } = new();
    private string? _moduleName;

    public CSharpCaptureOutputter() : base(new EmitOptions())
    {
    }

    public override void InitializeModule(IModuleSymbol moduleSymbol)
    {
        _moduleName = moduleSymbol.Name;
        Console.WriteLine($"  [Outputter] InitializeModule: {_moduleName}");
    }

    public override void AddApplicationObject(IApplicationObjectTypeSymbol symbol, byte[] code, string metadata, string debugCode)
    {
        Console.WriteLine($"  [Outputter] AddApplicationObject: {symbol.Name} (code={code?.Length ?? 0} bytes, metadata={metadata?.Length ?? 0} chars)");
        CapturedObjects.Add(new CapturedObject(symbol.Name, code, metadata, debugCode));
    }

    public override void AddProfileObject(ISymbol symbol, byte[] code, string metadata, string debugCode)
    {
        Console.WriteLine($"  [Outputter] AddProfileObject: {symbol.Name}");
    }

    public override void AddNavigationObject(string content)
    {
        Console.WriteLine($"  [Outputter] AddNavigationObject ({content.Length} chars)");
    }

    public override void AddExternalBusinessEvent(string content)
    {
        Console.WriteLine($"  [Outputter] AddExternalBusinessEvent ({content.Length} chars)");
    }

    public override void AddMovedObjects(string content)
    {
        Console.WriteLine($"  [Outputter] AddMovedObjects ({content.Length} chars)");
    }

    public override void FinalizeModule()
    {
        Console.WriteLine($"  [Outputter] FinalizeModule");
    }

    public override ImmutableArray<Diagnostic> GetDiagnostics()
    {
        return ImmutableArray<Diagnostic>.Empty;
    }
}
