using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Rpc;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Transport;

/// <summary>
/// <see cref="RabbitMqTransport"/> broker-free unit coverage: identity, argument
/// guards, and the <see cref="RabbitMqTransport.BuildProperties"/> AMQP-property mapping (N1: reserved
/// headers as UTF-8 <c>byte[]</c>, attempt as boxed <see cref="int"/>, persistent + MessageId).
/// </summary>
public sealed class RabbitMqTransportUnitTests
{
    private static RabbitMqTransport BuildTransport()
    {
        var manager = new RabbitMqConnectionManager(
            Microsoft.Extensions.Options.Options.Create(new RabbitMqTransportOptions()),
            Microsoft.Extensions.Options.Options.Create(new UKBatchOptions()),
            NullLogger<RabbitMqConnectionManager>.Instance);
        var replyRouter = new RabbitMqReplyRouter(manager, NullLogger<RabbitMqReplyRouter>.Instance);
        return new RabbitMqTransport(manager, replyRouter, NullLogger<RabbitMqTransport>.Instance);
    }

    private static JobMessage BuildMessage(
        string? source = "orchestrator",
        string? batch = "batch-1",
        string? step = "step-1",
        int attempt = 2)
        => new()
        {
            MessageId = "m-1",
            CorrelationId = "c-1",
            JobName = "DoWork",
            SourceService = source!,
            TargetService = "worker",
            BatchId = batch,
            BatchStepId = step,
            Parameters = new Dictionary<string, object?>(),
            Headers = new Dictionary<string, string>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = attempt,
        };

    [Fact]
    public void Name_IsRabbitMq()
    {
        BuildTransport().Name.Should().Be("RabbitMQ");
    }

    [Fact]
    public async Task PublishAsync_NullMessage_Throws()
    {
        var act = async () => await BuildTransport().PublishAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task RequestReplyAsync_BlankTargetService_Throws(string? target)
    {
        var act = async () => await BuildTransport().RequestReplyAsync(
            target!, BuildMessage(), TimeSpan.FromSeconds(1), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RequestReplyAsync_NullMessage_Throws()
    {
        var act = async () => await BuildTransport().RequestReplyAsync(
            "worker", null!, TimeSpan.FromSeconds(1), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task SubscribeAsync_BlankTopic_Throws(string? topic)
    {
        var transport = BuildTransport();
        var act = async () =>
        {
            await foreach (var _ in transport.SubscribeAsync(topic!, CancellationToken.None))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ===== BuildProperties mapping (N1) =====

    [Fact]
    public void BuildProperties_SetsPersistentAndMessageId()
    {
        var props = RabbitMqTransport.BuildProperties(BuildMessage(), replyTo: null);
        props.Persistent.Should().BeTrue("durability: messages are published persistent");
        props.MessageId.Should().Be("m-1");
    }

    [Fact]
    public void BuildProperties_CorrelationId_FallsBackToMessageId_WhenNull()
    {
        var msg = BuildMessage() with { CorrelationId = null };
        RabbitMqTransport.BuildProperties(msg, replyTo: null)
            .CorrelationId.Should().Be("m-1", "a null CorrelationId falls back to MessageId");
    }

    [Fact]
    public void BuildProperties_CorrelationId_UsesProvidedValue()
    {
        RabbitMqTransport.BuildProperties(BuildMessage(), replyTo: null)
            .CorrelationId.Should().Be("c-1");
    }

    [Fact]
    public void BuildProperties_ReplyTo_Passed_WhenProvided()
    {
        RabbitMqTransport.BuildProperties(BuildMessage(), replyTo: "amq.rabbitmq.reply-to")
            .ReplyTo.Should().Be("amq.rabbitmq.reply-to");
    }

    [Fact]
    public void BuildProperties_ReservedHeaders_AreUtf8ByteArrays()
    {
        // N1: AMQP delivers string headers back as byte[], so they are written as UTF-8 byte[].
        var props = RabbitMqTransport.BuildProperties(BuildMessage(source: "orchestrator"), replyTo: null);
        props.Headers.Should().NotBeNull();

        props.Headers!["x-ukbatch-source"].Should().BeOfType<byte[]>();
        Encoding.UTF8.GetString((byte[])props.Headers["x-ukbatch-source"]!).Should().Be("orchestrator");
        Encoding.UTF8.GetString((byte[])props.Headers["x-ukbatch-batch"]!).Should().Be("batch-1");
        Encoding.UTF8.GetString((byte[])props.Headers["x-ukbatch-step"]!).Should().Be("step-1");
    }

    [Fact]
    public void BuildProperties_AttemptNumber_IsBoxedInt()
    {
        var props = RabbitMqTransport.BuildProperties(BuildMessage(attempt: 5), replyTo: null);
        props.Headers!["x-ukbatch-attempt"].Should().Be(5);
        props.Headers["x-ukbatch-attempt"].Should().BeOfType<int>("attempt is a boxed AMQP int, not a byte[]");
    }

    [Fact]
    public void BuildProperties_OmitsEmptyOptionalHeaders()
    {
        // Null batch/step should not emit those header keys.
        var props = RabbitMqTransport.BuildProperties(
            BuildMessage(batch: null, step: null), replyTo: null);
        props.Headers!.ContainsKey("x-ukbatch-batch").Should().BeFalse();
        props.Headers.ContainsKey("x-ukbatch-step").Should().BeFalse();
    }

    [Fact]
    public void BuildProperties_PreservesCallerSuppliedHeaders()
    {
        var msg = BuildMessage() with
        {
            Headers = new Dictionary<string, string>
            {
                ["traceparent"] = "00-trace-span-01",
            },
        };
        var props = RabbitMqTransport.BuildProperties(msg, replyTo: null);
        Encoding.UTF8.GetString((byte[])props.Headers!["traceparent"]!).Should().Be("00-trace-span-01");
    }
}
