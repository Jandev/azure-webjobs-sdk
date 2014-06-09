﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Azure.Jobs.Host.Bindings;

namespace Microsoft.Azure.Jobs
{
    // Enqueue an azure queue message to notify that we have a new blob. 
    // This is useful when everything is in the same process. It avoids an Azure queue that     
    // could be conflicting between multiple users sharing the same logging account. 
    internal class NotifyNewBlobViaInMemory : INotifyNewBlob, INotifyNewBlobListener
    {
        static ConcurrentQueue<BlobWrittenMessage> _queue = new ConcurrentQueue<BlobWrittenMessage>();

        public NotifyNewBlobViaInMemory()
        {
        }

        public void Notify(BlobWrittenMessage msg)
        {
            _queue.Enqueue(msg);
        }

        public void ProcessMessages(Action<BlobWrittenMessage, RuntimeBindingProviderContext> fpOnNewBlob, RuntimeBindingProviderContext context)
        {
            BlobWrittenMessage msg;
            while (!context.CancellationToken.IsCancellationRequested && _queue.TryDequeue(out msg))
            {
                fpOnNewBlob(msg, context);
            }
        }
    }
}