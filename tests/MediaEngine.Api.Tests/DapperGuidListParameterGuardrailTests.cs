using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Dapper expands a list parameter (<c>IN @ids</c>) element by element and skips the registered Guid
/// handler, so each id binds in .NET byte order instead of the big-endian BLOB the data store holds and
/// the query silently matches nothing. Every Guid list handed to Dapper must be converted first with
/// <c>.Select(GuidSql.ToBlob).ToArray()</c>.
/// </summary>
/// <remarks>
/// The check reads the compiled Engine assemblies rather than the source text: every anonymous object the
/// compiler built is a generic instantiation whose type arguments are the members' real static types, so a
/// Guid collection member is found exactly, whatever the variable is called or how it was built. Objects
/// handed straight to <c>JsonSerializer</c> are skipped; any other non-Dapper use goes in
/// <c>DapperGuidListParameterGuardrailAllowlist.txt</c> with a reason.
/// </remarks>
public sealed partial class DapperGuidListParameterGuardrailTests
{
    private static readonly IReadOnlyDictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value), opCode => opCode.OperandType);

    private static readonly HashSet<ushort> CallOpCodes =
    [
        unchecked((ushort)OpCodes.Call.Value),
        unchecked((ushort)OpCodes.Callvirt.Value),
        unchecked((ushort)OpCodes.Newobj.Value),
    ];

    private static readonly HashSet<string> CollectionTypeNames = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableList`1",
        "System.Collections.Immutable.ImmutableHashSet`1",
    };

    [Fact]
    public void DapperParameterObjects_NeverBindAGuidCollection()
    {
        var assemblies = EngineAssemblyPaths();
        Assert.Contains(assemblies, path => Path.GetFileName(path) == "MediaEngine.Api.dll");
        Assert.Contains(assemblies, path => Path.GetFileName(path) == "MediaEngine.Storage.dll");
        var allowlist = ReadAllowlist();

        var found = assemblies.SelectMany(FindGuidCollectionMembers).ToList();
        var offenders = found
            .Where(member => !allowlist.Contains(member.Key))
            .Select(member => member.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var staleEntries = allowlist
            .Where(entry => !found.Any(member => member.Key == entry))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Dapper binds these Guid lists without the Guid handler, so they match no rows. "
            + "Convert each with .Select(GuidSql.ToBlob).ToArray():"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
        Assert.True(
            staleEntries.Count == 0,
            "Remove these allowlist entries; the code no longer builds them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, staleEntries));
    }

    [Fact]
    public void Guardrail_FindsAGuidListInAnAnonymousParameterObject()
    {
        var found = FindGuidCollectionMembers(typeof(DapperGuidListParameterGuardrailTests).Assembly.Location)
            .Select(member => member.Key)
            .ToList();

        Assert.Contains($"{typeof(DapperGuidListParameterGuardrailTests).FullName}.{nameof(KnownBadParameterObject)}:workIds", found);
        Assert.Contains($"{typeof(DapperGuidListParameterGuardrailTests).FullName}.{nameof(KnownBadAsyncParameterObject)}:ids", found);
        Assert.DoesNotContain(found, key => key.Contains(nameof(KnownGoodParameterObject), StringComparison.Ordinal));
        Assert.DoesNotContain(found, key => key.Contains(nameof(KnownJsonObject), StringComparison.Ordinal));
    }

    // Fixtures for the self-check above.
    private static object KnownBadParameterObject(IReadOnlyList<Guid> workIds) => new { like = "%", workIds };

    private static async Task<object> KnownBadAsyncParameterObject(HashSet<Guid> ids)
    {
        await Task.Yield();
        return new { ids };
    }

    private static object KnownGoodParameterObject(IReadOnlyList<Guid> workIds) =>
        new { like = "%", workIds = workIds.Select(id => id.ToByteArray()).ToArray() };

    private static string KnownJsonObject(IReadOnlyList<Guid> workIds) =>
        System.Text.Json.JsonSerializer.Serialize(new { workIds });

    private static IReadOnlyList<string> EngineAssemblyPaths() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "MediaEngine.*.dll")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return !name.EndsWith(".Tests", StringComparison.Ordinal)
                    && !name.Equals("MediaEngine.TestSupport", StringComparison.Ordinal);
            })
            .ToList();

    private static HashSet<string> ReadAllowlist()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        var path = Path.Combine(repoRoot, "tests", "MediaEngine.Api.Tests", "DapperGuidListParameterGuardrailAllowlist.txt");
        Assert.True(File.Exists(path), "Missing Dapper Guid list guardrail allowlist.");

        return File.ReadAllLines(path)
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<GuidCollectionMember> FindGuidCollectionMembers(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var provider = new TypeShapeProvider();
        var found = new List<GuidCollectionMember>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var calls = CallTokens(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader());
                for (var index = 0; index < calls.Count; index++)
                {
                    if (calls[index].Kind != HandleKind.MemberReference)
                    {
                        continue;
                    }

                    var constructor = reader.GetMemberReference((MemberReferenceHandle)calls[index]);
                    if (constructor.Parent.Kind != HandleKind.TypeSpecification
                        || reader.GetString(constructor.Name) != ".ctor")
                    {
                        continue;
                    }

                    var instantiation = reader.GetTypeSpecification((TypeSpecificationHandle)constructor.Parent)
                        .DecodeSignature(provider, genericContext: null);
                    if (instantiation.Definition.IsNil
                        || !instantiation.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                        || (index + 1 < calls.Count && IsJsonSerializerCall(reader, calls[index + 1])))
                    {
                        continue;
                    }

                    var memberNames = reader.GetTypeDefinition(instantiation.Definition)
                        .GetGenericParameters()
                        .Select(parameter => CompilerName(reader.GetString(reader.GetGenericParameter(parameter).Name)))
                        .ToList();
                    for (var member = 0; member < instantiation.Arguments.Length && member < memberNames.Count; member++)
                    {
                        if (IsGuidCollection(instantiation.Arguments[member]))
                        {
                            found.Add(new GuidCollectionMember(
                                SourceMethodName(reader, typeHandle, reader.GetString(method.Name)),
                                memberNames[member],
                                instantiation.Arguments[member].ToString()));
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The method tokens of every call, callvirt and newobj in the body, in order.</summary>
    private static List<EntityHandle> CallTokens(BlobReader il)
    {
        var tokens = new List<EntityHandle>();
        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();
            if (code == 0xFE)
            {
                code = (ushort)(0xFE00 | il.ReadByte());
            }

            if (CallOpCodes.Contains(code))
            {
                tokens.Add(MetadataTokens.EntityHandle(il.ReadInt32()));
                continue;
            }

            switch (OperandTypes[code])
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var targetCount = il.ReadInt32();
                    il.Offset += 4 * targetCount;
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }

        return tokens;
    }

    private static bool IsJsonSerializerCall(MetadataReader reader, EntityHandle token)
    {
        if (token.Kind == HandleKind.MethodSpecification)
        {
            token = reader.GetMethodSpecification((MethodSpecificationHandle)token).Method;
        }

        if (token.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        var parent = reader.GetMemberReference((MemberReferenceHandle)token).Parent;
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.GetString(type.Namespace) == "System.Text.Json"
            && reader.GetString(type.Name) == "JsonSerializer";
    }

    private static bool IsGuid(TypeShape type) =>
        type.Name == "System.Guid"
        || (type.Name == "System.Nullable`1" && type.Arguments.Length == 1 && IsGuid(type.Arguments[0]));

    private static bool IsGuidCollection(TypeShape type) =>
        type.Arguments.Length == 1
        && IsGuid(type.Arguments[0])
        && (type.Name == TypeShape.ArrayName || CollectionTypeNames.Contains(type.Name));

    /// <summary>
    /// The source-level type and method a compiled method came from, so lambdas, async state machines and
    /// closures report (and are allowlisted) as <c>Namespace.Type.Method</c> rather than compiler names.
    /// </summary>
    private static string SourceMethodName(MetadataReader reader, TypeDefinitionHandle typeHandle, string methodName)
    {
        var userMethod = CompilerName(methodName);
        var handle = typeHandle;
        while (true)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            if (!name.StartsWith('<'))
            {
                return $"{FullName(reader, handle)}.{userMethod}";
            }

            // An async or iterator state machine is named after the method it implements.
            var stateMachine = StateMachineName().Match(name);
            if (stateMachine.Success)
            {
                userMethod = stateMachine.Groups[1].Value;
            }

            handle = type.GetDeclaringType();
        }
    }

    private static string CompilerName(string name)
    {
        var match = CompilerGeneratedName().Match(name);
        return match.Success ? match.Groups[1].Value : name;
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullName(reader, declaring)}+{name}";
        }

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    // "<workIds>j__TPar" (anonymous member) or "<SearchAsync>b__0" (lambda) → the source name.
    [GeneratedRegex(@"^<([^>]+)>")]
    private static partial Regex CompilerGeneratedName();

    [GeneratedRegex(@"^<([^>]+)>d__")]
    private static partial Regex StateMachineName();

    private sealed record GuidCollectionMember(string Method, string Member, string Type)
    {
        public string Key => $"{Method}:{Member}";

        public override string ToString() => $"{Key} binds {Type}";
    }

    private sealed record TypeShape(string Name, ImmutableArray<TypeShape> Arguments, TypeDefinitionHandle Definition)
    {
        public const string ArrayName = "[]";

        public static TypeShape Named(string name) => new(name, [], default);

        public override string ToString() => Name == ArrayName
            ? $"{Arguments[0]}[]"
            : Arguments.IsEmpty ? Name : $"{Name}<{string.Join(", ", Arguments)}>";
    }

    private sealed class TypeShapeProvider : ISignatureTypeProvider<TypeShape, object?>
    {
        public TypeShape GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeShape.Named($"System.{typeCode}");

        public TypeShape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(FullName(reader, handle), [], handle);

        public TypeShape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var ns = reader.GetString(reference.Namespace);
            var name = reader.GetString(reference.Name);
            return TypeShape.Named(ns.Length == 0 ? name : $"{ns}.{name}");
        }

        public TypeShape GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public TypeShape GetSZArrayType(TypeShape elementType) => new(TypeShape.ArrayName, [elementType], default);

        public TypeShape GetArrayType(TypeShape elementType, ArrayShape shape) => TypeShape.Named($"{elementType}[{shape.Rank}]");

        public TypeShape GetGenericInstantiation(TypeShape genericType, ImmutableArray<TypeShape> typeArguments) =>
            genericType with { Arguments = typeArguments };

        public TypeShape GetByReferenceType(TypeShape elementType) => TypeShape.Named($"{elementType}&");

        public TypeShape GetPointerType(TypeShape elementType) => TypeShape.Named($"{elementType}*");

        public TypeShape GetPinnedType(TypeShape elementType) => elementType;

        public TypeShape GetModifiedType(TypeShape modifier, TypeShape unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeShape GetFunctionPointerType(MethodSignature<TypeShape> signature) => TypeShape.Named("fnptr");

        public TypeShape GetGenericMethodParameter(object? genericContext, int index) => TypeShape.Named($"!!{index}");

        public TypeShape GetGenericTypeParameter(object? genericContext, int index) => TypeShape.Named($"!{index}");
    }
}
