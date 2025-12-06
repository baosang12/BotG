#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.Indicators.Interfaces;
using NSubstitute;
using Xunit;

namespace AnalysisModule.Preprocessor.Tests.Core;

public sealed class AnalysisPreprocessorEngineTests
{
    private static PreprocessorOptions CreateOptions() => new(
        new[] { TimeFrame.M1 },
        new[] { "SMA" },
        maxRecentTicks: 10,
        snapshotDebounce: TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task ProcessTickAsync_ProducesSnapshot()
    {
        var orchestrator = Substitute.For<IIndicatorOrchestrator>();
        orchestrator.CalculateAsync(Arg.Any<PreprocessorContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyDictionary<string, IndicatorResult>>(new Dictionary<string, IndicatorResult>
            {
                ["SMA"] = new("SMA", 10)
            }));

        var engine = new AnalysisPreprocessorEngine(orchestrator);
        var source = Substitute.For<IPreprocessorSource>();
        engine.Start(source, CreateOptions());

        PreprocessorSnapshot? snapshot = null;
        engine.SnapshotGenerated += (_, s) => snapshot = s;

        await engine.ProcessTickAsync(new Tick(DateTime.UtcNow, 10, 11, 1));

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot!.Indicators["SMA"]);
        Assert.False(snapshot.IsDegraded);
    }

    [Fact]
    public async Task ProcessTickAsync_DegradedWhenCalculatorThrows()
    {
        var orchestrator = Substitute.For<IIndicatorOrchestrator>();
        orchestrator.CalculateAsync(Arg.Any<PreprocessorContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<IReadOnlyDictionary<string, IndicatorResult>>(new InvalidOperationException("boom")));

        var engine = new AnalysisPreprocessorEngine(orchestrator);
        var source = Substitute.For<IPreprocessorSource>();
        engine.Start(source, CreateOptions());

        PreprocessorSnapshot? snapshot = null;
        engine.SnapshotGenerated += (_, s) => snapshot = s;

        await engine.ProcessTickAsync(new Tick(DateTime.UtcNow, 10, 11, 1));

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsDegraded);
        Assert.Equal("boom", snapshot.DegradedReason);
    }

    [Fact]
    public async Task GetStatus_ReturnsRunningState()
    {
        var orchestrator = Substitute.For<IIndicatorOrchestrator>();
        orchestrator.CalculateAsync(Arg.Any<PreprocessorContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyDictionary<string, IndicatorResult>>(new Dictionary<string, IndicatorResult>()));

        var engine = new AnalysisPreprocessorEngine(orchestrator);
        var source = Substitute.For<IPreprocessorSource>();
        engine.Start(source, CreateOptions());

        await engine.ProcessTickAsync(new Tick(DateTime.UtcNow, 10, 11, 1));

        var status = engine.GetStatus();
        Assert.Equal(PreprocessorState.Running, status.State);
        Assert.Equal(1, status.ProcessedTicks);
        Assert.NotNull(status.LastTickTimestampUtc);
    }
}
