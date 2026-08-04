using System.Text;
using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public sealed class IdeEventValidationTests
{
    [Fact]
    public void AcceptsTheByteExactIdeEnvelope()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));

        var envelope = IdeEndpoints.ParseEvent(bytes, Instant.FromUtc(2026, 8, 4, 12, 1));

        envelope.EventType.Should().Be("routing.decided");
        envelope.Classification.Should().Be(0);
        envelope.Identity.MissionId.Value.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public void RejectsContentOrIdentityOutsideTheClosedContract(string json)
    {
        var parse = () => IdeEndpoints.ParseEvent(Encoding.UTF8.GetBytes(json), Instant.FromUtc(2026, 8, 4, 12, 1));

        parse.Should().Throw<InvalidDataException>();
    }

    public static TheoryData<string> InvalidEnvelopes
    {
        get
        {
            var valid = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));
            return
            [
                valid.Replace("\"routing.decided\"", "\"unknown.event\"", StringComparison.Ordinal),
                valid.Replace("\"classification\":0", "\"classification\":1", StringComparison.Ordinal),
                valid.Replace(
                    "753cb584-cd0b-4e16-9f08-6c0ce130a84a",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    StringComparison.Ordinal
                ),
                valid.Replace("\"role\":\"implementer\"", "\"role\":\" \"", StringComparison.Ordinal),
                valid.Replace(
                    "\"routingRuleVersion\":\"assisted-v1\"",
                    "\"routingRuleVersion\":\"assisted-v1\",\"rawPrompt\":\"no\"",
                    StringComparison.Ordinal
                ),
                valid.Replace(
                    "\"selectedModelId\":\"gpt-5.6-sol\"",
                    "\"selectedModelId\":\"gpt-5.6-sol\",\"selectedModelId\":\"claude-fable-5\"",
                    StringComparison.Ordinal
                ),
                valid.Replace(
                    "\"classification\":0",
                    "\"classification\":0,\"unknown\":true",
                    StringComparison.Ordinal
                ),
                valid.Replace("2026-08-04T12:00:00Z", "2026-08-04T12:02:00Z", StringComparison.Ordinal),
            ];
        }
    }
}
