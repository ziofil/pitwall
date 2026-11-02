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
    case "screenshots" when args.Length >= 3:
        await Terminal.OutLineAsync($"Rendering screenshots from {args[1]}");
        await new Screenshots().RenderAsync(
            repositoryRoot,
            args[1],
            int.Parse(args[2]),
            args.ElementAtOrDefault(3)
        );
        await Terminal.OutLineAsync("Finished rendering screenshots");
        break;
    default:
        await Terminal.OutLineAsync(
            "Usage: dotnet run --project gen/Pitwall.CodeGenerator -- [schema | circuits | screenshots <recording folder> <lap> [team]]"
        );
        break;
}
