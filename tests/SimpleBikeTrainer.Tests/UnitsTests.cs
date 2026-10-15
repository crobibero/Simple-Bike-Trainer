namespace SimpleBikeTrainer.Tests;

public class UnitsTests
{
    [Fact]
    public void ConvertsForDisplay()
    {
        Assert.Equal("10.00 km", UnitSystem.Metric.FormatDistance(10000));
        Assert.Equal("6.21 mi", UnitSystem.Imperial.FormatDistance(10000));
        Assert.Equal("36.0 km/h", UnitSystem.Metric.FormatSpeed(10));
        Assert.Equal("22.4 mph", UnitSystem.Imperial.FormatSpeed(10));
        Assert.Equal("80 kg", UnitSystem.Metric.FormatWeight(80));
        Assert.Equal("176.4 lb", UnitSystem.Imperial.FormatWeight(80));
        Assert.Equal(80, UnitSystem.Imperial.WeightToKg(176.37), 2);
    }

    [Fact]
    public void ConfigStoresUnitsByName()
    {
        var dir = Directory.CreateTempSubdirectory("trainer-units").FullName;
        try
        {
            var config = AppConfig.Load(Path.Combine(dir, "config.json"));
            Assert.Equal(UnitSystem.Metric, config.Units);
            config.Units = UnitSystem.Imperial;
            config.Save();

            Assert.Contains("\"units\": \"imperial\"", File.ReadAllText(config.Path), StringComparison.Ordinal);
            Assert.Equal(UnitSystem.Imperial, AppConfig.Load(config.Path).Units);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
