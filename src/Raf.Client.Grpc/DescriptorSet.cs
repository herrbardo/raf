using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc;

/// <summary>
/// Los descriptores de un servidor: sus servicios, métodos y tipos de mensaje. Se arma con los
/// <c>FileDescriptorProto</c> que mandó el server reflection (o, en la etapa 2, con los .proto compilados).
/// </summary>
internal sealed class DescriptorSet
{
    /// <summary>Los .proto de Google que vienen con Google.Protobuf: si el servidor no los manda, se usan estos.</summary>
    private static readonly Dictionary<string, ByteString> BuiltIn = new[]
    {
        AnyReflection.Descriptor, ApiReflection.Descriptor, DurationReflection.Descriptor, EmptyReflection.Descriptor,
        FieldMaskReflection.Descriptor, SourceContextReflection.Descriptor, StructReflection.Descriptor,
        TimestampReflection.Descriptor, TypeReflection.Descriptor, WrappersReflection.Descriptor, DescriptorReflection.Descriptor,
    }.ToDictionary(f => f.Name, f => f.SerializedData);

    private readonly Dictionary<string, MessageDescriptor> _messages;
    private readonly Dictionary<string, MethodDescriptor> _methods;

    private DescriptorSet(IReadOnlyList<FileDescriptor> files)
    {
        _messages = files.SelectMany(f => f.MessageTypes).SelectMany(Nested)
            .GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.First());
        _methods = files.SelectMany(f => f.Services)
            .Where(s => !s.FullName.StartsWith("grpc.reflection.", StringComparison.Ordinal))
            .SelectMany(s => s.Methods)
            .GroupBy(m => $"{m.Service.FullName}/{m.Name}").ToDictionary(g => g.Key, g => g.First());
        Methods = _methods.Values
            .OrderBy(m => m.Service.FullName, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Select(Describe).ToList();
    }

    private static IEnumerable<MessageDescriptor> Nested(MessageDescriptor type) => type.NestedTypes.SelectMany(Nested).Prepend(type);

    public IReadOnlyList<GrpcMethodInfo> Methods { get; }

    public MethodDescriptor? FindMethod(string service, string method) => _methods.GetValueOrDefault($"{service}/{method}");

    public MessageDescriptor? FindMessage(string fullName) => _messages.GetValueOrDefault(fullName.TrimStart('.'));

    /// <summary>
    /// Arma el conjunto con los archivos recibidos, en cualquier orden. Las dependencias que falten y sean de
    /// Google se completan; si falta otra, se avisa con su nombre.
    /// </summary>
    public static DescriptorSet Build(IEnumerable<ByteString> files)
    {
        var byName = new Dictionary<string, (ByteString Data, FileDescriptorProto Proto)>();
        foreach (var data in files)
        {
            var proto = FileDescriptorProto.Parser.ParseFrom(data);
            byName.TryAdd(proto.Name, (data, proto));
        }

        // Orden de dependencias: cada archivo después de los que importa.
        var ordered = new List<ByteString>();
        var visited = new HashSet<string>();
        void Visit(string name)
        {
            if (!visited.Add(name)) return;
            if (byName.TryGetValue(name, out var file))
            {
                foreach (var dependency in file.Proto.Dependency) Visit(dependency);
                ordered.Add(file.Data);
            }
            else if (BuiltIn.TryGetValue(name, out var builtIn))
            {
                var proto = FileDescriptorProto.Parser.ParseFrom(builtIn);
                foreach (var dependency in proto.Dependency) Visit(dependency);
                ordered.Add(builtIn);
            }
            else
            {
                throw new GrpcEngineException(string.Format(CoreTexts.Grpc_UnknownType, name));
            }
        }
        foreach (var name in byName.Keys.ToList()) Visit(name);

        try
        {
            return new DescriptorSet(FileDescriptor.BuildFromByteStrings(ordered));
        }
        catch (Exception e) when (e is DescriptorValidationException or ArgumentException or InvalidProtocolBufferException)
        {
            throw new GrpcEngineException(e.Message, e);
        }
    }

    /// <summary>Los archivos que importa alguno de los recibidos y no están entre ellos ni entre los de Google.</summary>
    public static IEnumerable<string> MissingDependencies(IEnumerable<ByteString> files)
    {
        var protos = files.Select(f => FileDescriptorProto.Parser.ParseFrom(f)).ToList();
        var known = protos.Select(p => p.Name).ToHashSet();
        return protos.SelectMany(p => p.Dependency).Where(d => !known.Contains(d) && !BuiltIn.ContainsKey(d)).Distinct();
    }

    private static GrpcMethodInfo Describe(MethodDescriptor method)
    {
        var kind = (method.IsClientStreaming, method.IsServerStreaming) switch
        {
            (false, false) => GrpcMethodKind.Unary,
            (false, true) => GrpcMethodKind.ServerStreaming,
            (true, false) => GrpcMethodKind.ClientStreaming,
            _ => GrpcMethodKind.DuplexStreaming,
        };
        return new GrpcMethodInfo(method.Service.FullName, method.Name, kind,
            method.InputType.FullName, method.OutputType.FullName, ProtoJson.Template(method.InputType));
    }
}
