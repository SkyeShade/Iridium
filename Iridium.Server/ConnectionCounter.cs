namespace Iridium.Server;

public sealed class ConnectionCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Connected() => Interlocked.Increment(ref _count);

    public void Disconnected() => Interlocked.Decrement(ref _count);
}
