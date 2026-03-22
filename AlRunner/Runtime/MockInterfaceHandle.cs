namespace AlRunner.Runtime;

/// <summary>
/// Lightweight replacement for NavInterfaceHandle.
/// In the BC runtime, NavInterfaceHandle wraps an ITreeObject to represent
/// AL interface references. For standalone execution, we just store the object.
/// </summary>
public class MockInterfaceHandle
{
    private object? _implementation;

    public MockInterfaceHandle()
    {
    }

    /// <summary>
    /// Assigns an interface implementation (codeunit) to this handle.
    /// In BC, ALAssign wraps the codeunit as an interface implementation.
    /// </summary>
    public void ALAssign(object? implementation)
    {
        _implementation = implementation;
    }

    public void Clear()
    {
        _implementation = null;
    }
}
