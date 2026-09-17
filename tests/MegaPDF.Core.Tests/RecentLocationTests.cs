using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The location line under a recent document's name (#165). Pure string work, so these
/// run the same on Windows and on the Mac.
/// </summary>
public class RecentLocationTests
{
    private static readonly NamedFolder[] Windows =
    [
        new(@"C:\Users\Sly", "Sly"),
        new(@"C:\Users\Sly\Documents", "Documents"),
        new(@"C:\Users\Sly\Downloads", "Téléchargements"),
        new(@"D:\OneDrive\My Documents", "David - Personal"),
    ];

    [Fact]
    public void KnownFolder_IsShownByItsDisplayName_AndNothingAboveIt()
    {
        var segments = RecentLocation.Segments(@"C:\Users\Sly\Documents\Clients\Smith\agreement.pdf", Windows);
        Assert.Equal(["Documents", "Clients", "Smith"], segments);
        Assert.Equal("Documents › Clients › Smith", RecentLocation.Line(segments));
    }

    [Fact]
    public void TheLongestKnownFolderWins_SoTheProfileDoesNotSwallowDocuments()
    {
        Assert.Equal(["Documents"], RecentLocation.Segments(@"C:\Users\Sly\Documents\a.pdf", Windows));
        Assert.Equal(["Sly", "Projects"], RecentLocation.Segments(@"C:\Users\Sly\Projects\a.pdf", Windows));
    }

    [Fact]
    public void LocalisedDisplayNames_AreUsedAsGiven()
    {
        Assert.Equal("Téléchargements", RecentLocation.Line(
            RecentLocation.Segments(@"C:\Users\Sly\Downloads\a.pdf", Windows)));
        Assert.Equal("David - Personal › Scans", RecentLocation.Line(
            RecentLocation.Segments(@"D:\OneDrive\My Documents\Scans\a.pdf", Windows)));
    }

    [Fact]
    public void ADriveOutsideAnyKnownFolder_ShowsItsLetterOnly()
    {
        Assert.Equal(["D:", "Clients", "Smith"],
                     RecentLocation.Segments(@"d:\Clients\Smith\a.pdf", Windows));
    }

    [Fact]
    public void AShare_ReadsAsServerThenShare()
    {
        Assert.Equal(["server", "scans", "2026"],
                     RecentLocation.Segments(@"\\server\scans\2026\a.pdf", Windows));
    }

    [Fact]
    public void MacPaths_UseTheSameRules()
    {
        NamedFolder[] mac = [new("/Users/claude", "claude"), new("/Users/claude/Documents", "Documents")];
        Assert.Equal(["Documents", "Clients"],
                     RecentLocation.Segments("/Users/claude/Documents/Clients/a.pdf", mac));
    }

    [Fact]
    public void AFileWithNoFolder_HasNoLocation()
    {
        Assert.Empty(RecentLocation.Segments("a.pdf", Windows));
        Assert.Equal("", RecentLocation.Line([]));
    }

    [Fact]
    public void ALongLine_LosesItsMiddle_KeepingThePlaceAndTheFolder()
    {
        string[] segments = ["Documents", "Clients", "Northern Region", "Smith and Partners", "2026", "March"];
        var line = RecentLocation.Line(segments, maxLength: 40);
        Assert.StartsWith("Documents", line);
        Assert.EndsWith("March", line);
        Assert.Contains("…", line);
        Assert.True(line.Length <= 40, line);
    }

    [Fact]
    public void APinnedTail_SurvivesTruncation_EvenWhenTheLineStaysLong()
    {
        string[] segments = ["Documents", "Clients", "Northern Region", "Smith and Partners", "2026", "March"];
        var line = RecentLocation.Line(segments, maxLength: 20, keepDeepest: 1);
        Assert.Equal("Documents › … › 2026 › March", line);
    }

    [Fact]
    public void TwoFoldersOfTheSameName_NeedTheFolderAboveThem()
    {
        IReadOnlyList<string>[] rows =
        [
            ["Documents", "Clients", "Smith", "2026"],
            ["Documents", "Clients", "Jones", "2026"],
        ];
        Assert.Equal(1, RecentLocation.DistinguishingDepth(rows));

        var first = RecentLocation.Line(rows[0], maxLength: 12, keepDeepest: 1);
        var second = RecentLocation.Line(rows[1], maxLength: 12, keepDeepest: 1);
        Assert.NotEqual(first, second);
        Assert.Contains("Smith", first);
        Assert.Contains("Jones", second);
    }

    [Fact]
    public void WhenTheParentFolderAlreadyDiffers_NothingElseIsPinned()
    {
        IReadOnlyList<string>[] rows = [["Documents", "Clients"], ["Documents", "Scans"]];
        Assert.Equal(0, RecentLocation.DistinguishingDepth(rows));
    }

    [Fact]
    public void OneRow_NeedsNoDisambiguation()
    {
        Assert.Equal(0, RecentLocation.DistinguishingDepth([["Documents"]]));
        Assert.Equal(0, RecentLocation.DistinguishingDepth([]));
    }

    [Fact]
    public void IdenticalLocations_AskForEverythingTheyHave()
    {
        IReadOnlyList<string>[] rows = [["Documents", "Clients"], ["Documents", "Clients"]];
        Assert.Equal(1, RecentLocation.DistinguishingDepth(rows));
    }
}
