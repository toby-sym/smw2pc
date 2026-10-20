using System.Runtime.InteropServices;

namespace Smw2Pc.Core.Machine;

// Explicit field transfers keep save states independent of reflection and object layout.
internal sealed class StateBuffer(Stream stream, bool reading)
{
    public bool Reading => reading;

    public T Value<T>(T value) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        if (reading) stream.ReadExactly(bytes);
        else stream.Write(bytes);
        return value;
    }

    public void Values<T>(T[] values) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        if (reading) stream.ReadExactly(bytes);
        else stream.Write(bytes);
    }

    public void Expect<T>(T expected) where T : unmanaged
    {
        if (!EqualityComparer<T>.Default.Equals(expected, Value(expected)))
            throw new InvalidDataException("The save state hardware configuration is incompatible.");
    }
}
