using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Tests;

public class ExperimentServiceTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static ExperimentService CreateService(AuraDbContext db) =>
        new(db, NullLogger<ExperimentService>.Instance);

    private const string TwoVariants = """[{"id":"control","weight":50},{"id":"variant_a","weight":50}]""";

    [Fact]
    public async Task Create_ValidExperiment_ReturnsDraftStatus()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        var exp = await svc.CreateAsync("test-project", "Test Experiment",
            "Testing hypothesis", TwoVariants, "latency_ms");

        Assert.NotEqual(Guid.Empty, exp.Id);
        Assert.Equal(ExperimentStatus.Draft, exp.Status);
        Assert.Equal("test-project", exp.Project);
    }

    [Fact]
    public async Task Create_InvalidWeights_Throws()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        var badVariants = """[{"id":"a","weight":30},{"id":"b","weight":30}]""";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateAsync("proj", "name", "hyp", badVariants, "metric"));
    }

    [Fact]
    public async Task Create_SingleVariant_Throws()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        var oneVariant = """[{"id":"only","weight":100}]""";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateAsync("proj", "name", "hyp", oneVariant, "metric"));
    }

    [Fact]
    public async Task AssignVariant_DeterministicForSameSubject()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "det-test", "hyp", TwoVariants, "metric");

        var variant1 = await svc.AssignVariantAsync(exp.Id, "user-123");
        var variant2 = await svc.AssignVariantAsync(exp.Id, "user-123");

        Assert.Equal(variant1, variant2);
    }

    [Fact]
    public async Task AssignVariant_Deduplication_SingleRow()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "dedup-test", "hyp", TwoVariants, "metric");

        await svc.AssignVariantAsync(exp.Id, "user-456");
        await svc.AssignVariantAsync(exp.Id, "user-456");

        var count = await db.ExperimentAssignments
            .Where(a => a.ExperimentId == exp.Id)
            .CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AssignVariant_DifferentSubjects_BothVariantsAssigned()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "dist-test", "hyp", TwoVariants, "metric");

        var variants = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            var v = await svc.AssignVariantAsync(exp.Id, $"subject-{i}");
            variants.Add(v);
        }

        // With 50/50 split and 100 subjects, both variants should appear
        Assert.Contains("control", variants);
        Assert.Contains("variant_a", variants);
    }

    [Fact]
    public async Task GetResults_AggregatesCorrectly()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "results-test", "hyp", TwoVariants, "latency");

        // Track known values for control: 10, 20, 30
        await svc.TrackEventAsync(exp.Id, "control", "hash1", "latency", 10);
        await svc.TrackEventAsync(exp.Id, "control", "hash2", "latency", 20);
        await svc.TrackEventAsync(exp.Id, "control", "hash3", "latency", 30);

        // Track known values for variant_a: 5, 15, 25
        await svc.TrackEventAsync(exp.Id, "variant_a", "hash4", "latency", 5);
        await svc.TrackEventAsync(exp.Id, "variant_a", "hash5", "latency", 15);
        await svc.TrackEventAsync(exp.Id, "variant_a", "hash6", "latency", 25);

        var results = await svc.GetResultsAsync(exp.Id);

        Assert.Equal(2, results.Variants.Count);

        var ctrl = results.Variants["control"];
        Assert.Equal(3, ctrl.SampleSize);
        Assert.Equal(20, ctrl.Mean, 1);
        Assert.Equal(10, ctrl.Min, 1);
        Assert.Equal(30, ctrl.Max, 1);

        var va = results.Variants["variant_a"];
        Assert.Equal(3, va.SampleSize);
        Assert.Equal(15, va.Mean, 1);
        Assert.Equal(5, va.Min, 1);
        Assert.Equal(25, va.Max, 1);
    }

    [Fact]
    public async Task GetResults_WelchTTest_SignificantDifference()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "sig-test", "hyp", TwoVariants, "time");

        // Control: clustered around 100
        for (int i = 0; i < 20; i++)
            await svc.TrackEventAsync(exp.Id, "control", $"c{i}", "time", 100 + (i % 5));

        // Variant: clustered around 50 (clearly different)
        for (int i = 0; i < 20; i++)
            await svc.TrackEventAsync(exp.Id, "variant_a", $"v{i}", "time", 50 + (i % 5));

        var results = await svc.GetResultsAsync(exp.Id);

        Assert.NotNull(results.Significance);
        Assert.True(results.Significance!.IsSignificant);
        Assert.True(results.Significance.PValue < 0.05);
    }

    [Fact]
    public async Task GetResults_WelchTTest_NotSignificant()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "nosig-test", "hyp", TwoVariants, "time");

        // Both variants have same distribution
        for (int i = 0; i < 10; i++)
        {
            await svc.TrackEventAsync(exp.Id, "control", $"c{i}", "time", 50 + (i % 5));
            await svc.TrackEventAsync(exp.Id, "variant_a", $"v{i}", "time", 50 + (i % 5));
        }

        var results = await svc.GetResultsAsync(exp.Id);

        Assert.NotNull(results.Significance);
        Assert.False(results.Significance!.IsSignificant);
    }

    [Fact]
    public async Task StatusTransition_DraftToRunning_SetsStartedAt()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "status-test", "hyp", TwoVariants, "metric");

        Assert.Null(exp.StartedAt);

        var updated = await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null);

        Assert.Equal(ExperimentStatus.Running, updated.Status);
        Assert.NotNull(updated.StartedAt);
    }

    [Fact]
    public async Task StatusTransition_RunningToConcluded_SetsConcludedAt()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "conclude-test", "hyp", TwoVariants, "metric");

        await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null);
        var concluded = await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Concluded, "Done");

        Assert.Equal(ExperimentStatus.Concluded, concluded.Status);
        Assert.NotNull(concluded.ConcludedAt);
        Assert.Equal("Done", concluded.Conclusion);
    }

    [Fact]
    public async Task StatusTransition_Invalid_DraftToConcluded_Throws()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "invalid-test", "hyp", TwoVariants, "metric");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Concluded, null));
    }

    [Fact]
    public async Task StatusTransition_Invalid_DraftToPaused_Throws()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "invalid2-test", "hyp", TwoVariants, "metric");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Paused, null));
    }

    [Fact]
    public async Task StatusTransition_Invalid_ConcludedToRunning_Throws()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "revive-test", "hyp", TwoVariants, "metric");

        await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null);
        await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Concluded, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null));
    }

    [Fact]
    public async Task StatusTransition_PausedToRunning_Valid()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);
        var exp = await svc.CreateAsync("proj", "resume-test", "hyp", TwoVariants, "metric");

        await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null);
        await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Paused, null);
        var resumed = await svc.UpdateAsync(exp.Id, null, null, ExperimentStatus.Running, null);

        Assert.Equal(ExperimentStatus.Running, resumed.Status);
    }

    [Fact]
    public async Task List_FilterByProject()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        await svc.CreateAsync("proj-a", "exp-a1", "hyp", TwoVariants, "metric");
        await svc.CreateAsync("proj-a", "exp-a2", "hyp", TwoVariants, "metric");
        await svc.CreateAsync("proj-b", "exp-b1", "hyp", TwoVariants, "metric");

        var (items, total) = await svc.ListAsync("proj-a", null, 0, 25);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("proj-a", i.Project));
    }

    [Fact]
    public async Task List_FilterByStatus()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        var exp1 = await svc.CreateAsync("proj", "draft", "hyp", TwoVariants, "metric");
        var exp2 = await svc.CreateAsync("proj", "running", "hyp", TwoVariants, "metric");
        await svc.UpdateAsync(exp2.Id, null, null, ExperimentStatus.Running, null);

        var (items, total) = await svc.ListAsync(null, ExperimentStatus.Running, 0, 25);

        Assert.Equal(1, total);
        Assert.Equal("running", items[0].Name);
    }

    [Fact]
    public async Task GetActive_ReturnsOnlyRunning()
    {
        var db = CreateInMemoryDb();
        var svc = CreateService(db);

        var draft = await svc.CreateAsync("aura", "draft-exp", "hyp", TwoVariants, "metric");
        var running = await svc.CreateAsync("aura", "running-exp", "hyp", TwoVariants, "metric");
        await svc.UpdateAsync(running.Id, null, null, ExperimentStatus.Running, null);

        var active = await svc.GetActiveAsync("aura");

        Assert.Single(active);
        Assert.Equal("running-exp", active[0].Name);
    }
}
