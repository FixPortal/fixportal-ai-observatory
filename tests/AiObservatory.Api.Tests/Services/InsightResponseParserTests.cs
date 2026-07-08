using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class InsightResponseParserTests
{
    [Fact]
    public void Parse_returns_insight_records_from_valid_json()
    {
        var json = """
            [
              {"type":"summary","title":"Daily summary","body":"You spent $4.12 yesterday.","data":{}},
              {"type":"efficiency","title":"Opus overuse","body":"41% of Opus calls under 400 tokens.","data":{"estimatedWeeklySaving":9.20}}
            ]
            """;

        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);
        var results = sut.Parse(json, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        results.Should().HaveCount(2);
        results[0].InsightType.Should().Be(InsightType.Summary);
        results[1].InsightType.Should().Be(InsightType.Efficiency);
        results[1].Body.Should().Contain("400 tokens");
    }
}
