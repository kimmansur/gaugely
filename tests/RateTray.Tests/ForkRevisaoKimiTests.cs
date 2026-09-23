using RateTray.Model;
using Xunit;

namespace RateTray.Tests;

public class ForkRevisaoKimiTests
{
    [Fact]
    public void FormatValue_ReturnsPercent_ForNormalReading()
    {
        var reading = new LimitReading
        {
            Id = "test.percent",
            Label = "Test",
            Group = "TestGroup",
            Percent = 42.6
        };

        var result = LimitReading.FormatValue(reading);

        Assert.Equal("43 %", result);
    }

    [Fact]
    public void FormatValue_ReturnsAmountWithUnit_ForInformationalReading()
    {
        var reading = new LimitReading
        {
            Id = "test.amount",
            Label = "Test",
            Group = "TestGroup",
            Amount = 17.5m,
            AmountUnit = "US$"
        };

        var result = LimitReading.FormatValue(reading);

        // O ToString("0.00") usará a cultura local, então verificamos se contém os valores esperados
        Assert.StartsWith("US$ ", result);
        Assert.Contains("17", result);
        Assert.Contains("50", result);
    }

    [Fact]
    public void FormatValue_ReturnsAmountWithoutUnit_WhenUnitIsNull()
    {
        var reading = new LimitReading
        {
            Id = "test.amount",
            Label = "Test",
            Group = "TestGroup",
            Amount = 9.9m
        };

        var result = LimitReading.FormatValue(reading);

        Assert.DoesNotContain("US$", result);
        Assert.Contains("9", result);
        Assert.Contains("90", result);
    }

    [Fact]
    public void FormatValue_ReturnsPercent_ForInformationalReadingWithNullAmount()
    {
        var reading = new LimitReading
        {
            Id = "test.null_amount",
            Label = "Test",
            Group = "TestGroup",
            Percent = 10,
            AmountUnit = "US$"
        };

        var result = LimitReading.FormatValue(reading);

        Assert.Equal("10 %", result);
    }
}
