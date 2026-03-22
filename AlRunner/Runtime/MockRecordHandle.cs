namespace AlRunner.Runtime;

using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.Runtime;  // NavValue, NavCode, NavText, NavDecimal, NavInteger, NavBoolean, Decimal18
using Microsoft.Dynamics.Nav.Types;     // NavType, DataError

/// <summary>
/// In-memory record store replacing INavRecordHandle + NavRecordHandle.
/// Each instance tracks field values for a single record cursor.
/// A static dictionary acts as the "database" for all tables.
/// Supports SetRange/SetFilter filtering with AND-combination across fields.
/// </summary>
public class MockRecordHandle
{
    private readonly int _tableId;
    private Dictionary<int, NavValue> _fields = new();

    // Global in-memory table store: tableId -> list of rows (each row = dict of fieldNo -> NavValue)
    private static readonly Dictionary<int, List<Dictionary<int, NavValue>>> _tables = new();

    // Current cursor position for iteration
    private int _cursorPosition = -1;
    private List<Dictionary<int, NavValue>>? _currentResultSet;

    // Per-field filters: fieldNo -> filter definition. Multiple fields are AND-combined.
    private readonly Dictionary<int, FieldFilter> _filters = new();

    // Current sort key fields (for ordering)
    private int[]? _currentKeyFields;
    private readonly Dictionary<int, bool> _ascending = new();

    public MockRecordHandle(int tableId)
    {
        _tableId = tableId;
        if (!_tables.ContainsKey(tableId))
            _tables[tableId] = new List<Dictionary<int, NavValue>>();
    }

    /// <summary>Reset all tables between test runs.</summary>
    public static void ResetAll() => _tables.Clear();

    public void ALInit()
    {
        _fields = new Dictionary<int, NavValue>();
    }

    public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        _fields[fieldNo] = value;
    }

    public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType)
    {
        if (_fields.TryGetValue(fieldNo, out var val))
            return val;
        return DefaultForType(expectedType);
    }

    /// <summary>For Error() formatting - returns the field value as NavValue.</summary>
    public NavValue GetFieldRefSafe(int fieldNo, NavType expectedType)
    {
        return GetFieldValueSafe(fieldNo, expectedType);
    }

    public void Clear()
    {
        _fields = new Dictionary<int, NavValue>();
        _cursorPosition = -1;
        _currentResultSet = null;
    }

    public bool ALInsert(DataError errorLevel) => ALInsert(errorLevel, false);

    public bool ALInsert(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];
        var row = new Dictionary<int, NavValue>(_fields);
        table.Add(row);
        return true;
    }

    public bool ALModify(DataError errorLevel) => ALModify(errorLevel, false);

    public bool ALModify(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];
        if (!_fields.ContainsKey(1)) return false;
        var pk = _fields[1].ToString();
        for (int i = 0; i < table.Count; i++)
        {
            if (table[i].ContainsKey(1) && table[i][1].ToString() == pk)
            {
                table[i] = new Dictionary<int, NavValue>(_fields);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found for Modify in table {_tableId}");
        return false;
    }

    public bool ALGet(DataError errorLevel, params NavValue[] keyValues)
    {
        var table = _tables[_tableId];
        var keyStr = keyValues.Length > 0 ? keyValues[0].ToString() : "";
        foreach (var row in table)
        {
            if (row.ContainsKey(1) && row[1].ToString() == keyStr)
            {
                _fields = new Dictionary<int, NavValue>(row);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found in table {_tableId} for key '{keyStr}'");
        return false;
    }

    // -----------------------------------------------------------------------
    // Find / iterate methods — all respect active filters
    // -----------------------------------------------------------------------

    public bool ALFind(DataError errorLevel, string searchMethod = "-")
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            _currentResultSet = filtered;
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = filtered;

        if (searchMethod == "+" || searchMethod == ">")
        {
            // Position at last record
            _cursorPosition = filtered.Count - 1;
            _fields = new Dictionary<int, NavValue>(filtered[^1]);
        }
        else
        {
            // Default ("-", "=", "<"): position at first record
            _cursorPosition = 0;
            _fields = new Dictionary<int, NavValue>(filtered[0]);
        }
        return true;
    }

    public bool ALFindSet(DataError errorLevel = DataError.ThrowError, bool forUpdate = false)
    {
        return ALFind(errorLevel, "-");
    }

    public bool ALFindFirst(DataError errorLevel = DataError.ThrowError)
    {
        return ALFind(errorLevel, "-");
    }

    public bool ALFindLast(DataError errorLevel = DataError.ThrowError)
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            _currentResultSet = filtered;
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = filtered;
        _cursorPosition = filtered.Count - 1;
        _fields = new Dictionary<int, NavValue>(filtered[^1]);
        return true;
    }

    public int ALNext()
    {
        if (_currentResultSet == null || _cursorPosition >= _currentResultSet.Count - 1)
            return 0;
        _cursorPosition++;
        _fields = new Dictionary<int, NavValue>(_currentResultSet[_cursorPosition]);
        return 1;
    }

    public bool ALDelete(DataError errorLevel, bool runTrigger = false)
    {
        var table = _tables[_tableId];
        if (!_fields.ContainsKey(1)) return false;
        var pk = _fields[1].ToString();
        for (int i = 0; i < table.Count; i++)
        {
            if (table[i].ContainsKey(1) && table[i][1].ToString() == pk)
            {
                table.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public void ALDeleteAll(DataError errorLevel = DataError.ThrowError, bool runTrigger = false)
    {
        if (_filters.Count == 0)
        {
            // No filters: delete all records in the table
            _tables[_tableId] = new List<Dictionary<int, NavValue>>();
        }
        else
        {
            // Filters active: only delete matching records
            var table = _tables[_tableId];
            var toRemove = GetFilteredRecords();
            var toRemoveKeys = new HashSet<string>(
                toRemove.Select(r => RowKey(r)));
            table.RemoveAll(r => toRemoveKeys.Contains(RowKey(r)));
        }
    }

    // -----------------------------------------------------------------------
    // SetRange — range-based filtering
    // -----------------------------------------------------------------------

    /// <summary>
    /// SetRange with from/to values. If both are default/empty for the type,
    /// this clears the filter for that field (AL behavior for SETRANGE(field)).
    /// </summary>
    public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        // Check if this is a "clear filter" call: both values are the default for the type
        // In AL, SETRANGE(FieldNo) with no value args clears the filter.
        // The transpiler emits ALSetRange with default values in that case.
        var fromStr = NavValueToString(fromValue);
        var toStr = NavValueToString(toValue);
        var defaultStr = NavValueToString(DefaultForType(expectedType));

        if (fromStr == defaultStr && toStr == defaultStr)
        {
            // Clear filter for this field
            _filters.Remove(fieldNo);
            return;
        }

        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FromValue = fromValue,
            ToValue = toValue,
            IsRangeFilter = true,
        };
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        // Single value = equality filter (from == to)
        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FromValue = value,
            ToValue = value,
            IsRangeFilter = true,
        };
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        ALSetRange(fieldNo, expectedType, fromValue, toValue);
    }

    // -----------------------------------------------------------------------
    // SetFilter — expression-based filtering
    // -----------------------------------------------------------------------

    /// <summary>
    /// SetFilter with a filter expression string and optional substitution args.
    /// Supports: equality ('VALUE'), range ('FROM..TO'), or-list ('V1|V2|V3'),
    /// wildcards ('*text*', 'text*', '*text'), case-insensitive prefix ('@'),
    /// not-equal ('&lt;&gt;VALUE'), and placeholder substitution (%1, %2, etc).
    /// Empty expression clears the filter for that field.
    /// </summary>
    /// <summary>Overload without NavType (transpiler emits this form).</summary>
    public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args)
    {
        ALSetFilter(fieldNo, NavType.Text, filterExpression, args);
    }

    public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args)
    {
        // Substitute %1, %2, ... with args
        var expr = SubstitutePlaceholders(filterExpression, args);

        // Empty expression clears filter
        if (string.IsNullOrEmpty(expr))
        {
            _filters.Remove(fieldNo);
            return;
        }

        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FilterExpression = expr,
            IsRangeFilter = false,
        };
    }

    // -----------------------------------------------------------------------
    // SetCurrentKey — set sort key (with DataError first param)
    // -----------------------------------------------------------------------

    public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos)
    {
        _currentKeyFields = fieldNos;
    }

    /// <summary>Legacy overload without DataError (kept for backward compat).</summary>
    public void ALSetCurrentKey(params int[] fieldNos)
    {
        _currentKeyFields = fieldNos;
    }

    public void ALSetAscending(int fieldNo, bool ascending)
    {
        _ascending[fieldNo] = ascending;
    }

    // -----------------------------------------------------------------------
    // Reset — clears filters, fields, and cursor
    // -----------------------------------------------------------------------

    public void ALReset()
    {
        _fields = new Dictionary<int, NavValue>();
        _cursorPosition = -1;
        _currentResultSet = null;
        _filters.Clear();
        _currentKeyFields = null;
        _ascending.Clear();
    }

    // -----------------------------------------------------------------------
    // Count / IsEmpty — respect active filters
    // -----------------------------------------------------------------------

    public int ALCount => GetFilteredRecords().Count;

    public bool ALIsEmpty => GetFilteredRecords().Count == 0;

    public int ALFieldNo(string fieldName)
    {
        // Simplified: return 0 (unknown) -- real impl would look up field metadata
        return 0;
    }

    // =======================================================================
    // Filter infrastructure
    // =======================================================================

    /// <summary>
    /// Per-field filter definition. Either a range (from/to) or a parsed expression.
    /// </summary>
    private class FieldFilter
    {
        public int FieldNo;
        public NavValue? FromValue;   // For range filters
        public NavValue? ToValue;     // For range filters
        public string? FilterExpression; // For SetFilter expression filters
        public bool IsRangeFilter;    // true = SetRange, false = SetFilter
    }

    /// <summary>
    /// Returns the subset of table rows matching all active filters.
    /// When no filters are set, returns all rows (preserving existing behavior).
    /// </summary>
    private List<Dictionary<int, NavValue>> GetFilteredRecords()
    {
        var table = _tables[_tableId];
        if (_filters.Count == 0)
            return table;

        var result = new List<Dictionary<int, NavValue>>();
        foreach (var row in table)
        {
            if (RowMatchesAllFilters(row))
                result.Add(row);
        }
        return result;
    }

    /// <summary>Check if a single row matches ALL active filters (AND-combined).</summary>
    private bool RowMatchesAllFilters(Dictionary<int, NavValue> row)
    {
        foreach (var filter in _filters.Values)
        {
            if (!RowMatchesFilter(row, filter))
                return false;
        }
        return true;
    }

    /// <summary>Check if a single row matches a specific field filter.</summary>
    private bool RowMatchesFilter(Dictionary<int, NavValue> row, FieldFilter filter)
    {
        var fieldValue = row.TryGetValue(filter.FieldNo, out var val) ? val : null;
        var fieldStr = fieldValue != null ? NavValueToString(fieldValue) : "";

        if (filter.IsRangeFilter)
        {
            return MatchesRange(fieldValue, fieldStr, filter.FromValue!, filter.ToValue!);
        }
        else
        {
            return MatchesFilterExpression(fieldValue, fieldStr, filter.FilterExpression!);
        }
    }

    // -----------------------------------------------------------------------
    // Range matching (SetRange)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Check if a field value falls within [from, to] range.
    /// Uses IComparable when available, falls back to string comparison.
    /// When from == to, this is an equality check.
    /// </summary>
    private static bool MatchesRange(NavValue? fieldValue, string fieldStr, NavValue from, NavValue to)
    {
        var fromStr = NavValueToString(from);
        var toStr = NavValueToString(to);

        // Equality shortcut: from == to
        if (fromStr == toStr)
            return string.Equals(fieldStr, fromStr, StringComparison.Ordinal);

        // Try IComparable-based comparison for proper numeric/date ordering
        if (fieldValue is IComparable comparable && from is IComparable && to is IComparable)
        {
            try
            {
                var cmpFrom = comparable.CompareTo(from);
                var cmpTo = comparable.CompareTo(to);
                return cmpFrom >= 0 && cmpTo <= 0;
            }
            catch
            {
                // Fall through to string comparison
            }
        }

        // String comparison fallback
        return string.Compare(fieldStr, fromStr, StringComparison.Ordinal) >= 0 &&
               string.Compare(fieldStr, toStr, StringComparison.Ordinal) <= 0;
    }

    // -----------------------------------------------------------------------
    // Expression matching (SetFilter)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluate a filter expression against a field value.
    /// Supports: equality, range (FROM..TO), or-list (V1|V2|V3),
    /// wildcards (* for any), case-insensitive (@), not-equal (&lt;&gt;),
    /// comparison operators (&gt;, &gt;=, &lt;, &lt;=).
    /// Pipe-separated alternatives are OR-combined.
    /// </summary>
    private static bool MatchesFilterExpression(NavValue? fieldValue, string fieldStr, string expression)
    {
        // Split on | for OR alternatives (but not inside quotes)
        var alternatives = SplitOrAlternatives(expression);

        foreach (var alt in alternatives)
        {
            if (MatchesSingleExpression(fieldValue, fieldStr, alt.Trim()))
                return true;
        }
        return false;
    }

    /// <summary>Evaluate a single filter expression (no OR pipe).</summary>
    private static bool MatchesSingleExpression(NavValue? fieldValue, string fieldStr, string expr)
    {
        if (string.IsNullOrEmpty(expr))
            return true;

        // Check for case-insensitive prefix @
        bool caseInsensitive = false;
        var working = expr;
        if (working.StartsWith('@'))
        {
            caseInsensitive = true;
            working = working.Substring(1);
        }

        // Strip surrounding single quotes if present
        working = StripQuotes(working);

        // Check for not-equal: <>VALUE
        if (working.StartsWith("<>"))
        {
            var notVal = working.Substring(2);
            return !StringEquals(fieldStr, notVal, caseInsensitive);
        }

        // Check for comparison operators: >=, <=, >, <
        if (working.StartsWith(">="))
        {
            var cmpVal = working.Substring(2);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) >= 0;
        }
        if (working.StartsWith("<="))
        {
            var cmpVal = working.Substring(2);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) <= 0;
        }
        if (working.StartsWith(">") && !working.StartsWith(">="))
        {
            var cmpVal = working.Substring(1);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) > 0;
        }
        if (working.StartsWith("<") && !working.StartsWith("<=") && !working.StartsWith("<>"))
        {
            var cmpVal = working.Substring(1);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) < 0;
        }

        // Check for range: FROM..TO
        var dotDotIdx = working.IndexOf("..", StringComparison.Ordinal);
        if (dotDotIdx >= 0)
        {
            var rangeFrom = working.Substring(0, dotDotIdx);
            var rangeTo = working.Substring(dotDotIdx + 2);
            var cmpFrom = StringCompareOp(fieldStr, rangeFrom, caseInsensitive);
            var cmpTo = StringCompareOp(fieldStr, rangeTo, caseInsensitive);
            return cmpFrom >= 0 && cmpTo <= 0;
        }

        // Check for wildcards: * means any characters
        if (working.Contains('*') || working.Contains('?'))
        {
            return WildcardMatch(fieldStr, working, caseInsensitive);
        }

        // Plain equality
        return StringEquals(fieldStr, working, caseInsensitive);
    }

    // -----------------------------------------------------------------------
    // String/comparison helpers
    // -----------------------------------------------------------------------

    private static bool StringEquals(string a, string b, bool caseInsensitive)
    {
        return string.Equals(a, b,
            caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static int StringCompareOp(string a, string b, bool caseInsensitive)
    {
        // Try numeric comparison first if both parse as decimal
        if (decimal.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var decA) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var decB))
        {
            return decA.CompareTo(decB);
        }

        return string.Compare(a, b,
            caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Simple wildcard matching: * = any sequence of chars, ? = any single char.
    /// Converts to regex for matching.
    /// </summary>
    private static bool WildcardMatch(string input, string pattern, bool caseInsensitive)
    {
        // Escape regex specials except * and ?
        var regexPattern = "^" +
            Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") +
            "$";
        var options = RegexOptions.Singleline;
        if (caseInsensitive)
            options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(input, regexPattern, options);
    }

    /// <summary>Strip surrounding single quotes from a filter value.</summary>
    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s.Substring(1, s.Length - 2);
        return s;
    }

    /// <summary>
    /// Split a filter expression on | (pipe) for OR alternatives.
    /// Respects single-quoted strings (pipe inside quotes is literal).
    /// </summary>
    private static List<string> SplitOrAlternatives(string expression)
    {
        var parts = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (expression[i] == '|' && !inQuotes)
            {
                parts.Add(expression.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(expression.Substring(start));
        return parts;
    }

    /// <summary>
    /// Substitute %1, %2, ... placeholders in a filter expression with NavValue args.
    /// </summary>
    private static string SubstitutePlaceholders(string expression, NavValue[] args)
    {
        if (args.Length == 0)
            return expression;

        var result = expression;
        for (int i = args.Length; i >= 1; i--)
        {
            result = result.Replace($"%{i}", NavValueToString(args[i - 1]));
        }
        return result;
    }

    /// <summary>
    /// Convert a NavValue to its string representation for comparison.
    /// Uses explicit casts to avoid any NavSession dependency from ToString().
    /// </summary>
    private static string NavValueToString(NavValue value)
    {
        if (value is NavText nt) return (string)nt;
        if (value is NavCode nc) return (string)nc;
        if (value is NavInteger ni) return ((int)ni).ToString();
        if (value is NavDecimal nd)
        {
            try { return Convert.ToDecimal(nd.GetType().GetProperty("Value")?.GetValue(nd)).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch { }
        }
        if (value is NavBoolean nb) return ((bool)nb).ToString();
        if (value is NavBigInteger nbi) return ((long)nbi).ToString();
        if (value is NavGuid ng) return ((Guid)ng).ToString();
        // Fallback
        try { return value.ToString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Generate a simple row key for identity comparison (used by ALDeleteAll with filters).
    /// </summary>
    private static string RowKey(Dictionary<int, NavValue> row)
    {
        // Use all fields sorted by field number for a unique-ish key
        var parts = row.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={NavValueToString(kv.Value)}");
        return string.Join("|", parts);
    }

    private static NavValue DefaultForType(NavType navType)
    {
        return navType switch
        {
            NavType.Decimal => NavDecimal.Default,
            NavType.Integer => NavInteger.Default,
            NavType.Text => NavText.Default(0),
            NavType.Code => new NavCode(20, ""),
            NavType.Boolean => NavBoolean.Default,
            _ => NavText.Default(0)
        };
    }
}
