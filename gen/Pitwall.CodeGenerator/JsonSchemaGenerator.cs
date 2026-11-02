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
    )
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Generates JSON Schema files for Pitwall configuration
    /// </summary>
    public async Task GenerateOptionsSchemaAsync(string repositoryRoot)
    {
        var config = new SchemaGeneratorConfiguration()
        {
            PropertyNameResolver = PropertyNameResolvers.CamelCase,
        };
        // The XML doc files of the referenced projects are copied next to this tool
        config.RegisterXmlCommentFile<LiveTimingOptions>(
            Path.Join(AppContext.BaseDirectory, "Pitwall.Data.xml")
        );
        config.RegisterXmlCommentFile<ConsoleOptions>(
            Path.Join(AppContext.BaseDirectory, "pitwall.xml")
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
