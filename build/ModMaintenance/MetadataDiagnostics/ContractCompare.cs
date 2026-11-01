using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MetadataDiagnostics;
public static class ContractCompare
{
 static readonly Dictionary<ushort,OpCode> Opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
 sealed record Instruction(int Offset,int End,OpCode Code,object? Value);
 sealed record Side(string Status,string? Text,string? Hash,string? Reason);
 public static object Run(JsonElement profile){
  var gaps=new List<string>();var findings=new List<string>();var results=new List<object>();
  using var before=new MetadataImage(profile.GetProperty("Before").GetString()!);using var after=new MetadataImage(profile.GetProperty("After").GetString()!);
  var selected=profile.GetProperty("Selections").EnumerateArray().ToArray();if(selected.Length==0||selected.Length>1000)throw new InvalidDataException("Explicit bounded selections required.");
  var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int changed=0,unchanged=0,unresolved=0;long retainedText=0;
  foreach(var s in selected){string key=s.GetProperty("Key").GetString()!;if(!keys.Add(key))throw new InvalidDataException("Duplicate selection key.");
   var a=retainedText>=16777216?new Side("Unresolved",null,null,"Aggregate text limit."):Read(before,s);var b=retainedText>=16777216?new Side("Unresolved",null,null,"Aggregate text limit."):Read(after,s);retainedText+=(a.Text?.Length??0)+(b.Text?.Length??0);if(retainedText>16777216){a=new("Unresolved",null,null,"Aggregate text limit.");b=a;}string state;
   if(a.Status!="Read"||b.Status!="Read"){state="Unresolved";unresolved++;gaps.Add(key+": "+a.Status+" / "+b.Status);}
   else if(a.Hash==b.Hash){state="Unchanged";unchanged++;}else{state="Changed";changed++;findings.Add(key+": selected contract changed");}
   results.Add(new{Key=key,Kind=s.GetProperty("Kind").GetString(),Type=s.GetProperty("Type").GetString(),Status=state,Before=a,After=b});
  }
  return new{State=unresolved>0?"ComparedWithGaps":changed>0?"ChangesFound":"UnchangedInSelectedScope",Inputs=new[]{new{Path=before.Path,Status="Present",SHA256=before.Sha256,MVID=before.Mvid,AssemblyIdentity=before.FullIdentity},new{Path=after.Path,Status="Present",SHA256=after.Sha256,MVID=after.Mvid,AssemblyIdentity=after.FullIdentity}}.DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToArray(),Selections=results,Coverage=new{Selected=selected.Length,Unchanged=unchanged,Changed=changed,Unresolved=unresolved},Gaps=gaps,Findings=findings};
 }
 static Side Read(MetadataImage image,JsonElement selection){try{
  string kind=selection.GetProperty("Kind").GetString()!,typeName=selection.GetProperty("Type").GetString()!;
  var types=image.Reader.TypeDefinitions.Where(h=>Unscope(image.TypeName(h))==typeName).ToArray();if(types.Length!=1)throw new NotSupportedException("Expected one exact type; found "+types.Length);
  var th=types[0];var type=image.Reader.GetTypeDefinition(th);string text;
  if(kind=="Type")text=TypeContract(image,th);
  else if(kind=="Field"){
   var fields=type.GetFields().Where(h=>image.Reader.GetString(image.Reader.GetFieldDefinition(h).Name)==selection.GetProperty("Name").GetString()).ToArray();if(fields.Length!=1)throw new NotSupportedException("Expected one exact field; found "+fields.Length);text=FieldContract(image,fields[0]);
  }else if(kind=="Method"){
   var methods=type.GetMethods().Where(h=>image.Reader.GetString(image.Reader.GetMethodDefinition(h).Name)==selection.GetProperty("Name").GetString()).ToArray();
   if(selection.TryGetProperty("ParameterTypes",out var parameters)){
    string[] expected=parameters.EnumerateArray().Select(x=>x.GetString()!).ToArray();methods=methods.Where(h=>image.Reader.GetMethodDefinition(h).DecodeSignature(image.Provider,(object?)null).ParameterTypes.SequenceEqual(expected,StringComparer.Ordinal)).ToArray();
   }
   if(methods.Length!=1)throw new NotSupportedException("Expected one exact method; found "+methods.Length);text=MethodContract(image,methods[0]);
  }else throw new NotSupportedException("Unknown selection kind.");
  text=AddExternalIdentities(image,text);if(text.Length>1048576)throw new NotSupportedException("Selected contract exceeds 1 MiB.");
  return new("Read",text,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),null);
 }catch(Exception ex)when(ex is BadImageFormatException or NotSupportedException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException){return new("Unresolved",null,null,ex.GetType().Name+": "+ex.Message);}}
 static string Unscope(string text){int at=text.IndexOf(']');return at<0?text:text[(at+1)..];}
 static string AddExternalIdentities(MetadataImage image,string text){
  var names=System.Text.RegularExpressions.Regex.Matches(text,@"\[([^\[\]]+)\]").Select(m=>m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
  // Intrinsic primitive signature types have no TypeRef token; include the explicit
  // runtime contract when present rather than silently dropping its identity.
  foreach(var r in image.Reader.AssemblyReferences){var n=image.Reader.GetString(image.Reader.GetAssemblyReference(r).Name);if(n is "mscorlib" or "System.Runtime" or "System.Private.CoreLib")names.Add(n);}
  var refs=image.Reader.AssemblyReferences.Where(h=>names.Contains(image.Reader.GetString(image.Reader.GetAssemblyReference(h).Name))).GroupBy(h=>image.Reader.GetString(image.Reader.GetAssemblyReference(h).Name),StringComparer.Ordinal);
  var lines=new List<string>();foreach(var group in refs){if(group.Count()!=1)throw new NotSupportedException("Ambiguous assembly reference: "+group.Key);lines.Add("external="+image.AssemblyReferenceIdentity(group.Single()));}
  return text+string.Join("\n",lines.Order(StringComparer.Ordinal))+"\n";
 }
 static string Attributes(MetadataImage i,CustomAttributeHandleCollection handles){var lines=new List<string>();foreach(var h in handles){var a=i.Reader.GetCustomAttribute(h);lines.Add("attribute="+i.EntityName(a.Constructor)+":"+Convert.ToHexString(i.Reader.GetBlobBytes(a.Value)));}return string.Join("\n",lines.Order(StringComparer.Ordinal));}
 static string Constant(MetadataImage i,ConstantHandle h){if(h.IsNil)return "none";var c=i.Reader.GetConstant(h);return c.TypeCode+":"+Convert.ToHexString(i.Reader.GetBlobBytes(c.Value));}
 static string Generics(MetadataImage i,GenericParameterHandleCollection handles){var lines=new List<string>();foreach(var h in handles){var g=i.Reader.GetGenericParameter(h);lines.Add("generic="+g.Index+":"+g.Attributes+":"+string.Join(",",g.GetConstraints().Select(c=>i.TypeName(i.Reader.GetGenericParameterConstraint(c).Type)).Order(StringComparer.Ordinal))+":"+Attributes(i,g.GetCustomAttributes()));}return string.Join("\n",lines);}
 static string FieldContract(MetadataImage i,FieldDefinitionHandle h){var f=i.Reader.GetFieldDefinition(h);if(f.GetRelativeVirtualAddress()!=0)throw new NotSupportedException("RVA-initialized fields require separate data inspection.");return "field="+i.FieldSignature(h)+"\nflags="+f.Attributes+"\ndefault="+Constant(i,f.GetDefaultValue())+"\nlayout="+f.GetOffset()+"\nmarshal="+Convert.ToHexString(i.Reader.GetBlobBytes(f.GetMarshallingDescriptor()))+"\n"+Attributes(i,f.GetCustomAttributes())+"\n";}
 static string TypeContract(MetadataImage i,TypeDefinitionHandle h){var t=i.Reader.GetTypeDefinition(h);var layout=t.GetLayout();var lines=new List<string>{"type="+i.TypeName(h),"flags="+t.Attributes,"base="+(t.BaseType.IsNil?"none":i.TypeName(t.BaseType)),"layout="+layout.PackingSize+":"+layout.Size,Generics(i,t.GetGenericParameters()),Attributes(i,t.GetCustomAttributes())};
  lines.AddRange(t.GetInterfaceImplementations().Select(x=>"interface="+i.TypeName(i.Reader.GetInterfaceImplementation(x).Interface)).Order(StringComparer.Ordinal));
  int fieldOrdinal=0;foreach(var field in t.GetFields())lines.Add("field-ordinal="+(fieldOrdinal++)+":"+FieldContract(i,field));
  lines.AddRange(t.GetMethods().Select(x=>{var m=i.Reader.GetMethodDefinition(x);return "declared-method="+i.MethodSignature(x)+";flags="+m.Attributes+";impl="+m.ImplAttributes;}).Order(StringComparer.Ordinal));
  foreach(var mh in t.GetMethodImplementations()){var m=i.Reader.GetMethodImplementation(mh);lines.Add("override="+i.EntityName(m.MethodBody)+"=>"+i.EntityName(m.MethodDeclaration));}
  foreach(var ph in t.GetProperties()){var p=i.Reader.GetPropertyDefinition(ph);var a=p.GetAccessors();lines.Add("property="+MetadataImage.Format(i.Reader.GetString(p.Name),p.DecodeSignature(i.Provider,(object?)null))+";flags="+p.Attributes+";default="+Constant(i,p.GetDefaultValue())+";get="+(a.Getter.IsNil?"none":i.MethodSignature(a.Getter))+";set="+(a.Setter.IsNil?"none":i.MethodSignature(a.Setter))+";other="+string.Join(",",a.Others.Select(i.MethodSignature))+";"+Attributes(i,p.GetCustomAttributes()));}
  foreach(var eh in t.GetEvents()){var e=i.Reader.GetEventDefinition(eh);var a=e.GetAccessors();lines.Add("event="+i.Reader.GetString(e.Name)+":"+i.TypeName(e.Type)+";flags="+e.Attributes+";add="+(a.Adder.IsNil?"none":i.MethodSignature(a.Adder))+";remove="+(a.Remover.IsNil?"none":i.MethodSignature(a.Remover))+";raise="+(a.Raiser.IsNil?"none":i.MethodSignature(a.Raiser))+";other="+string.Join(",",a.Others.Select(i.MethodSignature))+";"+Attributes(i,e.GetCustomAttributes()));}
  return string.Join("\n",lines)+"\n";
 }
 static string MethodContract(MetadataImage i,MethodDefinitionHandle h){var m=i.Reader.GetMethodDefinition(h);var text=new StringBuilder();text.AppendLine("method="+i.MethodSignature(h));text.AppendLine("flags="+m.Attributes);text.AppendLine("impl="+m.ImplAttributes);text.AppendLine(Generics(i,m.GetGenericParameters()));text.AppendLine(Attributes(i,m.GetCustomAttributes()));
  foreach(var p in m.GetParameters()){var v=i.Reader.GetParameter(p);text.AppendLine("parameter="+v.SequenceNumber+":"+i.Reader.GetString(v.Name)+":"+v.Attributes+":"+Constant(i,v.GetDefaultValue())+":"+Convert.ToHexString(i.Reader.GetBlobBytes(v.GetMarshallingDescriptor()))+":"+Attributes(i,v.GetCustomAttributes()));}
  if(m.RelativeVirtualAddress==0)throw new NotSupportedException("Method has no managed body.");
  if((m.ImplAttributes&MethodImplAttributes.CodeTypeMask)!=MethodImplAttributes.IL)throw new NotSupportedException("Non-IL method body.");
  var body=i.Pe.GetMethodBody(m.RelativeVirtualAddress);byte[] bytes=body.GetILBytes()??throw new BadImageFormatException("IL missing.");if(bytes.Length>1048576)throw new NotSupportedException("IL exceeds 1 MiB.");var code=Decode(bytes);var index=code.Select((x,n)=>(x.Offset,n)).ToDictionary(x=>x.Offset,x=>x.n);index[bytes.Length]=code.Count;
  int BranchLabel(int offset){if(offset==bytes.Length)throw new BadImageFormatException("Branch past final instruction.");return Label(offset);}
  int Label(int offset){if(!index.TryGetValue(offset,out var n))throw new BadImageFormatException("Branch/EH target is not an instruction boundary.");return n;}
  var methodSignature=m.DecodeSignature(i.Provider,(object?)null);int argumentCount=methodSignature.ParameterTypes.Length+((m.Attributes&MethodAttributes.Static)==0?1:0);int localCount=body.LocalSignature.IsNil?0:i.Reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(i.Provider,(object?)null).Length;
  text.AppendLine("maxstack="+body.MaxStack+";initlocals="+body.LocalVariablesInitialized);
  if(!body.LocalSignature.IsNil){var sig=i.Reader.GetStandaloneSignature(body.LocalSignature);text.AppendLine("locals="+string.Join(",",sig.DecodeLocalSignature(i.Provider,(object?)null)));}else text.AppendLine("locals=none");
  foreach(var ins in code){string name=ins.Code.Name!;if(ins.Code.OperandType==OperandType.ShortInlineBrTarget&&name.EndsWith(".s",StringComparison.Ordinal))name=name[..^2];string operand="";
   string original=ins.Code.Name!;int? variableIndex=null;bool argument=original.StartsWith("ldarg",StringComparison.Ordinal)||original.StartsWith("starg",StringComparison.Ordinal);bool local=original.StartsWith("ldloc",StringComparison.Ordinal)||original.StartsWith("stloc",StringComparison.Ordinal);
   if(argument||local){if(ins.Code.OperandType is OperandType.InlineVar or OperandType.ShortInlineVar)variableIndex=Convert.ToInt32(ins.Value,CultureInfo.InvariantCulture);else if(original.Length>0&&char.IsDigit(original[^1]))variableIndex=original[^1]-'0';else throw new NotSupportedException("Unknown variable instruction form.");if(variableIndex<0||variableIndex>=(argument?argumentCount:localCount))throw new BadImageFormatException("IL variable index outside method signature/body.");}
   switch(ins.Code.OperandType){
    case OperandType.InlineBrTarget:case OperandType.ShortInlineBrTarget:operand="@"+BranchLabel((int)ins.Value!);break;
    case OperandType.InlineSwitch:operand=string.Join(",",((int[])ins.Value!).Select(x=>"@"+BranchLabel(x)));break;
    case OperandType.InlineString:if(((uint)(int)ins.Value!>>24)!=0x70)throw new BadImageFormatException("Invalid user-string token table.");operand=JsonSerializer.Serialize(i.Reader.GetUserString(MetadataTokens.UserStringHandle((int)ins.Value!&0x00ffffff)));break;
    case OperandType.InlineField:case OperandType.InlineMethod:case OperandType.InlineType:case OperandType.InlineTok:
     var token=MetadataTokens.EntityHandle((int)ins.Value!);bool method=token.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification||token.Kind==HandleKind.MemberReference&&i.Reader.GetMemberReference((MemberReferenceHandle)token).GetKind()==MemberReferenceKind.Method;
     bool field=token.Kind==HandleKind.FieldDefinition||token.Kind==HandleKind.MemberReference&&i.Reader.GetMemberReference((MemberReferenceHandle)token).GetKind()==MemberReferenceKind.Field;bool type=token.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification;
     if(ins.Code.OperandType==OperandType.InlineMethod&&!method||ins.Code.OperandType==OperandType.InlineField&&!field||ins.Code.OperandType==OperandType.InlineType&&!type||ins.Code.OperandType==OperandType.InlineTok&&!method&&!field&&!type)throw new BadImageFormatException("Wrong token kind for IL operand.");operand=i.EntityName(token);break;
    case OperandType.InlineSig:var handle=MetadataTokens.EntityHandle((int)ins.Value!);if(handle.Kind!=HandleKind.StandaloneSignature)throw new NotSupportedException("Expected standalone call signature.");operand=MetadataImage.Format("calli",i.Reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(i.Provider,(object?)null));break;
    default:operand=Convert.ToString(ins.Value,CultureInfo.InvariantCulture)??"";break;
   }
   text.AppendLine(name+" "+operand);
  }
  foreach(var region in body.ExceptionRegions){text.AppendLine("eh="+region.Kind+";try="+Label(region.TryOffset)+".."+Label(region.TryOffset+region.TryLength)+";handler="+Label(region.HandlerOffset)+".."+Label(region.HandlerOffset+region.HandlerLength)+";filter="+(region.Kind==ExceptionRegionKind.Filter?Label(region.FilterOffset):-1)+";catch="+(region.CatchType.IsNil?"none":i.TypeName(region.CatchType)));}
  return text.ToString().Replace("\r\n","\n");
 }
 static List<Instruction> Decode(byte[] bytes){int at=0;var list=new List<Instruction>();
  int Need(int n){if(n<0||at>bytes.Length-n)throw new BadImageFormatException("Truncated IL operand.");int p=at;at+=n;return p;}
  while(at<bytes.Length){int start=at;ushort op=bytes[Need(1)];if(op==0xfe)op=(ushort)(0xfe00|bytes[Need(1)]);if(!Opcodes.TryGetValue(op,out var code))throw new NotSupportedException("Unknown opcode.");object? value=null;
   switch(code.OperandType){
    case OperandType.InlineNone:break;
    case OperandType.ShortInlineI:value=(sbyte)bytes[Need(1)];break;
    case OperandType.InlineI:value=BitConverter.ToInt32(bytes,Need(4));break;
    case OperandType.InlineI8:value=BitConverter.ToInt64(bytes,Need(8));break;
    case OperandType.ShortInlineR:value="float-bits:"+BitConverter.ToUInt32(bytes,Need(4)).ToString("X8",CultureInfo.InvariantCulture);break;
    case OperandType.InlineR:value="double-bits:"+BitConverter.ToUInt64(bytes,Need(8)).ToString("X16",CultureInfo.InvariantCulture);break;
    case OperandType.ShortInlineVar:value=bytes[Need(1)];break;
    case OperandType.InlineVar:value=BitConverter.ToUInt16(bytes,Need(2));break;
    case OperandType.ShortInlineBrTarget:int shortAt=Need(1);value=at+(sbyte)bytes[shortAt];break;
    case OperandType.InlineBrTarget:int longAt=Need(4);value=at+BitConverter.ToInt32(bytes,longAt);break;
    case OperandType.InlineSwitch:int count=BitConverter.ToInt32(bytes,Need(4));if(count<0||count>100000)throw new NotSupportedException("Switch target limit.");int pos=Need(checked(count*4));int end=at;value=Enumerable.Range(0,count).Select(n=>checked(end+BitConverter.ToInt32(bytes,pos+n*4))).ToArray();break;
    case OperandType.InlineField:case OperandType.InlineMethod:case OperandType.InlineType:case OperandType.InlineTok:case OperandType.InlineString:case OperandType.InlineSig:value=BitConverter.ToInt32(bytes,Need(4));break;
    default:throw new NotSupportedException("Unsupported operand "+code.OperandType);
   }
   list.Add(new(start,at,code,value));if(list.Count>250000)throw new NotSupportedException("Instruction count limit.");
  }return list;
 }
}
