using System.Text.Json;
using Json.More;
using Json.Schema;
using Json.Schema.Generation;
using Pitwall.Console;
using Pitwall.Data;

namespace Pitwall.CodeGenerator;

/// <summary>
/// Generates JSON Schema files for Pitwall configuration
/// </summary>
public sealed class JsonSchemaGenerator
{
    private static readonly JsonSerializerOptions _serializerOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    /// Generates JSON Schema files for Pitwall configuration
    /// </summary>
    public async Task GenerateOptionsSchemaAsync()
    {
        var repositoryRoot = Environment.ProcessPath;
        if (string.IsNullOrEmpty(repositoryRoot))
        {
            throw new InvalidOperationException("Unable to determine the path of the executable");
        }

        repositoryRoot = Directory.GetParent(repositoryRoot)!.ToString();
        repositoryRoot = Path.Join(repositoryRoot, "../../../../..");

        var config = new SchemaGeneratorConfiguration()
        {
            PropertyNameResolver = PropertyNameResolvers.CamelCase,
        };
        config.RegisterXmlCommentFile<LiveTimingOptions>(
            Path.Join(repositoryRoot, "Pitwall.Data/bin/Debug/net9.0/Pitwall.Data.xml")
        );
        config.RegisterXmlCommentFile<ConsoleOptions>(
            Path.Join(repositoryRoot, "Pitwall.Console/bin/Debug/net9.0/pitwall.xml")
        );

        var schemaBuilder = new JsonSchemaBuilder();
        var schema = schemaBuilder.FromType<ConsoleOptions>(config).Build();

        var schemaString = JsonSerializer.Serialize(schema.ToJsonDocument(), _serializerOptions);

        var outputPath = Path.Join(repositoryRoot, "config.schema.json");
        outputPath = Path.GetFullPath(outputPath);

        await Terminal.OutLineAsync($"Writing config JSON Schema to {outputPath}");

        await File.WriteAllTextAsync(outputPath, schemaString);
    }
}
