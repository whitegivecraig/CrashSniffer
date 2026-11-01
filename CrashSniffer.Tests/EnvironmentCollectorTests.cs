using CrashSniffer.Collectors;
using Xunit;

namespace CrashSniffer.Tests;

public class EnvironmentCollectorTests
{
    [Theory]
    [InlineData("20240815120000.000000+480", "2024-08-15")]
    [InlineData("20210520000000.000000+000", "2021-05-20")]
    [InlineData("2023-11-25", "2023-11-25")]
    [InlineData("12/31/2022", "2022-12-31")]
    public void ParseWmiDate_ValidDateStrings_ReturnsFormattedIsoDate(string input, string expected)
    {
        string? result = EnvironmentCollector.ParseWmiDate(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("invalid_date")]
    [InlineData("123")]
    public void ParseWmiDate_InvalidOrEmpty_ReturnsNullWithoutException(string? input)
    {
        string? result = EnvironmentCollector.ParseWmiDate(input);
        Assert.Null(result);
    }
}
