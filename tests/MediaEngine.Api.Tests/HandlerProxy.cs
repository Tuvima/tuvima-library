using System.Reflection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A hand-written stand-in for an interface: each method that a test cares about is given an answer, and any other call fails
/// the test. Used where a real implementation would need a whole data store to answer one question.
/// </summary>
public class HandlerProxy : DispatchProxy
{
    private Dictionary<string, Func<object?[], object?>> _handlers = new(StringComparer.Ordinal);

    public static T Create<T>(Dictionary<string, Func<object?[], object?>> handlers)
        where T : class
    {
        var proxy = Create<T, HandlerProxy>();
        ((HandlerProxy)(object)proxy)._handlers = handlers;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not null && _handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args ?? []);
        }

        throw new InvalidOperationException($"The test did not expect a call to {targetMethod?.Name}.");
    }
}
