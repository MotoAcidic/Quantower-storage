using AuctionResponse.Tests;

// Locate the spec files relative to the repository root, so the harness runs the same way
// from an IDE, from deploy.ps1, and from a bare `dotnet run`.
var dir = AppContext.BaseDirectory;
string? root = null;
for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    if (File.Exists(Path.Combine(d.FullName, "spec", "TEST-VECTORS.json"))) { root = d.FullName; break; }

if (root is null)
{
    Console.Error.WriteLine("Could not locate spec/TEST-VECTORS.json above " + dir);
    return 2;
}

Console.WriteLine("Auction Response Monitor - test harness");
Console.WriteLine("Spec files: " + Path.Combine(root, "spec"));

FixtureTests.Run(Path.Combine(root, "spec", "TEST-VECTORS.json"));
InvariantTests.Run();
CandidateFailureTests.Run();
StateTests.Run();
ReplayTests.Run();
ResearchTests.Run();
LayoutTests.Run();

return Harness.Report();
