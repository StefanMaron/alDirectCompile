namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;  // NavValue, NavCode, NavText, NavDecimal, NavInteger, NavBoolean, Decimal18
using Microsoft.Dynamics.Nav.Types;     // NavType, DataError

/// <summary>
/// In-memory record store replacing INavRecordHandle + NavRecordHandle.
/// Each instance tracks field values for a single record cursor.
/// A static dictionary acts as the "database" for all tables.
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

    public bool ALFind(DataError errorLevel, string searchMethod = "-")
    {
        var table = _tables[_tableId];
        if (table.Count == 0)
        {
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = table;
        _cursorPosition = 0;
        _fields = new Dictionary<int, NavValue>(table[0]);
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
        var table = _tables[_tableId];
        if (table.Count == 0)
        {
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = table;
        _cursorPosition = table.Count - 1;
        _fields = new Dictionary<int, NavValue>(table[^1]);
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
        _tables[_tableId] = new List<Dictionary<int, NavValue>>();
    }

    public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        // Simplified for PoC: no filtering
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        ALSetRange(fieldNo, expectedType, value, value);
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        ALSetRange(fieldNo, expectedType, fromValue, toValue);
    }

    public void ALReset()
    {
        _fields = new Dictionary<int, NavValue>();
        _cursorPosition = -1;
        _currentResultSet = null;
    }

    public int ALCount => _tables[_tableId].Count;

    public void ALSetCurrentKey(params int[] fieldNos)
    {
        // Simplified: no sorting for PoC
    }

    public void ALSetAscending(int fieldNo, bool ascending)
    {
        // Simplified: no sorting for PoC
    }

    public int ALFieldNo(string fieldName)
    {
        // Simplified: return 0 (unknown) — real impl would look up field metadata
        return 0;
    }

    public bool ALIsEmpty()
    {
        return _tables[_tableId].Count == 0;
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
