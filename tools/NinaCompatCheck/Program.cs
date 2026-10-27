// Binary compatibility check for the shipped plugin build.
//
// The plugin ships one build, compiled against NINA 3.2 (.NET 8), and NINA 3.3 (.NET 10) loads it
// too. Compiling the same sources against 3.3 catches source breaks, not binary ones (a method that
// gained an optional parameter still compiles, but the 3.2 build's call no longer binds). This tool
// resolves every NINA type and member the built DLL references against another NINA build's
// assemblies, signatures included, and fails if one is missing.
//
// Usage: NinaCompatCheck <plugin.dll> <dir with the target NINA's assemblies and their dependencies> [more dirs…]
// The .NET runtime (and Windows Desktop runtime, when installed) of this process is added automatically.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2) { Console.Error.WriteLine("usage: NinaCompatCheck <plugin.dll> <nina dir> [more dirs…]"); return 2; }
var plugin = args[0];
var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var dirs = args.Skip(1).ToList();
dirs.Add(runtimeDir);
// …/shared/Microsoft.NETCore.App/<v>/ → …/shared/Microsoft.WindowsDesktop.App/<v>/ (Windows only).
var desktop = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar)))!,
                           "Microsoft.WindowsDesktop.App");
if (Directory.Exists(desktop)) dirs.AddRange(Directory.GetDirectories(desktop));
var paths = dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.dll")).ToList();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Runtime");
using var fs = File.OpenRead(plugin); using var pe = new PEReader(fs); var md = pe.GetMetadataReader();
var prov = new NameProvider(md);
int checkedTypes = 0, checkedMembers = 0; var problems = new List<string>();

string ScopeAsm(TypeReferenceHandle h) {
  var tr = md.GetTypeReference(h);
  return tr.ResolutionScope.Kind switch {
    HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name),
    HandleKind.TypeReference => ScopeAsm((TypeReferenceHandle)tr.ResolutionScope),
    _ => null };
}
string FullName(TypeReferenceHandle h) {
  var tr = md.GetTypeReference(h);
  var name = md.GetString(tr.Name); var ns = md.GetString(tr.Namespace);
  if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return FullName((TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
  return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}
Type Resolve(TypeReferenceHandle h) {
  var asm = ScopeAsm(h); var a = mlc.LoadFromAssemblyName(asm);
  return a.GetType(FullName(h), throwOnError: false);
}
bool IsNina(TypeReferenceHandle h) => (ScopeAsm(h) ?? "").StartsWith("NINA");

foreach (var h in md.TypeReferences) {
  if (!IsNina(h)) continue; checkedTypes++;
  if (Resolve(h) == null) problems.Add($"missing type {FullName(h)} in {ScopeAsm(h)}");
}
foreach (var mh in md.MemberReferences) {
  var mr = md.GetMemberReference(mh);
  TypeReferenceHandle parent;
  if (mr.Parent.Kind == HandleKind.TypeReference) parent = (TypeReferenceHandle)mr.Parent;
  else if (mr.Parent.Kind == HandleKind.TypeSpecification) {
    var ts = md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
    var br = md.GetBlobReader(ts.Signature);
    if (br.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) continue;
    br.ReadCompressedInteger();
    var hh = br.ReadTypeHandle(); if (hh.Kind != HandleKind.TypeReference) continue; parent = (TypeReferenceHandle)hh;
  } else continue;
  if (!IsNina(parent)) continue;
  var t = Resolve(parent); if (t == null) continue; checkedMembers++;
  var name = md.GetString(mr.Name);
  var flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  if (mr.GetKind() == MemberReferenceKind.Field) {
    if (t.GetField(name, flags) == null) problems.Add($"missing field {t.FullName}.{name}");
    continue;
  }
  var sig = mr.DecodeMethodSignature(prov, null);
  var want = string.Join(",", sig.ParameterTypes);
  IEnumerable<MethodBase> cands = name == ".ctor" ? t.GetConstructors(flags) : AllMethods(t, flags).Where(m => m.Name == name);
  var found = cands.Any(m => string.Join(",", m.GetParameters().Select(p => Norm(p.ParameterType))) == want
                         && (m is not MethodInfo mi || Norm(mi.ReturnType) == sig.ReturnType || sig.ReturnType.StartsWith("!")));
  if (!found) problems.Add($"missing {t.FullName}::{name}({want}) : {sig.ReturnType}  — candidates: " +
      string.Join(" | ", cands.Select(m => "(" + string.Join(",", m.GetParameters().Select(p => Norm(p.ParameterType))) + ")")));
}
Console.WriteLine($"checked {checkedTypes} NINA types, {checkedMembers} NINA member references");
foreach (var p in problems) Console.WriteLine("PROBLEM " + p);
return problems.Count == 0 ? 0 : 1;

static IEnumerable<MethodInfo> AllMethods(Type t, BindingFlags f) {
  IEnumerable<MethodInfo> ms = t.GetMethods(f);
  if (t.IsInterface) foreach (var i in t.GetInterfaces()) ms = ms.Concat(i.GetMethods(f));
  return ms;
}
static string Norm(Type t) {
  if (t.IsGenericParameter) return "!" + t.GenericParameterPosition;
  if (t.IsByRef) return Norm(t.GetElementType()) + "&";
  if (t.IsArray) return Norm(t.GetElementType()) + "[]";
  if (t.IsGenericType) return (t.GetGenericTypeDefinition().FullName ?? t.Name) + "<" + string.Join(",", t.GetGenericArguments().Select(Norm)) + ">";
  return t.FullName ?? t.Name;
}

sealed class NameProvider : ISignatureTypeProvider<string, object> {
  readonly MetadataReader md; public NameProvider(MetadataReader m) => md = m;
  public string GetPrimitiveType(PrimitiveTypeCode c) => c switch {
    PrimitiveTypeCode.Void=>"System.Void", PrimitiveTypeCode.Boolean=>"System.Boolean", PrimitiveTypeCode.Char=>"System.Char",
    PrimitiveTypeCode.SByte=>"System.SByte", PrimitiveTypeCode.Byte=>"System.Byte", PrimitiveTypeCode.Int16=>"System.Int16",
    PrimitiveTypeCode.UInt16=>"System.UInt16", PrimitiveTypeCode.Int32=>"System.Int32", PrimitiveTypeCode.UInt32=>"System.UInt32",
    PrimitiveTypeCode.Int64=>"System.Int64", PrimitiveTypeCode.UInt64=>"System.UInt64", PrimitiveTypeCode.Single=>"System.Single",
    PrimitiveTypeCode.Double=>"System.Double", PrimitiveTypeCode.String=>"System.String", PrimitiveTypeCode.Object=>"System.Object",
    PrimitiveTypeCode.IntPtr=>"System.IntPtr", PrimitiveTypeCode.UIntPtr=>"System.UIntPtr", PrimitiveTypeCode.TypedReference=>"System.TypedReference", _=>c.ToString() };
  public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var d = r.GetTypeDefinition(h); var ns = r.GetString(d.Namespace); return (ns.Length>0?ns+".":"")+r.GetString(d.Name); }
  public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) {
    var tr = r.GetTypeReference(h); var n = r.GetString(tr.Name);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(r, (TypeReferenceHandle)tr.ResolutionScope, k) + "+" + n;
    var ns = r.GetString(tr.Namespace); return (ns.Length>0?ns+".":"")+n; }
  public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
  public string GetSZArrayType(string e) => e + "[]";
  public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank-1) + "]";
  public string GetByReferenceType(string e) => e + "&";
  public string GetPointerType(string e) => e + "*";
  public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
  public string GetGenericTypeParameter(object c, int i) => "!" + i;
  public string GetGenericMethodParameter(object c, int i) => "!!" + i;
  public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
  public string GetModifiedType(string m, string u, bool req) => u;
  public string GetPinnedType(string e) => e;
}
