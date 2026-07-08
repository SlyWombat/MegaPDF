using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

public class RecentFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-recent-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StorePath => Path.Combine(_dir, "recent.json");

    private string MakePdf(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Add_IsMostRecentFirst_AndDeduplicates()
    {
        var a = MakePdf("a.pdf");
        var b = MakePdf("b.pdf");
        var recent = new RecentFiles(StorePath);

        recent.Add(a);
        recent.Add(b);
        recent.Add(a); // re-open a

        Assert.Equal([a, b], recent.All);
    }

    [Fact]
    public void Capacity_IsEnforced()
    {
        var recent = new RecentFiles(StorePath);
        for (var i = 0; i < RecentFiles.Capacity + 3; i++)
            recent.Add(MakePdf($"doc{i}.pdf"));

        Assert.Equal(RecentFiles.Capacity, recent.All.Count);
        Assert.EndsWith($"doc{RecentFiles.Capacity + 2}.pdf", recent.All[0]);
    }

    [Fact]
    public void Load_PersistsAcrossInstances_AndPrunesMissingFiles()
    {
        var keep = MakePdf("keep.pdf");
        var gone = MakePdf("gone.pdf");
        new RecentFiles(StorePath).Add(keep);
        new RecentFiles(StorePath).Add(gone);
        File.Delete(gone);

        var reloaded = new RecentFiles(StorePath);

        Assert.Equal([keep], reloaded.All);
    }
}
