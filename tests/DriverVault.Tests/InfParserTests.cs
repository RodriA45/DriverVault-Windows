using DriverVault.Services;
using Xunit;

namespace DriverVault.Tests;

public class InfParserTests
{
    [Fact]
    public void ParseLines_ResolvesStringTokensAndDriverVer()
    {
        string[] lines =
        [
            "[Version]",
            "Signature=\"$WINDOWS NT$\"",
            "Class=Net ; Comentario",
            "Provider=%Mfg%",
            "DriverVer=08/15/2025,10.68.815.2025",
            "CatalogFile=rt640x64.cat",
            "",
            "[Strings]",
            "Mfg=\"Realtek Semiconductor Corp.\""
        ];

        InfMetadata meta = InfParser.ParseLines(lines);

        Assert.Equal("Realtek Semiconductor Corp.", meta.Provider);
        Assert.Equal("Net", meta.DeviceClass);
        Assert.Equal("10.68.815.2025", meta.Version);
        Assert.Equal("2025-08-15", meta.DriverDate);
        Assert.Equal("rt640x64.cat", meta.CatalogFile);
    }
}
