extern alias pbnet;
using Google.Protobuf;
using Raf.Client.Core.Grpc;
using Raf.Domain.Requests;
using PbFileSystem = pbnet::Google.Protobuf.Reflection.IFileSystem;
using PbSet = pbnet::Google.Protobuf.Reflection.FileDescriptorSet;

namespace Raf.Client.Grpc;

/// <summary>
/// Compila un conjunto de .proto (Plan 10, etapa 2) con el parser administrado de protobuf-net, sin protoc ni código
/// nativo, y lo lleva al mismo <see cref="DescriptorSet"/> que arma el server reflection. Los <c>import</c> se buscan
/// dentro del conjunto; los de <c>google/protobuf</c> vienen con la librería.
/// </summary>
internal static class ProtoCompiler
{
    /// <summary>El conjunto compilado, o los errores con archivo, línea y columna.</summary>
    public static (DescriptorSet? Set, IReadOnlyList<string> Errors) Compile(IReadOnlyList<ProtoFile> files)
    {
        if (files.Count == 0) return (null, [CoreTexts.Grpc_ProtoEmpty]);

        // La raíz del conjunto es la única ruta de import: "acme/users.proto" se busca tal cual entre los archivos.
        var set = new PbSet { FileSystem = new MemoryFileSystem(files) };
        set.AddImportPath("");
        foreach (var file in files) set.Add(file.Path, includeInOutput: true, new StringReader(file.Content));
        set.Process();

        var errors = set.GetErrors()
            .Where(e => e.IsError)
            .Select(e => $"{e.File}:{e.LineNumber}:{e.ColumnNumber}: {e.Message}")
            .Distinct()
            .ToList();
        if (errors.Count > 0) return (null, errors);

        using var stream = new MemoryStream();
        set.Serialize(ProtoBuf.Meta.RuntimeTypeModel.Default, stream, includeImports: true);
        var compiled = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(stream.ToArray());
        try
        {
            return (DescriptorSet.Build(compiled.File.Select(f => f.ToByteString())), []);
        }
        catch (GrpcEngineException e)
        {
            return (null, [e.Message]);
        }
    }

    /// <summary>Los archivos del conjunto, para que protobuf-net resuelva los imports sin tocar el disco.</summary>
    private sealed class MemoryFileSystem(IReadOnlyList<ProtoFile> files) : PbFileSystem
    {
        private readonly Dictionary<string, string> _files = files.ToDictionary(f => Normalize(f.Path), f => f.Content, StringComparer.Ordinal);

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

        public bool Exists(string path) => _files.ContainsKey(Normalize(path));

        public TextReader OpenText(string path) => new StringReader(_files[Normalize(path)]);
    }
}
