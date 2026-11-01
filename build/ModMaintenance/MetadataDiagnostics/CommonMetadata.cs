using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
namespace MetadataDiagnostics;
public sealed class MetadataImage : IDisposable
{
 readonly MemoryStream stream;
 readonly SignatureTypeProvider provider;
 public string Path {get;}
 public string AssemblyName {get;}
 public string FullIdentity {get;}
 public string Sha256 {get;}
 public Guid Mvid {get;}
 public MetadataReader Reader {get;}
 public PEReader Pe {get;}
 public MetadataImage(string path) {
  Path=ValidatePath(path);var info=new FileInfo(Path);if(!info.Exists||info.Length>128L*1024*1024)throw new InvalidDataException("Missing or oversized assembly.");
  byte[] bytes=File.ReadAllBytes(Path);Sha256=Convert.ToHexString(SHA256.HashData(bytes));stream=new MemoryStream(bytes,false);Pe=new PEReader(stream);
  if(!Pe.HasMetadata)throw new BadImageFormatException("Managed metadata required.");Reader=Pe.GetMetadataReader();if(!Reader.IsAssembly)throw new NotSupportedException("Netmodules unsupported.");
  var a=Reader.GetAssemblyDefinition();AssemblyName=Reader.GetString(a.Name);FullIdentity=AssemblyIdentity(a);Mvid=Reader.GetGuid(Reader.GetModuleDefinition().Mvid);provider=new SignatureTypeProvider(this);
 }
 public static string ValidatePath(string path) {
  if(string.IsNullOrWhiteSpace(path)||!System.IO.Path.IsPathFullyQualified(path)||path.StartsWith("\\\\",StringComparison.Ordinal))throw new InvalidDataException("Absolute local path required.");
  foreach(var part in path.Substring(System.IO.Path.GetPathRoot(path)!.Length).Split(new[]{'\\','/'},StringSplitOptions.None))if(part.Length==0||part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||part.IndexOfAny(new[]{':','*','?','[',']'})>=0||part.Any(char.IsControl)||System.Text.RegularExpressions.Regex.IsMatch(part,@"^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)"))throw new InvalidDataException("Unsafe path component.");
  var full=System.IO.Path.GetFullPath(path);for(string? p=full;p!=null;p=System.IO.Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse points refused.");return full;
 }
 string AssemblyIdentity(AssemblyDefinition a)=>Identity(Reader.GetString(a.Name),a.Version,Reader.GetString(a.Culture),Reader.GetBlobBytes(a.PublicKey),true);
 public string AssemblyReferenceIdentity(AssemblyReferenceHandle h){var a=Reader.GetAssemblyReference(h);return Identity(Reader.GetString(a.Name),a.Version,Reader.GetString(a.Culture),Reader.GetBlobBytes(a.PublicKeyOrToken),(a.Flags&System.Reflection.AssemblyFlags.PublicKey)!=0);}
 public static string Identity(string name,Version version,string culture,byte[] key,bool fullKey){byte[] token=key;if(fullKey&&key.Length>0){var digest=SHA1.HashData(key);token=digest[^8..].Reverse().ToArray();}return name+", Version="+version+", Culture="+(culture.Length==0?"neutral":culture)+", PublicKeyToken="+(token.Length==0?"null":Convert.ToHexString(token).ToLowerInvariant());}
 public string TypeName(EntityHandle h)=>h.Kind switch {
  HandleKind.TypeDefinition=>DefinitionType((TypeDefinitionHandle)h),HandleKind.TypeReference=>ReferenceType((TypeReferenceHandle)h),HandleKind.TypeSpecification=>Reader.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(provider,(object?)null),_=>throw new NotSupportedException("Unsupported type handle "+h.Kind)};
 string DefinitionType(TypeDefinitionHandle h,int depth=0){if(depth>64)throw new NotSupportedException("Nested type depth exceeded.");var t=Reader.GetTypeDefinition(h);var parent=t.GetDeclaringType();return parent.IsNil?"["+AssemblyName+"]"+Join(Reader.GetString(t.Namespace),Reader.GetString(t.Name)):DefinitionType(parent,depth+1)+"+"+Reader.GetString(t.Name);}
 string ReferenceType(TypeReferenceHandle h,int depth=0){if(depth>64)throw new NotSupportedException("Nested reference depth exceeded.");var t=Reader.GetTypeReference(h);return t.ResolutionScope.Kind==HandleKind.TypeReference?ReferenceType((TypeReferenceHandle)t.ResolutionScope,depth+1)+"+"+Reader.GetString(t.Name):ReferenceScope(t.ResolutionScope)+Join(Reader.GetString(t.Namespace),Reader.GetString(t.Name));}
 string ReferenceScope(EntityHandle h)=>h.Kind switch {HandleKind.AssemblyReference=>"["+Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)h).Name)+"]",HandleKind.ModuleDefinition=>"["+AssemblyName+"]",_=>throw new NotSupportedException("Unsupported type reference scope "+h.Kind)};
 static string Join(string ns,string name)=>ns.Length==0?name:ns+"."+name;
 public string MethodSignature(MethodDefinitionHandle h){var m=Reader.GetMethodDefinition(h);return Format(TypeName(m.GetDeclaringType())+"::"+Reader.GetString(m.Name),m.DecodeSignature(provider,(object?)null));}
 public string MethodSignature(MemberReferenceHandle h){var m=Reader.GetMemberReference(h);if(m.GetKind()!=MemberReferenceKind.Method)throw new InvalidDataException("Method reference required.");return Format(Owner(m.Parent)+"::"+Reader.GetString(m.Name),m.DecodeMethodSignature(provider,(object?)null));}
 public string FieldSignature(FieldDefinitionHandle h){var f=Reader.GetFieldDefinition(h);return TypeName(f.GetDeclaringType())+"::"+Reader.GetString(f.Name)+":"+f.DecodeSignature(provider,(object?)null);}
 public string FieldSignature(MemberReferenceHandle h){var f=Reader.GetMemberReference(h);if(f.GetKind()!=MemberReferenceKind.Field)throw new InvalidDataException("Field reference required.");return Owner(f.Parent)+"::"+Reader.GetString(f.Name)+":"+f.DecodeFieldSignature(provider,(object?)null);}
 string Owner(EntityHandle h)=>h.Kind==HandleKind.MethodDefinition?MethodSignature((MethodDefinitionHandle)h):TypeName(h);
 public static string Format(string name,MethodSignature<string> s)=>name+"``"+s.GenericParameterCount+"["+s.Header.RawValue+"]("+string.Join(",",s.ParameterTypes)+")->"+s.ReturnType+";required="+s.RequiredParameterCount;
 public string EntityName(EntityHandle h)=>h.Kind switch {HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification=>TypeName(h),HandleKind.MethodDefinition=>MethodSignature((MethodDefinitionHandle)h),HandleKind.FieldDefinition=>FieldSignature((FieldDefinitionHandle)h),HandleKind.MemberReference=>Reader.GetMemberReference((MemberReferenceHandle)h).GetKind()==MemberReferenceKind.Method?MethodSignature((MemberReferenceHandle)h):FieldSignature((MemberReferenceHandle)h),HandleKind.MethodSpecification=>MethodSpec((MethodSpecificationHandle)h),_=>throw new NotSupportedException("Unsupported entity handle "+h.Kind)};
 string MethodSpec(MethodSpecificationHandle h){var s=Reader.GetMethodSpecification(h);return EntityName(s.Method)+"<"+string.Join(",",s.DecodeSignature(provider,(object?)null))+">";}
 public SignatureTypeProvider Provider=>provider;
 public void Dispose(){Pe.Dispose();stream.Dispose();}
}
public sealed class SignatureTypeProvider : ISignatureTypeProvider<string,object?>
{
 static string Kind(byte k)=>k switch {0=>"type ",0x11=>"valuetype ",0x12=>"class ",_=>throw new NotSupportedException("Unsupported signature type kind "+k)};
 int depth;
 readonly MetadataImage image;public SignatureTypeProvider(MetadataImage image){this.image=image;}
 public string GetArrayType(string element,ArrayShape shape)=>element+"[rank="+shape.Rank+";sizes="+string.Join(",",shape.Sizes)+";lower="+string.Join(",",shape.LowerBounds)+"]";
 public string GetByReferenceType(string e)=>e+"&";
 public string GetFunctionPointerType(MethodSignature<string> s)=>"fnptr:"+MetadataImage.Format("",s);
 public string GetGenericInstantiation(string t,ImmutableArray<string> a)=>t+"<"+string.Join(",",a)+">";
 public string GetGenericMethodParameter(object? c,int i)=>"!!"+i;
 public string GetGenericTypeParameter(object? c,int i)=>"!"+i;
 public string GetModifiedType(string m,string u,bool required)=>u+(required?" modreq(":" modopt(")+m+")";
 public string GetPinnedType(string e)=>e+" pinned";
 public string GetPointerType(string e)=>e+"*";
 public string GetPrimitiveType(PrimitiveTypeCode c)=>c.ToString();
 public string GetSZArrayType(string e)=>e+"[]";
 public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k)=>Kind(k)+image.TypeName(h);
 public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k)=>Kind(k)+image.TypeName(h);
 public string GetTypeFromSpecification(MetadataReader r,object? c,TypeSpecificationHandle h,byte k){if(++depth>64){depth--;throw new NotSupportedException("Type specification depth exceeded.");}try{return r.GetTypeSpecification(h).DecodeSignature(this,c);}finally{depth--;}}
}
