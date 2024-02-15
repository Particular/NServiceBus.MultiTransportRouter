﻿namespace NServiceBus.MessagingBridge.CustomChecks;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Performance.TimeToBeReceived;
using Routing;
using Transport;

class ServiceControlBackend(string destinationQueue, IMessageDispatcher messageDispatcher, TimeSpan timeToBeReceived)
{
    public Task Send(object messageToSend, CancellationToken cancellationToken = default)
    {
        var body = Serialize(messageToSend);
        return Send(body, messageToSend.GetType().FullName, cancellationToken);
    }

    static byte[] Serialize(object messageToSend) => JsonSerializer.SerializeToUtf8Bytes(messageToSend);

    Task Send(byte[] body, string messageType, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            [Headers.EnclosedMessageTypes] = messageType,
            [Headers.ContentType] = ContentTypes.Json,
            [Headers.MessageIntent] = "Send"
        };

        var outgoingMessage = new OutgoingMessage(Guid.NewGuid().ToString(), headers, body);
        var dispatchProperties = new DispatchProperties
        {
            DiscardIfNotReceivedBefore = new DiscardIfNotReceivedBefore(timeToBeReceived)
        };
        var operation =
            new TransportOperation(outgoingMessage, new UnicastAddressTag(destinationQueue), dispatchProperties);
        return messageSender?.Dispatch(new TransportOperations(operation), new TransportTransaction(),
            cancellationToken);
    }

    IMessageDispatcher messageSender = messageDispatcher;
}