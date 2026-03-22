using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Runtime;

/// <summary>
/// Minimal base class replacing NavMethodScope&lt;T&gt; for standalone execution.
/// Provides stub implementations of the debug-hit methods and a Run() entry point.
/// </summary>
public class AlScope : IDisposable
{
    protected virtual void OnRun() { }

    public void Run() => OnRun();

    public void Dispose() { }

    // Debug coverage stubs - the AL compiler emits these for code coverage tracking
    protected void StmtHit(int n) { }
    protected bool CStmtHit(int n) => true;

    /// <summary>
    /// AL's asserterror keyword - catches expected errors.
    /// Sets the last error text for Assert.ExpectedError() to check.
    /// </summary>
    protected void AssertError(Action action)
    {
        try
        {
            action();
            LastErrorText = "";
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException from reflection calls
            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                inner = tie.InnerException;
            LastErrorText = inner.Message;
        }
    }

    /// <summary>
    /// Stores the last error message from asserterror blocks.
    /// </summary>
    public static string LastErrorText { get; set; } = "";
}

/// <summary>
/// Replacement for NavDialog static methods.
/// Translates AL Message/Error calls to console output / exceptions.
/// </summary>
public static class AlDialog
{
    public static void Message(string format, params object?[] args)
    {
        var netFormat = ConvertAlFormat(format);
        var stringArgs = args.Select(a => a?.ToString() ?? "").ToArray();
        if (stringArgs.Length > 0)
            Console.WriteLine(string.Format(netFormat, stringArgs));
        else
            Console.WriteLine(format);
    }

    public static void Error(string format, params object?[] args)
    {
        var netFormat = ConvertAlFormat(format);
        var stringArgs = args.Select(a => a?.ToString() ?? "").ToArray();
        if (stringArgs.Length > 0)
            throw new Exception(string.Format(netFormat, stringArgs));
        else
            throw new Exception(format);
    }

    /// <summary>
    /// Converts AL format placeholders (%1, %2, ...) to .NET format ({0}, {1}, ...).
    /// </summary>
    private static string ConvertAlFormat(string alFormat)
    {
        var result = alFormat;
        for (int i = 9; i >= 1; i--)
            result = result.Replace($"%{i}", $"{{{i - 1}}}");
        return result;
    }
}

/// <summary>
/// Lightweight replacement for ALCompiler static methods that depend on NavSession.
/// These methods are used in generated C# code for type conversions.
/// </summary>
public static class AlCompat
{
    /// <summary>
    /// Replacement for ALCompiler.ToNavValue - wraps a value as NavValue.
    /// NavValue is abstract; we create the appropriate concrete subtype.
    /// The original goes through NavValueFormatter/NavSession; we construct directly.
    /// </summary>
    public static NavValue ToNavValue(object? value)
    {
        if (value == null) return new NavText("");
        if (value is NavValue nv) return nv;
        if (value is string s) return new NavText(s);
        if (value is int i) return NavInteger.Create(i);
        if (value is decimal d) return NavDecimal.Create(new Microsoft.Dynamics.Nav.Runtime.Decimal18(d));
        if (value is Microsoft.Dynamics.Nav.Runtime.Decimal18 d18) return NavDecimal.Create(d18);
        if (value is bool b) return NavBoolean.Create(b);
        if (value is Guid g) return new NavGuid(g);
        if (value is long l) return NavBigInteger.Create(l);
        // Fall back to string representation
        return new NavText(value.ToString() ?? "");
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToDecimal.
    /// </summary>
    public static decimal ObjectToDecimal(object? value)
    {
        if (value == null) return 0m;
        return Convert.ToDecimal(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToBoolean.
    /// </summary>
    public static bool ObjectToBoolean(object? value)
    {
        if (value == null) return false;
        return Convert.ToBoolean(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.ToVariant / NavValueToVariant.
    /// Wraps a value as an object (Variant in AL is just object in C#).
    /// </summary>
    public static object ToVariant(object? value) => value ?? "";

    /// <summary>
    /// Replacement for NavFormatEvaluateHelper.Format.
    /// AL Format() trims trailing zeros from decimals and uses invariant formatting.
    /// </summary>
    public static string Format(object? value)
    {
        if (value == null) return "";
        // Handle native .NET numeric types
        if (value is decimal d) return FormatDecimal(d);
        if (value is double dbl) return FormatDecimal((decimal)dbl);
        if (value is float f) return FormatDecimal((decimal)f);
        if (value is int or long or short or byte) return value.ToString()!;
        // Handle Decimal18 and other BC numeric types — convert to decimal
        var typeName = value.GetType().Name;
        if (typeName == "Decimal18")
        {
            try
            {
                var d18 = Convert.ToDecimal(value);
                return FormatDecimal(d18);
            }
            catch { }
        }
        // Handle NavOption — ToString() triggers NavSession via NavOptionFormatter
        if (typeName == "NavOption")
        {
            try
            {
                // NavOption has a Value property (int) we can use
                var valProp = value.GetType().GetProperty("Value");
                if (valProp != null)
                    return valProp.GetValue(value)?.ToString() ?? "";
            }
            catch { }
        }
        // Handle NavValue subtypes — use ToText() where available, avoid ToString() which may need NavSession
        if (value is Microsoft.Dynamics.Nav.Runtime.NavValue nv)
        {
            try
            {
                if (value is Microsoft.Dynamics.Nav.Runtime.NavText nt) return (string)nt;
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBoolean nb) return ((bool)nb).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavInteger ni) return ((int)ni).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBigInteger nbi) return ((long)nbi).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavGuid ng) return ((Guid)ng).ToString();
                // For NavDecimal, extract the underlying Decimal18
                var decProp = value.GetType().GetProperty("Value");
                if (decProp != null)
                {
                    var inner = decProp.GetValue(value);
                    if (inner != null) return FormatDecimal(Convert.ToDecimal(inner));
                }
            }
            catch { }
        }
        return value.ToString() ?? "";
    }

    /// <summary>
    /// Format with AL format number and length.
    /// Used when AL code calls Format(value, formatNumber) or Format(value, formatNumber, formatLength)
    /// </summary>
    public static string Format(object? value, int formatNumber, int formatLength = 0)
    {
        // For now, ignore the format number/length and use default formatting
        return Format(value);
    }

    private static string FormatDecimal(decimal d)
    {
        // AL Format() shows whole numbers without decimals
        return d == Math.Truncate(d) ? d.ToString("0") : d.ToString("0.##########");
    }

    // NavVariant type-check properties (rewritten from value.ALIsXxx to AlCompat.ALIsXxx(value))
    public static bool ALIsBoolean(object? v) => v is bool;
    public static bool ALIsOption(object? v) => v is Enum || v?.GetType().Name == "NavOption";
    public static bool ALIsInteger(object? v) => v is int;
    public static bool ALIsByte(object? v) => v is byte;
    public static bool ALIsBigInteger(object? v) => v is long;
    public static bool ALIsDecimal(object? v) => v is decimal || v?.GetType().Name == "Decimal18";
    public static bool ALIsText(object? v) => v is string || v?.GetType().Name == "NavText";
    public static bool ALIsCode(object? v) => v?.GetType().Name == "NavCode";
    public static bool ALIsChar(object? v) => v is char;
    public static bool ALIsTextConst(object? v) => v?.GetType().Name == "NavTextConstant";
    public static bool ALIsDate(object? v) => v is DateTime dt && dt.TimeOfDay == TimeSpan.Zero;
    public static bool ALIsTime(object? v) => v?.GetType().Name == "NavTime";
    public static bool ALIsDuration(object? v) => v is TimeSpan;
    public static bool ALIsDateTime(object? v) => v is DateTime;
    public static bool ALIsDateFormula(object? v) => v?.GetType().Name == "NavDateFormula";
    public static bool ALIsGuid(object? v) => v is Guid;
    public static bool ALIsRecordId(object? v) => v?.GetType().Name == "NavRecordId";
    public static bool ALIsRecord(object? v) => v?.GetType().Name.StartsWith("Record") == true;
    public static bool ALIsRecordRef(object? v) => v?.GetType().Name == "NavRecordRef";
    public static bool ALIsFieldRef(object? v) => v?.GetType().Name == "NavFieldRef";
    public static bool ALIsCodeunit(object? v) => v?.GetType().Name.StartsWith("Codeunit") == true;
    public static bool ALIsFile(object? v) => v?.GetType().Name == "NavFile";
    public static bool ALIsDotNet(object? v) => false; // DotNet types not supported in standalone
    public static bool ALIsAutomation(object? v) => false; // Automation types not supported in standalone
}

// NavVariant type-check properties are rewritten from value.ALIsXxx to AlCompat.ALIsXxx(value)
// These are added as static methods on AlCompat above.
