using System.Reflection;

namespace MediaEngine.Api.Tests;

/// <summary>A hand-written stand-in for a large interface: answers only the methods a test names and fails loudly on any other call.</summary>
internal class MethodStub : DispatchProxy
{
    private Dictionary<string, Func<object?[], object?>> _answers = [];

    public static T Create<T>(params (string Method, Func<object?[], object?> Answer)[] answers)
        where T : class
    {
        var proxy = Create<T, MethodStub>();
        ((MethodStub)(object)proxy)._answers = answers.ToDictionary(entry => entry.Method, entry => entry.Answer);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer)
            ? answer(args ?? [])
            : throw new NotSupportedException($"{targetMethod?.Name} was not expected in this test.");
}
