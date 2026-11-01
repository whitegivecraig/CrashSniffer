using CrashSniffer.Collectors;
using CrashSniffer.Models;
using Xunit;

namespace CrashSniffer.Tests;

public class WheaDetailParserTests
{
    [Fact]
    public void Enrich_ErrorSourceType_0_IdentifiesCpu()
    {
        var ev = new CrashEvent();
        string xml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event"">
            <EventData>
                <Data Name=""ErrorSourceType"">0</Data>
            </EventData>
        </Event>";

        WheaDetailParser.Enrich(ev, 18, xml);

        Assert.Contains("CPU / 主板供电", ev.WheaHardwarePart);
    }

    [Fact]
    public void Enrich_ApicId_PinpointsLogicalAndPhysicalCoreAndAddsCoAdvice()
    {
        var ev = new CrashEvent();
        string xml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event"">
            <EventData>
                <Data Name=""ErrorSourceType"">0</Data>
                <Data Name=""ApicId"">6</Data>
            </EventData>
        </Event>";

        WheaDetailParser.Enrich(ev, 18, xml);

        Assert.Equal(6, ev.WheaApicId);
        Assert.Contains("逻辑核 #6", ev.WheaHardwarePart);
        Assert.Contains("物理核 #3", ev.WheaHardwarePart);
        Assert.Contains(ev.Troubleshooting, s => s.Contains("Curve Optimizer") && s.Contains("#3"));
    }

    [Fact]
    public void Enrich_ErrorSourceType_4_IdentifiesPcie()
    {
        var ev = new CrashEvent();
        string xml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event"">
            <EventData>
                <Data Name=""ErrorSourceType"">4</Data>
            </EventData>
        </Event>";

        WheaDetailParser.Enrich(ev, 46, xml);

        Assert.Contains("PCIe 设备", ev.WheaHardwarePart);
    }

    [Fact]
    public void Enrich_ErrorType_10_IdentifiesMemory()
    {
        var ev = new CrashEvent { WheaErrorType = "Memory Controller" };
        string xml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event"">
            <EventData>
                <Data Name=""ErrorSourceType"">2</Data>
                <Data Name=""ErrorType"">10</Data>
            </EventData>
        </Event>";

        WheaDetailParser.Enrich(ev, 17, xml);

        Assert.Contains("内存条", ev.WheaHardwarePart);
    }

    [Theory]
    [InlineData(18, "CPU")]
    [InlineData(19, "CPU")]
    [InlineData(46, "PCIe")]
    [InlineData(47, "PCIe")]
    public void Enrich_FallbackByEventId_WhenXmlLacksSourceType(int eventId, string expectedHardware)
    {
        var ev = new CrashEvent();
        string emptyXml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event""><EventData></EventData></Event>";

        WheaDetailParser.Enrich(ev, eventId, emptyXml);

        Assert.Contains(expectedHardware, ev.WheaHardwarePart);
    }

    [Fact]
    public void Enrich_MalformedXml_DoesNotThrow()
    {
        var ev = new CrashEvent();
        WheaDetailParser.Enrich(ev, 18, "invalid << xml");

        Assert.Contains("CPU", ev.WheaHardwarePart);
    }
}
