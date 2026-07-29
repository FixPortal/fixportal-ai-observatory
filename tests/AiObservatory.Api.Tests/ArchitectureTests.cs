// Ingest is an aliased reference (see this project's .csproj): both it and the API expose a
// public global-namespace Program, so an unaliased reference makes bare `Program` ambiguous
// and breaks every WebApplicationFactory test in this assembly.
extern alias ingest;
using System.Reflection;
using AiObservatory.Api.Services;
using AiObservatory.Data;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using AwesomeAssertions;
using FixPortal.CodeStyle.ArchRules;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace AiObservatory.Api.Tests;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(BudgetAlertService).Assembly,  // anchor: update if BudgetAlertService is renamed/moved
            typeof(AiObservatoryDbContext).Assembly,
            typeof(ingest::AiObservatory.Ingest.ProviderPollingWorkerService).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> ApiTypes =
        Types().That()
            .ResideInNamespace("AiObservatory.Api")
            .Or().ResideInNamespace("AiObservatory.Api.Endpoints")
            .Or().ResideInNamespace("AiObservatory.Api.Services")
            .Or().ResideInNamespace("AiObservatory.Api.Services.Fx")
            .Or().ResideInNamespace("AiObservatory.Api.Services.GitHub")
            .Or().ResideInNamespace("AiObservatory.Api.Services.Intelligence")
            .As("Api types");

    private static readonly IObjectProvider<IType> IngestTypes =
        Types().That()
            .ResideInNamespace("AiObservatory.Ingest")
            .Or().ResideInNamespace("AiObservatory.Ingest.Services.Anthropic")
            .Or().ResideInNamespace("AiObservatory.Ingest.Services.Copilot")
            .Or().ResideInNamespace("AiObservatory.Ingest.Services.Google")
            .Or().ResideInNamespace("AiObservatory.Ingest.Services.OpenAi")
            .As("Ingest types");

    [Fact]
    public void Interfaces_must_have_I_prefix()
    {
        FixPortalArchRules.InterfacesMustHaveIPrefix()
            .Check(Architecture);
    }

    [Fact]
    public void Model_types_must_be_sealed()
    {
        FixPortalArchRules.ModelTypesMustBeSealed("AiObservatory.Data.Entities")
            .Check(Architecture);
    }

    [Fact]
    public void Api_must_not_depend_on_Ingest()
    {
        FixPortalArchRules.LayerMustNotDependOn(ApiTypes, IngestTypes)
            .Check(Architecture);
    }

    [Fact]
    public void Ingest_must_not_depend_on_Api()
    {
        FixPortalArchRules.LayerMustNotDependOn(IngestTypes, ApiTypes)
            .Check(Architecture);
    }

    // The ledger deliberately holds no link back to a bank, card, invoice or
    // counterparty — that is the privacy boundary that let billed spend live in a
    // public repo at all (spec §3). A convention would erode; this makes it fail
    // the build. If a future feature genuinely needs one of these, that is a
    // design decision to reopen in the spec, not a test to relax.
    [Fact]
    public void SpendEntry_must_not_carry_bank_linkage()
    {
        var forbidden = new[] { "account", "card", "counterparty", "iban", "sortcode", "transactionid" };

        var offenders = typeof(AiObservatory.Data.Entities.SpendEntry)
            .GetProperties()
            .Where(p => forbidden.Any(f =>
                p.Name.Replace("_", "").Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "SpendEntry must not tie spend to a bank, card, invoice or counterparty (spec §3)");
    }

    // A class that takes AiObservatoryApiFactory boots the real host and migrates a
    // throwaway Postgres database. Untraited, it lands in the unit lane — which is the
    // lane Stryker mutates against, so every mutant pays a host boot plus a migration.
    // Three such classes shipped untraited and pushed the nightly mutation run past its
    // 45-minute budget. The trait is the only thing keeping them out; assert it.
    [Fact]
    public void Factory_backed_test_classes_must_be_traited_Integration()
    {
        var offenders = typeof(ArchitectureTests).Assembly
            .GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(AiObservatoryApiFactory))))
            .Where(t => !t.GetCustomAttributes<TraitAttribute>()
                .Any(a => a.Name == "Category" && a.Value == "Integration"))
            .Select(t => t.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "every AiObservatoryApiFactory-backed test class needs [Trait(\"Category\", \"Integration\")] "
            + "to stay out of the unit lane Stryker mutates against");
    }
}
