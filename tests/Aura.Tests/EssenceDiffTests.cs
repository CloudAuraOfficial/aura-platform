using Aura.Api.Controllers;
using Xunit;

namespace Aura.Tests;

public class EssenceDiffTests
{
    [Fact]
    public void ComputeJsonDiff_IdenticalJson_ReturnsEmpty()
    {
        var json = """{"layers":{"build":{"isEnabled":true}}}""";
        var changes = EssencesController.ComputeJsonDiff(json, json, "");
        Assert.Empty(changes);
    }

    [Fact]
    public void ComputeJsonDiff_AddedProperty_DetectsAddition()
    {
        var from = """{"name":"test"}""";
        var to = """{"name":"test","version":2}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("version", changes[0].Path);
        Assert.Equal("added", changes[0].ChangeType);
        Assert.Null(changes[0].OldValue);
        Assert.Equal("2", changes[0].NewValue);
    }

    [Fact]
    public void ComputeJsonDiff_RemovedProperty_DetectsRemoval()
    {
        var from = """{"name":"test","version":2}""";
        var to = """{"name":"test"}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("version", changes[0].Path);
        Assert.Equal("removed", changes[0].ChangeType);
        Assert.Equal("2", changes[0].OldValue);
        Assert.Null(changes[0].NewValue);
    }

    [Fact]
    public void ComputeJsonDiff_ModifiedValue_DetectsChange()
    {
        var from = """{"name":"old"}""";
        var to = """{"name":"new"}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("name", changes[0].Path);
        Assert.Equal("modified", changes[0].ChangeType);
        Assert.Equal("\"old\"", changes[0].OldValue);
        Assert.Equal("\"new\"", changes[0].NewValue);
    }

    [Fact]
    public void ComputeJsonDiff_NestedChanges_ReportsDottedPaths()
    {
        var from = """{"layers":{"build":{"isEnabled":true}}}""";
        var to = """{"layers":{"build":{"isEnabled":false}}}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("layers.build.isEnabled", changes[0].Path);
        Assert.Equal("modified", changes[0].ChangeType);
    }

    [Fact]
    public void ComputeJsonDiff_ArrayChanges_DetectsModifiedElements()
    {
        var from = """{"items":[1,2,3]}""";
        var to = """{"items":[1,2,4]}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("items[2]", changes[0].Path);
        Assert.Equal("modified", changes[0].ChangeType);
    }

    [Fact]
    public void ComputeJsonDiff_ArrayLengthIncrease_DetectsAddedElements()
    {
        var from = """{"items":[1]}""";
        var to = """{"items":[1,2]}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("items[1]", changes[0].Path);
        Assert.Equal("added", changes[0].ChangeType);
    }

    [Fact]
    public void ComputeJsonDiff_ArrayLengthDecrease_DetectsRemovedElements()
    {
        var from = """{"items":[1,2]}""";
        var to = """{"items":[1]}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("items[1]", changes[0].Path);
        Assert.Equal("removed", changes[0].ChangeType);
    }

    [Fact]
    public void ComputeJsonDiff_TypeChange_DetectsModification()
    {
        var from = """{"value":"string"}""";
        var to = """{"value":42}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Single(changes);
        Assert.Equal("value", changes[0].Path);
        Assert.Equal("modified", changes[0].ChangeType);
    }

    [Fact]
    public void ComputeJsonDiff_MultipleChanges_ReportsAll()
    {
        var from = """{"a":1,"b":2,"c":3}""";
        var to = """{"a":1,"b":99,"d":4}""";
        var changes = EssencesController.ComputeJsonDiff(from, to, "");

        Assert.Equal(3, changes.Count); // b modified, c removed, d added
        Assert.Contains(changes, c => c.Path == "b" && c.ChangeType == "modified");
        Assert.Contains(changes, c => c.Path == "c" && c.ChangeType == "removed");
        Assert.Contains(changes, c => c.Path == "d" && c.ChangeType == "added");
    }
}
