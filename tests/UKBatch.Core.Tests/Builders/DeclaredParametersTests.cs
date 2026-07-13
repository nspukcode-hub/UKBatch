using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using Xunit;

namespace UKBatch.Core.Tests.Builders;

/// <summary>
/// Declared job parameters: the <c>WithParameter&lt;T&gt;</c> fluent surface, the type→kind mapping, the
/// required→null default rule, accumulation onto the definition, and the registration-time shape checks
/// (blank / reserved-prefix / duplicate).
/// </summary>
[Collection("process-wide attribute discovery")]
public class DeclaredParametersTests
{
    private enum SampleEnum { A, B }
    private sealed record SampleRecord(int X);

    [Theory]
    [InlineData(typeof(string), ParameterValueKind.String)]
    [InlineData(typeof(bool), ParameterValueKind.Boolean)]
    [InlineData(typeof(int), ParameterValueKind.Integer)]
    [InlineData(typeof(long), ParameterValueKind.Integer)]
    [InlineData(typeof(byte), ParameterValueKind.Integer)]
    [InlineData(typeof(double), ParameterValueKind.Number)]
    [InlineData(typeof(float), ParameterValueKind.Number)]
    [InlineData(typeof(decimal), ParameterValueKind.Number)]
    [InlineData(typeof(DateTime), ParameterValueKind.DateTime)]
    [InlineData(typeof(DateTimeOffset), ParameterValueKind.DateTime)]
    [InlineData(typeof(int?), ParameterValueKind.Integer)]
    [InlineData(typeof(DateTime?), ParameterValueKind.DateTime)]
    [InlineData(typeof(Guid), ParameterValueKind.Object)]
    [InlineData(typeof(SampleEnum), ParameterValueKind.String)]
    [InlineData(typeof(SampleRecord), ParameterValueKind.Object)]
    public void KindFromClrType_MapsExpectedKind(Type clrType, ParameterValueKind expected)
        => JobParameterDescriptor.KindFromClrType(clrType).Should().Be(expected);

    [Fact]
    public void Create_Required_DropsDefaultValue()
    {
        var d = JobParameterDescriptor.Create<int>("x", defaultValue: 7, required: true, description: "d");

        d.Required.Should().BeTrue();
        d.DefaultValue.Should().BeNull("a required parameter has no meaningful default");
        d.Kind.Should().Be(ParameterValueKind.Integer);
    }

    [Fact]
    public void Create_Optional_KeepsBoxedDefault_IncludingDefaultOfT()
    {
        JobParameterDescriptor.Create<int>("a", defaultValue: 0, required: false, description: null)
            .DefaultValue.Should().Be(0);
        JobParameterDescriptor.Create<string>("b", defaultValue: "hi", required: false, description: null)
            .DefaultValue.Should().Be("hi");
    }

    [Fact]
    public void Create_BlankName_Throws()
    {
        var act = () => JobParameterDescriptor.Create<string>("  ", null, false, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithParameter_AccumulatesInRegistrationOrder()
    {
        var services = Host();
        services.AddUKBatch(b => b.AddJob<SucceedingJob>().Named("p.job")
            .WithParameter<string>("orderId", required: true, description: "id")
            .WithParameter<int>("retries", defaultValue: 3));

        using var sp = services.BuildServiceProvider();
        var def = sp.GetRequiredService<JobDefinitionRegistry>().TryGet("p.job")!;

        def.DeclaredParameters.Select(p => p.Name).Should().Equal("orderId", "retries");
        def.DeclaredParameters[0].Required.Should().BeTrue();
        def.DeclaredParameters[0].DefaultValue.Should().BeNull();
        def.DeclaredParameters[1].Kind.Should().Be(ParameterValueKind.Integer);
        def.DeclaredParameters[1].DefaultValue.Should().Be(3);
    }

    [Fact]
    public void NoWithParameter_LeavesDeclaredParametersEmpty()
    {
        var services = Host();
        services.AddUKBatch(b => b.AddJob<SucceedingJob>().Named("plain.job"));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<JobDefinitionRegistry>().TryGet("plain.job")!.DeclaredParameters.Should().BeEmpty();
    }

    [Fact]
    public void WithParameter_MutatingBuilderListAfterRegistration_DoesNotAlterDefinition()
    {
        // The factory defensive-copies the accumulated list; a later WithParameter on a *different* job
        // must not bleed in. (Distinct builders, distinct lists.)
        var services = Host();
        services.AddUKBatch(b =>
        {
            b.AddJob<SucceedingJob>().Named("j1").WithParameter<string>("a");
            b.AddJob<FailingJob>().Named("j2").WithParameter<string>("b");
        });

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<JobDefinitionRegistry>();
        registry.TryGet("j1")!.DeclaredParameters.Select(p => p.Name).Should().Equal("a");
        registry.TryGet("j2")!.DeclaredParameters.Select(p => p.Name).Should().Equal("b");
    }

    [Fact]
    public void WithParameter_ReservedPrefix_ThrowsAtRegistration()
    {
        var act = () => Host().AddUKBatch(b => b.AddJob<SucceedingJob>().Named("r.job")
            .WithParameter<string>("ukbatch.foo"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithParameter_DuplicateName_ThrowsAtRegistration()
    {
        var act = () => Host().AddUKBatch(b => b.AddJob<SucceedingJob>().Named("d.job")
            .WithParameter<string>("x").WithParameter<int>("x"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithParameter_BlankName_ThrowsImmediately()
    {
        var act = () => Host().AddUKBatch(b => b.AddJob<SucceedingJob>().WithParameter<string>("   "));
        act.Should().Throw<ArgumentException>();
    }

    private static IServiceCollection Host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());
        return services;
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted { get; } = default;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped { get; } = default;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
