using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

static (HashSet<string> Types, string[] References, string Version) Inspect(string path, bool merged)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var types = new HashSet<string>();
    foreach (var handle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(handle);
        var ns = metadata.GetString(type.Namespace);
        types.Add(ns + "." + metadata.GetString(type.Name));
        if (merged && ns.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) &&
            (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
            throw new InvalidDataException("Public Json.NET type: " + metadata.GetString(type.Name));
    }
    var references = metadata.AssemblyReferences.Select(h => {
        var reference = metadata.GetAssemblyReference(h);
        return metadata.GetString(reference.Name) + ", Version=" + reference.Version;
    }).Order().ToArray();
    if (merged && references.Any(r => r.StartsWith("Newtonsoft.Json,")))
        throw new InvalidDataException("External Newtonsoft.Json reference remains");
    return (types, references, metadata.GetAssemblyDefinition().Version.ToString());
}

if (args.Length != 3) throw new ArgumentException("Expected merged DLL, original DLL, Json.NET net35 DLL");
var merged = Inspect(args[0], true);
var original = Inspect(args[1], false);
var json = Inspect(args[2], false);
if (!merged.Types.Contains("Newtonsoft.Json.JsonWriter") || !merged.Types.Contains("Newtonsoft.Json.Linq.JObject"))
    throw new InvalidDataException("Private Json.NET missing");
// A strict type union prevents accidentally merging a game or another mod DLL.
original.Types.UnionWith(json.Types);
if (!merged.Types.SetEquals(original.Types))
    throw new InvalidDataException("Merged types differ from the mod + Json.NET allowlist");
if (json.Version != "13.0.0.0" || !json.References.Contains("mscorlib, Version=2.0.0.0"))
    throw new InvalidDataException("Expected Json.NET 13 net35");
var jsonFrameworks = new HashSet<string> { "mscorlib", "System", "System.Core", "System.Xml" };
if (json.References.Any(r => !jsonFrameworks.Contains(r.Split(',')[0])))
    throw new InvalidDataException("Json.NET contains unsupported framework dependencies; build JsonNet.CS1.csproj");
var forbidden = new HashSet<string> { "Newtonsoft.Json", "System.Runtime.Serialization", "System.Data", "System.Xml.Linq" };
if (merged.References.Any(r => forbidden.Contains(r.Split(',')[0])))
    throw new InvalidDataException("Private CS1 mod still depends on optional Json.NET framework assemblies");
var result = new {
    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    assemblyVersion = merged.Version, jsonNet = "13.0.4", jsonProfile = "cs1-net35", ilRepack = "2.0.48",
    jsonNetSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))),
    references = merged.References, jsonTypesInternal = true, onlyModAndJsonNet = true
};
var manifest = Path.ChangeExtension(args[0], ".verified.json");
File.WriteAllText(manifest, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Verified private CS1 mod: " + manifest);
