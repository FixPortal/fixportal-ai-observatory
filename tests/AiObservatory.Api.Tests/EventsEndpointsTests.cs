using AiObservatory.Api.Endpoints;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class EventsEndpointsTests
{
    [Theory]
    [InlineData("model\r\nforged", "model  forged")]
    [InlineData("ordinary-model", "ordinary-model")]
    public void PricingLogValueCannotInjectANewLogLine(string value, string expected)
    {
        EventsEndpoints.SanitizeLogValue(value).Should().Be(expected);
    }
}
