﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus;
using NServiceBus.AcceptanceTesting;
using NServiceBus.Faults;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NUnit.Framework;
using Conventions = NServiceBus.AcceptanceTesting.Customization.Conventions;

public class Retry : BridgeAcceptanceTest
{
    [Test]
    public async Task Should_work()
    {
        var ctx = await Scenario.Define<Context>()
            .WithEndpoint<ProcessingEndpoint>(builder =>
            {
                builder.DoNotFailOnErrorMessages();
                builder.When(c => c.EndpointsStarted, (session, _) => session.SendLocal(new FaultyMessage()));
            })
            .WithEndpoint<FakeSCError>()
            .WithBridge(bridgeConfiguration =>
            {
                var bridgeTransport = new TestableBridgeTransport(DefaultTestServer.GetTestTransportDefinition())
                {
                    Name = "DefaultTestingTransport"
                };
                bridgeTransport.AddTestEndpoint<FakeSCError>();
                bridgeConfiguration.AddTransport(bridgeTransport);

                var theOtherTransport = new TestableBridgeTransport(TransportBeingTested);
                theOtherTransport.AddTestEndpoint<ProcessingEndpoint>();
                bridgeConfiguration.AddTransport(theOtherTransport);
            })
            .Done(c => c.GotRetrySuccessfullAck)
            .Run();

        Assert.IsTrue(ctx.MessageFailed);
        Assert.IsTrue(ctx.RetryDelivered);
        Assert.IsTrue(ctx.GotRetrySuccessfullAck);

        foreach (var header in ctx.FailedMessageHeaders)
        {
            if (ctx.ReceivedMessageHeaders.TryGetValue(header.Key, out var receivedHeaderValue))
            {
                if (header.Key == Headers.ReplyToAddress)
                {
                    Assert.IsTrue(receivedHeaderValue.ToLower().Contains(nameof(ProcessingEndpoint).ToLower()),
                        $"The ReplyToAddress received by ServiceControl ({TransportBeingTested} physical address) should contain the logical name of the endpoint.");
                }
                else
                {
                    Assert.AreEqual(header.Value, receivedHeaderValue,
                        $"{header.Key} is not the same on processed message and message sent to the error queue");
                }
            }
        }
    }

    [Test]
    public async Task Should_log_warn_when_best_effort_ReplyToAddress_fails()
    {
        var ctx = await Scenario.Define<Context>()
            .WithEndpoint<SendingEndpoint>(builder =>
            {
                builder.DoNotFailOnErrorMessages();
                builder.When(c => c.EndpointsStarted, (session, _) => session.Send(new FaultyMessage()));
            })
            .WithEndpoint<ProcessingEndpoint>(builder =>
            {
                builder.DoNotFailOnErrorMessages();
                builder.When(c => c.EndpointsStarted, (session, _) =>
                {
                    var options = new SendOptions();
                    options.RouteToThisEndpoint();
                    options.SetHeader(Headers.ReplyToAddress, "address-not-declared-in-the-bridge");
                    return session.Send(new FaultyMessage(), options);
                });
            })
            .WithEndpoint<FakeSCError>()
            .WithBridge(bridgeConfiguration =>
            {
                var bridgeTransport = new TestableBridgeTransport(DefaultTestServer.GetTestTransportDefinition())
                {
                    Name = "DefaultTestingTransport"
                };
                bridgeTransport.AddTestEndpoint<FakeSCError>();
                bridgeConfiguration.AddTransport(bridgeTransport);

                var theOtherTransport = new TestableBridgeTransport(TransportBeingTested);
                theOtherTransport.AddTestEndpoint<ProcessingEndpoint>();
                bridgeConfiguration.AddTransport(theOtherTransport);
            })
            .Done(c => c.GotRetrySuccessfullAck)
            .Run();

        var translationFailureLogs = ctx.Logs.ToArray().Where(i =>
            i.Message.Contains("Could not translate") &&
            i.Message.Contains("address. Consider using `.HasEndpoint()`"));

        //There is only one warning here because the ServiceControl testing fake does not properly set the ReplyToAddress header value
        Assert.AreEqual(1, translationFailureLogs.Count(),
            "Bridge should log warnings when ReplyToAddress cannot be translated for failed message and retry.");
    }

    public class SendingEndpoint : EndpointConfigurationBuilder
    {
        public SendingEndpoint() => EndpointSetup<DefaultServer>(c =>
        {
            c.SendFailedMessagesTo(Conventions.EndpointNamingConvention(typeof(FakeSCError)));
            c.ConfigureRouting().RouteToEndpoint(typeof(FaultyMessage), Conventions.EndpointNamingConvention(typeof(ProcessingEndpoint)));
        });
    }

    public class ProcessingEndpoint : EndpointConfigurationBuilder
    {
        public ProcessingEndpoint() => EndpointSetup<DefaultServer>(c =>
        {
            c.SendFailedMessagesTo(Conventions.EndpointNamingConvention(typeof(FakeSCError)));
        });

        public class MessageHandler : IHandleMessages<FaultyMessage>
        {
            readonly Context testContext;

            public MessageHandler(Context context) => testContext = context;

            public Task Handle(FaultyMessage message, IMessageHandlerContext context)
            {
                if (testContext.MessageFailed)
                {
                    testContext.RetryDelivered = true;
                    return Task.CompletedTask;
                }

                testContext.ReceivedMessageHeaders =
                new ReadOnlyDictionary<string, string>((IDictionary<string, string>)context.MessageHeaders);

                throw new Exception("Simulated");
            }
        }
    }

    public class FakeSCError : EndpointConfigurationBuilder
    {
        public FakeSCError() => EndpointSetup<DefaultTestServer>((c, runDescriptor) =>
                c.Pipeline.Register(new ControlMessageBehavior(runDescriptor.ScenarioContext as Context), "Checks that the retry confirmation arrived"));

        class FailedMessageHander : IHandleMessages<FaultyMessage>
        {
            public FailedMessageHander(Context context) => testContext = context;

            public Task Handle(FaultyMessage message, IMessageHandlerContext context)
            {
                testContext.FailedMessageHeaders =
                    new ReadOnlyDictionary<string, string>((IDictionary<string, string>)context.MessageHeaders);

                testContext.MessageFailed = true;

                var sendOptions = new SendOptions();

                //Send the message to the FailedQ address
                string destination = context.MessageHeaders[FaultsHeaderKeys.FailedQ];
                sendOptions.SetDestination(destination);

                //ServiceControl adds these headers when retrying
                sendOptions.SetHeader("ServiceControl.Retry.UniqueMessageId", "some-id");
                sendOptions.SetHeader("ServiceControl.Retry.AcknowledgementQueue", Conventions.EndpointNamingConvention(typeof(FakeSCError)));
                return context.Send(new FaultyMessage(), sendOptions);
            }

            readonly Context testContext;
        }

        class ControlMessageBehavior : Behavior<IIncomingPhysicalMessageContext>
        {
            public ControlMessageBehavior(Context testContext) => this.testContext = testContext;

            public override async Task Invoke(IIncomingPhysicalMessageContext context, Func<Task> next)
            {
                if (context.MessageHeaders.ContainsKey("ServiceControl.Retry.Successful"))
                {
                    testContext.GotRetrySuccessfullAck = true;
                    return;
                }
                await next();

            }

            Context testContext;
        }
    }

    public class Context : ScenarioContext
    {
        public bool MessageFailed { get; set; }
        public IReadOnlyDictionary<string, string> ReceivedMessageHeaders { get; set; }
        public IReadOnlyDictionary<string, string> FailedMessageHeaders { get; set; }
        public bool RetryDelivered { get; set; }
        public bool GotRetrySuccessfullAck { get; set; }
    }

    public class FaultyMessage : IMessage
    {
    }
}