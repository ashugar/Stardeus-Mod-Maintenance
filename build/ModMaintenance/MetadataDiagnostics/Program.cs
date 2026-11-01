using System.Text.Json;
namespace MetadataDiagnostics;
internal static class Program
{
 public static int Main(string[] args)
 {
  try {
   if(args.Length != 2) throw new InvalidDataException("Expected mode and explicit JSON profile path.");
   var path=MetadataImage.ValidatePath(args[1]);
   if(new FileInfo(path).Length>4*1024*1024) throw new InvalidDataException("Profile exceeds 4 MiB.");
   using var profile=JsonDocument.Parse(File.ReadAllBytes(path),new JsonDocumentOptions{MaxDepth=32});
   object result=args[0] switch { "api" => ApiAudit.Run(profile.RootElement), "compare" => ContractCompare.Run(profile.RootElement), _ => throw new NotSupportedException("Mode implementation pending: "+args[0]) };
   Console.WriteLine(JsonSerializer.Serialize(result));return 0;
  } catch(Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new {State="Failed",Inputs=Array.Empty<object>(),References=Array.Empty<object>(),Patches=Array.Empty<object>(),Coverage=new {},Gaps=new[]{ex.GetType().Name+": "+ex.Message},Findings=Array.Empty<object>()}));return 0; }
 }
}
