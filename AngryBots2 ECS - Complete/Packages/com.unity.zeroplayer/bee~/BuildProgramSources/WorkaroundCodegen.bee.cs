using System.Text;
using Bee.Core;
using NiceIO;

public class WorkaroundCodegen
{
    public static NPath SetupCodegenFileThatReferencesSomethingFromEachDependency(string consumer, string[] dependencies)
    {
        consumer = consumer.Replace("-", "_");
        var sb = new StringBuilder();
        sb.AppendLine($"namespace workaround.{consumer} {{");
        sb.AppendLine($"  public class Dummy {{");
        foreach (var dep in dependencies)
        {
            var depName = dep.Replace("-", "_");
            sb.AppendLine($"    public static workaround.{depName}.Dummy _{depName}; ");
        }

        sb.AppendLine($"  }}");
        sb.AppendLine($"}}");

        var path = $"artifacts/modules/dummy_{consumer}.cs";
        Backend.Current.AddWriteTextAction(path, sb.ToString());
        return path;
    }
}