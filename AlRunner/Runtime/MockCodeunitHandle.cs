namespace AlRunner.Runtime;

using System.Reflection;

/// <summary>
/// Routes cross-codeunit method calls to generated codeunit classes.
/// Replaces NavCodeunitHandle for standalone execution.
/// </summary>
public class MockCodeunitHandle
{
    private readonly int _codeunitId;
    private object? _codeunitInstance;

    /// <summary>
    /// The assembly containing compiled codeunit classes. Set before execution.
    /// </summary>
    public static Assembly? CurrentAssembly { get; set; }

    public MockCodeunitHandle(int codeunitId)
    {
        _codeunitId = codeunitId;
    }

    /// <summary>
    /// Resets the handle (no-op for standalone execution).
    /// Called from OnClear() in generated codeunit classes.
    /// </summary>
    public void Clear()
    {
        _codeunitInstance = null;
    }

    /// <summary>
    /// Clears the reference (no-op for standalone execution).
    /// Called from generated codeunit code when resetting handles.
    /// </summary>
    public void ClearReference()
    {
        _codeunitInstance = null;
    }

    /// <summary>
    /// Static factory matching the rewritten constructor pattern.
    /// </summary>
    public static MockCodeunitHandle Create(int codeunitId)
    {
        return new MockCodeunitHandle(codeunitId);
    }

    /// <summary>
    /// Invoke a method by its member ID. The generated codeunit has public methods
    /// like ApplyDiscount(...) that create scope objects internally.
    /// We find the matching public method by looking at the scope class name which
    /// encodes the member ID.
    /// </summary>
    public object? Invoke(int memberId, object[] args)
    {
        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = FindCodeunitType(assembly);
        if (codeunitType == null)
            throw new InvalidOperationException($"Codeunit {_codeunitId} not found in assembly");

        // Lazily create codeunit instance and call InitializeComponent
        if (_codeunitInstance == null)
        {
            _codeunitInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(codeunitType);
            var initMethod = codeunitType.GetMethod("InitializeComponent",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            initMethod?.Invoke(_codeunitInstance, null);
        }

        // Find the public method whose scope class name contains the memberId.
        // Scope classes are named like: ApplyDiscount_Scope_1351223168
        // The memberId passed to Invoke matches the number in the scope name.
        // We look for a nested scope type matching the memberId, then call the
        // parent public method that creates that scope.
        var absMemberId = Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        // Strategy: find the nested scope class whose name ends with the memberId,
        // then find the public method on the codeunit that references it.
        foreach (var nested in codeunitType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            // Scope names look like: MethodName_Scope_NNNNN or MethodName_Scope__NNNNN (negative)
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                // Extract the method name from the scope class name
                // e.g. "ApplyDiscount_Scope_1351223168" -> "ApplyDiscount"
                var scopeName = nested.Name;
                var scopeIdx = scopeName.IndexOf("_Scope_");
                if (scopeIdx < 0) continue;
                var methodName = scopeName.Substring(0, scopeIdx);

                // Find the public method on the codeunit class
                var method = codeunitType.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (method == null) continue;

                // Convert args to match parameter types
                var parameters = method.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Length)
                    {
                        convertedArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                    }
                }

                return method.Invoke(_codeunitInstance, convertedArgs);
            }
        }

        throw new InvalidOperationException(
            $"Method with member ID {memberId} not found in codeunit {_codeunitId}");
    }

    /// <summary>
    /// Static dispatch: run a codeunit's OnRun trigger by ID.
    /// Replacement for NavCodeunit.RunCodeunit(DataError, codeunitId, record).
    /// </summary>
    public static void RunCodeunit(int codeunitId)
    {
        var handle = new MockCodeunitHandle(codeunitId);
        // Invoke the OnRun scope (member ID 0 or find OnRun explicitly)
        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = handle.FindCodeunitType(assembly);
        if (codeunitType == null)
            throw new InvalidOperationException($"Codeunit {codeunitId} not found in assembly");

        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(codeunitType);
        var initMethod = codeunitType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        initMethod?.Invoke(instance, null);

        // Find and invoke the OnRun method (parameterless or with record parameter)
        var onRunMethod = codeunitType.GetMethod("OnRun",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (onRunMethod != null)
        {
            onRunMethod.Invoke(instance, null);
            return;
        }

        // Try finding OnRun with parameters
        var onRunMethods = codeunitType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "OnRun").ToArray();
        if (onRunMethods.Length > 0)
        {
            onRunMethods[0].Invoke(instance, new object?[onRunMethods[0].GetParameters().Length]);
            return;
        }
    }

    private Type? FindCodeunitType(Assembly assembly)
    {
        var expectedName = $"Codeunit{_codeunitId}";
        return assembly.GetTypes().FirstOrDefault(t => t.Name == expectedName);
    }

    private static object? ConvertArg(object? arg, Type targetType)
    {
        if (arg == null) return null;
        if (targetType.IsAssignableFrom(arg.GetType())) return arg;

        // MockVariant -> unwrap to underlying value and retry
        if (arg is MockVariant mv)
        {
            return ConvertArg(mv.Value, targetType);
        }

        // int/decimal -> Decimal18 conversion
        if (targetType.Name == "Decimal18")
        {
            var intCtor = targetType.GetConstructor(new[] { typeof(int) });
            if (intCtor != null && arg is int intVal)
                return intCtor.Invoke(new object[] { intVal });
            var decCtor = targetType.GetConstructor(new[] { typeof(decimal) });
            if (decCtor != null)
            {
                decimal decVal = Convert.ToDecimal(arg);
                return decCtor.Invoke(new object[] { decVal });
            }
        }

        // object -> MockVariant conversion (Variant parameters in AL)
        if (targetType == typeof(MockVariant))
        {
            return new MockVariant(arg);
        }

        // object -> NavVariant conversion (Variant parameters in AL)
        if (targetType.Name == "NavVariant")
        {
            // NavVariant.Create(object) or NavVariant(object) constructor
            var createMethod = targetType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(object) }, null);
            if (createMethod != null)
                return createMethod.Invoke(null, new[] { arg });
            // Try constructor taking object
            var ctor = targetType.GetConstructor(new[] { typeof(object) });
            if (ctor != null)
                return ctor.Invoke(new[] { arg });
            // Fall back: try parameterless + set value
            var defaultCtor = targetType.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
                return defaultCtor.Invoke(null);
        }

        // Try general conversion
        try { return Convert.ChangeType(arg, targetType); }
        catch { return arg; }
    }
}
