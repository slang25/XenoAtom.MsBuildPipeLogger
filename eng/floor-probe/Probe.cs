// MSBuild support-floor probe.
//
// XenoAtom.MsBuildPipeLogger's rearchitecture reflects into MSBuild's own internal
// binlog serializer instead of bundling a copy of it. Concretely, the logger requires
// (see BuildEventArgsWriterProxy.cs):
//
//   * type   Microsoft.Build.Logging.BuildEventArgsWriter
//   * ctor   .ctor(System.IO.BinaryWriter)
//   * method void Write(Microsoft.Build.Framework.BuildEventArgs)
//
// and it locates that assembly via typeof(Microsoft.Build.Logging.BinaryLogger).Assembly.
// That surface first shipped in MSBuild 15.3 (VS 2017 15.3 / .NET SDK 2.0). Anything older
// cannot host the logger. This probe downloads a matrix of real Microsoft.Build packages,
// inspects the metadata of each Microsoft.Build.dll (no execution, fully cross-platform),
// prints a support table, and FAILS if the observed floor ever regresses.

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;

// (version, expectedSupported). The floor is MSBuild 15.3.
(string Version, bool ExpectedSupported)[] matrix =
[
    ("14.3.0",   false), // VS 2015 Update 3
    ("15.1.548", false), // VS 2017 RTM (15.0/15.1) - before binlog
    ("15.3.409", true),  // VS 2017 15.3 - binlog / BuildEventArgsWriter introduced  <-- floor
    ("15.9.20",  true),  // VS 2017 15.9 (final 2017)
    ("16.11.0",  true),  // VS 2019 16.11
    ("17.0.0",   true),  // VS 2022 17.0
    ("17.14.8",  true),  // VS 2022 17.14 (what GitHub windows runners ship)
    ("18.4.0",   true),  // current standalone MSBuild (net10 era) referenced by this repo
];

var workDir = Path.Combine(Path.GetTempPath(), "msbuild-floor-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
using var http = new HttpClient();

Console.WriteLine($"{"MSBuild",-12} {"BinaryLogger",-13} {"BEAWriter",-10} {"ctor(BinaryWriter)",-19} {"void Write(BEArgs)",-19} {"Verdict",-12} Expected");
Console.WriteLine(new string('-', 100));

var rows = new List<(string Version, bool Supported, bool Expected)>();
bool regression = false;

foreach (var (version, expected) in matrix)
{
    var dll = await DownloadBuildAssemblyAsync(http, version, workDir);
    var (hasBinaryLogger, hasType, hasCtor, hasWrite) = Inspect(dll);
    bool supported = hasBinaryLogger && hasType && hasCtor && hasWrite;
    rows.Add((version, supported, expected));
    if (supported != expected) regression = true;

    string verdict = supported ? "SUPPORTED" : "UNSUPPORTED";
    string flag = supported == expected ? "ok" : "*** MISMATCH ***";
    Console.WriteLine($"{version,-12} {YN(hasBinaryLogger),-13} {YN(hasType),-10} {YN(hasCtor),-19} {YN(hasWrite),-19} {verdict,-12} {(expected ? "SUPPORTED" : "UNSUPPORTED")} {flag}");
}

Console.WriteLine();
int floor = rows.Where(r => r.Supported).Select(r => Major(r.Version) * 1000 + Minor(r.Version)).DefaultIfEmpty(int.MaxValue).Min();
var firstSupported = rows.Where(r => r.Supported).OrderBy(r => Major(r.Version) * 1000 + Minor(r.Version)).FirstOrDefault();
Console.WriteLine($"Observed support floor: MSBuild {firstSupported.Version} and newer.");

WriteStepSummary(rows, firstSupported.Version);

if (regression)
{
    Console.Error.WriteLine("FAILED: observed MSBuild support differs from the expected floor. See the *** MISMATCH *** rows above.");
    return 1;
}

Console.WriteLine("PASSED: MSBuild support floor matches expectation (>= 15.3).");
return 0;

static string YN(bool b) => b ? "yes" : "NO";
static int Major(string v) => int.Parse(v.Split('.')[0]);
static int Minor(string v) => int.Parse(v.Split('.')[1]);

static async Task<string> DownloadBuildAssemblyAsync(HttpClient http, string version, string workDir)
{
    var nupkg = Path.Combine(workDir, $"microsoft.build.{version}.nupkg");
    var url = $"https://api.nuget.org/v3-flatcontainer/microsoft.build/{version}/microsoft.build.{version}.nupkg";

    // nuget.org occasionally drops a connection mid-stream; retry so the guard isn't flaky.
    const int attempts = 4;
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(nupkg, bytes);
            break;
        }
        catch (Exception ex) when (attempt < attempts)
        {
            Console.Error.WriteLine($"  download {version} attempt {attempt} failed ({ex.GetType().Name}); retrying...");
            await Task.Delay(2000 * attempt);
        }
    }

    using var archive = ZipFile.OpenRead(nupkg);
    // Any TFM's Microsoft.Build.dll exposes the same type shape; take the first one found.
    var entry = archive.Entries.First(e =>
        e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
        e.Name.Equals("Microsoft.Build.dll", StringComparison.OrdinalIgnoreCase));
    var dllPath = Path.Combine(workDir, $"Microsoft.Build.{version}.dll");
    entry.ExtractToFile(dllPath, overwrite: true);
    return dllPath;
}

static (bool BinaryLogger, bool Type, bool Ctor, bool Write) Inspect(string dllPath)
{
    using var fs = File.OpenRead(dllPath);
    using var pe = new PEReader(fs);
    var mr = pe.GetMetadataReader();
    var provider = new NameProvider();

    bool hasBinaryLogger = FindType(mr, "Microsoft.Build.Logging", "BinaryLogger") is not null;
    var writerType = FindType(mr, "Microsoft.Build.Logging", "BuildEventArgsWriter");

    bool hasCtor = false, hasWrite = false;
    if (writerType is TypeDefinition td)
    {
        foreach (var mh in td.GetMethods())
        {
            var md = mr.GetMethodDefinition(mh);
            string name = mr.GetString(md.Name);
            bool isInstance = (md.Attributes & MethodAttributes.Static) == 0;
            var sig = md.DecodeSignature(provider, null);

            if (name == ".ctor" && isInstance
                && sig.ParameterTypes.Length == 1
                && sig.ParameterTypes[0] == "System.IO.BinaryWriter")
            {
                hasCtor = true;
            }

            if (name == "Write" && isInstance
                && sig.ReturnType == "System.Void"
                && sig.ParameterTypes.Length == 1
                && sig.ParameterTypes[0] == "Microsoft.Build.Framework.BuildEventArgs")
            {
                hasWrite = true;
            }
        }
    }

    return (hasBinaryLogger, writerType is not null, hasCtor, hasWrite);
}

static TypeDefinition? FindType(MetadataReader mr, string ns, string name)
{
    foreach (var handle in mr.TypeDefinitions)
    {
        var td = mr.GetTypeDefinition(handle);
        if (mr.GetString(td.Name) == name && mr.GetString(td.Namespace) == ns)
            return td;
    }
    return null;
}

static void WriteStepSummary(List<(string Version, bool Supported, bool Expected)> rows, string floor)
{
    var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrEmpty(path)) return;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("### MSBuild support-floor probe");
    sb.AppendLine();
    sb.AppendLine($"Observed floor: **MSBuild {floor} and newer**. The rearchitecture reflects into MSBuild's internal `BuildEventArgsWriter`, which first shipped in MSBuild 15.3.");
    sb.AppendLine();
    sb.AppendLine("| MSBuild | Verdict |");
    sb.AppendLine("|---|---|");
    foreach (var r in rows)
        sb.AppendLine($"| {r.Version} | {(r.Supported ? "✅ supported" : "❌ unsupported")} |");
    File.AppendAllText(path, sb.ToString());
}

// Signature type provider that yields readable full type names (no assembly resolution).
sealed class NameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => c.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader mr, TypeDefinitionHandle h, byte rawTypeKind)
    {
        var td = mr.GetTypeDefinition(h);
        var ns = mr.GetString(td.Namespace);
        var n = mr.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromReference(MetadataReader mr, TypeReferenceHandle h, byte rawTypeKind)
    {
        var tr = mr.GetTypeReference(h);
        var ns = mr.GetString(tr.Namespace);
        var n = mr.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? gc, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? gc, int i) => "!" + i;
    public string GetModifiedType(string mod, string un, bool req) => un;
    public string GetPinnedType(string e) => e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetTypeFromSpecification(MetadataReader mr, object? gc, TypeSpecificationHandle h, byte rawTypeKind) => "spec";
}
