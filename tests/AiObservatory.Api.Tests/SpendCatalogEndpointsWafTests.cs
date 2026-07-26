using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class SpendCatalogEndpointsWafTests(AiObservatoryApiFactory factory)
    : IClassFixture<AiObservatoryApiFactory>
{
    private static object NewCategory(string key) =>
        new { Key = key, DisplayName = "Code Review", ColorVar = "--spend-code-review", SortOrder = 10 };

    [Fact]
    public async Task PostCategory_CreatesAndListsIt()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key),
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories",
            TestContext.Current.CancellationToken);
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(key);
    }

    [Fact]
    public async Task PostCategory_WithDuplicateKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostVendor_WithUnknownProvider_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"v-{Guid.NewGuid():N}", DisplayName = "Nope", Provider = "not-a-provider" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostVendor_WithNullProvider_IsAccepted()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"v-{Guid.NewGuid():N}", DisplayName = "CodeRabbit", Provider = (string?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "vendors with no token estimate are the point of a separate vendor axis");
    }

    [Fact]
    public async Task ArchivedCategory_IsExcludedFromTheDefaultList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        var patch = await client.PatchAsJsonAsync($"/api/spend/categories/{id}",
            new { Archived = true }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories", TestContext.Current.CancellationToken);
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().NotContain(key);

        var all = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories?includeArchived=true",
            TestContext.Current.CancellationToken);
        all.EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(key, "history still references archived categories");
    }

    [Fact]
    public async Task PostCategory_WithNonSlugKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/categories",
            new { Key = "Not A Slug!", DisplayName = "Code Review", ColorVar = "--spend-code-review", SortOrder = 10 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCategory_WithOverlongDisplayName_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/spend/categories",
            new { Key = key, DisplayName = new string('x', 101), ColorVar = "--spend-code-review", SortOrder = 10 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostVendor_WithUnknownDefaultCategory_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "Ghost Vendor",
                Provider = (string?)null,
                DefaultCategoryId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchCategory_WithUnknownId_IsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PatchAsJsonAsync($"/api/spend/categories/{Guid.NewGuid()}",
            new { DisplayName = "Anything" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchCategory_Unarchiving_RestoresItToTheList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        await client.PatchAsJsonAsync($"/api/spend/categories/{id}",
            new { Archived = true }, TestContext.Current.CancellationToken);

        var unarchive = await client.PatchAsJsonAsync($"/api/spend/categories/{id}",
            new { Archived = false }, TestContext.Current.CancellationToken);
        unarchive.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories", TestContext.Current.CancellationToken);
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(key, "un-archiving restores it to the default (non-archived) list");
    }

    [Fact]
    public async Task PatchVendor_RenamesAndRepointsDefaultCategory()
    {
        using var client = factory.CreateAdminClient();
        var categoryKey = $"cat-{Guid.NewGuid():N}";

        var categoryCreated = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(categoryKey),
            TestContext.Current.CancellationToken);
        var categoryId = (await categoryCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        var vendorCreated = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"v-{Guid.NewGuid():N}", DisplayName = "Old Name", Provider = (string?)null },
            TestContext.Current.CancellationToken);
        var vendorId = (await vendorCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        var patch = await client.PatchAsJsonAsync($"/api/spend/vendors/{vendorId}",
            new { DisplayName = "New Name", DefaultCategoryId = categoryId }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await patch.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("displayName").GetString().Should().Be("New Name");
        body.GetProperty("defaultCategoryId").GetGuid().Should().Be(categoryId);
    }
}
