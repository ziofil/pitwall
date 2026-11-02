using Pitwall.CodeGenerator;

var repositoryRoot = RepositoryRoot.Find();

var task = args.FirstOrDefault() ?? "schema";
switch (task)
{
    case "schema":
        await Terminal.OutLineAsync("Running JSON Schema Generator");
        await new JsonSchemaGenerator().GenerateOptionsSchemaAsync(repositoryRoot);
        await Terminal.OutLineAsync("Finished JSON Schema Generator");
        break;
    case "circuits":
        await Terminal.OutLineAsync("Downloading circuit data from the MultiViewer API");
        await new CircuitDownloader().DownloadAsync(repositoryRoot);
        await Terminal.OutLineAsync("Finished downloading circuit data");
        break;
    default:
        await Terminal.OutLineAsync(
            "Usage: dotnet run --project gen/Pitwall.CodeGenerator -- [schema|circuits]"
        );
        break;
}
