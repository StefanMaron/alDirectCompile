using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

class Program
{
    static readonly string BaseDir = "/home/stefan/Documents/Repos/community/alDirectCompile";
    static readonly string RefAsmDir = Path.Combine(BaseDir, "StartupHook/refasm");
    static readonly string PatchedDir = Path.Combine(BaseDir, "StartupHook/patched");
    static readonly string ServiceTierDir = Path.Combine(BaseDir,
        "artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service");
    static readonly string WebClientRefsDir = Path.Combine(BaseDir,
        "artifacts/onprem/27.5.46862.0/platform/WebClient/PFiles/Microsoft Dynamics NAV/270/Web Client/WebPublish/refs");

    // Search directories for resolving target assemblies (in priority order)
    static readonly string[] SearchDirs = new[]
    {
        ServiceTierDir,
        RefAsmDir,
        WebClientRefsDir,
    };

    static void Main(string[] args)
    {
        Directory.CreateDirectory(PatchedDir);

        // 1. Merge netstandard.dll
        MergeAssembly(
            sourcePath: Path.Combine(RefAsmDir, "netstandard.dll"),
            outputPath: Path.Combine(PatchedDir, "netstandard-merged.dll"),
            extraSearchDirs: null);

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 2. Merge DocumentFormat.OpenXml.dll
        MergeAssembly(
            sourcePath: Path.Combine(ServiceTierDir, "DocumentFormat.OpenXml.dll"),
            outputPath: Path.Combine(PatchedDir, "DocumentFormat.OpenXml-merged.dll"),
            extraSearchDirs: new[] { ServiceTierDir });

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 3. Merge System.Drawing.dll
        MergeAssembly(
            sourcePath: Path.Combine(WebClientRefsDir, "System.Drawing.dll"),
            outputPath: Path.Combine(PatchedDir, "System.Drawing-merged.dll"),
            extraSearchDirs: new[] { WebClientRefsDir });

        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // 4. Merge System.Core.dll
        MergeAssembly(
            sourcePath: Path.Combine(WebClientRefsDir, "System.Core.dll"),
            outputPath: Path.Combine(PatchedDir, "System.Core-merged.dll"),
            extraSearchDirs: new[] { WebClientRefsDir });

        Console.WriteLine("\nAll merges complete.");
    }

    static void MergeAssembly(string sourcePath, string outputPath, string[]? extraSearchDirs)
    {
        Console.WriteLine($"Reading {sourcePath}");
        if (!File.Exists(sourcePath))
        {
            Console.WriteLine($"  ERROR: Source assembly not found: {sourcePath}");
            return;
        }

        // Build the full search path for this assembly
        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        var allSearchDirs = new List<string> { sourceDir };
        if (extraSearchDirs != null)
        {
            foreach (var d in extraSearchDirs)
                if (!allSearchDirs.Contains(d))
                    allSearchDirs.Add(d);
        }
        foreach (var d in SearchDirs)
            if (!allSearchDirs.Contains(d))
                allSearchDirs.Add(d);

        // Set up a resolver so Cecil can find referenced assemblies during Write
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in allSearchDirs)
        {
            if (Directory.Exists(dir))
                resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters
        {
            ReadWrite = false,
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, readerParams);
        var module = asm.MainModule;

        Console.WriteLine($"Assembly: {asm.Name.FullName}");
        Console.WriteLine($"ExportedTypes (type-forwards): {module.ExportedTypes.Count}");
        Console.WriteLine($"Existing types: {module.Types.Count}");

        // Collect ALL exported types, including nested ones
        // Group top-level forwards by target assembly
        var forwardsByAssembly = new Dictionary<string, List<ExportedType>>();
        var nestedForwards = new List<ExportedType>();

        foreach (var et in module.ExportedTypes)
        {
            if (et.DeclaringType != null)
            {
                // This is a nested type forward (e.g., Dictionary`2+KeyCollection)
                nestedForwards.Add(et);
                continue;
            }

            var scope = et.Scope;
            string asmName;
            if (scope is AssemblyNameReference anr)
                asmName = anr.Name;
            else
                continue;

            if (!forwardsByAssembly.ContainsKey(asmName))
                forwardsByAssembly[asmName] = new List<ExportedType>();
            forwardsByAssembly[asmName].Add(et);
        }

        Console.WriteLine($"\nTarget assemblies: {forwardsByAssembly.Count}");
        foreach (var kvp in forwardsByAssembly.OrderByDescending(k => k.Value.Count))
            Console.WriteLine($"  {kvp.Key}: {kvp.Value.Count} types");
        if (nestedForwards.Count > 0)
            Console.WriteLine($"Nested type-forwards: {nestedForwards.Count}");

        // Cache loaded assemblies
        var asmCache = new Dictionary<string, AssemblyDefinition>();

        int copied = 0, failed = 0, skipped = 0;
        var failedTypes = new List<string>();

        // For each target assembly, find the types and create stubs
        foreach (var (asmName, forwards) in forwardsByAssembly)
        {
            var dllPath = FindAssembly(asmName, allSearchDirs);
            if (dllPath == null)
            {
                Console.WriteLine($"  WARNING: {asmName}.dll not found in any search dir, skipping {forwards.Count} types");
                skipped += forwards.Count;
                continue;
            }

            if (!asmCache.TryGetValue(asmName, out var targetAsm))
            {
                try
                {
                    targetAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadSymbols = false });
                    asmCache[asmName] = targetAsm;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to read {dllPath}: {ex.Message}");
                    skipped += forwards.Count;
                    continue;
                }
            }

            foreach (var fwd in forwards)
            {
                var fullName = string.IsNullOrEmpty(fwd.Namespace)
                    ? fwd.Name
                    : fwd.Namespace + "." + fwd.Name;

                // Find the type in the target assembly
                var srcType = targetAsm.MainModule.Types.FirstOrDefault(t =>
                    t.Name == fwd.Name && t.Namespace == fwd.Namespace);

                if (srcType == null)
                {
                    // Type might be forwarded further - just create an empty stub
                    try
                    {
                        CreateEmptyStub(module, fwd.Namespace, fwd.Name, null);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failedTypes.Add($"{fullName} (stub failed: {ex.Message})");
                    }
                    continue;
                }

                try
                {
                    CopyTypeStub(module, srcType, null);
                    copied++;
                }
                catch (Exception ex)
                {
                    // Fallback: create empty stub
                    try
                    {
                        CreateEmptyStub(module, fwd.Namespace, fwd.Name, srcType);
                        copied++;
                    }
                    catch
                    {
                        failed++;
                        failedTypes.Add($"{fullName}: {ex.Message}");
                    }
                }
            }
        }

        // Now handle nested type-forwards (e.g., Dictionary`2+KeyCollection)
        // These reference a declaring ExportedType which we've already resolved above
        foreach (var nested in nestedForwards)
        {
            var fullName = BuildNestedFullName(nested);

            try
            {
                ResolveNestedForward(module, nested, asmCache, allSearchDirs);
                copied++;
            }
            catch (Exception ex)
            {
                // Create a minimal nested stub as fallback
                try
                {
                    EnsureNestedStub(module, nested);
                    copied++;
                }
                catch
                {
                    failed++;
                    failedTypes.Add($"{fullName} (nested): {ex.Message}");
                }
            }
        }

        // Remove all ExportedTypes (type-forwards)
        module.ExportedTypes.Clear();

        Console.WriteLine($"\nResults: {copied} copied, {failed} failed, {skipped} skipped");
        if (failedTypes.Count > 0)
        {
            Console.WriteLine("Failed types:");
            foreach (var ft in failedTypes.Take(20))
                Console.WriteLine($"  {ft}");
            if (failedTypes.Count > 20)
                Console.WriteLine($"  ... and {failedTypes.Count - 20} more");
        }

        Console.WriteLine($"Total types in module now: {module.Types.Count}");

        // Write output
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        asm.Write(outputPath);
        Console.WriteLine($"\nWritten to {outputPath}");
        Console.WriteLine($"Size: {new FileInfo(outputPath).Length:N0} bytes");

        // Cleanup
        foreach (var a in asmCache.Values)
            a.Dispose();
    }

    /// <summary>
    /// Find an assembly DLL in the search directories.
    /// </summary>
    static string? FindAssembly(string asmName, List<string> searchDirs)
    {
        foreach (var dir in searchDirs)
        {
            var path = Path.Combine(dir, asmName + ".dll");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Build the full name for a nested ExportedType (e.g., "System.Collections.Generic.Dictionary`2+KeyCollection").
    /// </summary>
    static string BuildNestedFullName(ExportedType et)
    {
        if (et.DeclaringType != null)
        {
            var parentName = BuildNestedFullName(et.DeclaringType);
            return parentName + "+" + et.Name;
        }
        return string.IsNullOrEmpty(et.Namespace) ? et.Name : et.Namespace + "." + et.Name;
    }

    /// <summary>
    /// Resolve a nested type-forward by finding its declaring type in the module
    /// and ensuring the nested type exists within it (copying from source if needed).
    /// </summary>
    static void ResolveNestedForward(ModuleDefinition module, ExportedType nested,
        Dictionary<string, AssemblyDefinition> asmCache, List<string> searchDirs)
    {
        // Walk up the chain to find the top-level declaring type and its assembly
        var chain = new List<ExportedType>();
        var current = nested;
        while (current != null)
        {
            chain.Insert(0, current);
            current = current.DeclaringType;
        }

        // chain[0] is the top-level type, chain[1..] are the nested types
        var topLevel = chain[0];

        // Find the declaring type in the module (should have been copied already)
        var parentType = module.Types.FirstOrDefault(t =>
            t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);

        if (parentType == null)
        {
            // The top-level type wasn't copied - try to find and copy it
            string? targetAsmName = null;
            if (topLevel.Scope is AssemblyNameReference anr)
                targetAsmName = anr.Name;

            if (targetAsmName != null)
            {
                var dllPath = FindAssembly(targetAsmName, searchDirs);
                if (dllPath != null)
                {
                    if (!asmCache.TryGetValue(targetAsmName, out var targetAsm))
                    {
                        targetAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadSymbols = false });
                        asmCache[targetAsmName] = targetAsm;
                    }
                    var srcType = targetAsm.MainModule.Types.FirstOrDefault(t =>
                        t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);
                    if (srcType != null)
                    {
                        parentType = CopyTypeStub(module, srcType, null);
                    }
                }
            }

            if (parentType == null)
            {
                // Create empty stub for parent
                parentType = CreateEmptyStub(module, topLevel.Namespace, topLevel.Name, null);
            }
        }

        // Now walk down the chain to ensure each nested type exists
        var currentParent = parentType;
        for (int i = 1; i < chain.Count; i++)
        {
            var nestedEt = chain[i];
            var existingNested = currentParent.NestedTypes.FirstOrDefault(t => t.Name == nestedEt.Name);

            if (existingNested != null)
            {
                currentParent = existingNested;
                continue;
            }

            // Try to find the nested type in the source assembly
            // Walk through the top-level scope to find the assembly
            string? srcAsmName = null;
            if (topLevel.Scope is AssemblyNameReference anr2)
                srcAsmName = anr2.Name;

            TypeDefinition? srcNested = null;
            if (srcAsmName != null && asmCache.TryGetValue(srcAsmName, out var srcAsm))
            {
                // Navigate to the corresponding nested type in source
                var srcParent = srcAsm.MainModule.Types.FirstOrDefault(t =>
                    t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);
                if (srcParent != null)
                {
                    for (int j = 1; j <= i; j++)
                    {
                        var step = chain[j];
                        var found = srcParent.NestedTypes.FirstOrDefault(t => t.Name == step.Name);
                        if (found == null) break;
                        if (j == i)
                            srcNested = found;
                        else
                            srcParent = found;
                    }
                }
            }

            if (srcNested != null)
            {
                currentParent = CopyTypeStub(module, srcNested, currentParent);
            }
            else
            {
                // Create empty nested stub
                var nestedType = new TypeDefinition("", nestedEt.Name,
                    TypeAttributes.NestedPublic | TypeAttributes.Class,
                    module.TypeSystem.Object);
                currentParent.NestedTypes.Add(nestedType);
                currentParent = nestedType;
            }
        }
    }

    /// <summary>
    /// Ensure a minimal nested type stub exists for the given ExportedType chain.
    /// Used as fallback when full resolution fails.
    /// </summary>
    static void EnsureNestedStub(ModuleDefinition module, ExportedType nested)
    {
        var chain = new List<ExportedType>();
        var current = nested;
        while (current != null)
        {
            chain.Insert(0, current);
            current = current.DeclaringType;
        }

        var topLevel = chain[0];
        var parentType = module.Types.FirstOrDefault(t =>
            t.Name == topLevel.Name && t.Namespace == topLevel.Namespace);

        if (parentType == null)
        {
            parentType = CreateEmptyStub(module, topLevel.Namespace, topLevel.Name, null);
        }

        var currentParent = parentType;
        for (int i = 1; i < chain.Count; i++)
        {
            var nestedEt = chain[i];
            var existing = currentParent.NestedTypes.FirstOrDefault(t => t.Name == nestedEt.Name);
            if (existing != null)
            {
                currentParent = existing;
                continue;
            }

            var nestedType = new TypeDefinition("", nestedEt.Name,
                TypeAttributes.NestedPublic | TypeAttributes.Class,
                module.TypeSystem.Object);
            currentParent.NestedTypes.Add(nestedType);
            currentParent = nestedType;
        }
    }

    /// <summary>
    /// Copy a type definition as a stub into the target module.
    /// Methods get empty/throw bodies; fields, properties, events are copied.
    /// </summary>
    static TypeDefinition CopyTypeStub(ModuleDefinition targetModule, TypeDefinition srcType, TypeDefinition? declaringType)
    {
        // Check if type already exists
        if (declaringType == null)
        {
            var existing = targetModule.Types.FirstOrDefault(t =>
                t.Name == srcType.Name && t.Namespace == srcType.Namespace);
            if (existing != null)
                return existing;
        }
        else
        {
            var existing = declaringType.NestedTypes.FirstOrDefault(t => t.Name == srcType.Name);
            if (existing != null)
                return existing;
        }

        var newType = new TypeDefinition(
            declaringType == null ? srcType.Namespace : "",
            srcType.Name,
            srcType.Attributes);

        // Base type
        if (srcType.BaseType != null)
        {
            newType.BaseType = targetModule.ImportReference(srcType.BaseType);
        }

        // Generic parameters
        CopyGenericParameters(srcType, newType, targetModule);

        // Interfaces
        foreach (var iface in srcType.Interfaces)
        {
            try
            {
                newType.Interfaces.Add(new InterfaceImplementation(
                    targetModule.ImportReference(iface.InterfaceType)));
            }
            catch { /* skip problematic interfaces */ }
        }

        // Fields (public only for non-enum, all for enum)
        foreach (var field in srcType.Fields)
        {
            if (!field.IsPublic && !field.IsFamily && !field.IsFamilyOrAssembly && !srcType.IsEnum)
                continue;

            try
            {
                var newField = new FieldDefinition(field.Name, field.Attributes,
                    targetModule.ImportReference(field.FieldType));
                if (field.HasConstant)
                    newField.Constant = field.Constant;
                if (field.InitialValue != null && field.InitialValue.Length > 0)
                    newField.InitialValue = field.InitialValue;
                newType.Fields.Add(newField);
            }
            catch { /* skip problematic fields */ }
        }

        // Methods (public/protected only)
        foreach (var method in srcType.Methods)
        {
            if (!method.IsPublic && !method.IsFamily && !method.IsFamilyOrAssembly)
                continue;

            try
            {
                var newMethod = new MethodDefinition(method.Name, method.Attributes,
                    targetModule.ImportReference(method.ReturnType));

                CopyGenericParameters(method, newMethod, targetModule);

                foreach (var param in method.Parameters)
                {
                    var newParam = new ParameterDefinition(param.Name, param.Attributes,
                        targetModule.ImportReference(param.ParameterType));
                    if (param.HasConstant)
                        newParam.Constant = param.Constant;
                    newMethod.Parameters.Add(newParam);
                }

                // Stub body: throw null (for non-abstract, non-extern methods)
                if (method.HasBody)
                {
                    newMethod.Body = new Mono.Cecil.Cil.MethodBody(newMethod);
                    var il = newMethod.Body.GetILProcessor();
                    il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                    il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Throw));
                }

                newType.Methods.Add(newMethod);
            }
            catch { /* skip problematic methods */ }
        }

        // Properties (public only)
        foreach (var prop in srcType.Properties)
        {
            var getter = prop.GetMethod;
            var setter = prop.SetMethod;
            bool isPublic = (getter != null && (getter.IsPublic || getter.IsFamily || getter.IsFamilyOrAssembly))
                         || (setter != null && (setter.IsPublic || setter.IsFamily || setter.IsFamilyOrAssembly));
            if (!isPublic) continue;

            try
            {
                var newProp = new PropertyDefinition(prop.Name, prop.Attributes,
                    targetModule.ImportReference(prop.PropertyType));
                if (prop.HasConstant)
                    newProp.Constant = prop.Constant;

                // Link to the copied methods
                if (getter != null)
                    newProp.GetMethod = newType.Methods.FirstOrDefault(m => m.Name == getter.Name);
                if (setter != null)
                    newProp.SetMethod = newType.Methods.FirstOrDefault(m => m.Name == setter.Name);

                newType.Properties.Add(newProp);
            }
            catch { /* skip problematic properties */ }
        }

        // Events (public only)
        foreach (var evt in srcType.Events)
        {
            var add = evt.AddMethod;
            var remove = evt.RemoveMethod;
            bool isPublic = (add != null && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly))
                         || (remove != null && (remove.IsPublic || remove.IsFamily || remove.IsFamilyOrAssembly));
            if (!isPublic) continue;

            try
            {
                var newEvt = new EventDefinition(evt.Name, evt.Attributes,
                    targetModule.ImportReference(evt.EventType));
                if (add != null)
                    newEvt.AddMethod = newType.Methods.FirstOrDefault(m => m.Name == add.Name);
                if (remove != null)
                    newEvt.RemoveMethod = newType.Methods.FirstOrDefault(m => m.Name == remove.Name);
                newType.Events.Add(newEvt);
            }
            catch { /* skip problematic events */ }
        }

        // Custom attributes on the type
        foreach (var attr in srcType.CustomAttributes)
        {
            try
            {
                var newAttr = new CustomAttribute(targetModule.ImportReference(attr.Constructor));
                foreach (var arg in attr.ConstructorArguments)
                    newAttr.ConstructorArguments.Add(new CustomAttributeArgument(
                        targetModule.ImportReference(arg.Type), arg.Value));
                newType.CustomAttributes.Add(newAttr);
            }
            catch { /* skip problematic attributes */ }
        }

        // Add to module or declaring type
        if (declaringType == null)
            targetModule.Types.Add(newType);
        else
            declaringType.NestedTypes.Add(newType);

        // Nested types (recursive) - copy ALL public/protected nested types
        foreach (var nested in srcType.NestedTypes)
        {
            if (!nested.IsNestedPublic && !nested.IsNestedFamily && !nested.IsNestedFamilyOrAssembly)
                continue;
            try
            {
                CopyTypeStub(targetModule, nested, newType);
            }
            catch { /* skip problematic nested types */ }
        }

        return newType;
    }

    /// <summary>
    /// Create a minimal empty type stub when we can't find/copy the real type.
    /// </summary>
    static TypeDefinition CreateEmptyStub(ModuleDefinition targetModule, string ns, string name, TypeDefinition? srcType)
    {
        // Check if already exists
        var existing = targetModule.Types.FirstOrDefault(t => t.Name == name && t.Namespace == ns);
        if (existing != null) return existing;

        var attrs = TypeAttributes.Public;
        TypeReference? baseType = targetModule.TypeSystem.Object;

        if (srcType != null)
        {
            attrs = srcType.Attributes;
            if (srcType.BaseType != null)
            {
                try { baseType = targetModule.ImportReference(srcType.BaseType); }
                catch { }
            }
            if (srcType.IsInterface)
                baseType = null;
        }

        var newType = new TypeDefinition(ns, name, attrs, baseType);

        if (srcType != null)
            CopyGenericParameters(srcType, newType, targetModule);

        targetModule.Types.Add(newType);
        return newType;
    }

    static void CopyGenericParameters(IGenericParameterProvider source, IGenericParameterProvider target, ModuleDefinition module)
    {
        foreach (var gp in source.GenericParameters)
        {
            var newGp = new GenericParameter(gp.Name, target)
            {
                Attributes = gp.Attributes
            };

            foreach (var constraint in gp.Constraints)
            {
                try
                {
                    newGp.Constraints.Add(new GenericParameterConstraint(
                        module.ImportReference(constraint.ConstraintType)));
                }
                catch { /* skip problematic constraints */ }
            }

            target.GenericParameters.Add(newGp);
        }
    }
}
