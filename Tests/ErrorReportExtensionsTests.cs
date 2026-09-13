// Validates: Requirements 2.1, 3.1
using EbenezerBackend.LoadTests.Reporting;
using NBomber.Contracts.Stats;
using Xunit;

namespace EbenezerBackend.LoadTests.Tests;

/// <summary>
/// Example tests for <see cref="ErrorReportExtensions.ExtractFailures"/>.
/// Validates that the adapter correctly projects the NBomber <see cref="NodeStats"/>
/// hierarchy into a flat sequence of <see cref="StepFailure"/> records.
/// </summary>
public sealed class ErrorReportExtensionsTests
{
    /// <summary>
    /// Constructs a <see cref="NodeStats"/> with two scenarios and three step-level
    /// failure status codes and verifies that <see cref="ErrorReportExtensions.ExtractFailures"/>
    /// projects every field (StepName, Message, StatusCode, Count) correctly.
    /// </summary>
    [Fact]
    public void ExtractFailures_ProjectsCorrectly()
    {
        // Arrange ----------------------------------------------------------------
        // StatusCodeStats entries for step "01_register"
        var sc1 = new StatusCodeStats
        {
            StatusCode = "500",
            IsError    = true,
            Message    = "Internal Server Error",
            Count      = 3,
            Percent    = 60,
        };
        var sc2 = new StatusCodeStats
        {
            StatusCode = "503",
            IsError    = true,
            Message    = "Service Unavailable",
            Count      = 2,
            Percent    = 40,
        };

        // StatusCodeStats entry for step "02_login" (Count = 0 → should be excluded)
        var sc_zero = new StatusCodeStats
        {
            StatusCode = "401",
            IsError    = true,
            Message    = "Unauthorized",
            Count      = 0,
            Percent    = 0,
        };

        // StatusCodeStats entry for step "02_login" (Count = 1 → should be included)
        var sc3 = new StatusCodeStats
        {
            StatusCode = "404",
            IsError    = true,
            Message    = "Not Found",
            Count      = 1,
            Percent    = 100,
        };

        // Build StepStats → ScenarioStats → NodeStats
        var step1 = new StepStats
        {
            StepName = "01_register",
            Fail     = new MeasurementStats { StatusCodes = new[] { sc1, sc2 } },
            Ok       = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
        };
        var step2 = new StepStats
        {
            StepName = "02_login",
            Fail     = new MeasurementStats { StatusCodes = new[] { sc_zero, sc3 } },
            Ok       = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
        };

        var scenario = new ScenarioStats
        {
            ScenarioName = "write_heavy",
            StepStats    = new[] { step1, step2 },
            Ok           = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
            Fail         = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
        };

        var nodeStats = new NodeStats
        {
            ScenarioStats = new[] { scenario },
        };

        // Act --------------------------------------------------------------------
        var failures = ErrorReportExtensions.ExtractFailures(nodeStats).ToList();

        // Assert -----------------------------------------------------------------
        // Only entries with Count > 0 are projected: sc1, sc2, sc3
        Assert.Equal(3, failures.Count);

        // Verify first failure: sc1
        var f1 = failures[0];
        Assert.Equal("01_register",          f1.StepName);
        Assert.Equal("Internal Server Error", f1.Message);
        Assert.Equal("500",                  f1.StatusCode);
        Assert.Equal(3,                      f1.Count);
        Assert.Equal("write_heavy",          f1.ScenarioName);

        // Verify second failure: sc2
        var f2 = failures[1];
        Assert.Equal("01_register",         f2.StepName);
        Assert.Equal("Service Unavailable", f2.Message);
        Assert.Equal("503",                 f2.StatusCode);
        Assert.Equal(2,                     f2.Count);
        Assert.Equal("write_heavy",         f2.ScenarioName);

        // Verify third failure: sc3 (sc_zero skipped because Count == 0)
        var f3 = failures[2];
        Assert.Equal("02_login",  f3.StepName);
        Assert.Equal("Not Found", f3.Message);
        Assert.Equal("404",       f3.StatusCode);
        Assert.Equal(1,           f3.Count);
        Assert.Equal("write_heavy", f3.ScenarioName);
    }

    /// <summary>
    /// When no steps have failure status codes with Count > 0, ExtractFailures returns
    /// an empty sequence without throwing.
    /// </summary>
    [Fact]
    public void ExtractFailures_EmptyStats_ReturnsEmpty()
    {
        var nodeStats = new NodeStats
        {
            ScenarioStats = Array.Empty<ScenarioStats>(),
        };

        var failures = ErrorReportExtensions.ExtractFailures(nodeStats).ToList();

        Assert.Empty(failures);
    }

    /// <summary>
    /// A step with only Count == 0 status codes contributes no failures.
    /// </summary>
    [Fact]
    public void ExtractFailures_AllCountsZero_ReturnsEmpty()
    {
        var scZero = new StatusCodeStats { StatusCode = "500", IsError = true, Message = "error", Count = 0 };
        var step   = new StepStats
        {
            StepName = "01_register",
            Fail     = new MeasurementStats { StatusCodes = new[] { scZero } },
            Ok       = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
        };
        var scenario = new ScenarioStats
        {
            ScenarioName = "s",
            StepStats    = new[] { step },
            Ok           = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
            Fail         = new MeasurementStats { StatusCodes = Array.Empty<StatusCodeStats>() },
        };

        var nodeStats = new NodeStats { ScenarioStats = new[] { scenario } };
        var failures  = ErrorReportExtensions.ExtractFailures(nodeStats).ToList();

        Assert.Empty(failures);
    }
}
