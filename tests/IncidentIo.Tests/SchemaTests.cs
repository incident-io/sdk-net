using System.Reflection;
using System.Text.Json;
using Xunit;

namespace IncidentIo.Tests;

// Checks the generated code against the committed schema, so a Kiota upgrade or
// a change in scripts/prepare_spec.py that quietly drops something fails here.
public class SchemaTests
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch"];

    private static readonly JsonDocument Schema = JsonDocument.Parse(File.ReadAllText("openapi.json"));

    private static IEnumerable<JsonProperty> Operations() =>
        Schema.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(op => HttpMethods.Contains(op.Name));

    private static IEnumerable<MethodInfo> EndpointMethods() =>
        typeof(IncidentIoClient).Assembly.GetExportedTypes()
            .Where(t => t.Name.EndsWith("RequestBuilder"))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => HttpMethods.Any(verb => m.Name.Equals(verb + "Async", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void HasAMethodForEveryOperation()
    {
        Assert.Equal(Operations().Count(), EndpointMethods().Count());
    }

    [Fact]
    public void HasNoSnakeCaseNamesInThePublicApi()
    {
        var names = typeof(IncidentIoClient).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Append(t.FullName!))
            // Compiler-generated: property accessors, operators, and every enum's value__ field.
            .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("op_") && n != "value__");

        Assert.Empty(names.Where(n => n.Contains('_')).Distinct());
    }

    [Fact]
    public void MarksEveryDeprecatedOperation()
    {
        var deprecated = Operations().Count(op =>
            op.Value.TryGetProperty("deprecated", out var flag) && flag.GetBoolean());

        var marked = EndpointMethods().Count(m =>
            m.GetCustomAttribute<ObsoleteAttribute>()?.Message?.Contains("deprecated in the incident.io API") == true);

        Assert.True(deprecated > 0);
        Assert.Equal(deprecated, marked);
    }
}
