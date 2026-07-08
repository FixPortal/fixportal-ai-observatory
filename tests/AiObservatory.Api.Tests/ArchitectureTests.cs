using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using AiObservatory.Api.Services;
using AiObservatory.Data;
using AiObservatory.Ingest;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace AiObservatory.Api.Tests;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(BudgetAlertService).Assembly,  // anchor: update if BudgetAlertService is renamed/moved
            typeof(AiObservatoryDbContext).Assembly,
            typeof(ProviderPollingWorkerService).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> ApiTypes =
        Types().That()
            .ResideInNamespace("AiObservatory.Api")
            .Or().ResideInNamespace("AiObservatory.Api.Endpoints")
            .Or().ResideInNamespace("AiObservatory.Api.Services")
            .Or().ResideInNamespace("AiObservatory.Api.Services.Fx")
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
}
