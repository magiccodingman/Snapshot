using Snapshot.Protocol.Output;

var routeCount = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 10_000;
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
long aliases = 0;
for (var index = 0; index < routeCount; index++)
{
    var route = $"/Products/Category{index / 100}/SpecialOffer{index}";
    aliases += SnapshotOutputPlanner.GenerateCaseVariants(route).LongCount() - 1;
}
stopwatch.Stop();
Console.WriteLine($"Planned {routeCount:N0} canonical routes and {aliases:N0} aliases in {stopwatch.Elapsed}.");
